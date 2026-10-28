using CxCompiler.Model;
using CxCompiler.Model.Common;
using CxCompiler.Model.Expressions;
using CxCompiler.Model.Errors;
using CxCompiler.Model.Project;
using CxCompiler.Model.Statements;
using CxCompiler.Model.Types;
using CxCompiler.Model.Types.BuiltInTypes;
using CxCompiler.Semantics;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace CxCompiler.OutputGenerators;

public static partial class CCodeOutputGenerator
{
    private static StreamWriter OpenGeneratedFile(string outputFilePath) =>
        new ReproducibleStreamWriter(outputFilePath);

    private static void WriteSourceLocation(IndentingWriter writer, SourceSpan? sourceSpan)
    {
        if (sourceSpan is not { } location || location.StartLine < 1)
            return;

        var sourcePath = Path.GetFullPath(location.FilePath)
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);
        writer.WriteLine($"#line {location.StartLine} \"{sourcePath}\"");
    }

    private sealed class ReproducibleStreamWriter : StreamWriter
    {
        private readonly string _destination;
        private readonly string _temporaryPath;
        private bool _finished;

        public ReproducibleStreamWriter(string destination)
            : this(Path.GetFullPath(destination),
                Path.Combine(Path.GetDirectoryName(Path.GetFullPath(destination))!,
                    $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp"))
        {
        }

        private ReproducibleStreamWriter(string destination, string temporaryPath)
            : base(temporaryPath, append: false)
        {
            _destination = destination;
            _temporaryPath = temporaryPath;
        }

        protected override void Dispose(bool disposing)
        {
            if (_finished)
            {
                base.Dispose(disposing);
                return;
            }
            base.Dispose(disposing);
            _finished = true;
            try
            {
                if (File.Exists(_destination) &&
                    File.ReadAllBytes(_destination).AsSpan().SequenceEqual(File.ReadAllBytes(_temporaryPath)))
                    File.Delete(_temporaryPath);
                else
                    File.Move(_temporaryPath, _destination, overwrite: true);
            }
            catch
            {
                if (File.Exists(_temporaryPath)) File.Delete(_temporaryPath);
                throw;
            }
        }
    }

    public static void GenerateOutput(CxProject project, string filePath)
    {
        var outputDirectory = Path.GetDirectoryName(filePath)
            ?? throw new ArgumentException("Output path must include a directory.", nameof(filePath));
        GenerateOutput(project, filePath, outputDirectory);
    }

    public static void GenerateOutput(CxProject project, string filePath, string projectDirectory)
    {
        if (project == null)
        {
            throw new InternalCompilerException("Project is null");
        }

        var outputDirectory = Path.GetDirectoryName(filePath)
            ?? throw new ArgumentException("Output path must include a directory.", nameof(filePath));
        var declarations = GetDeclarations(project);
        var entryPoint = FindNativeEntryPoint(project, declarations);
        Directory.CreateDirectory(outputDirectory);
        projectDirectory = Path.GetFullPath(projectDirectory);
        WriteCMakeListsFile(
            project,
            Path.Combine(outputDirectory, "CMakeLists.txt"),
            outputDirectory,
            projectDirectory);
        WriteProjectHeaderFile(
            project,
            Path.Combine(outputDirectory, $"{project.Name}.h"),
            publicApi: true);
        WriteProjectHeaderFile(
            project,
            Path.Combine(outputDirectory, $"{project.Name}.internal.h"),
            publicApi: false);
        WriteProjectSourceFile(project, Path.Combine(outputDirectory, $"{project.Name}.c"),
            declarations, entryPoint);
        if (project.Name == "cxcore" && HasCoreGeneratedSupportTypes(declarations))
        {
            WriteCoreGeneratedSupportSourceFile(
                project,
                Path.Combine(outputDirectory, "cxcore.generated.c"),
                declarations);
        }
        WriteProjectNatvisFile(
            project,
            Path.Combine(outputDirectory, $"{project.Name}.natvis"),
            declarations);
    }

    private static bool HasCoreGeneratedSupportTypes(
        IReadOnlyCollection<DeclarationBase> declarations)
    {
        var requiredTypes = new HashSet<string>(StringComparer.Ordinal)
        {
            "System.IAsyncDisposable",
            "System.Collections.Array",
            "System.Collections.ArrayEnumerator",
            "System.Collections.IAsyncEnumerator",
            "System.Collections.IEnumerator",
        };
        foreach (var declaration in EnumerateClasses(declarations))
        {
            requiredTypes.Remove(declaration.FullName.ToString());
        }
        return requiredTypes.Count == 0;
    }

    private static void WriteCoreGeneratedSupportSourceFile(
        CxProject project,
        string outputFilePath,
        IReadOnlyCollection<DeclarationBase> declarations)
    {
        var genericTypeInstances = GetGenericTypeInstancesWithClosedInterfaces(
            project.GenericTypeInstances);
        using var fileWriter = OpenGeneratedFile(outputFilePath);
        var writer = new IndentingWriter(fileWriter);

        writer.WriteLine("// Generated generic CX core support linked into the native runtime.");
        writer.WriteLine("#include \"cxcore.internal.h\"");
        writer.WriteLine("#include \"cxruntime.h\"");
        writer.WriteLine("#include <stdlib.h>");
        writer.WriteLine("#include <string.h>");
        writer.WriteLine("#if !defined(CX_RUNTIME_ABI_VERSION) || CX_RUNTIME_ABI_VERSION != 1");
        writer.WriteLine("#error This generated code requires cxcore runtime ABI version 1.");
        writer.WriteLine("#endif");
        writer.WriteLine();
        writer.WriteLine(
            "extern struct cxcore_System_String __namespace_System;");
        writer.WriteLine();

        var nativeSupportTypes = EnumerateClasses(declarations)
            .Where(declaration => declaration.FullName.ToString() is
                "System.IAsyncDisposable" or
                "System.Collections.ArrayEnumerator" or
                "System.Collections.IAsyncEnumerator" or
                "System.Collections.IEnumerator")
            .Cast<DeclarationBase>()
            .ToArray();
        foreach (var declaration in nativeSupportTypes.OfType<ClassDeclaration>())
        {
            var nameIdentifier = new QualifiedIdentifier("__name", declaration.FullName);
            writer.WriteLine(
                $"CX_STRING_DEF({nameIdentifier.ToCIdentifier()}, \"{declaration.Name}\");");
        }
        writer.WriteLine(
            "CX_STRING_DEF(__namespace_System_Collections, \"System.Collections\");");
        writer.WriteLine();

        // Generated generic functions conventionally enter through the CX core
        // root-registration hook. The native core owns the actual static roots;
        // this support unit has no CX static fields of its own.
        writer.WriteLine("static void __cx_register_static_roots_CXCORE(void)");
        writer.WriteLine("{");
        writer.IncreaseIndent();
        writer.WriteLine("cx_gc_check_thread();");
        writer.DecreaseIndent();
        writer.WriteLine("}");
        writer.WriteLine();

        WriteTypeInfos(writer, nativeSupportTypes, project.Name);
        WriteClosedGenericTypeInfos(
            writer,
            genericTypeInstances,
            project.GenericFunctionInstances,
            project.Name);
        var genericCollectionImplementations = EnumerateClasses(declarations)
            .Where(declaration => declaration.FullName.ToString() ==
                "System.Collections.ArrayEnumerator")
            .Cast<DeclarationBase>()
            .ToArray();
        WriteFunctionDefinitions(writer, genericCollectionImplementations, project.Name);
        writer.WriteLine(
            "cx_uint cxcore_System_Collections_Array_Count___const_get(" +
            "const struct cxcore_System_Collections_Array* __this) {");
        writer.IncreaseIndent();
        writer.WriteLine(
            "return cxcore_System_Collections_Array_Length___const_get(__this);");
        writer.DecreaseIndent();
        writer.WriteLine("}");
        writer.WriteLine();
        WriteGenericFunctionDefinitions(
            writer,
            project.GenericFunctionInstances,
            project.Name);
    }

    private static void WriteProjectNatvisFile(
        CxProject project,
        string outputFilePath,
        IReadOnlyCollection<DeclarationBase> declarations)
    {
        XNamespace ns = "http://schemas.microsoft.com/vstudio/debugger/natvis/2010";
        var visualizers = declarations
            .OfType<ClassDeclaration>()
            .Where(declaration => !declaration.IsStatic &&
                declaration.ClassType is ClassType.Class or ClassType.Struct)
            .Select(declaration => new XElement(ns + "Type",
                new XAttribute("Name", GetNatvisTypeName(declaration, project.Name)),
                new XElement(ns + "Expand",
                    EnumerateNatvisFields(declaration, "", new HashSet<ClassDeclaration>())
                        .Select(field => new XElement(ns + "Item",
                            new XAttribute("Name", field.Name),
                            field.Expression)))))
            .ToArray();

        var document = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement(ns + "AutoVisualizer", visualizers));
        using var writer = OpenGeneratedFile(outputFilePath);
        document.Save(writer);
    }

    private static string GetNatvisTypeName(ClassDeclaration declaration, string defaultModuleName)
    {
        var moduleName = declaration.ProjectName ?? defaultModuleName;
        return string.Join("_", new[] { moduleName }.Concat(declaration.FullName.Parts));
    }

    private static IEnumerable<(string Name, string Expression)> EnumerateNatvisFields(
        ClassDeclaration declaration,
        string fieldPrefix,
        HashSet<ClassDeclaration> visited)
    {
        if (!visited.Add(declaration)) yield break;

        if (declaration.ClassType == ClassType.Class &&
            declaration.BaseClassDeclaration is { } baseDeclaration)
        {
            foreach (var field in EnumerateNatvisFields(
                baseDeclaration,
                fieldPrefix + "__base.",
                visited))
            {
                yield return field;
            }
        }

        var isRuntimeObject = declaration.Name == "Object" &&
            declaration.FullName.ToString() == "System.Object";
        foreach (var field in declaration.MemberDeclarations.Declarations
            .OfType<FieldDeclaration>()
            .Where(field => !field.IsStatic && !isRuntimeObject))
        {
            yield return (field.Name, fieldPrefix + field.Name);
        }
    }

    private static void WriteCMakeListsFile(
        CxProject project,
        string outputFilePath,
        string outputDirectory,
        string projectDirectory)
    {
        using var fileWriter = OpenGeneratedFile(outputFilePath);

        fileWriter.WriteLine("cmake_minimum_required(VERSION 3.31)");
        fileWriter.WriteLine($"project({project.Name})");
        fileWriter.WriteLine();

        fileWriter.WriteLine("set(CX_PROJECT_SOURCES");
        fileWriter.WriteLine($"    \"${{CMAKE_CURRENT_LIST_DIR}}/{EscapeCMakePath(project.Name)}.c\"");
        fileWriter.WriteLine(")");
        foreach (var nativeSource in project.Name == "cxcore"
            ? Array.Empty<string>()
            : EnumerateNativeCSourceFiles(
                projectDirectory,
                Path.Combine(outputDirectory, $"{project.Name}.c")))
        {
            var relativeSourcePath = Path.GetRelativePath(outputDirectory, nativeSource);
            var relativeProjectPath = Path.GetRelativePath(projectDirectory, nativeSource)
                .Replace('\\', '/');
            var pathParts = relativeProjectPath.Split('/');
            var platform = pathParts.Length > 2 &&
                pathParts[0].Equals("platform", StringComparison.OrdinalIgnoreCase)
                ? pathParts[1]
                : null;
            if (platform is "win" or "linux")
            {
                fileWriter.WriteLine(platform == "win" ? "if(WIN32)" :
                    "if(CMAKE_SYSTEM_NAME STREQUAL \"Linux\")");
                fileWriter.WriteLine(
                    $"    list(APPEND CX_PROJECT_SOURCES \"${{CMAKE_CURRENT_LIST_DIR}}/" +
                    $"{EscapeCMakePath(relativeSourcePath)}\")");
                fileWriter.WriteLine("endif()");
            }
            else
            {
                fileWriter.WriteLine(
                    $"list(APPEND CX_PROJECT_SOURCES \"${{CMAKE_CURRENT_LIST_DIR}}/" +
                    $"{EscapeCMakePath(relativeSourcePath)}\")");
            }
        }
        fileWriter.WriteLine();

        var binaryDirectory = Path.GetRelativePath(
            outputDirectory,
            Path.Combine(projectDirectory, ".bin"));
        fileWriter.WriteLine(
            $"set(CX_BIN_DIRECTORY \"${{CMAKE_CURRENT_LIST_DIR}}/{EscapeCMakePath(binaryDirectory)}\")");
        fileWriter.WriteLine("set(CMAKE_RUNTIME_OUTPUT_DIRECTORY \"${CX_BIN_DIRECTORY}\")");
        fileWriter.WriteLine("set(CMAKE_LIBRARY_OUTPUT_DIRECTORY \"${CX_BIN_DIRECTORY}\")");
        fileWriter.WriteLine("set(CMAKE_ARCHIVE_OUTPUT_DIRECTORY \"${CMAKE_CURRENT_LIST_DIR}\")");
        fileWriter.WriteLine();
        fileWriter.WriteLine("option(CX_STATIC_LINK \"Link CX runtime and project references statically\" ON)");
        fileWriter.WriteLine("if(CX_STATIC_LINK AND CMAKE_SYSTEM_NAME STREQUAL \"Linux\")");
        fileWriter.WriteLine("    set(CMAKE_POSITION_INDEPENDENT_CODE ON)");
        fileWriter.WriteLine("endif()");
        fileWriter.WriteLine("if(DEFINED CXCORE_SOURCE_DIR AND NOT TARGET cxcore)");
        fileWriter.WriteLine("    if(CX_STATIC_LINK)");
        fileWriter.WriteLine("        set(CX_BUILD_STATIC ON CACHE BOOL \"\" FORCE)");
        fileWriter.WriteLine("    else()");
        fileWriter.WriteLine("        set(CX_BUILD_STATIC OFF CACHE BOOL \"\" FORCE)");
        fileWriter.WriteLine("    endif()");
        fileWriter.WriteLine("    set(BUILD_TESTING OFF CACHE BOOL \"\" FORCE)");
        fileWriter.WriteLine("    add_subdirectory(\"${CXCORE_SOURCE_DIR}\" \"${CMAKE_CURRENT_BINARY_DIR}/cxcore\")");
        fileWriter.WriteLine("endif()");
        fileWriter.WriteLine("if(NOT DEFINED CXCORE_ABI_VERSION)");
        fileWriter.WriteLine("    message(FATAL_ERROR \"This generated project requires a cxcore runtime that publishes CXCORE_ABI_VERSION (required ABI 1).\")");
        fileWriter.WriteLine("elseif(NOT CXCORE_ABI_VERSION EQUAL 1)");
        fileWriter.WriteLine("    message(FATAL_ERROR \"This generated project requires cxcore ABI 1; found ABI ${CXCORE_ABI_VERSION}.\")");
        fileWriter.WriteLine("endif()");
        fileWriter.WriteLine();

        if (project.Name == "cxcore")
        {
            fileWriter.WriteLine("if(NOT TARGET cxcore)");
            fileWriter.WriteLine(
                "    message(FATAL_ERROR \"The cxcore source project must be built with its own CMake project.\")");
            fileWriter.WriteLine("endif()");
        }
        else switch (project.Type)
        {
            case CxProjectType.Library:
                fileWriter.WriteLine($"if(CX_BUILDING_DEPENDENCY AND CX_STATIC_LINK)");
                fileWriter.WriteLine($"    add_library({project.Name} STATIC ${{CX_PROJECT_SOURCES}})");
                fileWriter.WriteLine("else()");
                fileWriter.WriteLine($"    add_library({project.Name} SHARED ${{CX_PROJECT_SOURCES}})");
                fileWriter.WriteLine("endif()");
                break;

            case CxProjectType.Executable:
                fileWriter.WriteLine($"add_executable({project.Name} ${{CX_PROJECT_SOURCES}})");
                break;

            default:
                throw new ArgumentOutOfRangeException($"Invalid CxProjectType {project.Type}");
        }

        fileWriter.WriteLine(
            $"target_compile_definitions({project.Name} PRIVATE {GetModuleExportDefine(project.Name)})");
        fileWriter.WriteLine($"target_compile_features({project.Name} PRIVATE c_std_11)");
        fileWriter.WriteLine($"if(MSVC)");
        fileWriter.WriteLine(
            $"    target_compile_options({project.Name} PRIVATE /utf-8 /experimental:c11atomics)");
        fileWriter.WriteLine($"endif()");
        fileWriter.WriteLine($"if(CX_STATIC_LINK)");
        fileWriter.WriteLine($"    target_compile_definitions({project.Name} PRIVATE CX_STATIC_LINK)");
        fileWriter.WriteLine("else()");
        fileWriter.WriteLine($"    target_compile_definitions({project.Name} PRIVATE CX_DYNAMIC_MODULE)");
        fileWriter.WriteLine("endif()");
        var includeDirectory = Path.GetRelativePath(outputDirectory, projectDirectory);
        fileWriter.WriteLine(
            $"target_include_directories({project.Name} PRIVATE \"${{CMAKE_CURRENT_LIST_DIR}}/{EscapeCMakePath(includeDirectory)}\")");
        fileWriter.WriteLine(
            $"target_include_directories({project.Name} PRIVATE \"${{CMAKE_CURRENT_LIST_DIR}}\")");
        var dependencyLinkVisibility = project.Type == CxProjectType.Library
            ? "PUBLIC"
            : "PRIVATE";
        foreach (var dependencyDirectory in project.ResolvedProjectDirectories)
        {
            var dependencyOutput = Path.GetRelativePath(
                outputDirectory, Path.Combine(dependencyDirectory, ".obj"));
            fileWriter.WriteLine(
                $"target_include_directories({project.Name} BEFORE {dependencyLinkVisibility} " +
                $"\"${{CMAKE_CURRENT_LIST_DIR}}/{EscapeCMakePath(dependencyOutput)}\")");
            var dependencyRoot = Directory.GetParent(dependencyDirectory)?.FullName;
            var dependencyInclude = dependencyRoot is null
                ? null
                : Path.Combine(dependencyRoot, "inc");
            if (dependencyInclude is not null && Directory.Exists(dependencyInclude))
            {
                var relativeDependencyInclude = Path.GetRelativePath(
                    outputDirectory, dependencyInclude);
                fileWriter.WriteLine(
                    $"target_include_directories({project.Name} {dependencyLinkVisibility} " +
                    $"\"${{CMAKE_CURRENT_LIST_DIR}}/" +
                    $"{EscapeCMakePath(relativeDependencyInclude)}\")");
            }
        }
        var nativeIncludeDirectories = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        void AddNativeIncludeDirectory(string path)
        {
            var fullPath = Path.GetFullPath(path);
            if (Directory.Exists(fullPath))
            {
                nativeIncludeDirectories.Add(fullPath);
            }
        }

        AddNativeIncludeDirectory(Path.Combine(projectDirectory, "include"));
        AddNativeIncludeDirectory(Path.Combine(projectDirectory, "platform", "common"));
        foreach (var platform in new[] { "win", "linux" })
        {
            AddNativeIncludeDirectory(Path.Combine(projectDirectory, "platform", platform));
        }
        foreach (var dependencyDirectory in project.ResolvedProjectDirectories)
        {
            var dependencyRoot = Directory.GetParent(dependencyDirectory)?.FullName;
            if (dependencyRoot is null)
            {
                continue;
            }

            AddNativeIncludeDirectory(dependencyDirectory);
            AddNativeIncludeDirectory(Path.Combine(dependencyRoot, "inc"));
            AddNativeIncludeDirectory(Path.Combine(dependencyRoot, "include"));
            AddNativeIncludeDirectory(Path.Combine(dependencyRoot, "platform"));
            AddNativeIncludeDirectory(Path.Combine(dependencyRoot, "platform", "common"));
            foreach (var platform in new[] { "win", "linux" })
            {
                AddNativeIncludeDirectory(
                    Path.Combine(dependencyRoot, "platform", platform));
            }
        }
        foreach (var nativeIncludeDirectory in nativeIncludeDirectories)
        {
            var relativeInclude = Path.GetRelativePath(outputDirectory, nativeIncludeDirectory);
            fileWriter.WriteLine(
                $"target_include_directories({project.Name} PRIVATE " +
                $"\"${{CMAKE_CURRENT_LIST_DIR}}/{EscapeCMakePath(relativeInclude)}\")");
        }
        if (project.Name != "cxcore")
        {
            fileWriter.WriteLine($"if(TARGET cxcore)");
            fileWriter.WriteLine($"    target_link_libraries({project.Name} PRIVATE cxcore)");
            fileWriter.WriteLine($"endif()");
        }
        foreach (var (directory, name) in project.ResolvedProjectDirectories
            .Zip(project.ResolvedProjectNames))
        {
            var dependencyOutput = Path.GetRelativePath(outputDirectory, Path.Combine(directory, ".obj"));
            var dependencyBinaryDir = $"${{CMAKE_CURRENT_BINARY_DIR}}/dep_{GetModuleToken(name)}";
            fileWriter.WriteLine($"if(NOT TARGET {name})");
            fileWriter.WriteLine("    set(CX_BUILDING_DEPENDENCY ON)");
            fileWriter.WriteLine(
                $"    add_subdirectory(\"${{CMAKE_CURRENT_LIST_DIR}}/{EscapeCMakePath(dependencyOutput)}\" \"{dependencyBinaryDir}\")");
            fileWriter.WriteLine("    unset(CX_BUILDING_DEPENDENCY)");
            fileWriter.WriteLine("endif()");
            fileWriter.WriteLine(
                $"target_link_libraries({project.Name} {dependencyLinkVisibility} {name})");
            fileWriter.WriteLine("if(NOT CX_STATIC_LINK)");
            fileWriter.WriteLine(
                $"    add_custom_command(TARGET {project.Name} POST_BUILD COMMAND ${{CMAKE_COMMAND}} -E copy_if_different \"$<TARGET_FILE:{name}>\" \"$<TARGET_FILE_DIR:{project.Name}>\")");
            fileWriter.WriteLine("endif()");
        }

        WriteNativeDependencies(fileWriter, project, projectDirectory, outputDirectory);
        fileWriter.Flush();
        fileWriter.Close();
    }

    private static IEnumerable<string> EnumerateNativeCSourceFiles(
        string projectDirectory,
        string generatedSourcePath)
    {
        var generatedSourceFullPath = Path.GetFullPath(generatedSourcePath);
        var generatedOutputDirectory = Path.GetDirectoryName(generatedSourceFullPath)!;
        var excludedDirectoryNames = new HashSet<string>(
            [".obj", ".bin", ".git", ".vs", ".packages", ".dist", "CMakeFiles", "tests"],
            StringComparer.OrdinalIgnoreCase);

        return Directory.EnumerateFiles(projectDirectory, "*.c", SearchOption.AllDirectories)
            .Where(path => !string.Equals(
                Path.GetFullPath(path),
                generatedSourceFullPath,
                StringComparison.OrdinalIgnoreCase))
            .Where(path => !IsWithinDirectory(Path.GetFullPath(path), generatedOutputDirectory))
            .Where(path => !IsGeneratedCxSource(path))
            .Where(path => Path.GetRelativePath(projectDirectory, path)
                .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                    StringSplitOptions.RemoveEmptyEntries)
                .SkipLast(1)
                .All(directory => !excludedDirectoryNames.Contains(directory)))
            .OrderBy(path => Path.GetRelativePath(projectDirectory, path),
                StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsGeneratedCxSource(string path) =>
        File.ReadLines(path).FirstOrDefault()?.StartsWith(
            "// This is an autogenerated C source file for project '",
            StringComparison.Ordinal) == true;

    private static bool IsWithinDirectory(string path, string directory)
    {
        var relative = Path.GetRelativePath(directory, path);
        return !Path.IsPathRooted(relative) && relative != "." &&
            relative != ".." && !relative.StartsWith($"..{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal) && !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}",
                StringComparison.Ordinal);
    }

    private static string EscapeCMakePath(string path) =>
        path.Replace('\\', '/').Replace("\"", "\\\"").Replace(";", "\\;");


    private static void WriteProjectHeaderFile(
        CxProject project,
        string outputFilePath,
        bool publicApi)
    {
        using var fileWriter = OpenGeneratedFile(outputFilePath);
        var writer = new IndentingWriter(fileWriter);

        writer.WriteLine($"// This is an autogenerated C header file for project '{project.Name}'");
        writer.WriteLine();

        string headerGuardName = project.Name.ToUpperInvariant().Replace('.', '_') +
            (publicApi ? string.Empty : "_INTERNAL");
        writer.WriteLine($"#ifndef _{headerGuardName}_H_");
        writer.WriteLine($"#define _{headerGuardName}_H_");
        writer.WriteLine();
        if (publicApi)
        {
            writer.WriteLine($"#if defined({GetModuleExportDefine(project.Name)})");
            writer.WriteLine($"#include \"{project.Name}.internal.h\"");
            writer.WriteLine("#else");
            writer.WriteLine();
        }
        writer.WriteLine(project.Name == "cxcore" ||
            project.ResolvedProjectNames.Count > 0
            ? "#include <cx.h>"
            : "#include <cxcore.h>");
        writer.WriteLine();

        foreach (var (dependencyDirectory, dependencyName) in
            project.ResolvedProjectDirectories.Zip(project.ResolvedProjectNames))
        {
            var dependencyHeader = Path.GetFullPath(Path.Combine(
                dependencyDirectory, ".obj",
                publicApi ? $"{dependencyName}.h" : $"{dependencyName}.internal.h"));
            var relativeDependencyHeader = Path.GetRelativePath(
                Path.GetDirectoryName(outputFilePath)!, dependencyHeader)
                .Replace('\\', '/');
            writer.WriteLine($"#include \"{relativeDependencyHeader}\"");
        }
        if (project.ResolvedProjectNames.Count != 0)
        {
            writer.WriteLine();
        }

        writer.WriteLine($"#if defined({GetModuleExportDefine(project.Name)})");
        writer.WriteLine($"#define {GetModuleApiName(project.Name)} CX_EXPORT");
        writer.WriteLine("#elif defined(CX_STATIC_LINK)");
        writer.WriteLine($"#define {GetModuleApiName(project.Name)}");
        writer.WriteLine("#else");
        writer.WriteLine($"#define {GetModuleApiName(project.Name)} CX_IMPORT");
        writer.WriteLine("#endif");
        writer.WriteLine($"#if defined({GetModuleExportDefine(project.Name)})");
        writer.WriteLine($"#define {GetModuleDataApiName(project.Name)} CX_EXPORT");
        writer.WriteLine("#elif defined(CX_STATIC_LINK)");
        writer.WriteLine($"#define {GetModuleDataApiName(project.Name)}");
        writer.WriteLine("#else");
        writer.WriteLine($"#define {GetModuleDataApiName(project.Name)} CX_IMPORT");
        writer.WriteLine("#endif");
        writer.WriteLine("#undef CX_CURRENT_TYPE_API");
        writer.WriteLine($"#define CX_CURRENT_TYPE_API {GetModuleDataApiName(project.Name)}");
        writer.WriteLine();

        if (publicApi && project.Name == "cxcore")
        {
            writer.WriteLine("#include <stdint.h>");
            writer.WriteLine("extern CX_CXCORE_API uint64_t cx_task_scheduler_post_on_socket(");
            writer.WriteLine("    uintptr_t socket_handle, int writable, cx_action continuation,");
            writer.WriteLine("    cx_action shutdown_continuation);");
            writer.WriteLine("extern CX_CXCORE_API int cx_task_scheduler_cancel_socket_wait(");
            writer.WriteLine("    uint64_t registration_id);");
            writer.WriteLine();
        }

        WriteNativeHeaders(writer, project, publicApi);
        var allDeclarations = GetDeclarations(project);
        var publicTypeNames = publicApi
            ? GetPublicApiTypeNames(allDeclarations, GetReferencedDeclarations(project))
            : null;
        var declarations = publicApi
            ? GetPublicApiDeclarations(allDeclarations, publicTypeNames!)
            : allDeclarations;
        var genericTypeInstances = GetGenericTypeInstancesWithClosedInterfaces(publicApi
            ? GetPublicApiGenericTypeInstances(project, publicTypeNames!)
            : project.GenericTypeInstances);
        var genericFunctionInstances = publicApi
            ? project.GenericFunctionInstances.Where(IsPublicApiGenericFunction).ToArray()
            : project.GenericFunctionInstances;

        writer.WriteLine("//");
        writer.WriteLine("// Forward type declarations");
        writer.WriteLine("//");
        writer.WriteLine();
        WriteTypesForwardDeclarations(writer, declarations, project.Name, publicApi, publicTypeNames);
        foreach (var instance in genericTypeInstances.Where(item =>
            RequiresClosedValueLayout(item.Type)))
        {
            writer.WriteLine($"struct {instance.Type.ConstructedIdentity!.CIdentifier};");
        }
        writer.WriteLine();
        WriteFunctionTypeDeclarations(writer, declarations, project.Name);

        writer.WriteLine("//");
        writer.WriteLine("// Type declarations");
        writer.WriteLine("//");
        writer.WriteLine();
        var cxcoreObjectDeclaration = project.Name == "cxcore"
            ? OrderClassDeclarations(declarations, publicApi, publicTypeNames)
                .FirstOrDefault(classDeclaration => classDeclaration.Name == "Object")
            : null;
        if (cxcoreObjectDeclaration is not null)
        {
            // Open generic value placeholders and closed generic value layouts
            // embed Object by value, so cxcore must define it before either.
            WriteTypesDeclarations(
                writer,
                declarations,
                project.Name,
                publicApi,
                publicTypeNames,
                candidate => ReferenceEquals(candidate, cxcoreObjectDeclaration));
        }
        var nullableStorageDeclaration = GetClassDeclarations(
                declarations, publicApi, publicTypeNames)
            .FirstOrDefault(declaration =>
                declaration.ClassType == ClassType.Struct &&
                declaration.GenericTypeNames.Length > 0 &&
                declaration.FullName.ToString() == "System.Nullable");
        if (nullableStorageDeclaration is not null)
        {
            // NullableType is represented by Nullable<T>'s fixed pointer-sized
            // storage, so expose that layout before structs that embed nullable
            // reference fields by value (for example TypeInfo.BaseType).
            WriteTypesDeclarations(
                writer,
                declarations,
                project.Name,
                publicApi,
                publicTypeNames,
                candidate => ReferenceEquals(candidate, nullableStorageDeclaration));
        }
        WriteTypesDeclarations(
            writer,
            declarations,
            project.Name,
            publicApi,
            publicTypeNames,
            classDeclaration => classDeclaration.ClassType == ClassType.Struct &&
                classDeclaration.GenericTypeNames.Length == 0);
        WriteClosedValueTypeDeclarations(
            writer,
            genericTypeInstances.Where(instance =>
                instance.Declaration.ClassType == ClassType.Struct).ToArray());
        var genericStructDeclarations = OrderGenericStructDeclarations(
                declarations, moduleName: project.Name, publicApi, publicTypeNames)
            .Where(declaration => !ReferenceEquals(
                declaration, nullableStorageDeclaration));
        foreach (var genericStructDeclaration in genericStructDeclarations)
        {
            WriteTypesDeclarations(
                writer,
                declarations,
                project.Name,
                publicApi,
                publicTypeNames,
                candidate => ReferenceEquals(candidate, genericStructDeclaration));
        }
        foreach (var classDeclaration in OrderClassDeclarations(
            declarations, publicApi, publicTypeNames))
        {
            if (ReferenceEquals(classDeclaration, cxcoreObjectDeclaration))
            {
                continue;
            }
            WriteTypesDeclarations(
                writer,
                declarations,
                project.Name,
                publicApi,
                publicTypeNames,
                candidate => ReferenceEquals(candidate, classDeclaration));
        }
        WriteClosedValueTypeDeclarations(
            writer,
            genericTypeInstances.Where(instance =>
                instance.Declaration.ClassType == ClassType.Class).ToArray());
        WriteStaticFieldDeclarations(writer, declarations, project.Name, publicApi, publicTypeNames);
        foreach (var instance in genericTypeInstances.OrderBy(item =>
            item.Type.ConstructedIdentity!.CanonicalName, StringComparer.Ordinal))
        {
            ValidateClosedReferenceLayout(instance.Type, instance.Declaration);
            writer.WriteLine($"CX_VTABLE_DECL({instance.Type.ConstructedIdentity!.CIdentifier});");
            writer.WriteLine($"CX_TYPEINFO_DECL({instance.Type.ConstructedIdentity.CIdentifier});");
        }
        writer.WriteLine();

        writer.WriteLine("//");
        writer.WriteLine("// Function and property declarations");
        writer.WriteLine("//");
        writer.WriteLine();
        writer.WriteLine($"extern {GetModuleApiName(project.Name)} void __cx_module_init_{GetModuleToken(project.Name)}(void);");
        if (publicApi)
        {
            foreach (var dependencyName in project.ResolvedProjectNames)
            {
                writer.WriteLine($"extern {GetModuleApiName(dependencyName)} void __cx_module_init_{GetModuleToken(dependencyName)}(void);");
            }
        }
        WriteFunctionsForwardDeclarations(writer, declarations, project.Name, publicApi, publicTypeNames);
        WriteGenericFunctionDeclarations(writer, genericFunctionInstances);
        writer.WriteLine();

        writer.WriteLine();
        if (publicApi)
        {
            writer.WriteLine("#endif");
        }
        writer.WriteLine($"#endif // _{headerGuardName}_H_");

        fileWriter.Flush();
        fileWriter.Close();
    }

    private static string GetModuleToken(string moduleName) =>
        new(moduleName.Select(character => char.IsLetterOrDigit(character)
            ? char.ToUpperInvariant(character)
            : '_').ToArray());

    private static string GetModuleExportDefine(string moduleName) =>
        $"CX_{GetModuleToken(moduleName)}_BUILD";

    private static string GetModuleApiName(string moduleName) =>
        $"CX_{GetModuleToken(moduleName)}_API";

    private static string GetModuleDataApiName(string moduleName) =>
        $"CX_{GetModuleToken(moduleName)}_DATA_API";

    private static void WriteTypesForwardDeclarations(
        IndentingWriter writer,
        IReadOnlyCollection<DeclarationBase> declarations,
        string moduleName,
        bool publicApi,
        IReadOnlySet<QualifiedIdentifier>? publicTypeNames)
    {
        foreach (var declaration in declarations)
        {
            if (declaration is EnumDeclaration enumDeclaration)
            {
                var enumName = enumDeclaration.ToCIdentifier(moduleName);
                if (enumDeclaration.UnderlyingType is { } underlying)
                {
                    writer.WriteLine($"typedef {underlying.ToCIdentifier(false)} {enumName};");
                    System.Numerics.BigInteger nextValue = 0;
                    foreach (var member in enumDeclaration.Members)
                    {
                        if (member.NativeConstantValue is { } value) nextValue = value;
                        writer.WriteLine($"#define {member.ToCIdentifier(moduleName)} (({enumName})({nextValue}))");
                        ++nextValue;
                    }
                    writer.WriteLine($"CX_TYPEINFO_DECL({enumName});");
                    continue;
                }
                writer.WriteLine($"typedef enum {enumName} {{");
                writer.IncreaseIndent();
                for (var index = 0; index < enumDeclaration.Members.Count; index++)
                {
                    var member = enumDeclaration.Members[index];
                    var value = member.Value is null
                        ? string.Empty
                        : $" = {member.Value.SourceText}";
                    var comma = index + 1 < enumDeclaration.Members.Count ? "," : string.Empty;
                    writer.WriteLine($"{member.ToCIdentifier(moduleName)}{value}{comma}");
                }
                writer.DecreaseIndent();
                writer.WriteLine($"}} {enumName};");
                writer.WriteLine($"CX_TYPEINFO_DECL({enumName});");
            }
            else if (declaration is ClassDeclaration classDeclaration)
            {
                writer.WriteLine($"{classDeclaration.ToCIdentifier(moduleName)};");

                var memberDeclarations = classDeclaration.MemberDeclarations.Declarations
                    .Where(x => x is ClassDeclaration or EnumDeclaration)
                    .Where(member => !publicApi || IsPublicApiType(member, publicTypeNames!))
                    .ToArray();
                if (memberDeclarations.Length != 0)
                {
                    WriteTypesForwardDeclarations(
                        writer, memberDeclarations, moduleName, publicApi, publicTypeNames);
                }
            }
        }
    }

    private static void WriteTypesDeclarations(
        IndentingWriter writer,
        IReadOnlyCollection<DeclarationBase> declarations,
        string moduleName,
        bool publicApi,
        IReadOnlySet<QualifiedIdentifier>? publicTypeNames,
        Func<ClassDeclaration, bool> includeClassDeclaration)
    {
        // Generic value placeholders use the same Object prefix as their closed
        // layouts. In cxcore the Object definition must therefore precede those
        // declarations (and any other by-value Object fields).
        foreach (var declaration in declarations.OrderBy(declaration =>
            declaration is ClassDeclaration { Name: "Object" } && moduleName == "cxcore"
                ? 0
                : 1))
        {
            if (declaration is ClassDeclaration classDeclaration)
            {
                if (includeClassDeclaration(classDeclaration) &&
                    classDeclaration.ClassType == ClassType.Class && classDeclaration.IsStatic)
                {
                    writer.WriteLine($"CX_STATIC_TYPE_DEF({classDeclaration.ToCIdentifier(moduleName, false)});");
                }
                else if (includeClassDeclaration(classDeclaration))
                {
                    writer.WriteLine($"CX_TYPE_DEF({classDeclaration.ToCIdentifier(moduleName, false)}) {{");

                    writer.IncreaseIndent();

                    if (moduleName == "cxcore" && classDeclaration.Name == "Object")
                    {
                        writer.WriteLine("void* __vtable;");
                    }
                    else if (classDeclaration.ClassType == ClassType.Interface)
                    {
                        writer.WriteLine("void* __vtable;");
                        writer.WriteLine("void* __root;");
                    }
                    else if (classDeclaration.ClassType == ClassType.Class)
                    {
                        var baseType = classDeclaration.BaseClassType ?? BuiltInSystemTypes.Object;
                        writer.WriteLine($"{ToCStorageType(baseType)} __base;");
                    }
                    else if (classDeclaration.ClassType == ClassType.Struct &&
                        classDeclaration.GenericTypeNames.Length > 0 &&
                        classDeclaration.FullName.ToString() != "System.Nullable")
                    {
                        // Closed generic value layouts carry Object as their first field.
                        // Keep the open placeholder used by generic method bodies in the
                        // same shape, or field access through that placeholder reads the
                        // wrong offset from a closed value (for example ValueTask<T>).
                        writer.WriteLine($"{ToCStorageType(BuiltInSystemTypes.Object)} __base;");
                    }

                    var fieldMemberDeclarations = classDeclaration.MemberDeclarations.Declarations
                        .OfType<FieldDeclaration>()
                        .Where(field => !field.IsStatic)
                        .ToArray();
                    if (!fieldMemberDeclarations.Any() &&
                        classDeclaration.ClassType == ClassType.Struct)
                    {
                        writer.WriteLine("cx_byte __dummy;");
                    }
                    else
                    {
                        foreach (var fieldDeclaration in fieldMemberDeclarations)
                        {
                            var fieldType = fieldDeclaration.Type is GenericType &&
                                classDeclaration.GenericTypeNames.Length > 0
                                ? "cx_ptr"
                                : fieldDeclaration.Type.ToCIdentifier(false);
                            writer.WriteLine($"{fieldType} {fieldDeclaration.Name};");
                        }
                    }

                    writer.DecreaseIndent();
                    writer.WriteLine("};");

                }

                var classMemberDeclarations = classDeclaration.MemberDeclarations.Declarations
                    .OfType<ClassDeclaration>()
                    .Where(member => !publicApi || IsPublicApiType(member, publicTypeNames!))
                    .ToArray();
                if (classMemberDeclarations.Length != 0)
                {
                    WriteTypesDeclarations(
                        writer,
                        classMemberDeclarations,
                        moduleName,
                        publicApi,
                        publicTypeNames,
                        includeClassDeclaration);
                }
            }
        }
    }

    private static void WriteStaticFieldDeclarations(
        IndentingWriter writer,
        IReadOnlyCollection<DeclarationBase> declarations,
        string moduleName,
        bool publicApi,
        IReadOnlySet<QualifiedIdentifier>? publicTypeNames)
    {
        foreach (var classDeclaration in GetClassDeclarations(
            declarations, publicApi, publicTypeNames))
        {
            foreach (var staticField in classDeclaration.MemberDeclarations.Declarations
                .OfType<FieldDeclaration>()
                .Where(field => field.IsStatic &&
                    (!publicApi || field.MemberModifiers.Contains(MemberModifier.Public))))
            {
                writer.WriteLine(
                    $"{GetModuleDataApiName(moduleName)} extern " +
                    $"{staticField.Type.ToCIdentifier(false)} " +
                    $"{GetStaticFieldIdentifier(staticField, moduleName)};");
            }
        }
    }

    private static IEnumerable<ClassDeclaration> GetClassDeclarations(
        IReadOnlyCollection<DeclarationBase> declarations,
        bool publicApi,
        IReadOnlySet<QualifiedIdentifier>? publicTypeNames)
    {
        foreach (var classDeclaration in declarations.OfType<ClassDeclaration>())
        {
            yield return classDeclaration;
            var nestedTypes = classDeclaration.MemberDeclarations.Declarations
                .OfType<ClassDeclaration>()
                .Where(member => !publicApi || IsPublicApiType(member, publicTypeNames!))
                .Cast<DeclarationBase>().ToArray();
            foreach (var nestedDeclaration in GetClassDeclarations(
                nestedTypes, publicApi, publicTypeNames))
            {
                yield return nestedDeclaration;
            }
        }
    }

    private static void WriteFunctionsForwardDeclarations(
        IndentingWriter writer,
        IReadOnlyCollection<DeclarationBase> declarations,
        string moduleName,
        bool publicApi,
        IReadOnlySet<QualifiedIdentifier>? publicTypeNames)
    {
        bool exportable;
        bool isFirstParameter;

        foreach (var declaration in declarations)
        {
            switch (declaration)
            {
                case ClassDeclaration classDeclaration:
                    var memberDeclarations = classDeclaration.MemberDeclarations.Declarations
                        .Where(member => !publicApi || IsPublicApiMember(member, classDeclaration))
                        .ToArray();
                    if (memberDeclarations.Length != 0)
                    {
                        WriteFunctionsForwardDeclarations(
                            writer, memberDeclarations, moduleName, publicApi, publicTypeNames);
                    }

                    break;

                case ExtensionDeclaration extensionDeclaration:
                    if (!publicApi || extensionDeclaration.Visibility == Visibility.Public)
                    {
                        WriteFunctionsForwardDeclarations(
                            writer,
                            extensionDeclaration.Members.MemberDeclarations.Declarations,
                            moduleName,
                            publicApi,
                            publicTypeNames);
                    }
                    break;

                case FunctionDeclaration functionDeclaration:
                    if (functionDeclaration.NativeImport is not null)
                    {
                        if (!publicApi || IsPublicApiFunction(functionDeclaration))
                            WriteNativeImportDeclaration(writer, functionDeclaration, moduleName);
                        break;
                    }
                    if (publicApi && !IsPublicApiFunction(functionDeclaration))
                    {
                        break;
                    }
                    if (functionDeclaration.IsGenericSpecialization)
                    {
                        break;
                    }
                    if (functionDeclaration.GenericTypeNames.Length > 0)
                    {
                        break;
                    }
                    if (IsClosedClassConceptMethodReturnBody(functionDeclaration) ||
                        IsClosedClassConceptVoidCallBody(functionDeclaration))
                    {
                        break;
                    }
                    if (functionDeclaration.OperatorToken is not null &&
                        functionDeclaration.ParentClassDeclaration is
                            { GenericTypeNames.Length: > 0, ClassType: ClassType.Struct })
                    {
                        break;
                    }
                    if (functionDeclaration.ParentClassDeclaration?.ClassType == ClassType.Interface &&
                        !functionDeclaration.IsExtension)
                    {
                        break;
                    }
                    exportable =
                        (
                            functionDeclaration.ParentClassDeclaration == null &&
                            functionDeclaration.MemberModifiers.Contains(MemberModifier.Public)
                        ) ||
                        IsCoreTaskStateAbiFunction(functionDeclaration) ||
                        (
                            functionDeclaration.ParentClassDeclaration?.ClassType == ClassType.Struct &&
                            functionDeclaration.MemberModifiers.Contains(MemberModifier.Public) &&
                            functionDeclaration.ParentClassDeclaration?.Visibility == Visibility.Public
                        ) ||
                        (
                            functionDeclaration.ParentClassDeclaration?.ClassType == ClassType.Class &&
                            functionDeclaration.MemberModifiers.Contains(MemberModifier.Public) &&
                            functionDeclaration.ParentClassDeclaration?.Visibility == Visibility.Public
                        ) ||
                        (
                            functionDeclaration.ParentClassDeclaration?.ClassType == ClassType.Interface &&
                            functionDeclaration.IsExtension &&
                            functionDeclaration.MemberModifiers.Contains(MemberModifier.Public) &&
                            functionDeclaration.ParentClassDeclaration.Visibility == Visibility.Public
                        ); // TODO: Traverse up all class hierarchy
                    var nameOverrideIndex = GetNameOverrideIndex(functionDeclaration, declarations);

                    writer.WriteIndent();
                    writer.Write($"extern {(exportable ? $"{GetModuleApiName(moduleName)} " : "")}{functionDeclaration.ToCIdentifier(moduleName, nameOverrideIndex)}(");
                    writer.IncreaseIndent();

                    isFirstParameter = true;
                    if (!functionDeclaration.IsStatic)
                    {
                        writer.WriteNewLine();
                        writer.WriteIndent();
                        var receiverConst = functionDeclaration.Const ? "const " : string.Empty;
                        var receiverModule = functionDeclaration.ParentClassDeclaration!.ProjectName ?? moduleName;
                        writer.Write(functionDeclaration.ExtensionTargetType is { } extensionTarget &&
                            IsPrimitiveExtensionReceiverType(extensionTarget)
                                ? $"{receiverConst}{ToCParameterType(extensionTarget)} __this"
                            : functionDeclaration.IsExtension &&
                            functionDeclaration.ParentClassDeclaration.ClassType == ClassType.Interface
                                ? $"{receiverConst}struct cx_iface_ref __this"
                                : $"{ToCReceiverType(functionDeclaration.ParentClassDeclaration, receiverModule, functionDeclaration.Const)} __this");
                        isFirstParameter = false;
                    }

                    var hasGenericReturn = IsGenericValueType(functionDeclaration.ReturnType) &&
                        !ReturnsGenericClassReference(functionDeclaration);
                    if (functionDeclaration.Parameters.Count > 0)
                    {
                        if (!isFirstParameter)
                        {
                            writer.Write(",");
                        }

                        writer.WriteNewLine();

                        for (int i = 0; i < functionDeclaration.Parameters.Count; i++)
                        {
                            FunctionParameter? parameter = functionDeclaration.Parameters[i];

                            writer.WriteIndent();
                            writer.Write($"{ToCParameterType(parameter.ParameterType)} {parameter.Name}");

                            if (i != functionDeclaration.Parameters.Count - 1 || hasGenericReturn)
                            {
                                writer.Write(",");
                            }

                            writer.WriteNewLine();
                        }
                    }

                    if (hasGenericReturn)
                    {
                        if (functionDeclaration.Parameters.Count == 0)
                        {
                            if (!isFirstParameter)
                            {
                                writer.Write(",");
                            }
                            writer.WriteNewLine();
                        }
                        writer.WriteIndent();
                        writer.Write("void* __returnValue");
                        writer.WriteNewLine();
                    }
                    else if (functionDeclaration.Parameters.Count == 0 && !isFirstParameter)
                    {
                        writer.WriteLine();
                    }

                    writer.DecreaseIndent();
                    writer.WriteLine(");");
                    break;

                case PropertyDeclaration propertyDeclaration:
                    if (publicApi &&
                        !IsPublicApiMember(propertyDeclaration, propertyDeclaration.ParentClassDeclaration))
                    {
                        break;
                    }
                    if (propertyDeclaration.ParentClassDeclaration.ClassType == ClassType.Interface &&
                        !propertyDeclaration.IsExtension)
                    {
                        break;
                    }
                    foreach (var propertyAccessorDeclaration in propertyDeclaration.PropertyAccessorDeclarations)
                    {
                        if (IsClosedGenericArrayPropertyGetter(
                            propertyAccessorDeclaration,
                            propertyDeclaration.ParentClassDeclaration,
                            propertyDeclaration.ParentClassDeclaration.MemberDeclarations
                                .Declarations.OfType<FieldDeclaration>().ToArray()))
                        {
                            continue;
                        }
                        exportable =
                        (
                            propertyDeclaration.ParentClassDeclaration.ClassType == ClassType.Interface &&
                            propertyDeclaration.IsExtension &&
                            propertyDeclaration.MemberModifiers.Contains(MemberModifier.Public) &&
                            propertyDeclaration.ParentClassDeclaration.Visibility == Visibility.Public
                        ) ||
                        (
                            propertyDeclaration.ParentClassDeclaration.ClassType == ClassType.Struct &&
                            propertyDeclaration.MemberModifiers.Contains(MemberModifier.Public) &&
                            propertyDeclaration.ParentClassDeclaration.Visibility == Visibility.Public
                        ) ||
                        (
                            propertyDeclaration.ParentClassDeclaration.ClassType == ClassType.Class &&
                            propertyDeclaration.MemberModifiers.Contains(MemberModifier.Public) &&
                            propertyDeclaration.ParentClassDeclaration.Visibility == Visibility.Public
                        ) || IsCoreTaskStateAbiProperty(propertyDeclaration);

                        writer.WriteIndent();
                        writer.Write($"extern {(exportable ? $"{GetModuleApiName(moduleName)} " : "")}{propertyAccessorDeclaration.ToCIdentifier(moduleName)}(");
                        writer.IncreaseIndent();

                        isFirstParameter = true;
                        if (!propertyDeclaration.IsStatic)
                        {
                            writer.WriteNewLine();
                            writer.WriteIndent();
                            var receiverConst = propertyAccessorDeclaration.Const ? "const " : string.Empty;
                            var receiverModule = propertyDeclaration.ParentClassDeclaration.ProjectName ?? moduleName;
                            writer.Write(propertyDeclaration.ExtensionTargetType is { } extensionTarget &&
                                IsPrimitiveExtensionReceiverType(extensionTarget)
                                    ? $"{receiverConst}{ToCParameterType(extensionTarget)} __this"
                                : propertyDeclaration.IsExtension &&
                                propertyDeclaration.ParentClassDeclaration.ClassType == ClassType.Interface
                                    ? $"{receiverConst}struct cx_iface_ref __this"
                                    : $"{ToCReceiverType(propertyDeclaration.ParentClassDeclaration, receiverModule, propertyAccessorDeclaration.Const)} __this");
                            isFirstParameter = false;
                        }

                        if (propertyAccessorDeclaration.Parameters.Count > 0)
                        {
                            if (!isFirstParameter)
                            {
                                writer.Write(",");
                            }

                            writer.WriteNewLine();

                            for (int i = 0; i < propertyAccessorDeclaration.Parameters.Count; i++)
                            {
                                FunctionParameter? parameter = propertyAccessorDeclaration.Parameters[i];
                                isFirstParameter = false;

                                writer.WriteIndent();
                                writer.Write($"{parameter.ParameterType.ToCIdentifier(@const: false)} {parameter.Name}");

                                if (i != propertyAccessorDeclaration.Parameters.Count - 1)
                                {
                                    writer.Write(",");
                                }

                                writer.WriteNewLine();
                            }
                        }

                        var hasGenericValue = IsGenericValueType(propertyDeclaration.Type);
                        if (propertyAccessorDeclaration.Name == "set")
                        {
                            if (!isFirstParameter)
                            {
                                writer.Write(",");
                            }

                            writer.WriteNewLine();
                            writer.WriteIndent();
                            writer.Write(hasGenericValue
                                ? "void* value"
                                : $"{propertyDeclaration.Type.ToCIdentifier(false)} value");
                            isFirstParameter = false;
                        }
                        else if (hasGenericValue &&
                            !ReturnsGenericClassPropertyReference(propertyAccessorDeclaration))
                        {
                            if (!isFirstParameter)
                            {
                                writer.Write(",");
                            }

                            writer.WriteNewLine();

                            writer.WriteIndent();
                            writer.Write("void* __returnValue");
                            isFirstParameter = false;
                        }

                        if (!isFirstParameter)
                        {
                            writer.WriteLine();
                        }

                        writer.DecreaseIndent();
                        writer.WriteLine(");");
                    }

                    break;
            }
        }
    }

    private static void WriteGenericFunctionDeclarations(
        IndentingWriter writer,
        IEnumerable<CxCompiler.Semantics.FunctionSymbol> instances)
    {
        foreach (var instance in instances.OrderBy(item => item.SpecializationName, StringComparer.Ordinal))
        {
            var name = instance.SpecializationName!;
            var declaration = instance.Declaration!;
            var parameters = GetGenericFunctionParameters(
                instance, instance.ImplementationDeclaration ?? declaration);
            var export = declaration.MemberModifiers.Contains(MemberModifier.Public)
                ? $"{GetModuleApiName(instance.ModuleName)} "
                : string.Empty;
            writer.WriteLine($"extern {export}{instance.ReturnType.ToCIdentifier(false)} " +
                $"{name}({string.Join(", ", parameters)});");
        }
    }

    private static IReadOnlyList<string> GetGenericFunctionParameters(
        CxCompiler.Semantics.FunctionSymbol instance,
        FunctionDeclaration? declarationOverride = null)
    {
        var declaration = declarationOverride ?? instance.Declaration!;
        var parameters = new List<string>();
        if (declaration.LambdaOwner is not null)
        {
            parameters.Add("void *__cx_environment");
        }
        else if (!declaration.IsStatic)
        {
            var receiverConst = declaration.Const ? "const " : string.Empty;
            var containingTypeModule = declaration.ParentClassDeclaration!.ProjectName ??
                instance.ModuleName;
            var receiverType = instance.ClosedContainingType is
            { ConstructedIdentity: not null } closedType &&
                RequiresClosedValueLayout(closedType)
                    ? $"struct {closedType.ConstructedIdentity.CIdentifier}"
                    : declaration.ParentClassDeclaration.ToCIdentifier(containingTypeModule);
            var receiverParameter = declaration.IsExtension &&
                declaration.ParentClassDeclaration.ClassType == ClassType.Interface
                    ? $"{receiverConst}struct cx_iface_ref __this"
                    : declaration.IsExtension &&
                    declaration.ExtensionTargetType is { } targetType &&
                    IsPrimitiveExtensionReceiverType(targetType)
                        ? $"{receiverConst}{ToCParameterType(targetType)} __this"
                        : $"{receiverConst}{receiverType}* __this";
            parameters.Add(receiverParameter);
        }
        parameters.AddRange(declaration.Parameters.Zip(instance.ParameterTypes)
            .Select(pair => $"{pair.Second.ToCIdentifier(false)} {pair.First.Name}"));
        return parameters;
    }

    private static void WriteProjectSourceFile(
        CxProject project,
        string outputFilePath,
        IReadOnlyCollection<DeclarationBase> declarations,
        FunctionDeclaration? entryPoint)
    {
        var genericTypeInstances = GetGenericTypeInstancesWithClosedInterfaces(
            project.GenericTypeInstances);
        var genericFunctionInstances = project.GenericFunctionInstances;
        using var fileWriter = OpenGeneratedFile(outputFilePath);
        var writer = new IndentingWriter(fileWriter);

        writer.WriteLine($"// This is an autogenerated C source file for project '{project.Name}'");
        writer.WriteLine();
        writer.WriteLine($"#include \"{project.Name}.internal.h\"");
        writer.WriteLine("#include \"cxruntime.h\"");
        writer.WriteLine("#include <stdlib.h>");
        writer.WriteLine("#include <string.h>");
        writer.WriteLine("#if !defined(CX_RUNTIME_ABI_VERSION) || CX_RUNTIME_ABI_VERSION != 1");
        writer.WriteLine("#error This generated code requires cxcore runtime ABI version 1.");
        writer.WriteLine("#endif");
        writer.WriteLine("#if defined(_WIN32)");
        writer.WriteLine("#define NOMINMAX");
        writer.WriteLine("#define WIN32_LEAN_AND_MEAN");
        writer.WriteLine("#include <windows.h>");
        writer.WriteLine("#endif");
        writer.WriteLine();

        writer.WriteLine("//");
        writer.WriteLine("// Strings");
        writer.WriteLine("//");
        writer.WriteLine();
        WriteStrings(writer, declarations, project.Name);
        writer.WriteLine();

        writer.WriteLine("//");
        writer.WriteLine("// Static fields");
        writer.WriteLine("//");
        writer.WriteLine();
        WriteStaticFields(writer, declarations, project.Name);
        WriteStaticRootRegistration(writer, declarations, project.Name);
        writer.WriteLine();

        writer.WriteLine("//");
        writer.WriteLine("// Interface dispatch thunks");
        writer.WriteLine("//");
        writer.WriteLine();
        WriteInterfaceDispatchThunks(writer, declarations, project.Name);
        writer.WriteLine();

        writer.WriteLine("//");
        writer.WriteLine("// TypeInfos");
        writer.WriteLine("//");
        WriteTypeInfos(writer, declarations, project.Name);
        WriteClosedGenericTypeInfos(
            writer,
            genericTypeInstances,
            genericFunctionInstances,
            project.Name);
        var genericVirtualEntries = GetGenericVirtualDispatchEntries(project);
        if (genericVirtualEntries.Count != 0)
        {
            writer.WriteLine("#define CX_MODULE_HAS_GENERIC_VIRTUAL_REGISTRATIONS 1");
        }
        WriteGenericVirtualDispatchRegistrations(
            writer, genericVirtualEntries, project.Name);
        WriteModuleInitializationFunction(
            writer, project, genericTypeInstances, genericVirtualEntries);
        writer.WriteLine();

        writer.WriteLine("//");
        writer.WriteLine("// Functions");
        writer.WriteLine("//");
        writer.WriteLine();
        WriteFunctionDefinitions(writer, declarations, project.Name);
        WriteGenericFunctionDefinitions(writer, genericFunctionInstances, project.Name);
        WriteNativeEntryPoint(writer, project, entryPoint);

        fileWriter.Flush();
        fileWriter.Close();
    }

    private static FunctionDeclaration? FindNativeEntryPoint(
        CxProject project,
        IReadOnlyCollection<DeclarationBase> declarations)
    {
        if (project.Type != CxProjectType.Executable)
            return null;

        var mainFunctions = declarations.OfType<FunctionDeclaration>()
            .Where(function => function.Name == "Main" && function.ParentClassDeclaration is null)
            .ToArray();
        if (mainFunctions.Length == 0)
            return null;
        if (mainFunctions.Length != 1)
            throw new CompilationErrorException(
                "An executable project must declare exactly one top-level Main function.");

        var main = mainFunctions[0];
        var takesArguments = main.Parameters.Count == 1 &&
            main.Parameters[0].ParameterType is ArrayType { ElementType: StringType };
        if (main.ParentClassDeclaration is not null || main.GenericTypeNames.Length != 0 ||
            main.Body is null || main.ReturnType is not (VoidType or IntType) ||
            (main.Parameters.Count != 0 && !takesArguments))
        {
            throw new CompilationErrorException(
                "Executable entry point 'Main' must be a non-generic top-level function returning void or int, with no parameters or one string[] parameter.");
        }
        return main;
    }

    private static void WriteNativeEntryPoint(
        IndentingWriter writer,
        CxProject project,
        FunctionDeclaration? entryPoint)
    {
        if (entryPoint is null)
            return;
        var main = entryPoint;
        var takesArguments = main.Parameters.Count == 1;
        var functionName = new QualifiedIdentifier(project.Name, main.FullName).ToCIdentifier();
        var moduleToken = GetModuleToken(project.Name);
        writer.WriteLine();
        writer.WriteLine("// Native executable entry point");
        WriteSourceLocation(writer, main.SourceSpan);
        if (takesArguments)
            WriteArgumentStringHelper(writer);
        writer.WriteLine("#if defined(_WIN32)");
        writer.WriteLine(takesArguments ? "int wmain(int argc, wchar_t **argv)" : "int wmain(void)");
        writer.WriteLine("#else");
        writer.WriteLine(takesArguments ? "int main(int argc, char **argv)" : "int main(void)");
        writer.WriteLine("#endif");
        writer.WriteLine("{");
        writer.IncreaseIndent();
        writer.WriteLine($"__cx_register_static_roots_{moduleToken}();");
        writer.WriteLine("cx_runtime_require_abi(CX_RUNTIME_ABI_VERSION);");
        writer.WriteLine($"__cx_module_init_{moduleToken}();");

        if (takesArguments)
        {
            writer.WriteLine("cx_uint __cx_argument_count = argc > 1 ? (cx_uint)(argc - 1) : 0;");
            writer.WriteLine("struct cxcore_System_Collections_Array *__cx_arguments = " +
                "cx_array_with_type(cx_array_new(__cx_argument_count, (cx_uint)sizeof(struct cxcore_System_String*)), " +
                ArrayTypeKey(BuiltInSystemTypes.String) + ");");
            writer.WriteLine("for (int __cx_index = 1; __cx_index < argc; ++__cx_index)");
            writer.WriteLine("{");
            writer.IncreaseIndent();
            writer.WriteLine("struct cxcore_System_String *__cx_argument = " +
                "__cx_string_from_main_argument(argv[__cx_index]);");
            writer.WriteLine("*(struct cxcore_System_String**)cx_array_at(" +
                "__cx_arguments, (cx_uint)(__cx_index - 1), " +
                "(cx_uint)sizeof(struct cxcore_System_String*)) = __cx_argument;");
            writer.DecreaseIndent();
            writer.WriteLine("}");
        }

        var call = takesArguments ? $"{functionName}(__cx_arguments)" : $"{functionName}()";
        if (main.ReturnType is VoidType)
        {
            writer.WriteLine($"{call};");
            writer.WriteLine("cxcore_System_Threading_Tasks_TaskScheduler_Shutdown();");
            writer.WriteLine("cx_thread_wait_all();");
            writer.WriteLine("return 0;");
        }
        else
        {
            writer.WriteLine($"int __cx_exit_code = (int){call};");
            writer.WriteLine("cxcore_System_Threading_Tasks_TaskScheduler_Shutdown();");
            writer.WriteLine("cx_thread_wait_all();");
            writer.WriteLine("return __cx_exit_code;");
        }
        writer.DecreaseIndent();
        writer.WriteLine("}");
    }

    private static void WriteArgumentStringHelper(IndentingWriter writer)
    {
        writer.WriteLine("#if defined(_WIN32)");
        writer.WriteLine("static struct cxcore_System_String* " +
            "__cx_string_from_main_argument(const wchar_t *value)");
        writer.WriteLine("{");
        writer.IncreaseIndent();
        writer.WriteLine("int byte_count = WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, value, -1, " +
            "NULL, 0, NULL, NULL);");
        writer.WriteLine("if (byte_count <= 0) exit(EXIT_FAILURE);");
        writer.WriteLine("char *utf8 = (char*)cxcore_System_Memory_Alloc((cx_uint)byte_count);");
        writer.WriteLine("if (WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, value, -1, " +
            "utf8, byte_count, NULL, NULL) <= 0) exit(EXIT_FAILURE);");
        writer.WriteLine("struct cxcore_System_String *result = " +
            "(struct cxcore_System_String*)cxcore_System_Memory_Alloc(" +
            "(cx_uint)sizeof(struct cxcore_System_String));");
        writer.WriteLine("cxcore_System_String___constructor__2(" +
            "result, (cx_uint)(byte_count - 1), utf8);");
        writer.WriteLine("return result;");
        writer.DecreaseIndent();
        writer.WriteLine("}");
        writer.WriteLine("#else");
        writer.WriteLine("static struct cxcore_System_String* " +
            "__cx_string_from_main_argument(const char *value)");
        writer.WriteLine("{");
        writer.IncreaseIndent();
        writer.WriteLine("size_t byte_count = strlen(value);");
        writer.WriteLine("char *utf8 = byte_count == 0 ? NULL : (char*)cxcore_System_Memory_Alloc((cx_uint)byte_count);");
        writer.WriteLine("if (byte_count != 0) memcpy(utf8, value, byte_count);");
        writer.WriteLine("struct cxcore_System_String *result = " +
            "(struct cxcore_System_String*)cxcore_System_Memory_Alloc(" +
            "(cx_uint)sizeof(struct cxcore_System_String));");
        writer.WriteLine("cxcore_System_String___constructor__2(" +
            "result, (cx_uint)byte_count, utf8);");
        writer.WriteLine("return result;");
        writer.DecreaseIndent();
        writer.WriteLine("}");
        writer.WriteLine("#endif");
    }

    private static void WriteStrings(
        IndentingWriter writer,
        IReadOnlyCollection<DeclarationBase> declarations,
        string moduleName)
    {
        List<QualifiedIdentifier> namespaces = [];

        foreach (var declaration in EnumerateClasses(declarations))
        {
            var nameIdentifier = new QualifiedIdentifier("__name", declaration.FullName);
            writer.WriteLine($"CX_STRING_DEF({nameIdentifier.ToCIdentifier()}, \"{declaration.Name}\");");

            var @namespace = declaration.Namespace;
            if (!namespaces.Contains(@namespace))
            {
                namespaces.Add(@namespace);
            }
        }

        foreach (var declaration in EnumerateEnums(declarations))
        {
            var nameIdentifier = new QualifiedIdentifier("__name", declaration.FullName);
            writer.WriteLine($"CX_STRING_DEF({nameIdentifier.ToCIdentifier()}, \"{declaration.Name}\");");
            if (!namespaces.Contains(declaration.Namespace))
            {
                namespaces.Add(declaration.Namespace);
            }
        }

        foreach (var @namespace in namespaces)
        {
            var namespaceIdentifier = new QualifiedIdentifier("__namespace", @namespace);
            writer.WriteLine($"CX_STRING_DEF({namespaceIdentifier.ToCIdentifier()}, \"{@namespace}\");");
        }

        var functionLiterals = EnumerateFunctions(declarations)
            .Where(function => function.Body is not null)
            .SelectMany(function => function.Body!)
            .SelectMany(EnumerateStringLiterals)
            .Concat(EnumerateLocalFunctions(declarations)
                .Where(function => function.Body is not null)
                .SelectMany(function => function.Body!)
                .SelectMany(EnumerateStringLiterals));
        var fieldLiterals = EnumerateFields(declarations)
            .Where(field => field.Initializer is LiteralExpression { IsString: true })
            .Select(field => (LiteralExpression)field.Initializer!);
        foreach (var literal in functionLiterals
            .Concat(fieldLiterals)
            .DistinctBy(literal => literal.SourceText))
        {
            writer.WriteLine(
                $"CX_STRING_DEF({GetStringIdentifier(literal, moduleName).ToCIdentifier()}, {literal.SourceText});");
        }
    }

    private sealed record GenericVirtualDispatchEntry(
        NamedType RuntimeType,
        FunctionDeclaration Contract,
        IReadOnlyList<TypeBase> MethodTypeArguments,
        FunctionSymbol Implementation,
        string MethodIdentity,
        string ImplementationIdentity,
        string RegistrationIdentifier,
        string ThunkIdentifier,
        string TypeArgumentsIdentifier);

    private static IReadOnlyCollection<GenericVirtualDispatchEntry>
        GetGenericVirtualDispatchEntries(CxProject project)
    {
        var entries = new Dictionary<string, GenericVirtualDispatchEntry>(StringComparer.Ordinal);
        void AddEntries(
            FunctionDeclaration contract,
            IReadOnlyCollection<GenericVirtualDispatchTarget> dispatchTargets)
        {
            contract = contract.VirtualContract ?? contract;
            foreach (var dispatchTarget in dispatchTargets)
            {
                var implementation = dispatchTarget.Implementation;
                var implementationMethodNames = implementation.Declaration?.GenericTypeNames ?? [];
                var methodTypeArguments = implementationMethodNames
                    .Select(name => implementation.GenericTypeArguments.TryGetValue(
                        name, out var type) ? type : null)
                    .ToArray();
                if (methodTypeArguments.Any(type => type is null))
                {
                    throw new InternalCompilerException(
                        $"Generic virtual target '{implementation.FullName}' has open method arguments.");
                }
                var closedMethodTypeArguments = methodTypeArguments
                    .Cast<TypeBase>().ToArray();
                var methodIdentity = GetGenericVirtualMethodIdentity(contract);
                var implementationIdentity = GetGenericVirtualImplementationIdentity(
                    implementation, closedMethodTypeArguments);
                var runtimeTypeIdentity = dispatchTarget.RuntimeType.ConstructedIdentity?.CanonicalName ??
                    dispatchTarget.RuntimeType.ResolvedTypeFullName.ToString();
                var argumentsIdentity = GenericTypeIdentity.CreateFunction(
                    "cx-virtual-arguments", "method", closedMethodTypeArguments, [],
                    BuiltInSystemTypes.Void).CanonicalName;
                var key = $"{runtimeTypeIdentity}|{methodIdentity}|{argumentsIdentity}";
                var identifierHash = Convert.ToHexString(SHA256.HashData(
                    Encoding.UTF8.GetBytes(key)).AsSpan(0, 8)).ToLowerInvariant();
                var entry = new GenericVirtualDispatchEntry(
                    dispatchTarget.RuntimeType,
                    contract,
                    closedMethodTypeArguments,
                    implementation,
                    methodIdentity,
                    implementationIdentity,
                    $"__cx_generic_virtual_registration_{identifierHash}",
                    $"__cx_generic_virtual_thunk_{identifierHash}",
                    $"__cx_generic_virtual_types_{identifierHash}");
                if (entries.TryGetValue(key, out var existing))
                {
                    if (existing.ImplementationIdentity != implementationIdentity)
                    {
                        throw new InternalCompilerException(
                            $"Conflicting generic virtual implementations for '{methodIdentity}'.");
                    }
                    continue;
                }
                entries.Add(key, entry);
            }
        }
        foreach (var context in project.CompilationContexts)
        {
            foreach (var function in EnumerateFunctions(context.DeclarationScope.Declarations))
            {
                foreach (var expression in EnumerateBodyExpressions(function.Body ?? []))
                {
                    if (expression is InvocationExpression invocation &&
                        invocation.TargetSymbol?.Declaration is
                        {
                            GenericTypeNames.Length: > 0,
                            VirtualSlotIndex: not null,
                            ParentClassDeclaration.ClassType: ClassType.Class or ClassType.Interface,
                        } contract)
                    {
                        AddEntries(contract, invocation.GenericVirtualDispatchTargets);
                    }
                    else if (expression is MemberAccessExpression
                        { FunctionValueSymbol: { Declaration: { } method } } methodValue &&
                        method.GenericTypeNames.Length > 0 && method.VirtualSlotIndex is not null &&
                        method.ParentClassDeclaration?.ClassType is
                            ClassType.Class or ClassType.Interface)
                    {
                        AddEntries(method.VirtualContract ?? method,
                            methodValue.GenericVirtualDispatchTargets);
                    }
                }
            }
        }
        return entries.Values.OrderBy(entry => entry.RegistrationIdentifier, StringComparer.Ordinal)
            .ToArray();
    }

    private static string GetGenericVirtualMethodIdentity(FunctionDeclaration contract)
    {
        var ownerName = contract.ParentClassDeclaration?.FullName.ToString() ??
            contract.Namespace + "." + contract.Name;
        var parameters = string.Join(",", contract.Parameters.Select(parameter =>
            GetGenericVirtualSignatureType(parameter.ParameterType)));
        return $"{ownerName}.{contract.Name}`{contract.GenericTypeNames.Length}" +
            $"({parameters})->{GetGenericVirtualSignatureType(contract.ReturnType)}";
    }

    private static string GetGenericVirtualSignatureType(TypeBase type) => type switch
    {
        ConstType constant => $"const {GetGenericVirtualSignatureType(constant.UnderlyingType)}",
        GenericType generic => $"!{generic.Name}",
        ArrayType array => $"Array<{GetGenericVirtualSignatureType(array.ElementType)}>",
        NullableType nullable =>
            $"Nullable<{GetGenericVirtualSignatureType(nullable.UnderlyingType)}>",
        FunctionType function =>
            $"fn({string.Join(",", function.ParameterTypes.Select(GetGenericVirtualSignatureType))})" +
            $"->{GetGenericVirtualSignatureType(function.ReturnType)}",
        NamedType named when named.TypeArguments.Count > 0 =>
            $"{named.ResolvedTypeFullName}<{string.Join(",", named.TypeArguments.Select(GetGenericVirtualSignatureType))}>",
        NamedType named => named.ResolvedTypeFullName.ToString(),
        _ => type.FullName.ToString(),
    };

    private static string GetGenericVirtualImplementationIdentity(
        FunctionSymbol implementation,
        IReadOnlyList<TypeBase> methodTypeArguments)
    {
        var declaration = implementation.ImplementationDeclaration ??
            implementation.Declaration ?? throw new InternalCompilerException(
                "A generic virtual implementation has no declaration.");
        var isReplacement = implementation.ImplementationDeclaration is not null &&
            !ReferenceEquals(implementation.ImplementationDeclaration, implementation.Declaration);
        var moduleName = declaration.ParentClassDeclaration?.ProjectName ??
            implementation.ModuleName;
        var identityName = declaration.FullName.ToString() +
            (isReplacement ? "#replacement" : "#primary");
        return GenericTypeIdentity.CreateFunction(
            moduleName,
            identityName,
            methodTypeArguments,
            implementation.ParameterTypes,
            implementation.ReturnType).CanonicalName;
    }

    private static void WriteGenericVirtualDispatchRegistrations(
        IndentingWriter writer,
        IReadOnlyCollection<GenericVirtualDispatchEntry> entries,
        string moduleName)
    {
        foreach (var entry in entries)
        {
            writer.WriteLine(
                $"static cx_ptr {entry.TypeArgumentsIdentifier}[{entry.MethodTypeArguments.Count}];");
            var implementationDeclaration = entry.Implementation.ImplementationDeclaration ??
                entry.Implementation.Declaration ?? throw new InternalCompilerException(
                    "A generic virtual implementation has no declaration.");
            var implementationOwner = implementationDeclaration.ParentClassDeclaration ??
                throw new InternalCompilerException(
                    "A generic virtual implementation has no declaring class.");
            var implementationModule = implementationOwner.ProjectName ?? moduleName;
            var implementationReceiverType =
                $"{(implementationDeclaration.Const ? "const " : string.Empty)}" +
                $"{implementationOwner.ToCIdentifier(implementationModule)}*";
            var parameterNames = implementationDeclaration.Parameters
                .Select(parameter => parameter.Name).ToArray();
            var parameters = new[] { "cx_ptr __cx_receiver" }
                .Concat(entry.Implementation.ParameterTypes.Zip(parameterNames)
                    .Select(pair => $"{pair.First.ToCIdentifier(false)} {pair.Second}"))
                .ToArray();
            writer.WriteLine(
                $"static {entry.Implementation.ReturnType.ToCReturnType(false)} " +
                $"{entry.ThunkIdentifier}({string.Join(", ", parameters)}) {{");
            writer.IncreaseIndent();
            var receiver = $"({implementationReceiverType})__cx_receiver";
            var callArguments = new[] { receiver }.Concat(parameterNames);
            var call = $"{entry.Implementation.SpecializationName ?? throw new InternalCompilerException("A generic virtual implementation has no closed symbol.")}({string.Join(", ", callArguments)})";
            writer.WriteLine(entry.Implementation.ReturnType is VoidType
                ? $"{call};"
                : $"return {call};");
            writer.DecreaseIndent();
            writer.WriteLine("}");
            writer.WriteLine(
                $"static struct cx_generic_virtual_registration {entry.RegistrationIdentifier};");
        }
        if (entries.Count > 0)
        {
            writer.WriteLine();
        }
    }

    private static string EscapeCString(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);

    private static void WriteModuleInitializationFunction(
        IndentingWriter writer,
        CxProject project,
        IReadOnlyCollection<(NamedType Type, ClassDeclaration Declaration)> genericTypeInstances,
        IReadOnlyCollection<GenericVirtualDispatchEntry> genericVirtualEntries)
    {
        var token = GetModuleToken(project.Name);
        writer.WriteLine($"static struct cx_runtime_once __cx_module_once_{token};");
        writer.WriteLine($"void {GetModuleApiName(project.Name)} __cx_module_init_{token}(void)");
        writer.WriteLine("{");
        writer.IncreaseIndent();
        writer.WriteLine($"__cx_register_static_roots_{token}();");
        writer.WriteLine("cx_runtime_require_abi(CX_RUNTIME_ABI_VERSION);");
        writer.WriteLine($"if (!cx_runtime_once_begin(&__cx_module_once_{token})) return;");
        writer.WriteLine("struct cx_exception_frame __cx_module_exception_frame;");
        writer.WriteLine("cx_exception_push(&__cx_module_exception_frame);");
        writer.WriteLine("if (setjmp(__cx_module_exception_frame.environment) == 0)");
        writer.WriteLine("{");
        writer.IncreaseIndent();
        foreach (var dependencyName in project.ResolvedProjectNames)
        {
            writer.WriteLine($"__cx_module_init_{GetModuleToken(dependencyName)}();");
        }
        var declarations = GetDeclarations(project);
        writer.WriteLine("#if !defined(CX_STATIC_LINK)");
        foreach (var stringIdentifier in GetProjectStringIdentifiers(declarations, project.Name))
        {
            writer.WriteLine($"{stringIdentifier}.__base.__vtable = " +
                "cxcore_System_String___vtable;");
        }
        foreach (var classDeclaration in EnumerateClasses(declarations))
        {
            WriteDynamicTypeInfoInitialization(writer, classDeclaration, project.Name);
            WriteDynamicReflectionMetadataInitialization(writer, classDeclaration, project.Name);
        }
        foreach (var instance in genericTypeInstances)
        {
            var identity = instance.Type.ConstructedIdentity!;
            foreach (var stringIdentifier in new[]
            {
                $"{identity.CIdentifier}___name",
                $"{identity.CIdentifier}___namespace",
            })
            {
                writer.WriteLine($"{stringIdentifier}.__base.__vtable = " +
                    "cxcore_System_String___vtable;");
            }
            WriteDynamicClosedGenericMetadataInitialization(
                writer, instance.Type, instance.Declaration, project.Name);
        }
        writer.WriteLine("#endif");
        foreach (var entry in genericVirtualEntries)
        {
            for (var index = 0; index < entry.MethodTypeArguments.Count; index++)
            {
                writer.WriteLine(
                    $"{entry.TypeArgumentsIdentifier}[{index}] = " +
                    $"{ToTypeInfoPointer(entry.MethodTypeArguments[index])};");
            }
            writer.WriteLine(
                $"{entry.RegistrationIdentifier}.receiverTypeInfo = " +
                $"{ToTypeInfoPointer(entry.RuntimeType)};");
            writer.WriteLine(
                $"{entry.RegistrationIdentifier}.methodIdentity = " +
                $"\"{EscapeCString(entry.MethodIdentity)}\";");
            writer.WriteLine(
                $"{entry.RegistrationIdentifier}.methodTypeArguments = " +
                $"{entry.TypeArgumentsIdentifier};");
            writer.WriteLine(
                $"{entry.RegistrationIdentifier}.methodTypeArgumentCount = " +
                $"{entry.MethodTypeArguments.Count}u;");
            writer.WriteLine(
                $"{entry.RegistrationIdentifier}.implementationIdentity = " +
                $"\"{EscapeCString(GetGenericVirtualImplementationIdentity(entry.Implementation, entry.MethodTypeArguments))}\";");
            writer.WriteLine(
                $"{entry.RegistrationIdentifier}.implementation = " +
                $"(cx_vtable_function){entry.ThunkIdentifier};");
            writer.WriteLine(
                $"{entry.RegistrationIdentifier}.next = CX_NULL;");
            writer.WriteLine(
                $"cx_register_generic_virtual_method(&{entry.RegistrationIdentifier});");
        }
        WriteDynamicStaticFieldInitializers(writer, declarations, project.Name);
        foreach (var constructor in EnumerateClasses(declarations)
            .SelectMany(type => type.MemberDeclarations.Declarations
                .OfType<ConstructorDeclaration>())
            .Where(constructor => constructor.IsStatic))
        {
            writer.WriteLine($"{ToCFunctionName(constructor, project.Name)}();");
        }
        writer.WriteLine("cx_exception_pop(&__cx_module_exception_frame);");
        writer.WriteLine($"cx_runtime_once_complete(&__cx_module_once_{token});");
        writer.DecreaseIndent();
        writer.WriteLine("}");
        writer.WriteLine("else");
        writer.WriteLine("{");
        writer.IncreaseIndent();
        writer.WriteLine("cx_exception_pop(&__cx_module_exception_frame);");
        writer.WriteLine($"cx_runtime_once_fail(&__cx_module_once_{token}, cx_exception_current());");
        writer.WriteLine("CX_RETHROW();");
        writer.DecreaseIndent();
        writer.WriteLine("}");
        writer.DecreaseIndent();
        writer.WriteLine("}");
    }

    private static IEnumerable<string> GetProjectStringIdentifiers(
        IReadOnlyCollection<DeclarationBase> declarations,
        string moduleName)
    {
        var identifiers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var declaration in EnumerateClasses(declarations))
        {
            identifiers.Add(new QualifiedIdentifier("__name", declaration.FullName).ToCIdentifier());
        }
        foreach (var declaration in EnumerateEnums(declarations))
        {
            identifiers.Add(new QualifiedIdentifier("__name", declaration.FullName).ToCIdentifier());
        }
        foreach (var @namespace in EnumerateClasses(declarations).Select(item => item.Namespace)
            .Concat(EnumerateEnums(declarations).Select(item => item.Namespace)).Distinct())
        {
            identifiers.Add(new QualifiedIdentifier("__namespace", @namespace).ToCIdentifier());
        }
        var functionLiterals = EnumerateFunctions(declarations)
            .Where(function => function.Body is not null)
            .SelectMany(function => function.Body!)
            .SelectMany(EnumerateStringLiterals);
        var fieldLiterals = EnumerateFields(declarations)
            .Where(field => field.Initializer is LiteralExpression { IsString: true })
            .Select(field => (LiteralExpression)field.Initializer!);
        foreach (var literal in functionLiterals.Concat(fieldLiterals)
            .DistinctBy(literal => literal.SourceText))
        {
            identifiers.Add(GetStringIdentifier(literal, moduleName).ToCIdentifier());
        }
        return identifiers.Order(StringComparer.Ordinal);
    }

    private static void WriteDynamicReflectionMetadataInitialization(
        IndentingWriter writer,
        ClassDeclaration declaration,
        string moduleName)
    {
        var fields = GetReflectedFields(declaration);
        var fieldArray = GetReflectionFieldsIdentifier(declaration, moduleName).ToCIdentifier();
        for (var index = 0; index < fields.Length; index++)
        {
            var field = fields[index];
            var offset = field.IsStatic
                ? "CX_REFLECTION_NO_OFFSET"
                : $"(cx_uint)offsetof({declaration.ToCIdentifier(moduleName)}, {field.Name})";
            writer.WriteLine($"{fieldArray}[{index}] = (struct cx_reflection_field){{ " +
                $"{GetMemberFlags(field.MemberModifiers)}, {offset}, " +
                $"{ToTypeInfoPointer(field.Type)}, \"{field.Name}\" }};");
        }

        var functions = GetReflectionFunctions(declaration).ToArray();
        for (var index = 0; index < functions.Length; index++)
        {
            var function = functions[index];
            var parameterArray = GetReflectionParametersIdentifier(
                declaration, moduleName, index).ToCIdentifier();
            for (var parameterIndex = 0; parameterIndex < function.Parameters.Count; parameterIndex++)
            {
                var parameter = function.Parameters[parameterIndex];
                writer.WriteLine($"{parameterArray}[{parameterIndex}] = " +
                    $"(struct cx_reflection_parameter){{ 0, " +
                    $"{ToTypeInfoPointer(parameter.ParameterType)}, " +
                    $"\"{parameter.Name}\", CX_NULL }};");
            }
            var parameters = function.Parameters.Count == 0
                ? "CX_NULL"
                : parameterArray;
            var slot = function.VirtualSlotIndex is { } virtualSlot
                ? virtualSlot.ToString()
                : "CX_REFLECTION_NO_SLOT";
            var functionArray = GetReflectionFunctionsIdentifier(
                declaration, moduleName).ToCIdentifier();
            writer.WriteLine($"{functionArray}[{index}] = (struct cx_reflection_function){{ " +
                $"{GetFunctionFlags(function)}, {slot}, " +
                $"{ToTypeInfoPointer(function.ReturnType)}, \"{function.Name}\", " +
                $"{parameters}, {function.Parameters.Count} }};");
        }

        var interfaces = declaration.InterfaceDispatchTables
            .Where(table => !HasOpenGenericInterfaceSignature(
                declaration, table.Interface))
            .ToArray();
        var interfaceArray = GetInterfaceRuntimeMapIdentifier(
            declaration, moduleName).ToCIdentifier();
        for (var index = 0; index < interfaces.Length; index++)
        {
            var table = interfaces[index];
            var interfaceDeclaration = table.Interface;
            var interfaceTypeInfo = GetInterfaceTypeInfoPointer(table, moduleName);
            var vtable = declaration.ClassType == ClassType.Class ||
                declaration.ClassType == ClassType.Struct &&
                declaration.GenericTypeNames.Length == 0
                ? GetInterfaceVTableIdentifier(
                    declaration, interfaceDeclaration, moduleName).ToCIdentifier()
                : "CX_NULL";
            writer.WriteLine($"{interfaceArray}[{index}] = (struct cx_interface_impl){{ " +
                $"{interfaceTypeInfo}, {vtable} }};");
        }
    }

    private static void WriteDynamicTypeInfoInitialization(
        IndentingWriter writer,
        ClassDeclaration declaration,
        string moduleName)
    {
        if (declaration.ClassType != ClassType.Class || declaration.IsStatic ||
            (moduleName == "cxcore" && declaration.Name == "Object"))
        {
            return;
        }
        var baseTypeInfo = declaration.BaseClassType is NamedType named
            ? new QualifiedIdentifier(named.ResolvedTypeFullName, "__typeinfo").ToCIdentifier()
            : "cxcore_System_Object___typeinfo";
        var typeInfo = new QualifiedIdentifier(
            moduleName, declaration.FullName, "__typeinfo").ToCIdentifier();
        WriteDynamicReadonlyBaseTypeInitialization(writer, typeInfo, baseTypeInfo);
    }

    private static void WriteDynamicReadonlyBaseTypeInitialization(
        IndentingWriter writer,
        string typeInfo,
        string baseTypeInfo)
    {
        writer.WriteLine("{");
        writer.IncreaseIndent();
        writer.WriteLine($"cx_ptr __cx_base_type_info = (cx_ptr)&{baseTypeInfo};");
        writer.WriteLine(
            $"memcpy((void *)&{typeInfo}.BaseType._obj, &__cx_base_type_info, " +
            "sizeof(__cx_base_type_info));");
        writer.DecreaseIndent();
        writer.WriteLine("}");
    }

    private static void WriteDynamicClosedGenericMetadataInitialization(
        IndentingWriter writer,
        NamedType type,
        ClassDeclaration declaration,
        string moduleName)
    {
        var identity = type.ConstructedIdentity!;
        var containingTypeModule = declaration.ProjectName ?? moduleName;
        var arguments = declaration.GenericTypeNames.Zip(type.TypeArguments)
            .ToDictionary(pair => pair.First, pair => pair.Second);
        var typeInfo = $"CX_TYPEINFO_NAME({identity.CIdentifier})";
        if (declaration.ClassType == ClassType.Class)
        {
            WriteDynamicReadonlyBaseTypeInitialization(
                writer,
                typeInfo,
                GetClosedGenericClassBaseTypeInfoIdentifier(type, declaration));
        }
        var closedInterfaceTypes = declaration.ClassType == ClassType.Struct &&
            RequiresClosedValueLayout(type) ||
            declaration.ClassType == ClassType.Class
                ? GetClosedImplementedInterfaceTypes(type, declaration)
                : new Dictionary<ClassDeclaration, NamedType>();
        var closedInterfaces = declaration.InterfaceDispatchTables
            .Where(table => closedInterfaceTypes.ContainsKey(table.Interface) &&
                (declaration.ClassType == ClassType.Struct ||
                 table.Interface.GenericTypeNames.Length != 0))
            .ToArray();
        var interfaceArray = $"{identity.CIdentifier}___interfaces";
        for (var index = 0; index < closedInterfaces.Length; index++)
        {
            var @interface = closedInterfaces[index].Interface;
            var closedInterfaceType = closedInterfaceTypes[@interface];
            var interfaceTypeInfo = ToTypeInfoPointer(closedInterfaceType);
            var closedTable = GetClosedGenericInterfaceVTableIdentifier(
                identity.CIdentifier,
                @interface.ProjectName ?? moduleName,
                @interface.FullName).ToCIdentifier();
            writer.WriteLine($"{interfaceArray}[{index}] = (struct cx_interface_impl){{ " +
                $"{interfaceTypeInfo}, {closedTable} }};");
        }

        var fields = GetReflectedFields(declaration);
        var fieldArray = $"{identity.CIdentifier}___reflection_fields";
        for (var index = 0; index < fields.Length; index++)
        {
            var field = fields[index];
            var offset = field.IsStatic
                ? "CX_REFLECTION_NO_OFFSET"
                : $"(cx_uint)offsetof({(RequiresClosedValueLayout(type)
                    ? $"struct {identity.CIdentifier}"
                    : declaration.ToCIdentifier(containingTypeModule))}, {field.Name})";
            var fieldType = GenericTypeSubstitution.Substitute(field.Type, arguments);
            writer.WriteLine($"{fieldArray}[{index}] = (struct cx_reflection_field){{ " +
                $"{GetMemberFlags(field.MemberModifiers)}, {offset}, " +
                $"{ToTypeInfoPointer(fieldType)}, \"{field.Name}\" }};");
        }

        var functions = GetReflectionFunctions(declaration).ToArray();
        for (var index = 0; index < functions.Length; index++)
        {
            var function = functions[index];
            var parameterArray =
                $"{identity.CIdentifier}___reflection_parameters_{index}";
            for (var parameterIndex = 0; parameterIndex < function.Parameters.Count; parameterIndex++)
            {
                var parameter = function.Parameters[parameterIndex];
                var parameterType = GenericTypeSubstitution.Substitute(
                    parameter.ParameterType, arguments);
                writer.WriteLine($"{parameterArray}[{parameterIndex}] = " +
                    $"(struct cx_reflection_parameter){{ 0, " +
                    $"{ToTypeInfoPointer(parameterType)}, " +
                    $"\"{parameter.Name}\", CX_NULL }};");
            }
            var parameters = function.Parameters.Count == 0
                ? "CX_NULL"
                : parameterArray;
            var returnType = GenericTypeSubstitution.Substitute(
                function.ReturnType, arguments);
            writer.WriteLine(
                $"{identity.CIdentifier}___reflection_functions[{index}] = " +
                "(struct cx_reflection_function){" +
                $"{GetFunctionFlags(function)}, CX_REFLECTION_NO_SLOT, " +
                $"{ToTypeInfoPointer(returnType)}, \"{function.Name}\", " +
                $"{parameters}, {function.Parameters.Count} }};");
        }
    }

    private static void WriteStaticFields(
        IndentingWriter writer,
        IEnumerable<DeclarationBase> declarations,
        string moduleName)
    {
        foreach (var field in EnumerateFields(declarations).Where(field => field.IsStatic))
        {
            var initializer = !HasStaticLiteralInitializer(field) || field.Initializer is not LiteralExpression literal
                ? string.Empty
                : $" = {ToCFieldInitializer(literal, moduleName)}";
            var export = field.MemberModifiers.Contains(MemberModifier.Public) &&
                field.ParentClassDeclaration?.Visibility == Visibility.Public
                ? $"{GetModuleDataApiName(moduleName)} "
                : "static ";
            writer.WriteLine(
                $"{export}{field.Type.ToCIdentifier(false)} " +
                $"{GetStaticFieldIdentifier(field, moduleName)}{initializer};");
        }
    }

    private static void WriteDynamicStaticFieldInitializers(
        IndentingWriter writer,
        IEnumerable<DeclarationBase> declarations,
        string moduleName)
    {
        var initializerContext = new FunctionDeclaration(
            "__cx_static_field_initializer",
            moduleName,
            BuiltInSystemTypes.Void,
            [],
            null);
        foreach (var field in EnumerateFields(declarations).Where(field =>
                     field.IsStatic && field.Initializer is not null && !HasStaticLiteralInitializer(field)))
        {
            writer.WriteLine(
                $"{GetStaticFieldIdentifier(field, moduleName)} = " +
                $"{ToCExpressionAsType(
                    field.Initializer!, field.Type, initializerContext, moduleName)};");
        }
    }

    private static void WriteStaticRootRegistration(
        IndentingWriter writer,
        IEnumerable<DeclarationBase> declarations,
        string moduleName)
    {
        var staticFields = EnumerateFields(declarations).Where(field => field.IsStatic).ToArray();
        var helperName = $"__cx_register_static_roots_{GetModuleToken(moduleName)}";
        writer.WriteLine($"static void {helperName}(void)");
        writer.WriteLine("{");
        writer.IncreaseIndent();
        writer.WriteLine("cx_gc_check_thread();");
        if (staticFields.Length != 0)
        {
            writer.WriteLine("static const struct cx_gc_root_region roots[] = {");
            writer.IncreaseIndent();
            foreach (var field in staticFields)
            {
                var fieldName = GetStaticFieldIdentifier(field, moduleName);
                writer.WriteLine($"{{ (const void *)&{fieldName}, (cx_uint)sizeof({fieldName}) }},");
            }
            writer.DecreaseIndent();
            writer.WriteLine("};");
        }
        writer.WriteLine(staticFields.Length == 0
            ? "cx_gc_register_static_roots(NULL, 0);"
            : $"cx_gc_register_static_roots(roots, (cx_uint){staticFields.Length});");
        writer.DecreaseIndent();
        writer.WriteLine("}");
        writer.WriteLine();
    }

    private static IEnumerable<FieldDeclaration> EnumerateFields(
        IEnumerable<DeclarationBase> declarations)
    {
        foreach (var declaration in declarations)
        {
            if (declaration is FieldDeclaration field)
            {
                yield return field;
            }
            else if (declaration is ClassDeclaration classDeclaration)
            {
                foreach (var nested in EnumerateFields(classDeclaration.MemberDeclarations.Declarations))
                {
                    yield return nested;
                }
            }
        }
    }

    private static string GetStaticFieldIdentifier(
        FieldDeclaration field,
        string moduleName)
    {
        return new QualifiedIdentifier(moduleName, field.FullName).ToCIdentifier();
    }

    private static string ToCFieldInitializer(ExpressionBase expression, string moduleName)
    {
        return expression switch
        {
            LiteralExpression { IsString: true } literal =>
                $"&{GetStringIdentifier(literal, moduleName).ToCIdentifier()}",
            LiteralExpression { SourceText: "true" } => "CX_TRUE",
            LiteralExpression { SourceText: "false" } => "CX_FALSE",
            LiteralExpression { SourceText: "null" } literal => ToCNullLiteral(literal),
            LiteralExpression literal when IsDecimalLiteral(literal) => ToCDecimalLiteral(literal)["(cx_decimal)".Length..],
            LiteralExpression literal => literal.SourceText,
            _ => throw new InternalCompilerException(
                $"Field initializer '{expression.GetType().Name}' is not supported."),
        };
    }

    private static QualifiedIdentifier GetStringIdentifier(
        LiteralExpression literal,
        string moduleName)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(literal.SourceText));
        return new QualifiedIdentifier(moduleName, $"__string_{Convert.ToHexString(hash.AsSpan(0, 8))}");
    }

    private static void WriteTypeInfos(
        IndentingWriter writer,
        IReadOnlyCollection<DeclarationBase> declarations,
        string moduleName)
    {
        foreach (var declaration in declarations.Where(declaration =>
            declaration is ClassDeclaration or EnumDeclaration))
        {
            var typeInfoIdentifier = new QualifiedIdentifier(
                moduleName, declaration.FullName, "__typeinfo").ToCIdentifier();
            writer.WriteLine(
                $"extern struct cxcore_System_Reflection_TypeInfo {typeInfoIdentifier};");
        }
        if (declarations.Any(declaration =>
            declaration is ClassDeclaration or EnumDeclaration))
        {
            writer.WriteLine();
        }

        foreach (var classDeclaration in EnumerateClasses(declarations))
        {
            writer.WriteLine();
            if (classDeclaration.ClassType == ClassType.Class && classDeclaration.IsStatic)
            {
                WriteReflectionMetadata(writer, classDeclaration, moduleName);
                WriteTypeInfoDefinition(writer, classDeclaration, moduleName);
                continue;
            }

            WriteVTableDefinition(writer, classDeclaration, moduleName);
            foreach (var table in classDeclaration.InterfaceDispatchTables.Where(table =>
                !HasOpenGenericInterfaceSignature(classDeclaration, table.Interface)))
            {
                WriteInterfaceVTableDefinition(writer, classDeclaration, table, moduleName);
            }
            WriteInterfaceRuntimeMap(writer, classDeclaration, moduleName);
            WriteReflectionMetadata(writer, classDeclaration, moduleName);
            WriteTypeInfoDefinition(writer, classDeclaration, moduleName);
        }

        foreach (var enumDeclaration in EnumerateEnums(declarations))
        {
            writer.WriteLine();
            WriteEnumTypeInfoDefinition(writer, enumDeclaration, moduleName);
        }
    }

    private static void ValidateClosedReferenceLayout(
        NamedType type,
        ClassDeclaration declaration)
    {
        if (declaration.Name.StartsWith("__CxIterator_", StringComparison.Ordinal))
        {
            return;
        }
        if (declaration.ClassType == ClassType.Interface ||
            ArrayType.TryGetElementType(type, out _))
        {
            return;
        }
        if (IsStableTaskReferenceType(type))
        {
            return;
        }
        if (RequiresClosedValueLayout(type))
        {
            ValidateClosedValueLayout(declaration);
            return;
        }
        var members = declaration.MemberDeclarations.Declarations;
        var constructors = members.OfType<ConstructorDeclaration>().ToArray();
        var fields = members.OfType<FieldDeclaration>().ToArray();
        var methods = members.OfType<FunctionDeclaration>()
            .Where(function => function is not ConstructorDeclaration).ToArray();
        var properties = members.OfType<PropertyDeclaration>().ToArray();
        var supportedConstructor = constructors.Length == 0 ||
            constructors.Length == 1 && constructors[0] is { } constructor &&
            (constructor.Body is { Count: 0 } && constructor.Parameters.Count == 0 ||
                IsReferenceFieldAssignments(constructor, fields) ||
                IsClosedGenericOwnerConstructor(constructor, declaration, fields) ||
                IsClosedGenericArrayConstructor(constructor, declaration, fields)) &&
            (constructor.Initializer is null ||
                constructor.Initializer is
                {
                    Kind: ConstructorInitializerKind.Base,
                    Arguments.Count: 0,
                });
        var supportedFields = fields.All(field =>
        {
            if (field.IsStatic || field.Initializer is not null &&
                !IsSupportedClosedGenericFieldInitializer(field, declaration))
            {
                return false;
            }
            if (field.Type is ArrayType &&
                IsGenericTypePattern(field.Type, declaration.GenericTypeNames))
            {
                return true;
            }
            if (field.Type is GenericType parameter)
            {
                var index = Array.IndexOf(declaration.GenericTypeNames, parameter.Name);
                return index >= 0 && index < type.TypeArguments.Count &&
                    IsReferenceTypeArgument(type.TypeArguments[index]);
            }
            if (field.Type is NamedType
                { ClassType: ClassType.Class or ClassType.Interface } namedField &&
                IsGenericTypePattern(namedField, declaration.GenericTypeNames))
            {
                return true;
            }
            return !ContainsGenericParameter(field.Type, declaration.GenericTypeNames) &&
                IsSupportedClosedValueFieldType(field.Type, declaration.GenericTypeNames);
        });
        var supportedMethods = methods.All(method =>
            !method.IsStatic &&
            (IsClosedGenericOwnerMethod(method, declaration) ||
                method.IsGenericSpecialization || method.GenericTypeNames.Length > 0 ||
                IsClosedClassConceptMethodReturnBody(method) ||
                IsClosedClassConceptVoidCallBody(method) ||
                IsClosedGenericArrayGetter(method, declaration, fields) ||
                IsClosedGenericArraySetter(method, declaration, fields) ||
                method.GenericTypeNames.Length == 0 &&
                (method.ReturnType is VoidType && IsReferenceFieldSetter(method, fields) ||
                    IsReferenceFieldGetter(method, fields) ||
                    IsClosedGenericConcreteExpressionMethod(method, declaration, fields) ||
                    IsClosedGenericConcreteFieldAssignmentMethod(method, declaration, fields)) ||
                IsClosedGenericMethodOperator(method)));
        var supportedProperties = properties.All(property =>
            !property.IsStatic &&
            (IsGenericTypePattern(property.Type, declaration.GenericTypeNames) ||
                !ContainsGenericParameter(property.Type, declaration.GenericTypeNames)) &&
            property.PropertyAccessorDeclarations.Count is >= 1 and <= 2 &&
            property.PropertyAccessorDeclarations.Select(accessor => accessor.Name)
                .Distinct().Count() == property.PropertyAccessorDeclarations.Count &&
            property.PropertyAccessorDeclarations.All(accessor =>
                IsReferenceFieldPropertyAccessor(property, accessor, fields) ||
                IsClosedGenericOwnerPropertyAccessor(property, accessor, declaration) ||
                IsClosedGenericArrayLengthAccessor(accessor, declaration, fields)));
        if (declaration.ClassType != ClassType.Class || declaration.IsStatic ||
            members.Count != constructors.Length + fields.Length + methods.Length + properties.Length ||
            !supportedConstructor || !supportedFields || !supportedMethods ||
            !supportedProperties ||
            !IsSupportedClosedGenericReferenceBase(declaration) ||
            !HasSupportedClosedGenericReferenceSlots(declaration) ||
            !HasSupportedClosedGenericInterfaceTables(declaration))
        {
            throw new CxCompiler.Model.Errors.CompilationErrorException(
                $"Closed runtime metadata for generic type '{declaration.FullName}' " +
                "currently requires an empty class, supported concrete fields, recursively nested " +
                "T-array fields, or T fields with class-reference arguments; it permits at most one " +
                "non-generic class base and otherwise requires at most an empty " +
                "parameterless constructor, or one assignment per reference " +
                "constructor parameter, or a void method with a " +
                "single-field assignment, or a T getter returning a T field, " +
                "or a field-backed generic property.");
        }
    }

    private static bool IsSupportedClosedGenericReferenceBase(
        ClassDeclaration declaration) =>
        declaration.BaseTypes.All(type =>
            type is NamedType { ClassType: ClassType.Interface } ||
            type is NamedType { ClassType: ClassType.Class } baseType &&
            baseType.TypeArguments.All(argument =>
                IsGenericTypePattern(argument, declaration.GenericTypeNames) ||
                !ContainsGenericParameter(argument, declaration.GenericTypeNames)));

    private static string GetClosedGenericClassBaseTypeInfoIdentifier(
        NamedType closedType,
        ClassDeclaration declaration)
    {
        if (declaration.BaseClassType is not NamedType baseType)
        {
            return "cxcore_System_Object___typeinfo";
        }
        if (baseType.TypeArguments.Count == 0)
        {
            return new QualifiedIdentifier(
                baseType.ResolvedTypeFullName, "__typeinfo").ToCIdentifier();
        }

        var arguments = declaration.GenericTypeNames.Zip(closedType.TypeArguments)
            .ToDictionary(pair => pair.First, pair => pair.Second);
        var closedBaseType = GenericTypeSubstitution.Substitute(baseType, arguments);
        if (closedBaseType is not NamedType
            { TypeArguments.Count: > 0 } constructedBaseType)
        {
            throw new InternalCompilerException(
                $"Cannot close generic base type '{baseType}'.");
        }
        var parts = constructedBaseType.ResolvedTypeFullName.Parts;
        var identity = GenericTypeIdentity.Create(
            parts[0], string.Join('.', parts.Skip(1)), constructedBaseType.TypeArguments);
        return $"CX_TYPEINFO_NAME({identity.CIdentifier})";
    }

    private static bool RequiresClosedValueLayout(NamedType type)
    {
        if (IsStableTaskReferenceType(type) &&
            string.Join(".", type.ResolvedTypeFullName.Parts.Skip(1)) ==
                "System.Threading.Tasks.TaskCompletionSource")
        {
            return false;
        }
        return type.ClassType != ClassType.Interface &&
            !ArrayType.TryGetElementType(type, out _) &&
            type.ConstructedIdentity is not null &&
            (type.ClassType == ClassType.Struct ||
             type.TypeArguments.Any(IsValueTypeArgument));
    }

    private static bool IsStableTaskReferenceType(NamedType type) =>
        type.ClassType == ClassType.Class &&
        string.Join(".", type.ResolvedTypeFullName.Parts.Skip(1)) is
            "System.Threading.Tasks.Task" or
            "System.Threading.Tasks.TaskCompletionSource";

    private static bool HasOnlyInheritedObjectSlots(ClassDeclaration declaration) =>
        declaration.VirtualMethodSlots.All(slot =>
            slot.Contract.ParentClassDeclaration?.FullName.ToString() == "System.Object" &&
            ReferenceEquals(slot.Contract, slot.Implementation));

    private static bool HasSupportedClosedGenericReferenceSlots(
        ClassDeclaration declaration) =>
        declaration.VirtualMethodSlots.All(slot =>
            slot.Implementation is null ||
            slot.Implementation.GenericTypeNames.Length > 0 ||
            !ReferenceEquals(slot.Implementation.ParentClassDeclaration, declaration));

    private static bool IsValueTypeArgument(TypeBase type) => type switch
    {
        ConstType constant => IsValueTypeArgument(constant.UnderlyingType),
        ArrayType or StringType or ObjectType => false,
        NamedType named => named.ClassType is ClassType.Struct or ClassType.Enum,
        _ => type is not GenericType,
    };

    private static bool IsReferenceTypeArgument(TypeBase type) => type switch
    {
        ConstType constant => IsReferenceTypeArgument(constant.UnderlyingType),
        ArrayType or StringType or ObjectType => true,
        NamedType named => named.ClassType is ClassType.Class or ClassType.Interface,
        _ => false,
    };

    private static void ValidateClosedValueLayout(ClassDeclaration declaration)
    {
        if (declaration.Name.StartsWith("__CxIterator_", StringComparison.Ordinal))
        {
            return;
        }
        var members = declaration.MemberDeclarations.Declarations;
        var fields = members.OfType<FieldDeclaration>().ToArray();
        var constructors = members.OfType<ConstructorDeclaration>().ToArray();
        var genericMethods = members.OfType<FunctionDeclaration>()
            .Where(function => function is not ConstructorDeclaration).ToArray();
        var properties = members.OfType<PropertyDeclaration>().ToArray();
        if (declaration.ClassType is not (ClassType.Class or ClassType.Struct) || declaration.IsStatic ||
            declaration.GenericTypeNames.Length == 0 ||
            members.Count != fields.Length + constructors.Length + genericMethods.Length + properties.Length ||
            genericMethods.Any(method => method.IsStatic ||
                method.VirtualSlotIndex is not null ||
                (method.GenericTypeNames.Length == 0 && !method.IsGenericSpecialization &&
                    !IsReferenceFieldGetter(method, fields) &&
                    !IsReferenceFieldSetter(method, fields) &&
                    !IsClosedGenericFieldGetter(method, declaration, fields) &&
                    !IsClosedGenericFieldSetter(method, declaration, fields) &&
                    !IsClosedGenericArrayGetter(method, declaration, fields) &&
                    !IsClosedGenericArraySetter(method, declaration, fields) &&
                    !IsGenericFieldSetter(method, fields) &&
                    !IsClosedGenericValueOperator(method, declaration) &&
                    !IsClosedGenericConstantMethod(method, declaration) &&
                    !IsClosedGenericDefaultFieldSetter(method, declaration, fields) &&
                    GetClosedGenericDefaultFieldAssignments(
                        method, declaration, fields) is null &&
                    GetClosedGenericSimpleFieldAssignments(
                        method, declaration, fields) is null &&
                    GetClosedGenericConditionalDefaultFieldReset(
                        method, declaration, fields) is null &&
                    GetClosedGenericConditionalFieldParameterAssignments(
                        method, declaration, fields) is null &&
                    GetClosedGenericConditionalConcreteFieldAssignments(
                        method, declaration, fields) is null &&
                    GetClosedGenericWhileConcreteFieldAssignments(
                        method, declaration, fields) is null &&
                    GetClosedGenericDoWhileConcreteFieldAssignments(
                        method, declaration, fields) is null &&
                    GetClosedGenericForConcreteFieldAssignments(
                        method, declaration, fields) is null &&
                    !IsClosedGenericConcreteExpressionMethod(method, declaration, fields) &&
                    !IsClosedGenericGenericResultExpressionMethod(method, declaration, fields) &&
                    !IsClosedGenericFieldConstantSetter(method, declaration, fields) &&
                    !IsClosedGenericConcreteFieldAssignmentMethod(method, declaration, fields) &&
                    !IsClosedGenericDefaultResultMethod(method, declaration) &&
                    !IsClosedGenericOwnerMethod(method, declaration) &&
                    !IsClosedClassConceptMethodReturnBody(method) &&
                    !IsClosedClassConceptVoidCallBody(method) &&
                    GetClosedGenericMixedFieldAssignments(
                        method, declaration, fields) is null &&
                    GetClosedGenericInterfaceReturnedParameterIndex(method, declaration) is null)) ||
            properties.Any(property =>
                !IsSupportedClosedGenericProperty(property, declaration, fields)) ||
            constructors.Length > 2 ||
            fields.Any(field => field.IsStatic ||
                field.Initializer is not null &&
                    !IsSupportedClosedGenericFieldInitializer(field, declaration) ||
                !IsSupportedClosedValueFieldType(field.Type, declaration.GenericTypeNames)) ||
            !IsSupportedClosedValueConstructor(declaration, fields, constructors) ||
            declaration.BaseTypes.Any(type =>
                type is not NamedType { ClassType: ClassType.Interface }) ||
            !HasOnlyInheritedObjectSlots(declaration) ||
            !HasSupportedClosedGenericInterfaceTables(declaration))
        {
            throw new CxCompiler.Model.Errors.CompilationErrorException(
                $"Closed value layout for generic type '{declaration.FullName}' " +
                "requires concrete fields, direct generic-parameter and generic-array fields " +
                "(including recursively nested arrays), " +
                "plus non-virtual generic methods or field-backed generic properties/getters/setters, " +
                "and an empty parameterless constructor with at most one additional constructor " +
                "that matches its generic field parameters. Members: " +
                string.Join(", ", members.Select(member => member.Name)) +
                $". Closed-owner methods: {string.Join(", ", genericMethods.Where(method => !IsClosedGenericOwnerMethod(method, declaration)).Select(method => method.Name))}; " +
                $"unsupported properties: {string.Join(", ", properties.Where(property => !IsSupportedClosedGenericProperty(property, declaration, fields)).Select(property => property.Name))}; " +
                $"constructor validation: {IsSupportedClosedValueConstructor(declaration, fields, constructors)}; " +
                $"field validation: {fields.All(field => !field.IsStatic && (field.Initializer is null || IsSupportedClosedGenericFieldInitializer(field, declaration)) && IsSupportedClosedValueFieldType(field.Type, declaration.GenericTypeNames))}; " +
                $"base validation: {!declaration.BaseTypes.Any(type => type is not NamedType { ClassType: ClassType.Interface })}; " +
                $"object slots: {HasOnlyInheritedObjectSlots(declaration)}; interface tables: {HasSupportedClosedGenericInterfaceTables(declaration)}.");
        }
    }

    private static bool IsClosedGenericConstantMethod(
        FunctionDeclaration method,
        ClassDeclaration declaration) =>
        method.GenericTypeNames.Length == 0 &&
        method.Body is [ReturnStatement { Expression: LiteralExpression }] &&
        !ContainsGenericParameter(method.ReturnType, declaration.GenericTypeNames);

    private static bool IsClosedGenericOwnerMethod(
        FunctionDeclaration method,
        ClassDeclaration declaration) =>
        method.GenericTypeNames.Length == 0 &&
        !method.IsStatic && method.VirtualSlotIndex is null &&
        method.Body is not null && declaration.GenericTypeNames.Length > 0;

    private static bool IsClosedGenericNoOpMethod(FunctionDeclaration method) =>
        !method.IsStatic && method.GenericTypeNames.Length == 0 &&
        method.ReturnType is VoidType && method.Body is { Count: 0 };

    private static bool IsClosedGenericOwnerPropertyAccessor(
        PropertyDeclaration property,
        PropertyAccessorDeclaration accessor,
        ClassDeclaration declaration) =>
        !property.IsStatic && accessor.BodyFunction is { } function &&
        IsClosedGenericOwnerMethod(function, declaration);

    private static bool IsClosedGenericInterfaceConstantMethod(
        FunctionDeclaration method,
        ClassDeclaration declaration) =>
        IsClosedGenericConstantMethod(method, declaration) &&
        method.Parameters.All(parameter =>
            !ContainsGenericParameter(parameter.ParameterType, declaration.GenericTypeNames));

    private static bool IsClosedGenericConcreteExpressionMethod(
        FunctionDeclaration method,
        ClassDeclaration declaration,
        IReadOnlyCollection<FieldDeclaration> fields) =>
        method.GenericTypeNames.Length == 0 &&
        method.ReturnType is not VoidType &&
        !ContainsGenericParameter(method.ReturnType, declaration.GenericTypeNames) &&
        method.Parameters.All(parameter =>
            !ContainsGenericParameter(parameter.ParameterType, declaration.GenericTypeNames)) &&
        (method.Body is [ReturnStatement { Expression: { } expression }] &&
            IsClosedGenericConcreteMethodExpression(
                expression, method, declaration.GenericTypeNames, fields) ||
         TryGetClosedGenericConcreteResultParts(
             method, out var condition, out var whenTrue, out var whenFalse) &&
            IsClosedGenericConcreteMethodExpression(
                condition, method, declaration.GenericTypeNames, fields) &&
            IsClosedGenericConcreteMethodExpression(
                whenTrue, method, declaration.GenericTypeNames, fields) &&
            IsClosedGenericConcreteMethodExpression(
                whenFalse, method, declaration.GenericTypeNames, fields));

    private static bool IsClosedGenericConcreteFieldAssignmentMethod(
        FunctionDeclaration method,
        ClassDeclaration declaration,
        IReadOnlyCollection<FieldDeclaration> fields)
    {
        if (method.GenericTypeNames.Length != 0 ||
            method.ReturnType is not VoidType ||
            method.Parameters.Any(parameter =>
                ContainsGenericParameter(
                    parameter.ParameterType, declaration.GenericTypeNames)) ||
            method.Body is not { Count: > 0 } body)
        {
            return false;
        }

        foreach (var statement in body)
        {
            if (statement is not ExpressionStatement
                {
                    Expression: AssignmentExpression assignment,
                } ||
                assignment.Operator is not ("=" or "+=" or "-=" or "*=" or "/=" or "%=") ||
                assignment.OperatorSymbol is not null ||
                assignment.Target is not IdentifierExpression
                    { TargetField: { } targetField } ||
                !fields.Contains(targetField.Declaration) ||
                ContainsGenericParameter(
                    targetField.Declaration.Type, declaration.GenericTypeNames) ||
                assignment.Value is not LiteralExpression { SourceText: "default" } &&
                !IsClosedGenericConcreteMethodExpression(
                    assignment.Value, method, declaration.GenericTypeNames, fields))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsClosedGenericGenericFieldDefaultResetMethod(
        FunctionDeclaration method,
        ClassDeclaration declaration,
        IReadOnlyCollection<FieldDeclaration> fields) =>
        !method.IsStatic &&
        method.GenericTypeNames.Length == 0 &&
        method.ReturnType is VoidType &&
        method.Parameters.Count == 0 &&
        method.Body is [ExpressionStatement
        {
            Expression: AssignmentExpression
            {
                Operator: "=",
                Target: IdentifierExpression { TargetField: { } targetField },
                Value: LiteralExpression { SourceText: "default" },
            },
        }] &&
        fields.Contains(targetField.Declaration) &&
        ContainsGenericParameter(
            targetField.Declaration.Type, declaration.GenericTypeNames);

    private static IReadOnlyList<AssignmentExpression>?
        GetClosedGenericGenericFieldParameterAssignments(
        FunctionDeclaration method,
        ClassDeclaration declaration,
        IReadOnlyCollection<FieldDeclaration> fields)
    {
        if (method.IsStatic || method.GenericTypeNames.Length != 0 ||
            method.ReturnType is not VoidType ||
            method.Body is not { Count: > 0 } body)
        {
            return null;
        }

        var assignments = new List<AssignmentExpression>();
        foreach (var statement in body)
        {
            if (statement is not ExpressionStatement
                {
                    Expression: AssignmentExpression
                    {
                        Operator: "=",
                        Target: IdentifierExpression { TargetField: { } targetField },
                        Value: IdentifierExpression { TargetLocal: { } valueLocal },
                    } assignment,
                } ||
                !fields.Contains(targetField.Declaration) ||
                !ContainsGenericParameter(
                    targetField.Declaration.Type, declaration.GenericTypeNames) ||
                !method.Parameters.Any(parameter =>
                    parameter.Name == valueLocal.Name &&
                    IsGenericTypePattern(
                        parameter.ParameterType, declaration.GenericTypeNames) &&
                    IsSameGenericTypePattern(
                        parameter.ParameterType, targetField.Declaration.Type)))
            {
                return null;
            }

            assignments.Add(assignment);
        }

        return assignments;
    }

    private static IReadOnlyList<AssignmentExpression>?
        GetClosedGenericMixedFieldAssignments(
            FunctionDeclaration method,
            ClassDeclaration declaration,
            IReadOnlyCollection<FieldDeclaration> fields)
    {
        if (method.GenericTypeNames.Length != 0 ||
            method.ReturnType is not VoidType ||
            method.Body is not { Count: > 1 } body)
        {
            return null;
        }

        var assignments = new List<AssignmentExpression>();
        var genericAssignmentCount = 0;
        var concreteAssignmentCount = 0;
        foreach (var statement in body)
        {
            if (statement is not ExpressionStatement
                {
                    Expression: AssignmentExpression
                    {
                        Operator: "=",
                        Target: IdentifierExpression { TargetField: { } targetField },
                        Value: var value,
                    } assignment,
                } || !fields.Contains(targetField.Declaration))
            {
                return null;
            }

            if (ContainsGenericParameter(
                    targetField.Declaration.Type, declaration.GenericTypeNames))
            {
                if (value is not IdentifierExpression { TargetLocal: { } targetLocal } ||
                    !method.Parameters.Any(parameter =>
                        parameter.Name == targetLocal.Name &&
                        IsGenericTypePattern(
                            parameter.ParameterType, declaration.GenericTypeNames) &&
                        IsSameGenericTypePattern(
                            parameter.ParameterType, targetField.Declaration.Type)))
                {
                    return null;
                }
                genericAssignmentCount++;
            }
            else
            {
                if (!IsClosedGenericConcreteMethodExpression(
                        value, method, declaration.GenericTypeNames, fields))
                {
                    return null;
                }
                concreteAssignmentCount++;
            }

            assignments.Add(assignment);
        }

        return genericAssignmentCount > 0 && concreteAssignmentCount > 0
            ? assignments
            : null;
    }

    private static bool IsClosedGenericConcreteMethodExpression(
        ExpressionBase expression,
        FunctionDeclaration method,
        IReadOnlyCollection<string> genericTypeNames,
        IReadOnlyCollection<FieldDeclaration> fields) => expression switch
    {
        LiteralExpression { SourceText: not "null" and not "default" } => true,
        IdentifierExpression { TargetField: { } field } =>
            fields.Contains(field.Declaration) &&
            !ContainsGenericParameter(field.Declaration.Type, genericTypeNames),
        IdentifierExpression identifier => method.Parameters.Any(parameter =>
            parameter.Name == identifier.Identifier.ToString() &&
            !ContainsGenericParameter(parameter.ParameterType, genericTypeNames)),
        UnaryExpression
        {
            OperatorSymbol: null,
            Postfix: false,
            Operator: "+" or "-" or "~" or "!",
        } unary => IsClosedGenericConcreteMethodExpression(
            unary.Operand, method, genericTypeNames, fields),
        BinaryExpression
        {
            OperatorSymbol: null,
            Operator: "+" or "-" or "*" or "/" or "%" or
                "<<" or ">>" or "&" or "|" or "^" or
                "==" or "!=" or "<" or "<=" or ">" or ">=" or "&&" or "||",
        } binary => IsClosedGenericConcreteMethodExpression(
                binary.Left, method, genericTypeNames, fields) &&
            IsClosedGenericConcreteMethodExpression(
                binary.Right, method, genericTypeNames, fields),
        ConditionalExpression conditional =>
            IsClosedGenericConcreteMethodExpression(
                conditional.Condition, method, genericTypeNames, fields) &&
            IsClosedGenericConcreteMethodExpression(
                conditional.WhenTrue, method, genericTypeNames, fields) &&
            IsClosedGenericConcreteMethodExpression(
                conditional.WhenFalse, method, genericTypeNames, fields),
        _ => false,
    };

    private static string ToCClosedGenericConcreteMethodExpression(
        ExpressionBase expression,
        IReadOnlyDictionary<string, string> parameterNames,
        FunctionDeclaration method,
        string closedTypeIdentifier,
        string moduleName,
        string? instanceExpression = null) => expression switch
    {
        LiteralExpression => ToCExpression(expression, method, moduleName),
        IdentifierExpression { TargetField: { } field } =>
            $"({instanceExpression ?? $"((struct {closedTypeIdentifier}*)__root)"})->{field.Declaration.Name}",
        IdentifierExpression identifier when parameterNames.TryGetValue(
            identifier.Identifier.ToString(), out var parameterName) => parameterName,
        UnaryExpression unary =>
            $"({unary.Operator}{ToCClosedGenericConcreteMethodExpression(unary.Operand, parameterNames, method, closedTypeIdentifier, moduleName, instanceExpression)})",
        BinaryExpression binary =>
            $"({ToCClosedGenericConcreteMethodExpression(binary.Left, parameterNames, method, closedTypeIdentifier, moduleName, instanceExpression)} " +
            $"{binary.Operator} {ToCClosedGenericConcreteMethodExpression(binary.Right, parameterNames, method, closedTypeIdentifier, moduleName, instanceExpression)})",
        ConditionalExpression conditional =>
            $"({ToCClosedGenericConcreteMethodExpression(conditional.Condition, parameterNames, method, closedTypeIdentifier, moduleName, instanceExpression)} ? " +
            $"{ToCClosedGenericConcreteMethodExpression(conditional.WhenTrue, parameterNames, method, closedTypeIdentifier, moduleName, instanceExpression)} : " +
            $"{ToCClosedGenericConcreteMethodExpression(conditional.WhenFalse, parameterNames, method, closedTypeIdentifier, moduleName, instanceExpression)})",
        _ => throw new InternalCompilerException(
            $"Expression '{expression.GetType().Name}' is not supported in a closed generic interface thunk."),
    };

    private static int? GetClosedGenericInterfaceReturnedParameterIndex(
        FunctionDeclaration method,
        ClassDeclaration declaration)
    {
        if (method.GenericTypeNames.Length != 0 ||
            method.Body is not
                [ReturnStatement
                {
                    Expression: IdentifierExpression { TargetLocal: { } local },
                }] ||
            !declaration.InterfaceDispatchTables.Any(table =>
                table.Interface.GenericTypeNames.Length != 0 &&
                table.Slots.Any(slot =>
                    ReferenceEquals(slot.ImplementationFunction, method))))
        {
            return null;
        }

        for (var index = 0; index < method.Parameters.Count; index++)
        {
            var parameter = method.Parameters[index];
            if (parameter.Name == local.Name &&
                IsSameGenericTypePattern(method.ReturnType, parameter.ParameterType))
            {
                return index;
            }
        }
        return null;
    }

    private static bool IsClosedGenericIteratorCurrentAccessor(
        PropertyAccessorDeclaration accessor,
        ClassDeclaration declaration)
    {
        var property = accessor.ParentPropertyDeclaration;
        if (accessor.Name != "get" || property.Name != "Current" ||
            !declaration.Name.StartsWith("__CxIterator_", StringComparison.Ordinal) ||
            property.Type is not GenericType genericType ||
            !declaration.GenericTypeNames.Contains(genericType.Name, StringComparer.Ordinal))
        {
            return false;
        }

        var fields = declaration.MemberDeclarations.Declarations
            .OfType<FieldDeclaration>()
            .ToArray();
        return fields.Any(field => field.Name == "__cx_hasCurrent") &&
            fields.Any(field => field.Name == "__cx_current" &&
                IsSameGenericTypePattern(field.Type, property.Type));
    }

    private static bool IsClosedGenericFieldGetter(
        FunctionDeclaration method,
        ClassDeclaration declaration,
        IReadOnlyCollection<FieldDeclaration> fields) =>
        method.Parameters.Count == 0 &&
        method.Body is
            [ReturnStatement
            {
                Expression: IdentifierExpression { TargetField: { } targetField },
            }] &&
        fields.Contains(targetField.Declaration) &&
        !ContainsGenericParameter(method.ReturnType, declaration.GenericTypeNames) &&
        !ContainsGenericParameter(targetField.Declaration.Type, declaration.GenericTypeNames) &&
        IsSameGenericTypePattern(method.ReturnType, targetField.Declaration.Type);

    private static bool IsClosedGenericFieldSetter(
        FunctionDeclaration method,
        ClassDeclaration declaration,
        IReadOnlyCollection<FieldDeclaration> fields) =>
        GetClosedGenericFieldSetterParameterIndex(method, declaration, fields) is not null;

    private static bool IsClosedGenericArrayGetter(
        FunctionDeclaration method,
        ClassDeclaration declaration,
        IReadOnlyCollection<FieldDeclaration> fields) =>
        method.Parameters.Count == 1 &&
        method.Parameters[0].ParameterType is UIntType &&
        method.Body is
            [ReturnStatement
            {
                Expression: ArrayAccessExpression
                {
                    Target: IdentifierExpression { TargetField: { } targetField },
                    Indices: [IdentifierExpression index],
                },
            }] &&
        fields.Contains(targetField.Declaration) &&
        targetField.Declaration.Type is ArrayType arrayType &&
        IsGenericTypePattern(arrayType.ElementType, declaration.GenericTypeNames) &&
        IsSameGenericTypePattern(method.ReturnType, arrayType.ElementType) &&
        method.Parameters[0].Name == index.Identifier.ToString();

    private static bool IsClosedGenericArraySetter(
        FunctionDeclaration method,
        ClassDeclaration declaration,
        IReadOnlyCollection<FieldDeclaration> fields)
    {
        if (method.Parameters.Count != 2 || method.ReturnType is not VoidType ||
            method.Parameters[0].ParameterType is not UIntType ||
            method.Body is not
                [ExpressionStatement
                {
                    Expression: AssignmentExpression
                    {
                        Operator: "=",
                        Target: ArrayAccessExpression
                        {
                            Target: IdentifierExpression { TargetField: { } targetField },
                            Indices: [IdentifierExpression index],
                        },
                        Value: IdentifierExpression value,
                    },
                }] ||
            !fields.Contains(targetField.Declaration) ||
            targetField.Declaration.Type is not ArrayType arrayType ||
            !IsGenericTypePattern(arrayType.ElementType, declaration.GenericTypeNames))
        {
            return false;
        }

        return method.Parameters[0].Name == index.Identifier.ToString() &&
            method.Parameters[1].Name == value.Identifier.ToString() &&
            IsSameGenericTypePattern(method.Parameters[1].ParameterType, arrayType.ElementType);
    }

    private static bool IsClosedGenericArrayLengthAccessor(
        PropertyAccessorDeclaration accessor,
        ClassDeclaration declaration,
        IReadOnlyCollection<FieldDeclaration> fields) =>
        accessor.Name == "get" && accessor.Const && accessor.Parameters.Count == 0 &&
        accessor.ParentPropertyDeclaration.Type is UIntType &&
        accessor.BodyFunction is { } bodyFunction &&
        IsClosedGenericArrayLengthMethod(bodyFunction, declaration, fields);

    private static bool IsClosedGenericArrayLengthMethod(
        FunctionDeclaration method,
        ClassDeclaration declaration,
        IReadOnlyCollection<FieldDeclaration> fields) =>
        method.ReturnType is UIntType &&
        method.Parameters.Count == 0 &&
        method.Body is
            [ReturnStatement
            {
                Expression: MemberAccessExpression
                {
                    MemberName: "Length",
                    Target: IdentifierExpression { TargetField: { } targetField },
                },
            }] &&
        fields.Contains(targetField.Declaration) &&
        targetField.Declaration.Type is ArrayType arrayType &&
        IsGenericTypePattern(arrayType.ElementType, declaration.GenericTypeNames);

    private static int? GetClosedGenericFieldSetterParameterIndex(
        FunctionDeclaration method,
        ClassDeclaration declaration,
        IReadOnlyCollection<FieldDeclaration> fields)
    {
        if (method.Parameters.Count == 0 || method.ReturnType is not VoidType ||
            method.Body is not
                [ExpressionStatement
                {
                    Expression: AssignmentExpression
                    {
                        Operator: "=",
                        Target: IdentifierExpression { TargetField: { } targetField },
                        Value: IdentifierExpression { TargetLocal: { } targetLocal },
                    },
                }] ||
            !fields.Contains(targetField.Declaration) ||
            ContainsGenericParameter(targetField.Declaration.Type, declaration.GenericTypeNames))
        {
            return null;
        }

        for (var index = 0; index < method.Parameters.Count; index++)
        {
            var parameter = method.Parameters[index];
            if (parameter.Name == targetLocal.Name &&
                !ContainsGenericParameter(parameter.ParameterType, declaration.GenericTypeNames) &&
                IsSameGenericTypePattern(parameter.ParameterType, targetField.Declaration.Type))
            {
                return index;
            }
        }
        return null;
    }

    private static bool IsClosedGenericFieldConstantSetter(
        FunctionDeclaration method,
        ClassDeclaration declaration,
        IReadOnlyCollection<FieldDeclaration> fields) =>
        method.Parameters.Count == 0 &&
        method.ReturnType is VoidType &&
        method.Body is
        [ExpressionStatement
        {
            Expression: AssignmentExpression
            {
                Operator: "=",
                Target: IdentifierExpression { TargetField: { } targetField },
                Value: var value,
            },
        }] &&
        fields.Contains(targetField.Declaration) &&
        !ContainsGenericParameter(targetField.Declaration.Type, declaration.GenericTypeNames) &&
        IsClosedGenericConstantExpression(value);

    private static FieldDeclaration? GetClosedGenericDefaultFieldSetter(
        FunctionDeclaration method,
        ClassDeclaration declaration,
        IReadOnlyCollection<FieldDeclaration> fields)
    {
        if (method.Parameters.Count != 0 || method.ReturnType is not VoidType ||
            method.Body is not
            [ExpressionStatement
            {
                Expression: AssignmentExpression
                {
                    Operator: "=",
                    Target: IdentifierExpression { TargetField: { } targetField },
                    Value: LiteralExpression { SourceText: "default" },
                },
            }] || !fields.Contains(targetField.Declaration) ||
            !ContainsGenericParameter(targetField.Declaration.Type, declaration.GenericTypeNames))
        {
            return null;
        }

        return targetField.Declaration;
    }

    private static IReadOnlyList<FieldDeclaration>? GetClosedGenericDefaultFieldAssignments(
        FunctionDeclaration method,
        ClassDeclaration declaration,
        IReadOnlyCollection<FieldDeclaration> fields)
    {
        if (method.Parameters.Count != 0 || method.ReturnType is not VoidType ||
            method.Body is not { Count: > 1 } body)
        {
            return null;
        }

        var assignments = new List<FieldDeclaration>();
        foreach (var statement in body)
        {
            if (statement is not ExpressionStatement
                {
                    Expression: AssignmentExpression
                    {
                        Operator: "=",
                        Target: IdentifierExpression { TargetField: { } targetField },
                        Value: LiteralExpression { SourceText: "default" },
                    },
                } || !fields.Contains(targetField.Declaration) ||
                !ContainsGenericParameter(
                    targetField.Declaration.Type, declaration.GenericTypeNames) ||
                assignments.Contains(targetField.Declaration))
            {
                return null;
            }

            assignments.Add(targetField.Declaration);
        }

        return assignments;
    }

    private static IReadOnlyList<(FieldDeclaration Field, ExpressionBase Value)>?
        GetClosedGenericSimpleFieldAssignments(
            FunctionDeclaration method,
            ClassDeclaration declaration,
            IReadOnlyCollection<FieldDeclaration> fields)
    {
        if (method.IsStatic || method.Parameters.Count != 0 ||
            method.ReturnType is not VoidType || method.Body is not { Count: > 0 } body)
        {
            return null;
        }

        var assignments = new List<(FieldDeclaration Field, ExpressionBase Value)>();
        foreach (var statement in body)
        {
            if (statement is not ExpressionStatement
                {
                    Expression: AssignmentExpression
                    {
                        Operator: "=",
                        Target: IdentifierExpression { TargetField: { } targetField },
                    } assignment,
                } || !fields.Contains(targetField.Declaration) ||
                assignment.Value is not LiteralExpression { SourceText: "default" } &&
                !IsClosedGenericConcreteMethodExpression(
                    assignment.Value, method, declaration.GenericTypeNames, fields))
            {
                return null;
            }

            assignments.Add((targetField.Declaration, assignment.Value));
        }

        return assignments;
    }

    private static (
        ExpressionBase Condition,
        FieldDeclaration WhenTrue,
        FieldDeclaration WhenFalse)? GetClosedGenericConditionalDefaultFieldReset(
            FunctionDeclaration method,
            ClassDeclaration declaration,
            IReadOnlyCollection<FieldDeclaration> fields)
    {
        if (method.Parameters is not [{ ParameterType: BoolType }] ||
            method.ReturnType is not VoidType ||
            method.Body is not [IfStatement branch] ||
            branch.ElseStatement is null)
        {
            return null;
        }

        var whenTrue = GetDefaultResetField(branch.ThenStatement, declaration, fields);
        var whenFalse = GetDefaultResetField(branch.ElseStatement, declaration, fields);
        return whenTrue is null || whenFalse is null ||
            !IsClosedGenericConcreteMethodExpression(
                branch.Condition, method, declaration.GenericTypeNames, fields)
            ? null
            : (branch.Condition, whenTrue, whenFalse);
    }

    private static FieldDeclaration? GetDefaultResetField(
        StatementBase statement,
        ClassDeclaration declaration,
        IReadOnlyCollection<FieldDeclaration> fields)
    {
        if (statement is BlockStatement { Statements: [var onlyStatement] })
        {
            statement = onlyStatement;
        }

        if (statement is not ExpressionStatement
            {
                Expression: AssignmentExpression
                {
                    Operator: "=",
                    Target: IdentifierExpression { TargetField: { } targetField },
                    Value: LiteralExpression { SourceText: "default" },
                },
            } || !fields.Contains(targetField.Declaration) ||
            !ContainsGenericParameter(
                targetField.Declaration.Type, declaration.GenericTypeNames))
        {
            return null;
        }

        return targetField.Declaration;
    }

    private static (
        ExpressionBase Condition,
        IReadOnlyList<(FieldDeclaration Field, int? ParameterIndex)> WhenTrue,
        IReadOnlyList<(FieldDeclaration Field, int? ParameterIndex)> WhenFalse)?
        GetClosedGenericConditionalFieldParameterAssignments(
            FunctionDeclaration method,
            ClassDeclaration declaration,
            IReadOnlyCollection<FieldDeclaration> fields)
    {
        if (method.Parameters.Count < 2 ||
            method.Parameters[0].ParameterType is not BoolType ||
            method.ReturnType is not VoidType ||
            method.Body is not [IfStatement branch] ||
            !IsClosedGenericConcreteMethodExpression(
                branch.Condition, method, declaration.GenericTypeNames, fields))
        {
            return null;
        }

        var whenTrue = GetGenericFieldParameterAssignments(
            branch.ThenStatement, method, declaration, fields);
        var whenFalse = branch.ElseStatement is null
            ? Array.Empty<(FieldDeclaration Field, int? ParameterIndex)>()
            : GetGenericFieldParameterAssignments(
                branch.ElseStatement, method, declaration, fields);
        return whenTrue is not { Count: > 0 } || whenFalse is null
            ? null
            : (branch.Condition, whenTrue, whenFalse);
    }

    private static IReadOnlyList<(FieldDeclaration Field, int? ParameterIndex)>?
        GetGenericFieldParameterAssignments(
            StatementBase statement,
            FunctionDeclaration method,
            ClassDeclaration declaration,
            IReadOnlyCollection<FieldDeclaration> fields)
    {
        var statements = statement is BlockStatement block
            ? block.Statements
            : new[] { statement };
        if (statements.Count == 0)
        {
            return null;
        }

        var assignments = new List<(FieldDeclaration Field, int? ParameterIndex)>();
        foreach (var item in statements)
        {
            if (item is not ExpressionStatement
                {
                    Expression: AssignmentExpression
                    {
                        Operator: "=",
                        Target: IdentifierExpression { TargetField: { } targetField },
                        Value: { } value,
                    },
                } || !fields.Contains(targetField.Declaration) ||
                !ContainsGenericParameter(
                    targetField.Declaration.Type, declaration.GenericTypeNames))
            {
                return null;
            }

            int? parameterIndex;
            if (value is LiteralExpression { SourceText: "default" })
            {
                parameterIndex = null;
            }
            else if (value is IdentifierExpression { TargetLocal: { } targetLocal })
            {
                parameterIndex = method.Parameters
                    .Select((parameter, index) => (Parameter: parameter, Index: index))
                    .Where(candidate => candidate.Parameter.Name == targetLocal.Name &&
                        IsGenericTypePattern(
                            candidate.Parameter.ParameterType, declaration.GenericTypeNames) &&
                        IsSameGenericTypePattern(
                            targetField.Declaration.Type, candidate.Parameter.ParameterType))
                    .Select(candidate => (int?)candidate.Index)
                    .FirstOrDefault();
                if (parameterIndex is null)
                {
                    return null;
                }
            }
            else
            {
                return null;
            }

            assignments.Add((targetField.Declaration, parameterIndex));
        }

        return assignments;
    }

    private static (
        ExpressionBase Condition,
        IReadOnlyList<(FieldDeclaration Field, AssignmentExpression Assignment)> WhenTrue,
        IReadOnlyList<(FieldDeclaration Field, AssignmentExpression Assignment)> WhenFalse)?
        GetClosedGenericConditionalConcreteFieldAssignments(
            FunctionDeclaration method,
            ClassDeclaration declaration,
            IReadOnlyCollection<FieldDeclaration> fields)
    {
        if (method.Parameters.Count == 0 ||
            method.Parameters[0].ParameterType is not BoolType ||
            method.ReturnType is not VoidType ||
            method.Body is not [IfStatement branch] ||
            !IsClosedGenericConcreteMethodExpression(
                branch.Condition, method, declaration.GenericTypeNames, fields))
        {
            return null;
        }

        var whenTrue = GetClosedGenericConcreteFieldAssignments(
            branch.ThenStatement, method, declaration, fields);
        var whenFalse = branch.ElseStatement is null
            ? Array.Empty<(FieldDeclaration Field, AssignmentExpression Assignment)>()
            : GetClosedGenericConcreteFieldAssignments(
                branch.ElseStatement, method, declaration, fields);
        return whenTrue is null || whenFalse is null
            ? null
            : (branch.Condition, whenTrue, whenFalse);
    }

    private static (
        ExpressionBase Condition,
        IReadOnlyList<(FieldDeclaration Field, AssignmentExpression Assignment)> Assignments)?
        GetClosedGenericWhileConcreteFieldAssignments(
            FunctionDeclaration method,
            ClassDeclaration declaration,
            IReadOnlyCollection<FieldDeclaration> fields)
    {
        if (method.IsStatic || method.GenericTypeNames.Length != 0 ||
            method.ReturnType is not VoidType ||
            method.Parameters.Any(parameter =>
                ContainsGenericParameter(
                    parameter.ParameterType, declaration.GenericTypeNames)) ||
            method.Body is not [WhileStatement loop] ||
            !IsClosedGenericConcreteMethodExpression(
                loop.Condition, method, declaration.GenericTypeNames, fields))
        {
            return null;
        }

        var assignments = GetClosedGenericConcreteFieldAssignments(
            loop.Body, method, declaration, fields);
        return assignments is null || assignments.Any(assignment =>
                ContainsGenericParameter(
                    assignment.Field.Type, declaration.GenericTypeNames))
            ? null
            : (loop.Condition, assignments);
    }

    private static (
        IReadOnlyList<(FieldDeclaration Field, AssignmentExpression Assignment)> Assignments,
        ExpressionBase Condition)?
        GetClosedGenericDoWhileConcreteFieldAssignments(
            FunctionDeclaration method,
            ClassDeclaration declaration,
            IReadOnlyCollection<FieldDeclaration> fields)
    {
        if (method.IsStatic || method.GenericTypeNames.Length != 0 ||
            method.ReturnType is not VoidType ||
            method.Parameters.Any(parameter =>
                ContainsGenericParameter(
                    parameter.ParameterType, declaration.GenericTypeNames)) ||
            method.Body is not [DoWhileStatement loop] ||
            !IsClosedGenericConcreteMethodExpression(
                loop.Condition, method, declaration.GenericTypeNames, fields))
        {
            return null;
        }

        var assignments = GetClosedGenericConcreteFieldAssignments(
            loop.Body, method, declaration, fields);
        return assignments is null || assignments.Any(assignment =>
                ContainsGenericParameter(
                    assignment.Field.Type, declaration.GenericTypeNames))
            ? null
            : (assignments, loop.Condition);
    }

    private static (
        AssignmentExpression Initializer,
        ExpressionBase Condition,
        AssignmentExpression Iterator,
        IReadOnlyList<(FieldDeclaration Field, AssignmentExpression Assignment)> Assignments)?
        GetClosedGenericForConcreteFieldAssignments(
            FunctionDeclaration method,
            ClassDeclaration declaration,
            IReadOnlyCollection<FieldDeclaration> fields)
    {
        if (method.IsStatic || method.GenericTypeNames.Length != 0 ||
            method.ReturnType is not VoidType ||
            method.Parameters.Any(parameter =>
                ContainsGenericParameter(
                    parameter.ParameterType, declaration.GenericTypeNames)) ||
            method.Body is not [ForStatement
            {
                DeclarationInitializer: null,
                InitializerExpressions: [AssignmentExpression initializer],
                Condition: { } condition,
                Iterators: [AssignmentExpression iterator],
            } loop])
        {
            return null;
        }

        bool IsConcreteFieldAssignment(AssignmentExpression assignment) =>
            assignment.Operator is "=" or "+=" or "-=" or "*=" or "/=" or "%=" &&
            assignment.OperatorSymbol is null &&
            assignment.Target is IdentifierExpression { TargetField: { } targetField } &&
            fields.Contains(targetField.Declaration) &&
            !ContainsGenericParameter(
                targetField.Declaration.Type, declaration.GenericTypeNames) &&
            IsClosedGenericConcreteMethodExpression(
                assignment.Value, method, declaration.GenericTypeNames, fields);

        if (!IsClosedGenericConcreteMethodExpression(
                condition, method, declaration.GenericTypeNames, fields) ||
            !IsConcreteFieldAssignment(initializer) ||
            !IsConcreteFieldAssignment(iterator))
        {
            return null;
        }

        var assignments = GetClosedGenericConcreteFieldAssignments(
            loop.Body, method, declaration, fields);
        return assignments is null || assignments.Any(assignment =>
                ContainsGenericParameter(
                    assignment.Field.Type, declaration.GenericTypeNames))
            ? null
            : (initializer, condition, iterator, assignments);
    }

    private static IReadOnlyList<(FieldDeclaration Field, AssignmentExpression Assignment)>?
        GetClosedGenericConcreteFieldAssignments(
            StatementBase statement,
            FunctionDeclaration method,
            ClassDeclaration declaration,
            IReadOnlyCollection<FieldDeclaration> fields)
    {
        var statements = statement is BlockStatement block
            ? block.Statements
            : new[] { statement };
        if (statements.Count == 0)
        {
            return null;
        }

        var assignments = new List<(FieldDeclaration Field, AssignmentExpression Assignment)>();
        foreach (var item in statements)
        {
            if (item is not ExpressionStatement
                {
                    Expression: AssignmentExpression
                    {
                        Operator: "=" or "+=" or "-=" or "*=" or "/=" or "%=",
                        Target: IdentifierExpression { TargetField: { } targetField },
                    } assignment,
            } || assignment.OperatorSymbol is not null ||
                !fields.Contains(targetField.Declaration))
            {
                return null;
            }

            if (ContainsGenericParameter(
                    targetField.Declaration.Type, declaration.GenericTypeNames))
            {
                var matchesGenericField = assignment.Value is
                    LiteralExpression { SourceText: "default" } ||
                    assignment.Value is IdentifierExpression
                    {
                        TargetLocal: { } targetLocal,
                    } && method.Parameters.Any(parameter =>
                        parameter.Name == targetLocal.Name &&
                        IsGenericTypePattern(
                            parameter.ParameterType, declaration.GenericTypeNames) &&
                        IsSameGenericTypePattern(
                            targetField.Declaration.Type, parameter.ParameterType));
                if (!matchesGenericField)
                {
                    return null;
                }
            }
            else if (!IsClosedGenericConcreteMethodExpression(
                    assignment.Value, method, declaration.GenericTypeNames, fields))
            {
                return null;
            }

            assignments.Add((targetField.Declaration, assignment));
        }

        return assignments;
    }

    private static string ToCClosedGenericMixedFieldAssignmentValue(
        FieldDeclaration field,
        AssignmentExpression assignment,
        FunctionDeclaration method,
        IReadOnlyDictionary<string, TypeBase> classArguments,
        IReadOnlyDictionary<string, string> parameterNames,
        string closedTypeIdentifier,
        string moduleName,
        string instanceExpression)
    {
        if (ContainsGenericParameter(field.Type, method.ParentClassDeclaration!.GenericTypeNames))
        {
            if (assignment.Value is LiteralExpression { SourceText: "default" })
            {
                return ToCDefaultValue(GenericTypeSubstitution.Substitute(
                    field.Type, classArguments));
            }

            if (assignment.Value is IdentifierExpression
                {
                    TargetLocal: { } targetLocal,
                } && parameterNames.TryGetValue(targetLocal.Name, out var parameterName))
            {
                return parameterName;
            }

            throw new InternalCompilerException(
                $"Unsupported value for generic field '{field.Name}' in a closed struct method.");
        }

        return ToCClosedGenericConcreteMethodExpression(
            assignment.Value,
            parameterNames,
            method,
            closedTypeIdentifier,
            moduleName,
            instanceExpression);
    }

    private static bool IsClosedGenericDefaultFieldSetter(
        FunctionDeclaration method,
        ClassDeclaration declaration,
        IReadOnlyCollection<FieldDeclaration> fields) =>
        GetClosedGenericDefaultFieldSetter(method, declaration, fields) is not null;

    private static bool IsClosedGenericConstantAccessor(
        PropertyAccessorDeclaration accessor,
        ClassDeclaration declaration) =>
        accessor.Name == "get" && accessor.Const && accessor.Parameters.Count == 0 &&
        accessor.Body is [ReturnStatement { Expression: LiteralExpression }] &&
        !ContainsGenericParameter(
            accessor.ParentPropertyDeclaration.Type, declaration.GenericTypeNames);

    private static bool IsSupportedClosedGenericFieldInitializer(
        FieldDeclaration field,
        ClassDeclaration declaration) =>
        IsSupportedGenericNullFieldInitializer(field, declaration) ||
        IsSupportedClosedGenericObjectCreationFieldInitializer(field, declaration) ||
        IsSupportedClosedGenericArrayFieldInitializer(field, declaration) ||
        field.Initializer is LiteralExpression { SourceText: "default" } &&
            ContainsGenericParameter(field.Type, declaration.GenericTypeNames) ||
        field.Initializer is { } initializer &&
            IsClosedGenericFieldInitializerExpression(initializer, field, declaration) &&
            !ContainsGenericParameter(field.Type, declaration.GenericTypeNames);

    private static bool IsSupportedClosedGenericArrayFieldInitializer(
        FieldDeclaration field,
        ClassDeclaration declaration) =>
        declaration.ClassType is ClassType.Class or ClassType.Struct &&
        field.Type is ArrayType fieldArray &&
        (!ContainsGenericParameter(fieldArray.ElementType, declaration.GenericTypeNames) ||
            IsGenericTypePattern(fieldArray.ElementType, declaration.GenericTypeNames)) &&
        field.Initializer is ArrayCreationExpression
        {
            ElementType: var elementType,
            Length: { } length,
        } &&
        IsSameGenericTypePattern(fieldArray.ElementType, elementType) &&
        length.InferredType is UIntType &&
        (length is LiteralExpression { SourceText: var literal } &&
            literal.EndsWith('u') ||
            IsClosedGenericFieldInitializerExpression(length, field, declaration));

    private static bool IsClosedGenericFieldInitializerExpression(
        ExpressionBase expression,
        FieldDeclaration field,
        ClassDeclaration declaration)
    {
        var fields = declaration.MemberDeclarations.Declarations
            .OfType<FieldDeclaration>().ToArray();
        var currentIndex = Array.IndexOf(fields, field);
        if (currentIndex < 0)
        {
            return false;
        }

        bool IsSupported(ExpressionBase candidate) => candidate switch
        {
            IdentifierExpression { TargetField: { } target } =>
                !target.Declaration.IsStatic &&
                ReferenceEquals(target.Declaration.ParentClassDeclaration, declaration) &&
                Array.IndexOf(fields, target.Declaration) is var targetIndex &&
                targetIndex >= 0 && targetIndex < currentIndex &&
                !ContainsGenericParameter(
                    target.Declaration.Type, declaration.GenericTypeNames),
            LiteralExpression => true,
            UnaryExpression
            {
                OperatorSymbol: null,
                Postfix: false,
                Operator: "+" or "-" or "~" or "!",
            } unary => IsSupported(unary.Operand),
            BinaryExpression
            {
                OperatorSymbol: null,
                Operator: "+" or "-" or "*" or "/" or "%" or
                    "<<" or ">>" or ">>>" or "&" or "|" or "^" or
                    "==" or "!=" or "<" or "<=" or ">" or ">=" or "&&" or "||",
            } binary => IsSupported(binary.Left) && IsSupported(binary.Right),
            InvocationExpression invocation =>
                IsSupportedClosedGenericFieldInitializerInvocation(
                    invocation, declaration, fields, currentIndex) &&
                invocation.Arguments.All(IsSupported),
            _ => false,
        };

        return IsSupported(expression);
    }

    private static bool IsSupportedClosedGenericFieldInitializerInvocation(
        InvocationExpression invocation,
        ClassDeclaration declaration,
        IReadOnlyList<FieldDeclaration> fields,
        int currentIndex)
    {
        if (declaration.ClassType != ClassType.Class ||
            invocation.TargetSymbol is not { Declaration: { } method } targetSymbol ||
            method.GenericTypeNames.Length != 0 ||
            method.IsStatic || method.IsExtension ||
            method.ParentClassDeclaration is not { ClassType: ClassType.Class or ClassType.Struct } ||
            ContainsGenericParameter(targetSymbol.ReturnType, declaration.GenericTypeNames) ||
            targetSymbol.ParameterTypes.Any(parameter =>
                ContainsGenericParameter(parameter, declaration.GenericTypeNames)) ||
            invocation.Receiver is not IdentifierExpression
            {
                TargetField: { } receiverField,
            } ||
            invocation.Arguments.Any(argument =>
                argument.InferredType is not { } argumentType ||
                ContainsGenericParameter(argumentType, declaration.GenericTypeNames)) ||
            receiverField.Declaration.IsStatic ||
            !ReferenceEquals(receiverField.Declaration.ParentClassDeclaration, declaration) ||
            !fields.Take(currentIndex).Any(candidate =>
                ReferenceEquals(candidate, receiverField.Declaration)) ||
            ContainsGenericParameter(receiverField.Declaration.Type, declaration.GenericTypeNames))
        {
            return false;
        }

        return true;
    }

    private static bool IsSupportedClosedGenericObjectCreationFieldInitializer(
        FieldDeclaration field,
        ClassDeclaration declaration)
    {
        if (declaration.ClassType != ClassType.Class ||
            field.Type is not NamedType
                { ClassType: ClassType.Class or ClassType.Struct } ||
            field.Initializer is not ObjectCreationExpression
            {
                RequestedType: NamedType
                {
                    ClassType: ClassType.Class or ClassType.Struct,
                } requestedType,
                Constructor: { } constructor,
            } objectCreation ||
            !IsSupportedClosedGenericFieldTypePattern(field.Type, declaration) ||
            !IsSupportedClosedGenericFieldTypePattern(requestedType, declaration) ||
            objectCreation.Arguments.Any(argument =>
                argument.InferredType is not { } argumentType ||
                !IsSupportedClosedGenericFieldTypePattern(argumentType, declaration) ||
                !IsClosedGenericFieldInitializerExpression(argument, field, declaration)) ||
            constructor.ParameterTypes.Count != objectCreation.Arguments.Count ||
            constructor.Declaration is not { } constructorDeclaration ||
            constructorDeclaration.ParentClassDeclaration is not { } targetDeclaration ||
            targetDeclaration.GenericTypeNames.Length > 0 &&
                requestedType.ConstructedIdentity is null &&
                    !IsGenericTypePattern(requestedType, declaration.GenericTypeNames) ||
            constructor.ParameterTypes.Any(parameter =>
                !IsSupportedClosedGenericFieldTypePattern(parameter, declaration)))
        {
            return false;
        }

        return true;
    }

    private static bool IsSupportedClosedGenericFieldTypePattern(
        TypeBase type,
        ClassDeclaration declaration) =>
        !ContainsGenericParameter(type, declaration.GenericTypeNames) ||
        IsGenericTypePattern(type, declaration.GenericTypeNames);

    private static bool IsSupportedGenericNullFieldInitializer(
        FieldDeclaration field,
        ClassDeclaration declaration) =>
        field.Initializer is LiteralExpression { SourceText: "null" } &&
        (UnwrapConstType(field.Type) switch
        {
            ArrayType => true,
            NamedType { ClassType: ClassType.Class or ClassType.Interface } => true,
            GenericType parameter => declaration.GenericConstraints.Any(constraint =>
                constraint.ParameterName == parameter.Name &&
                (constraint.Kind == GenericConstraintKind.ReferenceType ||
                    constraint.Types.Any(required => UnwrapConstType(required) is NamedType
                    {
                        ClassType: ClassType.Class,
                    }))),
            _ => false,
        });

    private static TypeBase UnwrapConstType(TypeBase type) =>
        type is ConstType constant ? constant.UnderlyingType : type;

    private static bool IsClosedGenericConstantExpression(ExpressionBase expression) =>
        expression switch
        {
            LiteralExpression => true,
            UnaryExpression
            {
                OperatorSymbol: null,
                Postfix: false,
                Operator: "+" or "-" or "~" or "!",
            } unary => IsClosedGenericConstantExpression(unary.Operand),
            BinaryExpression
            {
                OperatorSymbol: null,
                Operator: "+" or "-" or "*" or "/" or "%" or
                    "<<" or ">>" or ">>>" or "&" or "|" or "^" or
                    "==" or "!=" or "<" or "<=" or ">" or ">=" or "&&" or "||",
            } binary => IsClosedGenericConstantExpression(binary.Left) &&
                IsClosedGenericConstantExpression(binary.Right),
            _ => false,
        };

    private static bool IsSupportedClosedGenericProperty(
        PropertyDeclaration property,
        ClassDeclaration declaration,
        IReadOnlyCollection<FieldDeclaration> fields)
    {
        if (property.IsStatic || property.PropertyAccessorDeclarations.Count is < 1 or > 2 ||
            property.PropertyAccessorDeclarations.Select(accessor => accessor.Name)
                .Distinct().Count() != property.PropertyAccessorDeclarations.Count)
        {
            return false;
        }

        if (IsGenericTypePattern(property.Type, declaration.GenericTypeNames))
        {
            return property.PropertyAccessorDeclarations.All(accessor =>
                    IsReferenceFieldPropertyAccessor(property, accessor, fields) ||
                    IsClosedGenericOwnerPropertyAccessor(property, accessor, declaration)) ||
                property.PropertyAccessorDeclarations.Count == 1 &&
                (IsClosedGenericGenericResultExpressionAccessor(
                    property.PropertyAccessorDeclarations.Single(), declaration, fields) ||
                 IsClosedGenericGenericFieldLocalAccessor(
                    property.PropertyAccessorDeclarations.Single(), declaration, fields) ||
                 IsClosedGenericArrayPropertyGetter(
                    property.PropertyAccessorDeclarations.Single(), declaration, fields));
        }

        return property.PropertyAccessorDeclarations.All(accessor =>
                IsReferenceFieldPropertyAccessor(property, accessor, fields) ||
                IsClosedGenericOwnerPropertyAccessor(property, accessor, declaration)) ||
            property.PropertyAccessorDeclarations.Count == 1 &&
                (IsClosedGenericConstantAccessor(
                        property.PropertyAccessorDeclarations.Single(), declaration) ||
                    IsClosedGenericConcreteExpressionAccessor(
                        property.PropertyAccessorDeclarations.Single(),
                        declaration,
                        fields) ||
                    IsClosedGenericArrayLengthAccessor(
                        property.PropertyAccessorDeclarations.Single(), declaration, fields) ||
                    IsClosedGenericConcreteExpressionSetter(
                        property.PropertyAccessorDeclarations.Single(),
                        declaration,
                        fields));
    }

    private static bool IsClosedGenericConcreteExpressionAccessor(
        PropertyAccessorDeclaration accessor,
        ClassDeclaration declaration,
        IReadOnlyCollection<FieldDeclaration> fields) =>
        accessor.Name == "get" && accessor.Const &&
        accessor.BodyFunction is { } function &&
        IsClosedGenericConcreteExpressionMethod(function, declaration, fields);

    private static bool IsClosedGenericConcreteExpressionSetter(
        PropertyAccessorDeclaration accessor,
        ClassDeclaration declaration,
        IReadOnlyCollection<FieldDeclaration> fields) =>
        accessor.Name == "set" &&
        !ContainsGenericParameter(
            accessor.ParentPropertyDeclaration.Type, declaration.GenericTypeNames) &&
        accessor.BodyFunction is { Parameters.Count: 1 } function &&
        !ContainsGenericParameter(
            function.Parameters[0].ParameterType, declaration.GenericTypeNames) &&
        IsSameGenericTypePattern(
            function.Parameters[0].ParameterType, accessor.ParentPropertyDeclaration.Type) &&
        function.Body is
            [ExpressionStatement
            {
                Expression: AssignmentExpression
                {
                    Operator: "=",
                    Target: IdentifierExpression { TargetField: { } targetField },
                    Value: var value,
                },
            }] &&
        fields.Contains(targetField.Declaration) &&
        !ContainsGenericParameter(targetField.Declaration.Type, declaration.GenericTypeNames) &&
        IsSameGenericTypePattern(
            targetField.Declaration.Type, accessor.ParentPropertyDeclaration.Type) &&
        IsClosedGenericConcreteMethodExpression(
            value, function, declaration.GenericTypeNames, fields);

    private static bool IsClosedGenericGenericResultExpressionAccessor(
        PropertyAccessorDeclaration accessor,
        ClassDeclaration declaration,
        IReadOnlyCollection<FieldDeclaration> fields) =>
        accessor.Name == "get" && accessor.Const &&
        IsGenericTypePattern(
            accessor.ParentPropertyDeclaration.Type, declaration.GenericTypeNames) &&
        accessor.BodyFunction is { } function &&
        function.Body is
            [ReturnStatement { Expression: ConditionalExpression conditional }] &&
        IsClosedGenericConcreteMethodExpression(
            conditional.Condition, function, declaration.GenericTypeNames, fields) &&
        IsClosedGenericGenericResultArmOrDefault(
            conditional.WhenTrue, accessor.ParentPropertyDeclaration.Type, fields) &&
        IsClosedGenericGenericResultArmOrDefault(
            conditional.WhenFalse, accessor.ParentPropertyDeclaration.Type, fields) &&
        (IsClosedGenericGenericResultArm(
                conditional.WhenTrue, accessor.ParentPropertyDeclaration.Type, fields) ||
            IsClosedGenericGenericResultArm(
                conditional.WhenFalse, accessor.ParentPropertyDeclaration.Type, fields));

    private static bool IsClosedGenericGenericFieldLocalAccessor(
        PropertyAccessorDeclaration accessor,
        ClassDeclaration declaration,
        IReadOnlyCollection<FieldDeclaration> fields)
    {
        if (accessor.Name != "get" || !accessor.Const ||
            !IsGenericTypePattern(
                accessor.ParentPropertyDeclaration.Type, declaration.GenericTypeNames) ||
            accessor.BodyFunction is not { } function ||
            function.Body is not
                [LocalVariableDeclarationStatement
                    { Declarators: [var declarator] } local,
                 ReturnStatement { Expression: IdentifierExpression returned }] ||
            declarator.Initializer is not { } initializer ||
            returned.TargetLocal is not { } returnedLocal ||
            returnedLocal.Name != declarator.Name ||
            !IsSameGenericTypePattern(
                declarator.Type ?? local.DeclaredType,
                accessor.ParentPropertyDeclaration.Type))
        {
            return false;
        }

        bool IsMatchingField(ExpressionBase expression) =>
            GetReferenceFieldDeclaration(expression) is { } field &&
            fields.Contains(field) && !field.IsStatic &&
            IsSameGenericTypePattern(
                field.Type, accessor.ParentPropertyDeclaration.Type);
        if (initializer is ConditionalExpression conditional)
        {
            return IsClosedGenericConcreteMethodExpression(
                    conditional.Condition, function, declaration.GenericTypeNames, fields) &&
                IsClosedGenericGenericResultArmOrDefault(
                    conditional.WhenTrue, accessor.ParentPropertyDeclaration.Type, fields) &&
                IsClosedGenericGenericResultArmOrDefault(
                    conditional.WhenFalse, accessor.ParentPropertyDeclaration.Type, fields) &&
                (IsClosedGenericGenericResultArm(
                        conditional.WhenTrue, accessor.ParentPropertyDeclaration.Type, fields) ||
                    IsClosedGenericGenericResultArm(
                        conditional.WhenFalse, accessor.ParentPropertyDeclaration.Type, fields));
        }

        return IsMatchingField(initializer);
    }

    private static ExpressionBase GetClosedGenericGenericFieldLocalInitializer(
        PropertyAccessorDeclaration accessor)
    {
        var function = accessor.BodyFunction ?? throw new InternalCompilerException(
            $"Supported generic-result property '{accessor.FullName}' has no body function.");
        return function.Body is
            [LocalVariableDeclarationStatement
                { Declarators: [{ Initializer: { } initializer }] },
             ReturnStatement]
            ? initializer
            : throw new InternalCompilerException(
                $"Supported generic-result property '{accessor.FullName}' has no local field copy.");
    }

    private static bool IsClosedGenericGenericResultExpressionMethod(
        FunctionDeclaration method,
        ClassDeclaration declaration,
        IReadOnlyCollection<FieldDeclaration> fields) =>
        method.GenericTypeNames.Length == 0 &&
        method.Parameters.All(parameter =>
            !ContainsGenericParameter(
                parameter.ParameterType, declaration.GenericTypeNames)) &&
        IsGenericTypePattern(method.ReturnType, declaration.GenericTypeNames) &&
        TryGetClosedGenericGenericResultParts(
            method, out var condition, out var whenTrue, out var whenFalse) &&
        IsClosedGenericConcreteMethodExpression(
            condition, method, declaration.GenericTypeNames, fields) &&
        IsClosedGenericGenericResultArmOrDefault(
            whenTrue, method.ReturnType, fields) &&
        IsClosedGenericGenericResultArmOrDefault(
            whenFalse, method.ReturnType, fields) &&
        (IsClosedGenericGenericResultArm(
                whenTrue, method.ReturnType, fields) ||
            IsClosedGenericGenericResultArm(
                whenFalse, method.ReturnType, fields));

    private static bool TryGetClosedGenericGenericResultParts(
        FunctionDeclaration method,
        out ExpressionBase condition,
        out ExpressionBase whenTrue,
        out ExpressionBase whenFalse)
    {
        if (method.Body is [ReturnStatement
            { Expression: ConditionalExpression conditional }])
        {
            condition = conditional.Condition;
            whenTrue = conditional.WhenTrue;
            whenFalse = conditional.WhenFalse;
            return true;
        }

        if (method.Body is [IfStatement branch] &&
            branch.ElseStatement is { } elseStatement &&
            GetSingleReturnedExpression(branch.ThenStatement) is { } trueExpression &&
            GetSingleReturnedExpression(elseStatement) is { } falseExpression)
        {
            condition = branch.Condition;
            whenTrue = trueExpression;
            whenFalse = falseExpression;
            return true;
        }

        condition = null!;
        whenTrue = null!;
        whenFalse = null!;
        return false;
    }

    private static bool TryGetClosedGenericConcreteResultParts(
        FunctionDeclaration method,
        out ExpressionBase condition,
        out ExpressionBase whenTrue,
        out ExpressionBase whenFalse)
    {
        if (method.Body is [IfStatement branch] &&
            branch.ElseStatement is { } elseStatement &&
            GetSingleReturnedExpression(branch.ThenStatement) is { } trueExpression &&
            GetSingleReturnedExpression(elseStatement) is { } falseExpression)
        {
            condition = branch.Condition;
            whenTrue = trueExpression;
            whenFalse = falseExpression;
            return true;
        }

        condition = null!;
        whenTrue = null!;
        whenFalse = null!;
        return false;
    }

    private static ExpressionBase? GetSingleReturnedExpression(StatementBase statement) =>
        statement switch
        {
            ReturnStatement { Expression: { } expression } => expression,
            BlockStatement { Statements: [ReturnStatement { Expression: { } expression }] } =>
                expression,
            _ => null,
        };

    private static bool IsClosedGenericDefaultResultMethod(
        FunctionDeclaration method,
        ClassDeclaration declaration) =>
        method.GenericTypeNames.Length == 0 &&
        method.ReturnType is GenericType genericReturnType &&
        declaration.GenericTypeNames.Contains(genericReturnType.Name) &&
        method.Body is
            [ReturnStatement { Expression: LiteralExpression { SourceText: "default" } }];

    private static bool IsClosedGenericGenericResultArmOrDefault(
        ExpressionBase expression,
        TypeBase propertyType,
        IReadOnlyCollection<FieldDeclaration> fields) =>
        expression is LiteralExpression { SourceText: "default" } ||
        IsClosedGenericGenericResultArm(expression, propertyType, fields);

    private static bool IsClosedGenericGenericResultArm(
        ExpressionBase expression,
        TypeBase propertyType,
        IReadOnlyCollection<FieldDeclaration> fields) =>
        GetReferenceFieldDeclaration(expression) is { } targetField &&
        fields.Contains(targetField) &&
        IsSameGenericTypePattern(targetField.Type, propertyType);

    private static string ToCClosedGenericGenericResultArm(
        ExpressionBase expression,
        TypeBase closedResultType,
        string instanceExpression) => expression switch
    {
        LiteralExpression { SourceText: "default" } => ToCDefaultValue(closedResultType),
        _ when GetReferenceFieldDeclaration(expression) is { } targetField =>
            $"({instanceExpression})->{targetField.Name}",
        _ => throw new InternalCompilerException(
            $"Expression '{expression.GetType().Name}' is not supported in a closed generic result getter."),
    };

    private static string ToCClosedGenericGenericResultExpression(
        ExpressionBase expression,
        TypeBase closedResultType,
        FunctionDeclaration function,
        string closedTypeIdentifier,
        string moduleName,
        string instanceExpression) => expression is ConditionalExpression conditional
        ? "(" + ToCClosedGenericConcreteMethodExpression(
            conditional.Condition,
            new Dictionary<string, string>(StringComparer.Ordinal),
            function,
            closedTypeIdentifier,
            moduleName,
            instanceExpression) + " ? " +
            ToCClosedGenericGenericResultArm(
                conditional.WhenTrue, closedResultType, instanceExpression) + " : " +
            ToCClosedGenericGenericResultArm(
                conditional.WhenFalse, closedResultType, instanceExpression) + ")"
        : ToCClosedGenericGenericResultArm(
            expression, closedResultType, instanceExpression);

    private static bool HasSupportedClosedGenericInterfaceTables(ClassDeclaration declaration)
    {
        var fields = declaration.MemberDeclarations.Declarations
            .OfType<FieldDeclaration>()
            .ToArray();
        return declaration.InterfaceDispatchTables.All(table => table.Slots
            .Where(slot => slot.ContractFunction is not
                { GenericTypeNames.Length: > 0 })
            .All(slot => slot.ImplementationFunction is { } implementation
                ? table.Interface.GenericTypeNames.Length != 0
                    ? IsClosedGenericNoOpMethod(implementation) ||
                        IsReferenceFieldGetter(implementation, fields) ||
                        GetClosedGenericSimpleFieldAssignments(
                            implementation, declaration, fields) is not null ||
                        IsGenericFieldSetter(implementation, fields) ||
                        IsClosedGenericDefaultFieldSetter(implementation, declaration, fields) ||
                        GetClosedGenericDefaultFieldAssignments(
                            implementation, declaration, fields) is not null ||
                        GetClosedGenericConditionalDefaultFieldReset(
                            implementation, declaration, fields) is not null ||
                        GetClosedGenericConditionalFieldParameterAssignments(
                            implementation, declaration, fields) is not null ||
                        GetClosedGenericConditionalConcreteFieldAssignments(
                            implementation, declaration, fields) is not null ||
                        GetClosedGenericWhileConcreteFieldAssignments(
                            implementation, declaration, fields) is not null ||
                        GetClosedGenericDoWhileConcreteFieldAssignments(
                            implementation, declaration, fields) is not null ||
                        GetClosedGenericForConcreteFieldAssignments(
                            implementation, declaration, fields) is not null ||
                        IsClosedGenericFieldGetter(implementation, declaration, fields) ||
                        IsClosedGenericFieldSetter(implementation, declaration, fields) ||
                        IsClosedGenericFieldConstantSetter(implementation, declaration, fields) ||
                        GetClosedGenericDefaultFieldAssignments(
                            implementation, declaration, fields) is not null ||
                        IsClosedGenericConcreteFieldAssignmentMethod(
                            implementation, declaration, fields) ||
                        GetClosedGenericMixedFieldAssignments(
                            implementation, declaration, fields) is not null ||
                        IsClosedGenericConcreteExpressionMethod(
                            implementation, declaration, fields) ||
                        IsClosedGenericDefaultResultMethod(
                            implementation, declaration) ||
                        IsClosedGenericInterfaceOwnerMethod(
                            implementation, declaration) ||
                        IsClosedGenericGenericResultExpressionMethod(
                            implementation, declaration, fields) ||
                        GetClosedGenericInterfaceReturnedParameterIndex(
                            implementation, declaration) is not null ||
                        IsClosedGenericInterfaceConstantMethod(implementation, declaration)
                    : IsClosedGenericNoOpMethod(implementation) ||
                        IsClosedGenericConstantMethod(implementation, declaration) ||
                        GetClosedGenericSimpleFieldAssignments(
                            implementation, declaration, fields) is not null ||
                        IsClosedGenericOwnerMethod(implementation, declaration) ||
                        IsClosedGenericFieldConstantSetter(implementation, declaration, fields) ||
                        IsClosedGenericDefaultFieldSetter(implementation, declaration, fields) ||
                        GetClosedGenericInterfaceReturnedParameterIndex(
                            implementation, declaration) is not null ||
                        IsReferenceFieldGetter(implementation, fields)
                : slot.ImplementationAccessor is { } accessor &&
                    (IsClosedGenericConstantAccessor(accessor, declaration) ||
                     IsClosedGenericGenericFieldLocalAccessor(
                         accessor, declaration, fields) ||
                     IsClosedGenericGenericResultExpressionAccessor(
                         accessor, declaration, fields) ||
                     IsClosedGenericConcreteExpressionSetter(
                         accessor, declaration, fields) ||
                     IsClosedGenericArrayPropertyGetter(
                         accessor, declaration, fields) ||
                     IsClosedGenericConcreteExpressionAccessor(
                         accessor, declaration, fields) ||
                     IsClosedGenericOwnerConstructedResultAccessor(
                         accessor, declaration) ||
                     IsReferenceFieldPropertyAccessor(
                         accessor.ParentPropertyDeclaration, accessor, fields))));
    }

    private static bool IsClosedGenericInterfaceOwnerMethod(
        FunctionDeclaration method,
        ClassDeclaration declaration)
    {
        if (!IsClosedGenericOwnerMethod(method, declaration))
        {
            return false;
        }

        var hasOwnerTypeParameters = ContainsGenericParameter(
                method.ReturnType, declaration.GenericTypeNames) ||
            method.Parameters.Any(parameter => ContainsGenericParameter(
                parameter.ParameterType, declaration.GenericTypeNames)) ||
            EnumerateBodyExpressions(method.Body ?? []).Any(expression =>
                expression.InferredType is { } type &&
                ContainsGenericParameter(type, declaration.GenericTypeNames)) ||
            EnumerateBodyStatements(method.Body ?? [])
                .OfType<LocalVariableDeclarationStatement>()
                .SelectMany(statement => statement.Declarators)
                .Any(declarator => declarator.Type is { } type &&
                    ContainsGenericParameter(type, declaration.GenericTypeNames));
        if (!hasOwnerTypeParameters)
        {
            return true;
        }

        if (method.Name == "GetEnumerator" && method.Parameters.Count == 0 &&
            method.Body is [ReturnStatement { Expression: not null }])
        {
            return true;
        }

        if (method.Name == "MoveNext" && method.ReturnType is BoolType &&
            method.Parameters.Count == 0)
        {
            return true;
        }

        return method.Name == "get" && method.Parameters.Count == 0 &&
            method.Body is [ReturnStatement { Expression: ObjectCreationExpression }] &&
            declaration.MemberDeclarations.Declarations
                .OfType<PropertyDeclaration>()
                .Any(property => property.Name == "Current" &&
                    property.PropertyAccessorDeclarations.Any(accessor =>
                        ReferenceEquals(accessor.BodyFunction, method)));
    }

    private static bool IsClosedGenericOwnerConstructedResultAccessor(
        PropertyAccessorDeclaration accessor,
        ClassDeclaration declaration) =>
        accessor.Name == "get" &&
        accessor.ParentPropertyDeclaration.Name == "Current" &&
        ContainsGenericParameter(
            accessor.ParentPropertyDeclaration.Type, declaration.GenericTypeNames) &&
        accessor.BodyFunction?.Body is
            [ReturnStatement { Expression: ObjectCreationExpression creation }] &&
        ContainsGenericParameter(creation.RequestedType, declaration.GenericTypeNames);

    private static bool IsClosedGenericArrayPropertyGetter(
        PropertyAccessorDeclaration accessor,
        ClassDeclaration declaration,
        IReadOnlyCollection<FieldDeclaration> fields)
    {
        if (accessor.Name != "get" || !accessor.Const ||
            accessor.Parameters.Count != 0 ||
            !IsGenericTypePattern(
                accessor.ParentPropertyDeclaration.Type, declaration.GenericTypeNames) ||
            accessor.BodyFunction is not { } function ||
            function.Body is not
                [ReturnStatement
                {
                    Expression: ArrayAccessExpression
                    {
                        Target: IdentifierExpression { TargetField: { } targetField },
                        Indices: [var index],
                    } arrayAccess,
                }] ||
            !fields.Contains(targetField.Declaration) ||
            targetField.Declaration.Type is not ArrayType arrayType ||
            !IsGenericTypePattern(arrayType.ElementType, declaration.GenericTypeNames) ||
            !IsSameGenericTypePattern(
                accessor.ParentPropertyDeclaration.Type, arrayType.ElementType))
        {
            return false;
        }

        return IsClosedGenericConcreteMethodExpression(
            index, function, declaration.GenericTypeNames, fields);
    }

    private static bool IsClosedGenericValueOperator(
        FunctionDeclaration method,
        ClassDeclaration declaration) =>
        (declaration.ClassType is ClassType.Class or ClassType.Struct) &&
        method.OperatorToken is not null &&
        method.Parameters.Count == 1 &&
        IsSameGenericTypePattern(method.ReturnType, method.Parameters[0].ParameterType) &&
        method.Body is
        [ReturnStatement
        {
            Expression: IdentifierExpression { Identifier.Parts: [var parameterName] },
        }] &&
        method.Parameters[0].Name == parameterName;

    private static bool IsClosedGenericMethodOperator(FunctionDeclaration method) =>
        method.OperatorToken is not null &&
        method.GenericTypeNames.Length == 1 &&
        method.Parameters.Count == 1 &&
        method.ReturnType is GenericType returnParameter &&
        returnParameter.Name == method.GenericTypeNames[0] &&
        method.Parameters[0].ParameterType is GenericType operandParameter &&
        operandParameter.Name == returnParameter.Name &&
        method.Body is
        [ReturnStatement
        {
            Expression: IdentifierExpression { Identifier.Parts: [var parameterName] },
        }] &&
        method.Parameters[0].Name == parameterName;

    private static bool IsClosedGenericArrayAllocation(
        FieldDeclaration field,
        ExpressionBase value,
        ConstructorDeclaration constructor,
        ClassDeclaration declaration)
    {
        if (field.Type is not ArrayType fieldArray ||
            ContainsGenericParameter(fieldArray.ElementType, declaration.GenericTypeNames) &&
                !IsGenericTypePattern(fieldArray.ElementType, declaration.GenericTypeNames) ||
            value is not ArrayCreationExpression allocation ||
            !IsSameGenericTypePattern(fieldArray.ElementType, allocation.ElementType) ||
            allocation.Length.InferredType is not UIntType ||
            !IsClosedGenericConcreteMethodExpression(
                allocation.Length,
                constructor,
                declaration.GenericTypeNames,
                Array.Empty<FieldDeclaration>()))
        {
            return false;
        }

        return true;
    }

    private static bool IsClosedGenericArrayConstructor(
        ConstructorDeclaration constructor,
        ClassDeclaration declaration,
        IReadOnlyCollection<FieldDeclaration> fields) =>
        constructor.Parameters.Count == 1 &&
        constructor.Body is
        [ExpressionStatement
        {
            Expression: AssignmentExpression
            {
                Operator: "=",
                Target: IdentifierExpression { TargetField: { } targetField },
                Value: { } value,
            },
        }] &&
        fields.Contains(targetField.Declaration) &&
        IsClosedGenericArrayAllocation(
            targetField.Declaration, value, constructor, declaration);

    private static bool IsClosedGenericOwnerConstructor(
        ConstructorDeclaration constructor,
        ClassDeclaration declaration,
        IReadOnlyCollection<FieldDeclaration> fields)
    {
        if (declaration.GenericTypeNames.Length == 0 ||
            constructor.Body is not { Count: > 0 } body)
        {
            return false;
        }

        var assignedFields = new HashSet<FieldDeclaration>();
        foreach (var statement in body)
        {
            if (statement is not ExpressionStatement
                {
                    Expression: AssignmentExpression
                    {
                        Operator: "=",
                        Target: IdentifierExpression { TargetField: { } targetField },
                        Value: { } value,
                    },
                } ||
                !fields.Contains(targetField.Declaration) ||
                !assignedFields.Add(targetField.Declaration))
            {
                return false;
            }

            var field = targetField.Declaration;
            var matchingParameterAssignment = value is IdentifierExpression parameterIdentifier &&
                constructor.Parameters.SingleOrDefault(parameter =>
                    parameter.Name == parameterIdentifier.Identifier.ToString()) is { } parameter &&
                IsSameGenericTypePattern(field.Type, parameter.ParameterType);
            var genericReferenceNullAssignment =
                value is LiteralExpression { SourceText: "null" } &&
                field.Type is NamedType { ClassType: ClassType.Class } &&
                IsGenericTypePattern(field.Type, declaration.GenericTypeNames);
            if (matchingParameterAssignment || genericReferenceNullAssignment ||
                value is ObjectCreationExpression &&
                    IsClosedGenericObjectCreation(
                        field, value, constructor, declaration, fields) ||
                value is ArrayCreationExpression &&
                    IsClosedGenericArrayAllocation(
                        field, value, constructor, declaration) ||
                value is not ObjectCreationExpression &&
                    value is not ArrayCreationExpression &&
                    IsClosedGenericConcreteMethodExpression(
                        value, constructor, declaration.GenericTypeNames, fields))
            {
                continue;
            }

            return false;
        }

        return assignedFields.Count > 0;
    }

    private static bool IsClosedGenericObjectCreation(
        FieldDeclaration field,
        ExpressionBase value,
        ConstructorDeclaration constructor,
        ClassDeclaration declaration,
        IReadOnlyCollection<FieldDeclaration> fields)
    {
        if (value is not ObjectCreationExpression creation)
        {
            return false;
        }

        var fieldIsArray = ArrayType.TryGetElementType(field.Type, out var fieldElementType);
        var requestedIsArray = ArrayType.TryGetElementType(
            creation.RequestedType, out var requestedElementType);
        if (fieldIsArray || requestedIsArray)
        {
            if (!fieldIsArray || !requestedIsArray ||
                !IsSameGenericTypePattern(fieldElementType, requestedElementType))
            {
                return false;
            }
        }
        else if (field.Type is not NamedType { ClassType: ClassType.Class } fieldType ||
            creation.RequestedType is not NamedType { ClassType: ClassType.Class } requestedType ||
            !IsSameGenericTypePattern(fieldType, requestedType))
        {
            return false;
        }

        return creation.Arguments.All(argument =>
            IsClosedGenericConcreteMethodExpression(
                argument, constructor, declaration.GenericTypeNames, fields));
    }

    private static bool IsSupportedClosedValueConstructor(
        ClassDeclaration declaration,
        IReadOnlyCollection<FieldDeclaration> fields,
        IReadOnlyList<ConstructorDeclaration> constructors)
    {
        if (constructors.Count == 0)
        {
            return fields.Count == 0;
        }
        if (constructors.Count > 2)
        {
            return false;
        }
        ConstructorDeclaration constructor;
        if (constructors.Count == 2)
        {
            var emptyParameterless = constructors.SingleOrDefault(candidate =>
                candidate.Parameters.Count == 0 && candidate.Body is { Count: 0 });
            if (emptyParameterless is null)
            {
                return false;
            }
            constructor = constructors.Single(candidate =>
                !ReferenceEquals(candidate, emptyParameterless));
        }
        else
        {
            constructor = constructors[0];
        }
        if (constructor.Initializer is not null and not
            { Kind: ConstructorInitializerKind.Base, Arguments.Count: 0 })
        {
            return false;
        }
        if (IsClosedGenericOwnerConstructor(constructor, declaration, fields))
        {
            return true;
        }
        if (fields.Count == 0)
        {
            return constructor.Parameters.Count == 0 && constructor.Body is { Count: 0 };
        }
        if (constructor.Parameters.Count == 0 && constructor.Body is { Count: 0 })
        {
            return true;
        }
        if (constructor.Body is not { Count: > 0 } body)
        {
            return false;
        }

        var fieldByName = fields.ToDictionary(field => field.Name, StringComparer.Ordinal);
        var assignedFields = new HashSet<string>(StringComparer.Ordinal);
        var assignedParameters = new HashSet<string>(StringComparer.Ordinal);
        foreach (var statement in body)
        {
            if (statement is not ExpressionStatement
                {
                    Expression: AssignmentExpression
                    {
                        Operator: "=",
                        Target: IdentifierExpression fieldIdentifier,
                        Value: { } value,
                    },
                })
            {
                return false;
            }
            var fieldName = fieldIdentifier.Identifier.Parts[^1];
            if (!fieldByName.TryGetValue(fieldName, out var field) ||
                !assignedFields.Add(fieldName))
            {
                return false;
            }
            var directGenericParameterAssignment = value is IdentifierExpression parameterIdentifier &&
                constructor.Parameters.SingleOrDefault(item =>
                    item.Name == parameterIdentifier.Identifier.ToString()) is { } parameter &&
                IsSameGenericTypePattern(field.Type, parameter.ParameterType);
            var concreteExpressionAssignment =
                !ContainsGenericParameter(field.Type, declaration.GenericTypeNames) &&
                IsClosedGenericConcreteMethodExpression(
                    value, constructor, declaration.GenericTypeNames, fields);
            var genericArrayAllocation = IsClosedGenericArrayAllocation(
                field, value, constructor, declaration);
            var genericObjectCreation = IsClosedGenericObjectCreation(
                field, value, constructor, declaration, fields);
            if (!directGenericParameterAssignment && !concreteExpressionAssignment &&
                !genericArrayAllocation && !genericObjectCreation)
            {
                return false;
            }
            foreach (var parameterName in EnumerateAllExpressions(value)
                .OfType<IdentifierExpression>()
                .Select(identifier => identifier.Identifier.ToString())
                .Where(name => constructor.Parameters.Any(parameter =>
                    parameter.Name == name)))
            {
                assignedParameters.Add(parameterName);
            }
        }
        return assignedParameters.Count == constructor.Parameters.Count;
    }

    private static bool IsSupportedClosedValueFieldType(
        TypeBase type,
        IReadOnlyCollection<string> genericTypeNames) => type switch
    {
        ConstType constant =>
            IsSupportedClosedValueFieldType(constant.UnderlyingType, genericTypeNames),
        GenericType parameter => genericTypeNames.Contains(parameter.Name),
        ArrayType => true,
        ValueTypeBase => true,
        ReferenceTypeBase => true,
        NamedType { ClassType: ClassType.Struct, TypeArguments.Count: 0 } => true,
        NamedType { ClassType: ClassType.Struct, ConstructedIdentity: not null } => true,
        NamedType { ClassType: ClassType.Struct, TypeArguments.Count: > 0 } named =>
            named.TypeArguments.All(argument =>
                IsGenericTypePattern(argument, genericTypeNames) ||
                IsSupportedClosedValueFieldType(argument, genericTypeNames)),
        NamedType { ClassType: ClassType.Class or ClassType.Interface or ClassType.Enum } => true,
        _ => false,
    };

    private static bool IsGenericTypePattern(
        TypeBase type,
        IReadOnlyCollection<string> genericTypeNames) => type switch
    {
        ConstType constant => IsGenericTypePattern(constant.UnderlyingType, genericTypeNames),
        GenericType parameter => genericTypeNames.Contains(parameter.Name),
        ArrayType array => IsGenericTypePattern(array.ElementType, genericTypeNames),
        NamedType
        {
            ClassType: ClassType.Class or ClassType.Struct or ClassType.Interface,
            TypeArguments.Count: > 0,
        } named =>
            named.TypeArguments.All(argument =>
                IsGenericTypePattern(argument, genericTypeNames)),
        _ => false,
    };

    private static void WriteClosedValueTypeDeclarations(
        IndentingWriter writer,
        IReadOnlyCollection<(NamedType Type, ClassDeclaration Declaration)> instances)
    {
        foreach (var (type, declaration) in OrderClosedValueTypeDeclarations(instances))
        {
            ValidateClosedValueLayout(declaration);
            var fields = declaration.MemberDeclarations.Declarations.OfType<FieldDeclaration>();
            var arguments = declaration.GenericTypeNames.Zip(type.TypeArguments)
                .ToDictionary(pair => pair.First, pair => pair.Second, StringComparer.Ordinal);
            writer.WriteLine($"struct {type.ConstructedIdentity!.CIdentifier} {{");
            writer.IncreaseIndent();
            writer.WriteLine($"{ToCStorageType(BuiltInSystemTypes.Object)} __base;");
            foreach (var field in fields)
            {
                var fieldType = GenericTypeSubstitution.Substitute(field.Type, arguments);
                EnsureConstructedGenericIdentities(fieldType);
                writer.WriteLine($"{fieldType.ToCIdentifier(false)} {field.Name};");
            }
            writer.DecreaseIndent();
            writer.WriteLine("};");
        }
    }

    private static IReadOnlyList<(NamedType Type, ClassDeclaration Declaration)>
        OrderClosedValueTypeDeclarations(
            IReadOnlyCollection<(NamedType Type, ClassDeclaration Declaration)> instances)
    {
        var layouts = instances.Where(item => RequiresClosedValueLayout(item.Type))
            .ToDictionary(item => item.Type.ConstructedIdentity!.CanonicalName,
                StringComparer.Ordinal);
        var ordered = new List<(NamedType Type, ClassDeclaration Declaration)>(layouts.Count);
        var states = new Dictionary<string, byte>(StringComparer.Ordinal);

        void Visit((NamedType Type, ClassDeclaration Declaration) instance)
        {
            var identity = instance.Type.ConstructedIdentity!.CanonicalName;
            if (states.TryGetValue(identity, out var state))
            {
                if (state == 1)
                {
                    throw new CxCompiler.Model.Errors.CompilationErrorException(
                        $"Closed generic value layouts contain a by-value cycle at " +
                        $"'{instance.Type.Name}'.");
                }
                return;
            }

            states.Add(identity, 1);
            var arguments = instance.Declaration.GenericTypeNames
                .Zip(instance.Type.TypeArguments)
                .ToDictionary(pair => pair.First, pair => pair.Second, StringComparer.Ordinal);
            foreach (var field in instance.Declaration.MemberDeclarations.Declarations
                .OfType<FieldDeclaration>().Where(field => !field.IsStatic))
            {
                var fieldType = GenericTypeSubstitution.Substitute(field.Type, arguments);
                EnsureConstructedGenericIdentities(fieldType);
                if (GetClosedValueFieldDependency(fieldType) is { } dependency &&
                    layouts.TryGetValue(dependency, out var dependencyInstance))
                {
                    Visit(dependencyInstance);
                }
            }

            states[identity] = 2;
            ordered.Add(instance);
        }

        foreach (var instance in layouts.Values.OrderBy(item =>
            item.Type.ConstructedIdentity!.CanonicalName, StringComparer.Ordinal))
        {
            Visit(instance);
        }
        return ordered;
    }

    private static string? GetClosedValueFieldDependency(TypeBase type) => type switch
    {
        ConstType constant => GetClosedValueFieldDependency(constant.UnderlyingType),
        NamedType { ClassType: ClassType.Struct, ConstructedIdentity: { } identity } =>
            identity.CanonicalName,
        _ => null,
    };

    private static IReadOnlyList<ClassDeclaration> OrderGenericStructDeclarations(
        IReadOnlyCollection<DeclarationBase> declarations,
        string moduleName,
        bool publicApi,
        IReadOnlySet<QualifiedIdentifier>? publicTypeNames)
    {
        var structs = GetClassDeclarations(declarations, publicApi, publicTypeNames)
            .Where(declaration => declaration.ClassType == ClassType.Struct &&
                declaration.GenericTypeNames.Length > 0).ToArray();
        var byName = structs.ToDictionary(declaration =>
            new QualifiedIdentifier(moduleName, declaration.FullName).ToString(),
            StringComparer.Ordinal);
        var ordered = new List<ClassDeclaration>(structs.Length);
        var states = new Dictionary<string, byte>(StringComparer.Ordinal);

        void Visit(ClassDeclaration declaration)
        {
            var name = new QualifiedIdentifier(moduleName, declaration.FullName).ToString();
            if (states.TryGetValue(name, out var state))
            {
                if (state == 1)
                {
                    throw new CxCompiler.Model.Errors.CompilationErrorException(
                        $"Generic struct declarations contain a by-value cycle at " +
                        $"'{declaration.FullName}'.");
                }
                return;
            }

            states.Add(name, 1);
            foreach (var field in declaration.MemberDeclarations.Declarations
                .OfType<FieldDeclaration>().Where(field => !field.IsStatic))
            {
                if (GetOpenGenericStructDependency(field.Type) is { } dependency &&
                    byName.TryGetValue(dependency, out var dependencyDeclaration))
                {
                    Visit(dependencyDeclaration);
                }
            }
            states[name] = 2;
            ordered.Add(declaration);
        }

        foreach (var declaration in structs.OrderBy(item =>
            new QualifiedIdentifier(moduleName, item.FullName).ToString(), StringComparer.Ordinal))
        {
            Visit(declaration);
        }
        return ordered;
    }

    private static IReadOnlyList<ClassDeclaration> OrderClassDeclarations(
        IReadOnlyCollection<DeclarationBase> declarations,
        bool publicApi,
        IReadOnlySet<QualifiedIdentifier>? publicTypeNames)
    {
        var classes = GetClassDeclarations(declarations, publicApi, publicTypeNames)
            .Where(declaration => declaration.ClassType != ClassType.Struct)
            .ToArray();
        var byName = classes.ToDictionary(declaration => declaration.FullName);
        var ordered = new List<ClassDeclaration>(classes.Length);
        var states = new Dictionary<QualifiedIdentifier, byte>();

        void Visit(ClassDeclaration declaration)
        {
            if (states.TryGetValue(declaration.FullName, out var state))
            {
                if (state == 1)
                {
                    throw new CxCompiler.Model.Errors.CompilationErrorException(
                        $"Class declarations contain an inheritance cycle at " +
                        $"'{declaration.FullName}'.");
                }
                return;
            }

            states.Add(declaration.FullName, 1);
            var baseClass = declaration.BaseClassType as NamedType ?? declaration.BaseTypes
                .OfType<NamedType>()
                .FirstOrDefault(type => type.ClassType == ClassType.Class);
            if (baseClass is not null &&
                (byName.TryGetValue(
                    baseClass.ResolvedTypeFullName, out var baseDeclaration) ||
                 (baseDeclaration = classes.FirstOrDefault(candidate =>
                    candidate.Name == baseClass.Name)) is not null))
            {
                Visit(baseDeclaration);
            }
            states[declaration.FullName] = 2;
            ordered.Add(declaration);
        }

        foreach (var declaration in classes)
        {
            Visit(declaration);
        }
        return ordered;
    }

    private static string? GetOpenGenericStructDependency(TypeBase type) => type switch
    {
        ConstType constant => GetOpenGenericStructDependency(constant.UnderlyingType),
        NamedType
        {
            ClassType: ClassType.Struct,
            ConstructedIdentity: null,
        } named => named.ResolvedTypeFullName.ToString(),
        _ => null,
    };

    private static void EnsureConstructedGenericIdentities(TypeBase type)
    {
        switch (type)
        {
            case ConstType constant:
                EnsureConstructedGenericIdentities(constant.UnderlyingType);
                break;
            case ArrayType array:
                EnsureConstructedGenericIdentities(array.ElementType);
                break;
            case NullableType nullable:
                EnsureConstructedGenericIdentities(nullable.UnderlyingType);
                break;
            case NamedType named:
                foreach (var argument in named.TypeArguments)
                {
                    EnsureConstructedGenericIdentities(argument);
                }
                if (named.TypeArguments.Count > 0 &&
                    named.TypeArguments.All(argument => !ContainsOpenGenericParameter(argument)) &&
                    named.ConstructedIdentity is null &&
                    named.ResolvedTypeFullName.Parts.Length > 1)
                {
                    var parts = named.ResolvedTypeFullName.Parts;
                    named.ConstructedIdentity = GenericTypeIdentity.Create(
                        parts[0], string.Join('.', parts.Skip(1)), named.TypeArguments);
                }
                break;
        }
    }

    private static bool ContainsOpenGenericParameter(TypeBase type) => type switch
    {
        GenericType => true,
        ConstType constant => ContainsOpenGenericParameter(constant.UnderlyingType),
        NullableType nullable => ContainsOpenGenericParameter(nullable.UnderlyingType),
        ArrayType array => ContainsOpenGenericParameter(array.ElementType),
        NamedType named => named.TypeArguments.Any(ContainsOpenGenericParameter),
        _ => false,
    };

    private static IReadOnlyDictionary<ClassDeclaration, NamedType>
        GetClosedImplementedInterfaceTypes(NamedType closedType, ClassDeclaration declaration)
    {
        var result = new Dictionary<ClassDeclaration, NamedType>();
        var arguments = declaration.GenericTypeNames
            .Zip(closedType.TypeArguments)
            .ToDictionary(pair => pair.First, pair => pair.Second, StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);

        foreach (var baseType in declaration.BaseTypes.OfType<NamedType>()
            .Where(type => type.ClassType == ClassType.Interface))
        {
            if (FindDeclaredInterface(declaration, baseType) is not { } interfaceDeclaration)
            {
                continue;
            }
            Visit(interfaceDeclaration, CloseInterfaceType(baseType, arguments));
        }

        return result;

        void Visit(ClassDeclaration interfaceDeclaration, NamedType closedInterface)
        {
            EnsureConstructedGenericIdentities(closedInterface);
            var key = closedInterface.ConstructedIdentity?.CanonicalName ??
                closedInterface.ResolvedTypeFullName.ToString();
            if (!visited.Add(key))
            {
                return;
            }
            if (result.TryGetValue(interfaceDeclaration, out var previous) &&
                previous.ConstructedIdentity?.CanonicalName !=
                    closedInterface.ConstructedIdentity?.CanonicalName)
            {
                throw new CxCompiler.Model.Errors.CompilationErrorException(
                    $"Closed runtime metadata for '{declaration.FullName}' cannot represent " +
                    $"multiple constructions of interface '{interfaceDeclaration.FullName}'.");
            }
            result[interfaceDeclaration] = closedInterface;

            var interfaceArguments = interfaceDeclaration.GenericTypeNames
                .Zip(closedInterface.TypeArguments)
                .ToDictionary(pair => pair.First, pair => pair.Second, StringComparer.Ordinal);
            foreach (var inheritedType in interfaceDeclaration.BaseTypes.OfType<NamedType>()
                .Where(type => type.ClassType == ClassType.Interface))
            {
                if (FindDeclaredInterface(interfaceDeclaration, inheritedType) is
                    { } inheritedDeclaration)
                {
                    Visit(inheritedDeclaration, CloseInterfaceType(
                        inheritedType, interfaceArguments));
                }
            }
        }

        static NamedType CloseInterfaceType(
            NamedType interfaceType,
            IReadOnlyDictionary<string, TypeBase> typeArguments)
        {
            var closed = GenericTypeSubstitution.Substitute(interfaceType, typeArguments)
                as NamedType ?? throw new InternalCompilerException(
                    $"Cannot substitute interface type '{interfaceType}'.");
            EnsureConstructedGenericIdentities(closed);
            return closed;
        }

        static ClassDeclaration? FindDeclaredInterface(
            ClassDeclaration owner,
            NamedType interfaceType)
        {
            var fullName = string.Join('.', interfaceType.ResolvedTypeFullName.Parts.Skip(1));
            return owner.BaseInterfaces.SingleOrDefault(candidate =>
                candidate.FullName.ToString() == fullName &&
                candidate.ProjectName == interfaceType.ResolvedTypeFullName.Parts.FirstOrDefault());
        }
    }

    private static IReadOnlyCollection<(NamedType Type, ClassDeclaration Declaration)>
        GetGenericTypeInstancesWithClosedInterfaces(
            IReadOnlyCollection<(NamedType Type, ClassDeclaration Declaration)> instances)
    {
        var result = instances.ToList();
        foreach (var instance in instances.Where(instance =>
            instance.Declaration.ClassType is ClassType.Class or ClassType.Struct))
        {
            foreach (var closedInterface in GetClosedImplementedInterfaceTypes(
                instance.Type, instance.Declaration))
            {
                if (closedInterface.Key.GenericTypeNames.Length == 0 ||
                    closedInterface.Value.ConstructedIdentity is null)
                {
                    continue;
                }
                result.Add((closedInterface.Value, closedInterface.Key));
            }
        }

        return result.DistinctBy(instance =>
            instance.Type.ConstructedIdentity?.CanonicalName ??
            instance.Type.ResolvedTypeFullName.ToString()).ToArray();
    }

    private static bool IsReferenceFieldPropertyAccessor(
        PropertyDeclaration property,
        PropertyAccessorDeclaration accessor,
        IReadOnlyCollection<FieldDeclaration> fields)
    {
        var genericTypeNames =
            property.ParentClassDeclaration.GenericTypeNames;
        if (accessor.Parameters.Count != 0 ||
            !(IsGenericTypePattern(property.Type, genericTypeNames) ||
                !ContainsGenericParameter(property.Type, genericTypeNames)))
        {
            return false;
        }
        FieldDeclaration? field = accessor.BodyFunction?.Body switch
        {
            [ReturnStatement
            {
                Expression: var expression,
            }] when accessor.Name == "get" => GetReferenceFieldDeclaration(expression),
            [ExpressionStatement
            {
                Expression: AssignmentExpression
                {
                    Operator: "=",
                    Target: var target,
                    Value: IdentifierExpression input,
                },
            }] when accessor.Name == "set" && input.Identifier.ToString() == "value" =>
                GetReferenceFieldDeclaration(target),
            _ => null,
        };
        return field is not null && fields.Contains(field) &&
            IsSameGenericTypePattern(field.Type, property.Type);
    }

    private static FieldDeclaration? GetReferenceFieldDeclaration(ExpressionBase? expression) =>
        expression switch
        {
            IdentifierExpression { TargetField: { } field } => field.Declaration,
            MemberAccessExpression
            {
                Target: ThisExpression,
                TargetField: { } field,
            } => field.Declaration,
            _ => null,
        };

    private static bool IsReferenceFieldSetter(
        FunctionDeclaration function,
        IReadOnlyCollection<FieldDeclaration> fields) =>
        function.Parameters.Count == 1 && IsReferenceFieldAssignments(function, fields);

    private static bool IsReferenceFieldAssignments(
        FunctionDeclaration function,
        IReadOnlyCollection<FieldDeclaration> fields)
    {
        if (function.Parameters.Count == 0 ||
            function.Body is null || function.Body.Count != function.Parameters.Count)
        {
            return false;
        }
        var assignedFields = new HashSet<FieldDeclaration>();
        for (var index = 0; index < function.Parameters.Count; index++)
        {
            var parameter = function.Parameters[index];
            if (function.Body[index] is not ExpressionStatement
                {
                    Expression: AssignmentExpression
                    {
                        Operator: "=",
                        Target: IdentifierExpression { TargetField: { } targetField },
                        Value: IdentifierExpression initialValue,
                    },
                } ||
                !fields.Contains(targetField.Declaration) ||
                !assignedFields.Add(targetField.Declaration) ||
                !IsSameGenericTypePattern(targetField.Declaration.Type, parameter.ParameterType) ||
                initialValue.Identifier.ToString() != parameter.Name)
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsReferenceFieldGetter(
        FunctionDeclaration function,
        IReadOnlyCollection<FieldDeclaration> fields) =>
        function.Parameters.Count == 0 &&
        IsGenericTypePattern(function.ReturnType,
            function.ParentClassDeclaration?.GenericTypeNames ?? []) &&
        function.Body is
        [ReturnStatement
        {
            Expression: IdentifierExpression { TargetField: { } targetField },
        }] &&
        fields.Contains(targetField.Declaration) &&
        IsSameGenericTypePattern(targetField.Declaration.Type, function.ReturnType);

    private static bool IsGenericFieldSetter(
        FunctionDeclaration function,
        IReadOnlyCollection<FieldDeclaration> fields) =>
        GetGenericFieldSetterAssignments(function, fields) is not null;

    private static IReadOnlyList<(FieldDeclaration Field, int ParameterIndex)>?
        GetGenericFieldSetterAssignments(
            FunctionDeclaration function,
            IReadOnlyCollection<FieldDeclaration> fields)
    {
        if (function.Parameters.Count == 0 || function.ReturnType is not VoidType ||
            function.Body is not { Count: > 0 })
        {
            return null;
        }

        var genericTypeNames = function.ParentClassDeclaration?.GenericTypeNames ?? [];
        var assignments = new List<(FieldDeclaration Field, int ParameterIndex)>();
        foreach (var statement in function.Body)
        {
            if (statement is not ExpressionStatement
                {
                    Expression: AssignmentExpression
                    {
                        Operator: "=",
                        Target: IdentifierExpression { TargetField: { } targetField },
                        Value: IdentifierExpression { TargetLocal: { } targetLocal },
                    },
                } || !fields.Contains(targetField.Declaration))
            {
                return null;
            }

            var parameterIndex = function.Parameters
                .Select((parameter, index) => (Parameter: parameter, Index: index))
                .Where(item => item.Parameter.Name == targetLocal.Name &&
                    IsGenericTypePattern(item.Parameter.ParameterType, genericTypeNames) &&
                    IsSameGenericTypePattern(
                        targetField.Declaration.Type, item.Parameter.ParameterType))
                .Select(item => (int?)item.Index)
                .FirstOrDefault();
            if (parameterIndex is null)
            {
                return null;
            }

            assignments.Add((targetField.Declaration, parameterIndex.Value));
        }

        return assignments;
    }

    private static int? GetGenericFieldSetterParameterIndex(
        FunctionDeclaration function,
        IReadOnlyCollection<FieldDeclaration> fields)
    {
        var assignments = GetGenericFieldSetterAssignments(function, fields);
        return assignments is { Count: 1 } ? assignments[0].ParameterIndex : null;
    }

    private static bool CanGenerateClosedGenericStructConstructor(FunctionSymbol function) =>
        function.Declaration is ConstructorDeclaration constructor &&
        constructor.MemberModifiers.Contains(MemberModifier.Public) &&
        constructor.ParentClassDeclaration?.ClassType == ClassType.Struct &&
        constructor.GenericTypeNames.Length == 0 &&
        constructor.Body is not null &&
        function.SpecializationName is not null &&
        function.ClosedContainingType is
            { ClassType: ClassType.Struct, ConstructedIdentity: not null } &&
        function.ParameterTypes.All(IsReflectionParameterType);

    private static void WriteClosedGenericStructConstructorInvoker(
        IndentingWriter writer,
        GenericTypeIdentity identity,
        FunctionSymbol constructor,
        string invokerName)
    {
        writer.WriteLine($"static cx_ptr {invokerName}(cx_ptr __target, const cx_ptr *__arguments, cx_uint __argumentCount) CX_MAYBE_UNUSED;");
        writer.WriteLine($"static cx_ptr {invokerName}(cx_ptr __target, " +
            "const cx_ptr *__arguments, cx_uint __argumentCount)");
        writer.WriteLine("{");
        writer.IncreaseIndent();
        writer.WriteLine($"if (__argumentCount != {constructor.ParameterTypes.Count} || " +
            $"({constructor.ParameterTypes.Count} != 0 && __arguments == CX_NULL) || " +
            "__target != CX_NULL)");
        writer.WriteLine("{");
        writer.IncreaseIndent();
        writer.WriteLine("return CX_NULL;");
        writer.DecreaseIndent();
        writer.WriteLine("}");
        if (constructor.ParameterTypes.Count == 0)
        {
            writer.WriteLine("(void)__arguments;");
        }

        writer.WriteLine($"struct {identity.CIdentifier} __instance = {{ 0 }};");
        var callArguments = new List<string> { "&__instance" };
        for (var parameterIndex = 0;
            parameterIndex < constructor.ParameterTypes.Count;
            parameterIndex++)
        {
            var parameterType = constructor.ParameterTypes[parameterIndex];
            if (IsReflectionInterfaceType(parameterType))
            {
                var interfaceArgumentName = $"__cx_interface_argument_{parameterIndex}";
                writer.WriteLine(
                    $"struct cx_iface_ref {interfaceArgumentName} = " +
                    $"cx_checked_cast_object_to_interface(__arguments[{parameterIndex}], " +
                    $"{ToTypeInfoPointer(parameterType)});");
                callArguments.Add(interfaceArgumentName);
            }
            else if (IsReflectionValueType(parameterType))
            {
                var valueArgumentName = $"__cx_value_argument_{parameterIndex}";
                var valueType = UnwrapConst(parameterType).ToCIdentifier(false);
                writer.WriteLine($"const {valueType} {valueArgumentName} = " +
                    $"*((const {valueType}*)cx_unbox_value(" +
                    $"__arguments[{parameterIndex}], {ToTypeInfoPointer(parameterType)}));");
                callArguments.Add(valueArgumentName);
            }
            else
            {
                callArguments.Add(
                    $"({parameterType.ToCIdentifier(false)})__arguments[{parameterIndex}]");
            }
        }

        writer.WriteLine($"{constructor.SpecializationName!}({string.Join(", ", callArguments)});");
        writer.WriteLine("return cx_box_value(&__instance, " +
            "(cx_uint)sizeof(__instance), " +
            $"&CX_TYPEINFO_NAME({identity.CIdentifier}));");
        writer.DecreaseIndent();
        writer.WriteLine("}");
    }

    private static bool CanGenerateClosedGenericStructReflectionMethod(
        FunctionDeclaration method,
        IReadOnlyList<TypeBase> parameterTypes,
        TypeBase returnType)
    {
        if (method.IsStatic || method is ConstructorDeclaration ||
            method.GenericTypeNames.Length != 0 || method.Body is null ||
            !parameterTypes.All(IsReflectionParameterType))
        {
            return false;
        }

        if (method.ReturnType is VoidType &&
            method.ParentClassDeclaration is { } genericDeclaration &&
            GetClosedGenericGenericFieldParameterAssignments(
                method, genericDeclaration,
                genericDeclaration.MemberDeclarations.Declarations
                    .OfType<FieldDeclaration>().ToArray()) is not null)
        {
            return true;
        }

        if (method.ReturnType is VoidType &&
            method.ParentClassDeclaration is { } parent &&
            (IsClosedGenericGenericFieldDefaultResetMethod(
                method, parent,
                parent.MemberDeclarations.Declarations
                    .OfType<FieldDeclaration>().ToArray()) ||
             IsClosedGenericConcreteFieldAssignmentMethod(
                method, parent,
                parent.MemberDeclarations.Declarations
                    .OfType<FieldDeclaration>().ToArray())))
        {
            return true;
        }

        if (method.ParentClassDeclaration is not { } declaration ||
            method.Body is not [ReturnStatement { Expression: { } expression }])
        {
            return false;
        }

        var fields = declaration.MemberDeclarations.Declarations
            .OfType<FieldDeclaration>().ToArray();
        return method.Parameters.Count == 0 && IsReflectionValueType(returnType) &&
                expression is IdentifierExpression returned && fields.Any(field =>
                    field.Name == returned.Identifier.Parts[^1]) ||
            IsReflectionValueType(returnType) &&
                IsClosedGenericConcreteExpressionMethod(method, declaration, fields);
    }

    private static void WriteClosedGenericStructReflectionMethodInvoker(
        IndentingWriter writer,
        GenericTypeIdentity identity,
        FunctionDeclaration method,
        IReadOnlyList<TypeBase> parameterTypes,
        TypeBase returnType,
        IReadOnlyDictionary<string, TypeBase> typeArguments,
        string moduleName,
        string invokerName)
    {
        writer.WriteLine($"static cx_ptr {invokerName}(cx_ptr __target, const cx_ptr *__arguments, cx_uint __argumentCount) CX_MAYBE_UNUSED;");
        writer.WriteLine($"static cx_ptr {invokerName}(cx_ptr __target, " +
            "const cx_ptr *__arguments, cx_uint __argumentCount)");
        writer.WriteLine("{");
        writer.IncreaseIndent();
        writer.WriteLine($"if (__target == CX_NULL || __argumentCount != {parameterTypes.Count} || " +
            $"({parameterTypes.Count} != 0 && __arguments == CX_NULL)) return CX_NULL;");
        if (parameterTypes.Count == 0)
        {
            writer.WriteLine("(void)__arguments;");
        }

        writer.WriteLine($"struct {identity.CIdentifier} *__instance = " +
            $"(struct {identity.CIdentifier}*)cx_unbox_value(" +
            $"__target, &CX_TYPEINFO_NAME({identity.CIdentifier}));");
        if (method.ReturnType is VoidType &&
            method.ParentClassDeclaration is { } resetDeclaration &&
            IsClosedGenericGenericFieldDefaultResetMethod(
                method, resetDeclaration,
                resetDeclaration.MemberDeclarations.Declarations
                    .OfType<FieldDeclaration>().ToArray()))
        {
            var assignment = (AssignmentExpression)((ExpressionStatement)method.Body![0]).Expression;
            var targetName = ((IdentifierExpression)assignment.Target).Identifier.Parts[^1];
            var targetField = ((IdentifierExpression)assignment.Target).TargetField!;
            var fieldType = GenericTypeSubstitution.Substitute(
                targetField.Declaration.Type, typeArguments);
            writer.WriteLine($"__instance->{targetName} = {ToCDefaultValue(fieldType)};");
            writer.WriteLine("return CX_NULL;");
        }
        else if (method.ReturnType is VoidType &&
            method.ParentClassDeclaration is { } genericParent &&
            GetClosedGenericGenericFieldParameterAssignments(
                method, genericParent,
                genericParent.MemberDeclarations.Declarations
                    .OfType<FieldDeclaration>().ToArray()) is { } genericAssignments)
        {
            for (var assignmentIndex = 0;
                assignmentIndex < genericAssignments.Count;
                assignmentIndex++)
            {
                var assignment = genericAssignments[assignmentIndex];
                var targetName =
                    ((IdentifierExpression)assignment.Target).Identifier.Parts[^1];
                var valueName =
                    ((IdentifierExpression)assignment.Value).Identifier.Parts[^1];
                var valueIndex = Enumerable.Range(0, method.Parameters.Count)
                    .Single(index => method.Parameters[index].Name == valueName);
                var parameterType = parameterTypes[valueIndex];
                var valueType = UnwrapConst(parameterType).ToCIdentifier(false);
                var temporary = $"__cx_value_{assignmentIndex}";
                writer.WriteLine($"const {valueType} {temporary} = *((const {valueType}*)" +
                    $"cx_unbox_value(__arguments[{valueIndex}], {ToTypeInfoPointer(parameterType)}));");
                writer.WriteLine($"__instance->{targetName} = {temporary};");
            }
            writer.WriteLine("return CX_NULL;");
        }
        else if (method.ReturnType is VoidType &&
            method.ParentClassDeclaration is { } assignmentDeclaration &&
            IsClosedGenericConcreteFieldAssignmentMethod(
                method, assignmentDeclaration,
                assignmentDeclaration.MemberDeclarations.Declarations
                    .OfType<FieldDeclaration>().ToArray()))
        {
            for (var parameterIndex = 0; parameterIndex < parameterTypes.Count; parameterIndex++)
            {
                var parameterType = parameterTypes[parameterIndex];
                var parameterName = method.Parameters[parameterIndex].Name;
                if (IsReflectionInterfaceType(parameterType))
                {
                    writer.WriteLine(
                        $"struct cx_iface_ref {parameterName} = " +
                        $"cx_checked_cast_object_to_interface(__arguments[{parameterIndex}], " +
                        $"{ToTypeInfoPointer(parameterType)});");
                }
                else if (IsReflectionValueType(parameterType))
                {
                    var argumentCType = UnwrapConst(parameterType).ToCIdentifier(false);
                    writer.WriteLine($"const {argumentCType} {parameterName} = " +
                        $"*((const {argumentCType}*)cx_unbox_value(" +
                        $"__arguments[{parameterIndex}], {ToTypeInfoPointer(parameterType)}));");
                }
                else
                {
                    writer.WriteLine($"const {parameterType.ToCIdentifier(false)} {parameterName} = " +
                        $"({parameterType.ToCIdentifier(false)})__arguments[{parameterIndex}];");
                }
            }

            foreach (var statement in method.Body!)
            {
                var expression = ((ExpressionStatement)statement).Expression;
                var cExpression = ToCExpression(expression, method, moduleName)
                    .Replace("__this", "__instance", StringComparison.Ordinal);
                writer.WriteLine($"{cExpression};");
            }
            writer.WriteLine("return CX_NULL;");
        }
        else if (method.ParentClassDeclaration is { } declaration &&
            IsClosedGenericConcreteExpressionMethod(
                method, declaration,
                declaration.MemberDeclarations.Declarations
                    .OfType<FieldDeclaration>().ToArray()))
        {
            for (var parameterIndex = 0; parameterIndex < parameterTypes.Count; parameterIndex++)
            {
                var parameterType = parameterTypes[parameterIndex];
                var parameterName = method.Parameters[parameterIndex].Name;
                if (IsReflectionInterfaceType(parameterType))
                {
                    writer.WriteLine(
                        $"struct cx_iface_ref {parameterName} = " +
                        $"cx_checked_cast_object_to_interface(__arguments[{parameterIndex}], " +
                        $"{ToTypeInfoPointer(parameterType)});");
                }
                else if (IsReflectionValueType(parameterType))
                {
                    var valueType = UnwrapConst(parameterType).ToCIdentifier(false);
                    writer.WriteLine($"const {valueType} {parameterName} = " +
                        $"*((const {valueType}*)cx_unbox_value(" +
                        $"__arguments[{parameterIndex}], {ToTypeInfoPointer(parameterType)}));");
                }
                else
                {
                    writer.WriteLine($"const {parameterType.ToCIdentifier(false)} {parameterName} = " +
                        $"({parameterType.ToCIdentifier(false)})__arguments[{parameterIndex}];");
                }
            }

            var expression = ((ReturnStatement)method.Body![0]).Expression!;
            var cExpression = ToCExpression(expression, method, moduleName)
                .Replace("__this", "__instance", StringComparison.Ordinal);
            var returnCType = returnType.ToCIdentifier(false);
            writer.WriteLine($"{returnCType} __cx_value = {cExpression};");
            writer.WriteLine("return cx_box_value(&__cx_value, " +
                "(cx_uint)sizeof(__cx_value), " +
                $"{ToTypeInfoPointer(returnType)});");
        }
        else
        {
            var returned = (IdentifierExpression)((ReturnStatement)method.Body![0]).Expression!;
            var fieldName = returned.Identifier.Parts[^1];
            var valueType = returnType.ToCIdentifier(false);
            writer.WriteLine($"{valueType} __cx_value = __instance->{fieldName};");
            writer.WriteLine("return cx_box_value(&__cx_value, " +
                "(cx_uint)sizeof(__cx_value), " +
                $"{ToTypeInfoPointer(returnType)});");
        }
        writer.DecreaseIndent();
        writer.WriteLine("}");
    }

    private static bool CanGenerateClosedGenericClassReflectionMethod(
        FunctionDeclaration method,
        IReadOnlyList<TypeBase> parameterTypes,
        TypeBase returnType) =>
        method is not ConstructorDeclaration &&
        method.VirtualSlotIndex is null &&
        method.GenericTypeNames.Length == 0 &&
        method.Body is not null &&
        parameterTypes.All(IsReflectionParameterType) &&
        (returnType is VoidType || IsReflectionObjectReferenceType(returnType) ||
         IsReflectionInterfaceType(returnType) || IsReflectionValueType(returnType));

    private static void WriteClosedGenericClassReflectionMethodInvoker(
        IndentingWriter writer,
        ClassDeclaration declaration,
        FunctionDeclaration method,
        IReadOnlyList<TypeBase> parameterTypes,
        TypeBase returnType,
        string moduleName,
        string invokerName,
        FunctionSymbol? specialization,
        bool requiresClosedValueLayout)
    {
        writer.WriteLine($"static cx_ptr {invokerName}(cx_ptr __target, const cx_ptr *__arguments, cx_uint __argumentCount) CX_MAYBE_UNUSED;");
        writer.WriteLine($"static cx_ptr {invokerName}(cx_ptr __target, " +
            "const cx_ptr *__arguments, cx_uint __argumentCount)");
        writer.WriteLine("{");
        writer.IncreaseIndent();
        writer.WriteLine($"if ({(method.IsStatic ? string.Empty : "__target == CX_NULL || ")}" +
            $"__argumentCount != {parameterTypes.Count} || " +
            $"({parameterTypes.Count} != 0 && __arguments == CX_NULL)) return CX_NULL;");
        if (parameterTypes.Count == 0)
        {
            writer.WriteLine("(void)__arguments;");
        }
        if (method.IsStatic)
        {
            writer.WriteLine("(void)__target;");
        }

        var methodModule = declaration.ProjectName ?? moduleName;
        var callArguments = new List<string>();
        if (!method.IsStatic)
        {
            var className = requiresClosedValueLayout &&
                specialization?.ClosedContainingType?.ConstructedIdentity is
                { } ownerIdentity
                ? $"struct {ownerIdentity.CIdentifier}"
                : $"struct {declaration.ToCIdentifier(methodModule, false)}";
            callArguments.Add(
                $"({(method.Const ? "const " : string.Empty)}{className}*)__target");
        }
        for (var index = 0; index < parameterTypes.Count; index++)
        {
            var parameterType = parameterTypes[index];
            if (IsReflectionInterfaceType(parameterType))
            {
                var argumentName = $"__cx_interface_argument_{index}";
                writer.WriteLine($"struct cx_iface_ref {argumentName} = " +
                    $"cx_checked_cast_object_to_interface(__arguments[{index}], " +
                    $"{ToTypeInfoPointer(parameterType)});");
                callArguments.Add(argumentName);
            }
            else if (IsReflectionValueType(parameterType))
            {
                var argumentName = $"__cx_value_argument_{index}";
                var valueType = UnwrapConst(parameterType).ToCIdentifier(false);
                writer.WriteLine($"const {valueType} {argumentName} = " +
                    $"*((const {valueType}*)cx_unbox_value(__arguments[{index}], " +
                    $"{ToTypeInfoPointer(parameterType)}));");
                callArguments.Add(argumentName);
            }
            else
            {
                callArguments.Add(
                    $"({parameterType.ToCIdentifier(false)})__arguments[{index}]");
            }
        }

        var methodName = specialization?.SpecializationName ??
            ToCFunctionName(method, methodModule);
        var call = $"{methodName}(" +
            $"{string.Join(", ", callArguments)})";
        if (returnType is VoidType)
        {
            writer.WriteLine($"{call};");
            writer.WriteLine("return CX_NULL;");
        }
        else if (IsReflectionInterfaceType(returnType))
        {
            writer.WriteLine($"return (cx_ptr)({call}).instance;");
        }
        else if (IsReflectionValueType(returnType))
        {
            var valueType = returnType.ToCIdentifier(false);
            writer.WriteLine($"{valueType} __cx_value_return = {call};");
            writer.WriteLine("return cx_box_value(&__cx_value_return, " +
                "(cx_uint)sizeof(__cx_value_return), " +
                $"{ToTypeInfoPointer(returnType)});");
        }
        else
        {
            writer.WriteLine($"return (cx_ptr){call};");
        }
        writer.DecreaseIndent();
        writer.WriteLine("}");
    }

    private static void WriteClosedGenericTypeInfos(
        IndentingWriter writer,
        IReadOnlyCollection<(NamedType Type, ClassDeclaration Declaration)> instances,
        IReadOnlyCollection<FunctionSymbol> genericFunctions,
        string moduleName)
    {
        foreach (var (type, declaration) in instances.OrderBy(item =>
            item.Type.ConstructedIdentity!.CanonicalName, StringComparer.Ordinal))
        {
            ValidateClosedReferenceLayout(type, declaration);
            var identity = type.ConstructedIdentity!;
            var containingTypeModule = declaration.ProjectName ?? moduleName;
            var closedNameIdentifier = $"{identity.CIdentifier}___name";
            var closedNamespaceIdentifier = $"{identity.CIdentifier}___namespace";
            var fields = GetReflectedFields(declaration);
            var closedFields = $"{identity.CIdentifier}___reflection_fields";
            var arguments = declaration.GenericTypeNames
                .Zip(type.TypeArguments)
                .ToDictionary(pair => pair.First, pair => pair.Second);
            var closedInterfaceTypes = declaration.ClassType == ClassType.Struct &&
                RequiresClosedValueLayout(type) ||
                declaration.ClassType == ClassType.Class
                    ? GetClosedImplementedInterfaceTypes(type, declaration)
                    : new Dictionary<ClassDeclaration, NamedType>();
            var closedInterfaces = declaration.InterfaceDispatchTables
                .Where(table => closedInterfaceTypes.ContainsKey(table.Interface))
                .ToArray();
            var closedInterfaceThunks = new Dictionary<(ClassDeclaration Interface, int Slot), string>();
            var closedInterfaceMap = $"{identity.CIdentifier}___interfaces";
            var closedInterfaceReceiverType = RequiresClosedValueLayout(type)
                ? identity.CIdentifier
                : declaration.ToCIdentifier(containingTypeModule, false);
            var functions = GetReflectionFunctions(declaration).ToArray();
            string? closedFunctions = null;
            if (functions.Length != 0)
            {
                closedFunctions = $"{identity.CIdentifier}___reflection_functions";
                for (var index = 0; index < functions.Length; index++)
                {
                    if (functions[index].Parameters.Count == 0)
                    {
                        continue;
                    }
                    var closedParameters =
                        $"{identity.CIdentifier}___reflection_parameters_{index}";
                    writer.WriteLine();
                    writer.WriteLine("#if !defined(CX_DYNAMIC_MODULE)");
                    writer.WriteLine($"static const struct cx_reflection_parameter {closedParameters}[] = {{");
                    writer.IncreaseIndent();
                    foreach (var parameter in functions[index].Parameters)
                    {
                        writer.WriteLine($"{{ 0, {ToTypeInfoPointer(GenericTypeSubstitution.Substitute(
                            parameter.ParameterType, arguments))}, " +
                            $"\"{parameter.Name}\", CX_NULL }},");
                    }
                    writer.DecreaseIndent();
                    writer.WriteLine("};");
                    writer.WriteLine("#else");
                    writer.WriteLine($"static struct cx_reflection_parameter {closedParameters}[{functions[index].Parameters.Count}];");
                    writer.WriteLine("#endif");
                }
                writer.WriteLine("#if !defined(CX_DYNAMIC_MODULE)");
                writer.WriteLine($"static const struct cx_reflection_function {closedFunctions}[] = {{");
                writer.IncreaseIndent();
                for (var index = 0; index < functions.Length; index++)
                {
                    var function = functions[index];
                    var closedParameters = function.Parameters.Count == 0
                        ? "CX_NULL"
                        : $"{identity.CIdentifier}___reflection_parameters_{index}";
                    writer.WriteLine($"{{ {GetFunctionFlags(function)}, CX_REFLECTION_NO_SLOT, " +
                        $"{ToTypeInfoPointer(GenericTypeSubstitution.Substitute(function.ReturnType, arguments))}, " +
                        $"\"{function.Name}\", {closedParameters}, {function.Parameters.Count} }},");
                }
                writer.DecreaseIndent();
                writer.WriteLine("};");
                writer.WriteLine("#else");
                writer.WriteLine($"static struct cx_reflection_function {closedFunctions}[{functions.Length}];");
                writer.WriteLine("#endif");
            }
            if (fields.Length != 0)
            {
                writer.WriteLine();
                writer.WriteLine("#if !defined(CX_DYNAMIC_MODULE)");
                writer.WriteLine($"static const struct cx_reflection_field {closedFields}[] = {{");
                writer.IncreaseIndent();
                foreach (var field in fields)
                {
                    var fieldType = GenericTypeSubstitution.Substitute(field.Type, arguments);
                    writer.WriteLine(
                        $"{{ {GetMemberFlags(field.MemberModifiers)}, " +
                        $"(cx_uint)offsetof({(RequiresClosedValueLayout(type)
                            ? $"struct {identity.CIdentifier}"
                            : declaration.ToCIdentifier(containingTypeModule))}, {field.Name}), " +
                        $"{ToTypeInfoPointer(fieldType)}, \"{field.Name}\" }},");
                }
                writer.DecreaseIndent();
                writer.WriteLine("};");
                writer.WriteLine("#else");
                writer.WriteLine($"static struct cx_reflection_field {closedFields}[{fields.Length}];");
                writer.WriteLine("#endif");
            }
            var runtimeFunctionCount = GetReflectionFunctionCount(declaration);
            var hasClosedReflectionLayout =
                declaration.ClassType == ClassType.Struct && RequiresClosedValueLayout(type) ||
                declaration.ClassType == ClassType.Class && !RequiresClosedValueLayout(type);
            var closedInvokers = !hasClosedReflectionLayout || functions.Length == 0
                ? null
                : $"{identity.CIdentifier}___reflection_invokers";
            var closedConstructorInvokerNames = new string[functions.Length];
            for (var index = 0; index < functions.Length; index++)
            {
                var function = functions[index];
                var closedParameterTypes = function.Parameters.Select(parameter =>
                    GenericTypeSubstitution.Substitute(parameter.ParameterType, arguments)).ToArray();
                foreach (var closedParameterType in closedParameterTypes)
                {
                    EnsureConstructedGenericIdentities(closedParameterType);
                }
                var specialization = function.IsConstructor
                    ? genericFunctions.FirstOrDefault(candidate =>
                        ReferenceEquals(candidate.Declaration, function.Declaration) &&
                        candidate.ClosedContainingType?.ConstructedIdentity?.CanonicalName ==
                            identity.CanonicalName)
                    : function.Declaration?.ParentClassDeclaration is
                        { ConceptConstraints.Count: > 0 }
                    ? genericFunctions.FirstOrDefault(candidate =>
                        candidate.Declaration?.Name == function.Name &&
                        candidate.ParameterTypes.Select(parameter =>
                                parameter.ToCIdentifier(false))
                            .SequenceEqual(closedParameterTypes.Select(parameter =>
                                parameter.ToCIdentifier(false)), StringComparer.Ordinal) &&
                        candidate.ClosedContainingType is { } closedContainingType &&
                        Equals(closedContainingType.ResolvedTypeFullName,
                            type.ResolvedTypeFullName) &&
                        closedContainingType.TypeArguments.Select(argument =>
                                argument.ToCIdentifier(false))
                            .SequenceEqual(type.TypeArguments.Select(argument =>
                                argument.ToCIdentifier(false)), StringComparer.Ordinal))
                    : genericFunctions.FirstOrDefault(candidate =>
                        ReferenceEquals(candidate.Declaration, function.Declaration) &&
                        candidate.ClosedContainingType?.ConstructedIdentity?.CanonicalName ==
                            identity.CanonicalName);
                var closedReturnType = GenericTypeSubstitution.Substitute(
                    function.ReturnType, arguments);
                EnsureConstructedGenericIdentities(closedReturnType);
                var method = function.Declaration;
                var directStructMethod = declaration.ClassType == ClassType.Struct &&
                    !function.IsConstructor && method is not null &&
                    CanGenerateClosedGenericStructReflectionMethod(
                        method, closedParameterTypes, closedReturnType);
                var directClassMethod = declaration.ClassType == ClassType.Class &&
                    !function.IsConstructor && method is not null &&
                    (declaration.GenericTypeNames.Length == 0 || specialization is not null) &&
                    CanGenerateClosedGenericClassReflectionMethod(
                        method, closedParameterTypes, closedReturnType);
                if (function.IsConstructor && (declaration.ClassType != ClassType.Struct ||
                        specialization is null ||
                        !CanGenerateClosedGenericStructConstructor(specialization)) ||
                    !function.IsConstructor && !directStructMethod && !directClassMethod)
                {
                    closedConstructorInvokerNames[index] = "CX_NULL";
                    continue;
                }

                var invokerName =
                    $"{identity.CIdentifier}___reflection_invoke_{index}";
                closedConstructorInvokerNames[index] = invokerName;
                if (function.IsConstructor)
                {
                    WriteClosedGenericStructConstructorInvoker(
                        writer, identity, specialization!, invokerName);
                }
                else if (directStructMethod)
                {
                    WriteClosedGenericStructReflectionMethodInvoker(
                        writer, identity, method!, closedParameterTypes,
                        closedReturnType, arguments, containingTypeModule, invokerName);
                }
                else
                {
                    WriteClosedGenericClassReflectionMethodInvoker(
                        writer, declaration, method!, closedParameterTypes,
                        closedReturnType, containingTypeModule, invokerName, specialization,
                        RequiresClosedValueLayout(type));
                }
            }
            if (closedInvokers is not null)
            {
                writer.WriteLine($"static const cx_reflection_invoker {closedInvokers}[] = {{");
                writer.IncreaseIndent();
                foreach (var invokerName in closedConstructorInvokerNames)
                {
                    writer.WriteLine($"{invokerName},");
                }
                writer.DecreaseIndent();
                writer.WriteLine("};");
            }
            var factoryConstructor = declaration.ClassType == ClassType.Class &&
                !declaration.IsStatic && !declaration.IsAbstract &&
                !RequiresClosedValueLayout(type)
                ? declaration.MemberDeclarations.Declarations
                    .OfType<ConstructorDeclaration>()
                    .SingleOrDefault(constructor => constructor.Parameters.Count == 0 &&
                        constructor.MemberModifiers.Contains(MemberModifier.Public))
                : null;
            var factoryForStruct = declaration.ClassType == ClassType.Struct &&
                !declaration.IsStatic && RequiresClosedValueLayout(type);
            var factoryIdentifier = $"{identity.CIdentifier}___reflection_create_instance";
            if (factoryConstructor is not null || factoryForStruct)
            {
                writer.WriteLine($"static cx_ptr {factoryIdentifier}(void) {{");
                writer.IncreaseIndent();
                if (factoryForStruct)
                {
                    var structType = RequiresClosedValueLayout(type)
                        ? $"struct {identity.CIdentifier}"
                        : declaration.ToCIdentifier(containingTypeModule);
                    var typeInfoName = $"CX_TYPEINFO_NAME({identity.CIdentifier})";
                    writer.WriteLine($"extern struct cxcore_System_Reflection_TypeInfo {typeInfoName};");
                    writer.WriteLine($"{structType} __instance = {{ 0 }};");
                    writer.WriteLine($"return cx_box_value(&__instance, " +
                        $"(cx_uint)sizeof(__instance), &{typeInfoName});");
                }
                else
                {
                    var classTypeName = declaration.ToCIdentifier(containingTypeModule, false);
                    var classStorageType = $"struct {classTypeName}";
                    writer.WriteLine($"{classStorageType} *__instance = ({classStorageType}*)" +
                        $"cxcore_System_Memory_Alloc((cx_uint)sizeof({classStorageType}));");
                    writer.WriteLine($"{ToCFunctionName(factoryConstructor!, containingTypeModule)}(__instance);");
                    writer.WriteLine($"CX_INIT_VTABLE(__instance, {classTypeName});");
                    writer.WriteLine("return __instance;");
                }
                writer.DecreaseIndent();
                writer.WriteLine("}");
            }
            if (closedInterfaces.Length != 0)
            {
                foreach (var table in closedInterfaces)
                {
                    var closedInterfaceType = closedInterfaceTypes[table.Interface];
                    foreach (var slot in table.Slots.Where(slot =>
                        slot.Implementation is not null &&
                        slot.ContractFunction is not
                            { GenericTypeNames.Length: > 0 }))
                    {
                        var closedThunk = GetClosedGenericInterfaceThunkIdentifier(
                            identity.CIdentifier,
                            table.Interface.ProjectName ?? moduleName,
                            table.Interface.FullName,
                            slot.Index).ToCIdentifier();
                        closedInterfaceThunks[(table.Interface, slot.Index)] = closedThunk;
                        WriteClosedGenericInterfaceDispatchThunk(
                            writer,
                            closedInterfaceReceiverType,
                            declaration,
                            table.Interface,
                            closedInterfaceType,
                            slot,
                            arguments,
                            moduleName,
                            closedThunk,
                            genericFunctions,
                            type);
                    }
                    var closedTable = GetClosedGenericInterfaceVTableIdentifier(
                        identity.CIdentifier,
                        table.Interface.ProjectName ?? moduleName,
                        table.Interface.FullName).ToCIdentifier();
                    writer.WriteLine($"extern union cx_vtable_entry {closedTable}[];");
                    if (closedInterfaceTypes[table.Interface].ConstructedIdentity is
                        { } interfaceIdentity)
                    {
                        writer.WriteLine(
                            "extern struct cxcore_System_Reflection_TypeInfo " +
                            $"CX_TYPEINFO_NAME({interfaceIdentity.CIdentifier});");
                    }
                }
                writer.WriteLine("#if !defined(CX_DYNAMIC_MODULE)");
                writer.WriteLine(
                    $"static const struct cx_interface_impl {closedInterfaceMap}[] = {{");
                writer.IncreaseIndent();
                foreach (var table in closedInterfaces)
                {
                    var closedInterfaceType = closedInterfaceTypes[table.Interface];
                    var interfaceTypeInfo = ToTypeInfoPointer(closedInterfaceType);
                    var closedTable = GetClosedGenericInterfaceVTableIdentifier(
                        identity.CIdentifier,
                        table.Interface.ProjectName ?? moduleName,
                        table.Interface.FullName).ToCIdentifier();
                    writer.WriteLine($"{{ {interfaceTypeInfo}, {closedTable} }},");
                }
                writer.DecreaseIndent();
                writer.WriteLine("};");
                writer.WriteLine("#else");
                writer.WriteLine(
                    $"static struct cx_interface_impl {closedInterfaceMap}[{closedInterfaces.Length}];");
                writer.WriteLine("#endif");
            }
            var runtimeTypeInfo = fields.Length == 0 && runtimeFunctionCount == 0 &&
                !factoryForStruct && closedInterfaces.Length == 0
                ? "CX_NULL"
                : $"(cx_ptr)&{identity.CIdentifier}___runtime_type_info";
            if (runtimeTypeInfo != "CX_NULL")
            {
                writer.WriteLine($"static const struct cx_runtime_type_info {identity.CIdentifier}___runtime_type_info = {{");
                writer.IncreaseIndent();
                writer.WriteLine($".interfaces = {(closedInterfaces.Length == 0 ? "CX_NULL" : $"(const struct cx_interface_impl*){closedInterfaceMap}")},");
                writer.WriteLine($".interfaceCount = {closedInterfaces.Length},");
                writer.WriteLine($".fields = {(fields.Length == 0 ? "CX_NULL" : $"(const struct cx_reflection_field*){closedFields}")},");
                writer.WriteLine($".fieldCount = {fields.Length},");
                writer.WriteLine($".functions = {(runtimeFunctionCount == 0 ? "CX_NULL" : $"(const struct cx_reflection_function*){closedFunctions ?? GetReflectionFunctionsIdentifier(declaration, moduleName).ToCIdentifier()}")},");
                writer.WriteLine($".functionCount = {runtimeFunctionCount},");
                writer.WriteLine($".createInstance = {(factoryConstructor is null && !factoryForStruct ? "CX_NULL" : factoryIdentifier)},");
                writer.WriteLine($".invokers = {closedInvokers ?? "CX_NULL"},");
                writer.DecreaseIndent();
                writer.WriteLine("};");
            }
            writer.WriteLine($"CX_STRING_DEF({closedNameIdentifier}, \"{declaration.Name}\");");
            writer.WriteLine($"CX_STRING_DEF({closedNamespaceIdentifier}, \"{declaration.Namespace}\");");
            writer.WriteLine();
            writer.WriteLine($"CX_BEGIN_VTABLE_DEF({identity.CIdentifier})");
            foreach (var slot in declaration.VirtualMethodSlots.OrderBy(slot => slot.Index))
            {
                writer.WriteLine(slot.Implementation is null
                    || slot.Implementation.GenericTypeNames.Length > 0
                    ? "{ .function = (cx_vtable_function)0 },"
                    : $"CX_VTABLE_ENTRY({ToCFunctionName(slot.Implementation, moduleName)})");
            }
            writer.WriteLine("CX_END_VTABLE_DEF;");
            if (RequiresClosedValueLayout(type) || closedInterfaces.Length != 0)
            {
                foreach (var table in declaration.InterfaceDispatchTables)
                {
                    var closedTable = GetClosedGenericInterfaceVTableIdentifier(
                        identity.CIdentifier,
                        table.Interface.ProjectName ?? moduleName,
                        table.Interface.FullName).ToCIdentifier();
                    foreach (var targetInterface in table.Interface.InterfaceUpcastTargets)
                    {
                        var targetTable = GetClosedGenericInterfaceVTableIdentifier(
                            identity.CIdentifier,
                            targetInterface.ProjectName ?? moduleName,
                            targetInterface.FullName).ToCIdentifier();
                        writer.WriteLine($"extern union cx_vtable_entry {targetTable}[];");
                    }
                    writer.WriteLine($"union cx_vtable_entry {closedTable}[] = {{");
                    writer.IncreaseIndent();
                    writer.WriteLine(
                        $"{{ .data = &CX_TYPEINFO_NAME({identity.CIdentifier}) }},");
                    foreach (var slot in table.Slots)
                    {
                        if (slot.Implementation is null ||
                            slot.ContractFunction is { GenericTypeNames.Length: > 0 })
                        {
                            writer.WriteLine("{ .function = (cx_vtable_function)0 },");
                        }
                        else if (closedInterfaceThunks.TryGetValue(
                            (table.Interface, slot.Index), out var closedThunk))
                        {
                            writer.WriteLine($"CX_VTABLE_ENTRY({closedThunk})");
                        }
                        else
                        {
                            var implementation = slot.ImplementationFunction ??
                                slot.ImplementationAccessor?.BodyFunction;
                            writer.WriteLine(implementation is null
                                ? "{ .function = (cx_vtable_function)0 },"
                                : $"CX_VTABLE_ENTRY({ToCFunctionName(implementation, moduleName)})");
                        }
                    }
                    foreach (var targetInterface in table.Interface.InterfaceUpcastTargets)
                    {
                        var targetTable = GetClosedGenericInterfaceVTableIdentifier(
                            identity.CIdentifier,
                            targetInterface.ProjectName ?? moduleName,
                            targetInterface.FullName).ToCIdentifier();
                        writer.WriteLine($"{{ .data = {targetTable} }} ,");
                    }
                    writer.DecreaseIndent();
                    writer.WriteLine("};");
                }
            }
            writer.WriteLine("#if !defined(CX_DYNAMIC_MODULE)");
            writer.WriteLine($"struct cxcore_System_Reflection_TypeInfo " +
                $"CX_TYPEINFO_NAME({identity.CIdentifier}) = {{");
            writer.IncreaseIndent();
            writer.WriteLine($".Hash = 0x{identity.RuntimeHash:X}ULL,");
            writer.WriteLine($".Flags = {string.Join(" | ", GetTypeInfoFlags(declaration))},");
            writer.WriteLine($".Size = {(declaration.ClassType == ClassType.Interface
                ? "0"
                : $"sizeof({(RequiresClosedValueLayout(type) ? $"struct {identity.CIdentifier}" : declaration.ToCIdentifier(containingTypeModule))})")},");
            writer.WriteLine($".Name = &{closedNameIdentifier},");
            writer.WriteLine($".Namespace = &{closedNamespaceIdentifier},");
            writer.WriteLine($".BaseType = {{ ._obj = {(declaration.ClassType == ClassType.Class ? $"&{GetClosedGenericClassBaseTypeInfoIdentifier(type, declaration)}" : "CX_NULL")} }},");
            writer.WriteLine($".RuntimeTypeInfo = {runtimeTypeInfo},");
            writer.WriteLine($".GenericArity = {declaration.GenericTypeNames.Length},");
            writer.DecreaseIndent();
            writer.WriteLine("};");
            writer.WriteLine("#else");
            writer.WriteLine($"struct cxcore_System_Reflection_TypeInfo " +
                $"CX_TYPEINFO_NAME({identity.CIdentifier}) = {{");
            writer.IncreaseIndent();
            writer.WriteLine($".Hash = 0x{identity.RuntimeHash:X}ULL,");
            writer.WriteLine($".Flags = {string.Join(" | ", GetTypeInfoFlags(declaration))},");
            writer.WriteLine($".Size = {(declaration.ClassType == ClassType.Interface
                ? "0"
                : $"sizeof({(RequiresClosedValueLayout(type) ? $"struct {identity.CIdentifier}" : declaration.ToCIdentifier(containingTypeModule))})")},");
            writer.WriteLine($".Name = &{closedNameIdentifier},");
            writer.WriteLine($".Namespace = &{closedNamespaceIdentifier},");
            writer.WriteLine(".BaseType = { ._obj = CX_NULL },");
            writer.WriteLine($".RuntimeTypeInfo = {runtimeTypeInfo},");
            writer.WriteLine($".GenericArity = {declaration.GenericTypeNames.Length},");
            writer.DecreaseIndent();
            writer.WriteLine("};");
            writer.WriteLine("#endif");
        }
    }

    private static void WriteEnumTypeInfoDefinition(
        IndentingWriter writer,
        EnumDeclaration declaration,
        string moduleName)
    {
        var nameIdentifier = new QualifiedIdentifier("__name", declaration.FullName).ToCIdentifier();
        var namespaceIdentifier = new QualifiedIdentifier("__namespace", declaration.Namespace).ToCIdentifier();
        var enumName = declaration.ToCIdentifier(moduleName);
        var visibility = declaration.Visibility switch
        {
            Visibility.Public => "CX_REFLECTION_FLAG_VISIBILITY_PUBLIC",
            Visibility.Protected => "CX_REFLECTION_FLAG_VISIBILITY_PROTECTED",
            Visibility.Internal => "CX_REFLECTION_FLAG_VISIBILITY_INTERNAL",
            Visibility.Private => "CX_REFLECTION_FLAG_VISIBILITY_PRIVATE",
            _ => "CX_REFLECTION_FLAG_VISIBILITY_HIDDEN",
        };
        writer.WriteLine($"struct cxcore_System_Reflection_TypeInfo {new QualifiedIdentifier(moduleName, declaration.FullName, "__typeinfo").ToCIdentifier()} = {{");
        writer.IncreaseIndent();
        writer.WriteLine($".Hash = 0x{GetTypeNameHash(declaration.FullName, moduleName):X},");
        writer.WriteLine($".Flags = {visibility} | CX_REFLECTION_FLAG_TYPE_ENUM,");
        writer.WriteLine($".Size = sizeof({enumName}),");
        writer.WriteLine($".Name = &{nameIdentifier},");
        writer.WriteLine($".Namespace = &{namespaceIdentifier},");
        writer.WriteLine(".BaseType = { ._obj = CX_NULL },");
        writer.DecreaseIndent();
        writer.WriteLine("};");
    }

    private static void WriteTypeInfoDefinition(
        IndentingWriter writer,
        ClassDeclaration classDeclaration,
        string moduleName)
    {
        var nameIdentifier = new QualifiedIdentifier("__name", classDeclaration.FullName);
        var namespaceIdentifier = new QualifiedIdentifier("__namespace", classDeclaration.Namespace);

        string[] flags = GetTypeInfoFlags(classDeclaration).ToArray();
        if (flags.Length == 0)
        {
            flags = ["0"];
        }
        string flagsString = string.Join(" | ", flags);

        var reflectedInterfaces = classDeclaration.InterfaceDispatchTables
            .Where(table => !HasOpenGenericInterfaceSignature(
                classDeclaration, table.Interface))
            .ToArray();
        var fields = GetReflectedFields(classDeclaration);
        var functionCount = GetReflectionFunctionCount(classDeclaration);
        var baseType = classDeclaration.ClassType == ClassType.Class && !classDeclaration.IsStatic &&
            !(moduleName == "cxcore" && classDeclaration.Name == "Object")
            ? classDeclaration.BaseClassType switch
            {
                NamedType namedType => $"&{new QualifiedIdentifier(namedType.ResolvedTypeFullName, "__typeinfo").ToCIdentifier()}",
                _ => "&cxcore_System_Object___typeinfo",
            }
            : "CX_NULL";
        var size = classDeclaration.IsStatic || classDeclaration.ClassType == ClassType.Interface
            ? "0"
            : $"sizeof({classDeclaration.ToCIdentifier(moduleName)})";
        var factoryConstructor = classDeclaration.ClassType == ClassType.Class &&
            !classDeclaration.IsStatic && !classDeclaration.IsAbstract &&
            classDeclaration.GenericTypeNames.Length == 0
            ? classDeclaration.MemberDeclarations.Declarations
                .OfType<ConstructorDeclaration>()
                .SingleOrDefault(constructor => constructor.Parameters.Count == 0 &&
                    constructor.MemberModifiers.Contains(MemberModifier.Public))
            : null;
        var factoryForStruct = classDeclaration.ClassType == ClassType.Struct &&
            !classDeclaration.IsStatic && classDeclaration.GenericTypeNames.Length == 0;
        var factoryIdentifier = GetReflectionFactoryIdentifier(classDeclaration, moduleName);
        if (factoryConstructor is not null || factoryForStruct)
        {
            var cType = classDeclaration.ToCIdentifier(moduleName, false);
            var cStorageType = $"struct {cType}";
            writer.WriteLine($"static cx_ptr {factoryIdentifier.ToCIdentifier()}(void) {{");
            writer.IncreaseIndent();
            if (factoryForStruct)
            {
                var typeInfo = new QualifiedIdentifier(
                    moduleName, classDeclaration.FullName, "__typeinfo").ToCIdentifier();
                writer.WriteLine($"{cStorageType} __instance = {{ 0 }};");
                writer.WriteLine($"return cx_box_value(&__instance, " +
                    $"(cx_uint)sizeof(__instance), &{typeInfo});");
            }
            else
            {
                writer.WriteLine($"{cStorageType} *__instance = ({cStorageType}*)" +
                    $"cxcore_System_Memory_Alloc((cx_uint)sizeof({cStorageType}));");
                writer.WriteLine($"{ToCFunctionName(factoryConstructor!, moduleName)}(__instance);");
                writer.WriteLine($"CX_INIT_VTABLE(__instance, {cType});");
                writer.WriteLine("return __instance;");
            }
            writer.DecreaseIndent();
            writer.WriteLine("}");
        }
        var hasRuntimeTypeInfo = reflectedInterfaces.Length != 0 || fields.Length != 0 ||
            functionCount != 0 || factoryConstructor is not null || factoryForStruct;
        var runtimeTypeInfo = !hasRuntimeTypeInfo
            ? "CX_NULL"
            : $"(cx_ptr)&{GetRuntimeTypeInfoIdentifier(classDeclaration, moduleName).ToCIdentifier()}";

        if (hasRuntimeTypeInfo)
        {
            writer.WriteLine($"static const struct cx_runtime_type_info {GetRuntimeTypeInfoIdentifier(classDeclaration, moduleName).ToCIdentifier()} = {{");
            writer.IncreaseIndent();
            writer.WriteLine($".interfaces = {(reflectedInterfaces.Length == 0 ? "CX_NULL" : $"(const struct cx_interface_impl*){GetInterfaceRuntimeMapIdentifier(classDeclaration, moduleName).ToCIdentifier()}")},");
            writer.WriteLine($".interfaceCount = {reflectedInterfaces.Length},");
            writer.WriteLine($".fields = {(fields.Length == 0 ? "CX_NULL" : $"(const struct cx_reflection_field*){GetReflectionFieldsIdentifier(classDeclaration, moduleName).ToCIdentifier()}")},");
            writer.WriteLine($".fieldCount = {fields.Length},");
            writer.WriteLine($".functions = {(functionCount == 0 ? "CX_NULL" : $"(const struct cx_reflection_function*){GetReflectionFunctionsIdentifier(classDeclaration, moduleName).ToCIdentifier()}")},");
            writer.WriteLine($".functionCount = {functionCount},");
            writer.WriteLine($".createInstance = {(factoryConstructor is null && !factoryForStruct ? "CX_NULL" : $"{factoryIdentifier.ToCIdentifier()}")},");
            writer.WriteLine($".invokers = {(functionCount == 0 ? "CX_NULL" : $"(const cx_reflection_invoker*){GetReflectionInvokersIdentifier(classDeclaration, moduleName).ToCIdentifier()}")},");
            writer.DecreaseIndent();
            writer.WriteLine("};");
        }

        writer.WriteLine("#if !defined(CX_DYNAMIC_MODULE)");
        writer.WriteLine($"struct cxcore_System_Reflection_TypeInfo {new QualifiedIdentifier(moduleName, classDeclaration.FullName, "__typeinfo").ToCIdentifier()} = {{");
        writer.IncreaseIndent();
        writer.WriteLine($".Hash = 0x{GetTypeNameHash(classDeclaration.FullName, moduleName, classDeclaration.GenericTypeNames.Length):X},");
        writer.WriteLine($".Flags = {flagsString},");
        writer.WriteLine($".Size = {size},");
        writer.WriteLine($".Name = &{nameIdentifier.ToCIdentifier()},");
        writer.WriteLine($".Namespace = &{namespaceIdentifier.ToCIdentifier()},");
        writer.WriteLine($".BaseType = {{ ._obj = {baseType} }},");
        writer.WriteLine($".RuntimeTypeInfo = {runtimeTypeInfo},");
        writer.WriteLine($".GenericArity = {classDeclaration.GenericTypeNames.Length},");
        writer.DecreaseIndent();
        writer.WriteLine("};");
        writer.WriteLine("#else");
        writer.WriteLine($"struct cxcore_System_Reflection_TypeInfo {new QualifiedIdentifier(moduleName, classDeclaration.FullName, "__typeinfo").ToCIdentifier()} = {{");
        writer.IncreaseIndent();
        writer.WriteLine($".Hash = 0x{GetTypeNameHash(classDeclaration.FullName, moduleName, classDeclaration.GenericTypeNames.Length):X},");
        writer.WriteLine($".Flags = {flagsString},");
        writer.WriteLine($".Size = {size},");
        writer.WriteLine($".Name = &{nameIdentifier.ToCIdentifier()},");
        writer.WriteLine($".Namespace = &{namespaceIdentifier.ToCIdentifier()},");
        writer.WriteLine(".BaseType = { ._obj = CX_NULL },");
        writer.WriteLine($".RuntimeTypeInfo = {runtimeTypeInfo},");
        writer.WriteLine($".GenericArity = {classDeclaration.GenericTypeNames.Length},");
        writer.DecreaseIndent();
        writer.WriteLine("};");
        writer.WriteLine("#endif");
    }

    private static IEnumerable<string> GetTypeInfoFlags(ClassDeclaration classDeclaration)
    {
        if (classDeclaration.IsStatic)
        {
            yield return "CX_REFLECTION_FLAG_STATIC";
        }
        if (classDeclaration.IsAbstract)
        {
            yield return "CX_REFLECTION_FLAG_ABSTRACT";
        }
        if (classDeclaration.IsFinal)
        {
            yield return "CX_REFLECTION_FLAG_FINAL";
        }
        if (classDeclaration.GenericTypeNames.Length > 0)
        {
            yield return "CX_REFLECTION_FLAG_GENERIC";
            yield return "CX_REFLECTION_FLAG_TYPE_GENERIC";
        }

        if (classDeclaration.Visibility == Visibility.Public)
        {
            yield return "CX_REFLECTION_FLAG_VISIBILITY_PUBLIC";
        }
        else if (classDeclaration.Visibility == Visibility.Protected)
        {
            yield return "CX_REFLECTION_FLAG_VISIBILITY_PROTECTED";
        }
        else if (classDeclaration.Visibility == Visibility.Internal)
        {
            yield return "CX_REFLECTION_FLAG_VISIBILITY_INTERNAL";
        }
        else if (classDeclaration.Visibility == Visibility.Private)
        {
            yield return "CX_REFLECTION_FLAG_VISIBILITY_PRIVATE";
        }
        else
        {
            yield return "CX_REFLECTION_FLAG_VISIBILITY_HIDDEN";
        }

        yield return classDeclaration.ClassType switch
        {
            ClassType.Class => "CX_REFLECTION_FLAG_TYPE_CLASS",
            ClassType.Struct => "CX_REFLECTION_FLAG_TYPE_STRUCT",
            ClassType.Interface => "CX_REFLECTION_FLAG_TYPE_INTERFACE",
            _ => throw new InternalCompilerException($"Unsupported class type: {classDeclaration.ClassType}")
        };
    }

    private static void WriteVTableDefinition(
        IndentingWriter writer,
        ClassDeclaration classDeclaration,
        string moduleName)
    {
        writer.WriteLine($"CX_BEGIN_VTABLE_DEF({classDeclaration.ToCIdentifier(moduleName, false)})");

        foreach (var slot in classDeclaration.VirtualMethodSlots.OrderBy(slot => slot.Index))
        {
            if (slot.Implementation is null ||
                slot.Implementation.GenericTypeNames.Length > 0)
            {
                writer.WriteLine("{ .function = (cx_vtable_function)0 },");
            }
            else
            {
                writer.WriteLine($"CX_VTABLE_ENTRY({ToCFunctionName(slot.Implementation, moduleName)})");
            }
        }

        writer.WriteLine("CX_END_VTABLE_DEF;");
    }

    private static void WriteInterfaceVTableDefinition(
        IndentingWriter writer,
        ClassDeclaration classDeclaration,
        InterfaceDispatchTable table,
        string moduleName)
    {
        var vtableName = GetInterfaceVTableIdentifier(
            classDeclaration,
            table.Interface,
            moduleName).ToCIdentifier();
        writer.WriteLine(
            $"CX_BEGIN_INTERFACE_VTABLE_DEF({vtableName}, " +
            $"{classDeclaration.ToCIdentifier(moduleName, false)})");
        foreach (var slot in table.Slots.OrderBy(slot => slot.Index))
        {
            writer.WriteLine(slot.Implementation is null ||
                slot.ContractFunction is { GenericTypeNames.Length: > 0 }
                ? "{ .function = (cx_vtable_function)0 },"
                : $"CX_VTABLE_ENTRY({GetInterfaceThunkIdentifier(classDeclaration, table.Interface, slot.Index, moduleName).ToCIdentifier()})");
        }
        foreach (var targetInterface in table.Interface.InterfaceUpcastTargets)
        {
            var targetVTable = GetInterfaceVTableIdentifier(
                classDeclaration,
                targetInterface,
                moduleName).ToCIdentifier();
            writer.WriteLine($"{{ .data = {targetVTable} }},");
        }
        writer.WriteLine("CX_END_VTABLE_DEF;");
    }

    private static void WriteInterfaceRuntimeMap(
        IndentingWriter writer,
        ClassDeclaration classDeclaration,
        string moduleName)
    {
        var interfaces = classDeclaration.InterfaceDispatchTables
            .Where(table => !HasOpenGenericInterfaceSignature(
                classDeclaration, table.Interface))
            .ToArray();
        if (interfaces.Length == 0)
        {
            return;
        }

        writer.WriteLine("#if !defined(CX_DYNAMIC_MODULE)");
        writer.WriteLine($"static const struct cx_interface_impl {GetInterfaceRuntimeMapIdentifier(classDeclaration, moduleName).ToCIdentifier()}[] = {{");
        writer.IncreaseIndent();
        foreach (var table in interfaces)
        {
            var @interface = table.Interface;
            var interfaceTypeInfo = GetInterfaceTypeInfoPointer(table, moduleName);
            var vtable = classDeclaration.ClassType == ClassType.Class ||
                classDeclaration.ClassType == ClassType.Struct &&
                classDeclaration.GenericTypeNames.Length == 0
                ? GetInterfaceVTableIdentifier(classDeclaration, @interface, moduleName).ToCIdentifier()
                : "CX_NULL";
            writer.WriteLine($"{{ {interfaceTypeInfo}, {vtable} }},");
        }
        writer.DecreaseIndent();
        writer.WriteLine("};");
        writer.WriteLine("#else");
        writer.WriteLine($"static struct cx_interface_impl {GetInterfaceRuntimeMapIdentifier(classDeclaration, moduleName).ToCIdentifier()}[{interfaces.Length}];");
        writer.WriteLine("#endif");
    }

    private static string GetInterfaceTypeInfoPointer(
        InterfaceDispatchTable table,
        string moduleName) => table.ConstructedInterfaceType is { } constructedType
            ? ToTypeInfoPointer(constructedType)
            : $"&{new QualifiedIdentifier(
                table.Interface.ProjectName ?? moduleName,
                table.Interface.FullName,
                "__typeinfo").ToCIdentifier()}";

    private static IEnumerable<ClassDeclaration> GetReflectedInterfaces(ClassDeclaration declaration)
    {
        return declaration.ClassType == ClassType.Interface
            ? declaration.InterfaceUpcastTargets
            : declaration.InterfaceDispatchTables.Select(table => table.Interface);
    }

    private static bool HasOpenGenericInterfaceSignature(
        ClassDeclaration classDeclaration,
        ClassDeclaration interfaceDeclaration) =>
        classDeclaration.GenericTypeNames.Length != 0 &&
        (classDeclaration.ClassType is ClassType.Struct or ClassType.Class ||
         interfaceDeclaration.GenericTypeNames.Length != 0);

    private static FieldDeclaration[] GetReflectedFields(ClassDeclaration declaration) =>
        declaration.MemberDeclarations.Declarations.OfType<FieldDeclaration>()
            .Where(field => !field.IsAutoPropertyBackingField)
            .ToArray();

    private static void WriteReflectionMetadata(
        IndentingWriter writer,
        ClassDeclaration declaration,
        string moduleName)
    {
        var fields = GetReflectedFields(declaration);
        if (fields.Length > 0)
        {
            writer.WriteLine("#if !defined(CX_DYNAMIC_MODULE)");
            writer.WriteLine($"static const struct cx_reflection_field {GetReflectionFieldsIdentifier(declaration, moduleName).ToCIdentifier()}[] = {{");
            writer.IncreaseIndent();
            foreach (var field in fields)
            {
                var offset = field.IsStatic
                    ? "CX_REFLECTION_NO_OFFSET"
                    : $"(cx_uint)offsetof({declaration.ToCIdentifier(moduleName)}, {field.Name})";
                writer.WriteLine(
                    $"{{ {GetMemberFlags(field.MemberModifiers)}, {offset}, " +
                    $"{ToTypeInfoPointer(field.Type)}, \"{field.Name}\" }},");
            }
            writer.DecreaseIndent();
            writer.WriteLine("};");
            writer.WriteLine("#else");
            writer.WriteLine($"static struct cx_reflection_field {GetReflectionFieldsIdentifier(declaration, moduleName).ToCIdentifier()}[{fields.Length}];");
            writer.WriteLine("#endif");
        }

        var functions = GetReflectionFunctions(declaration).ToArray();
        if (functions.Length != 0)
        {
            WriteReflectionInvokers(writer, declaration, moduleName, functions);
        }
        for (var index = 0; index < functions.Length; index++)
        {
            var function = functions[index];
            if (function.Parameters.Count == 0)
            {
                continue;
            }
            writer.WriteLine("#if !defined(CX_DYNAMIC_MODULE)");
            writer.WriteLine($"static const struct cx_reflection_parameter {GetReflectionParametersIdentifier(declaration, moduleName, index).ToCIdentifier()}[] = {{");
            writer.IncreaseIndent();
            foreach (var parameter in function.Parameters)
            {
                writer.WriteLine(
                    $"{{ 0, {ToTypeInfoPointer(parameter.ParameterType)}, " +
                    $"\"{parameter.Name}\", CX_NULL }},");
            }
            writer.DecreaseIndent();
            writer.WriteLine("};");
            writer.WriteLine("#else");
            writer.WriteLine($"static struct cx_reflection_parameter {GetReflectionParametersIdentifier(declaration, moduleName, index).ToCIdentifier()}[{function.Parameters.Count}];");
            writer.WriteLine("#endif");
        }

        if (functions.Length == 0)
        {
            return;
        }
        writer.WriteLine("#if !defined(CX_DYNAMIC_MODULE)");
        writer.WriteLine($"static const struct cx_reflection_function {GetReflectionFunctionsIdentifier(declaration, moduleName).ToCIdentifier()}[] = {{");
        writer.IncreaseIndent();
        for (var index = 0; index < functions.Length; index++)
        {
            var function = functions[index];
            var parameters = function.Parameters.Count == 0
                ? "CX_NULL"
                : GetReflectionParametersIdentifier(declaration, moduleName, index).ToCIdentifier();
            var slot = function.VirtualSlotIndex is { } virtualSlot
                ? virtualSlot.ToString()
                : "CX_REFLECTION_NO_SLOT";
            writer.WriteLine(
                $"{{ {GetFunctionFlags(function)}, {slot}, " +
                $"{ToTypeInfoPointer(function.ReturnType)}, \"{function.Name}\", " +
                $"{parameters}, {function.Parameters.Count} }},");
        }
        writer.DecreaseIndent();
        writer.WriteLine("};");
        writer.WriteLine("#else");
        writer.WriteLine($"static struct cx_reflection_function {GetReflectionFunctionsIdentifier(declaration, moduleName).ToCIdentifier()}[{functions.Length}];");
        writer.WriteLine("#endif");
    }

    private static void WriteReflectionInvokers(
        IndentingWriter writer,
        ClassDeclaration declaration,
        string moduleName,
        IReadOnlyList<ReflectionFunctionSource> functions)
    {
        for (var index = 0; index < functions.Count; index++)
        {
            if (!CanGenerateReflectionInvoker(declaration, functions[index]))
            {
                continue;
            }

            var function = functions[index].Declaration!;
            var invokerName = GetReflectionInvokerIdentifier(declaration, moduleName, index)
                .ToCIdentifier();
            writer.WriteLine($"static cx_ptr {invokerName}(cx_ptr __target, const cx_ptr *__arguments, cx_uint __argumentCount) CX_MAYBE_UNUSED;");
            writer.WriteLine($"static cx_ptr {invokerName}(cx_ptr __target, " +
                "const cx_ptr *__arguments, cx_uint __argumentCount)");
            writer.WriteLine("{");
            writer.IncreaseIndent();
            writer.WriteLine($"if (__argumentCount != {function.Parameters.Count} || " +
                $"({function.Parameters.Count} != 0 && __arguments == CX_NULL))");
            writer.WriteLine("{");
            writer.IncreaseIndent();
            writer.WriteLine("return CX_NULL;");
            writer.DecreaseIndent();
            writer.WriteLine("}");
            if (function.IsStatic)
            {
                writer.WriteLine("(void)__target;");
            }
            if (function.Parameters.Count == 0)
            {
                writer.WriteLine("(void)__arguments;");
            }

            var callArguments = new List<string>();
            var isConstructor = function is ConstructorDeclaration;
            FunctionDeclaration? virtualContract = function.VirtualSlotIndex is not null
                ? function.VirtualContract ?? function
                : null;
            if (isConstructor)
            {
                writer.WriteLine($"if (__target != CX_NULL) return CX_NULL;");
                if (declaration.ClassType == ClassType.Struct)
                {
                    var structType = declaration.ToCIdentifier(moduleName);
                    writer.WriteLine($"{structType} __instance = {{ 0 }};");
                    callArguments.Add("&__instance");
                }
                else
                {
                    var classType = declaration.ToCIdentifier(moduleName, false);
                    var classStorageType = $"struct {classType}";
                    writer.WriteLine($"{classStorageType} *__instance = " +
                        $"({classStorageType}*)cxcore_System_Memory_Alloc(" +
                        $"(cx_uint)sizeof({classStorageType}));");
                    callArguments.Add("__instance");
                }
            }
            else if (!function.IsStatic)
            {
                var receiverConst = (virtualContract?.Const ?? function.Const)
                    ? "const "
                    : string.Empty;
                if (declaration.ClassType == ClassType.Struct)
                {
                    var structType = declaration.ToCIdentifier(moduleName);
                    var typeInfo = new QualifiedIdentifier(
                        moduleName, declaration.FullName, "__typeinfo").ToCIdentifier();
                    callArguments.Add(
                        $"({receiverConst}{structType}*)cx_unbox_value(__target, &{typeInfo})");
                }
                else
                {
                    var receiverDeclaration = virtualContract?.ParentClassDeclaration ?? declaration;
                    callArguments.Add(
                        $"({receiverConst}{receiverDeclaration.ToCIdentifier(receiverDeclaration.ProjectName ?? moduleName)}*)__target");
                }
            }
            var parameters = virtualContract?.Parameters ?? function.Parameters;
            for (var parameterIndex = 0; parameterIndex < parameters.Count; parameterIndex++)
            {
                var parameter = parameters[parameterIndex];
                if (IsReflectionInterfaceType(parameter.ParameterType))
                {
                    var interfaceArgumentName = $"__cx_interface_argument_{parameterIndex}";
                    writer.WriteLine(
                        $"struct cx_iface_ref {interfaceArgumentName} = " +
                        $"cx_checked_cast_object_to_interface(__arguments[{parameterIndex}], " +
                        $"{ToTypeInfoPointer(parameter.ParameterType)});");
                    callArguments.Add(interfaceArgumentName);
                }
                else if (IsReflectionValueType(parameter.ParameterType))
                {
                    var valueArgumentName = $"__cx_value_argument_{parameterIndex}";
                    var valueType = UnwrapConst(parameter.ParameterType).ToCIdentifier(false);
                    writer.WriteLine($"const {valueType} {valueArgumentName} = " +
                        $"*((const {valueType}*)cx_unbox_value(" +
                        $"__arguments[{parameterIndex}], {ToTypeInfoPointer(parameter.ParameterType)}));");
                    callArguments.Add(valueArgumentName);
                }
                else
                {
                    callArguments.Add(
                        $"({parameter.ParameterType.ToCIdentifier(false)})__arguments[{parameterIndex}]");
                }
            }
            string call;
            if (virtualContract is { VirtualSlotIndex: { } slotIndex } contract)
            {
                var contractParent = contract.ParentClassDeclaration!;
                var parameterTypes = new[]
                {
                    $"{(contract.Const ? "const " : string.Empty)}" +
                        $"{contractParent.ToCIdentifier(contractParent.ProjectName ?? moduleName)}*",
                }.Concat(contract.Parameters.Select(parameter =>
                    parameter.ParameterType.ToCIdentifier(false)));
                var functionPointer =
                    $"({contract.ReturnType.ToCReturnType(false)} (*)({string.Join(", ", parameterTypes)}))";
                call = $"({functionPointer}((union cx_vtable_entry*)" +
                    $"CX_GET_VTABLE({callArguments[0]}))[{slotIndex}].function)" +
                    $"({string.Join(", ", callArguments)})";
            }
            else
            {
                call = $"{ToCFunctionName(function, moduleName)}(" +
                    $"{string.Join(", ", callArguments)})";
            }
            if (isConstructor)
            {
                writer.WriteLine($"{call};");
                if (declaration.ClassType == ClassType.Struct)
                {
                    var typeInfo = new QualifiedIdentifier(
                        moduleName, declaration.FullName, "__typeinfo").ToCIdentifier();
                    writer.WriteLine("return cx_box_value(&__instance, " +
                        $"(cx_uint)sizeof(__instance), &{typeInfo});");
                }
                else
                {
                    writer.WriteLine("return (cx_ptr)__instance;");
                }
            }
            else if (function.ReturnType is VoidType)
            {
                writer.WriteLine($"{call};");
                writer.WriteLine("return CX_NULL;");
            }
            else if (IsReflectionInterfaceType(function.ReturnType))
            {
                writer.WriteLine($"return (cx_ptr)({call}).instance;");
            }
            else if (IsReflectionValueType(function.ReturnType))
            {
                var valueType = function.ReturnType.ToCIdentifier(false);
                writer.WriteLine($"{valueType} __cx_value_return = {call};");
                writer.WriteLine("return cx_box_value(&__cx_value_return, " +
                    "(cx_uint)sizeof(__cx_value_return), " +
                    $"{ToTypeInfoPointer(function.ReturnType)});");
            }
            else
            {
                writer.WriteLine($"return (cx_ptr){call};");
            }
            writer.DecreaseIndent();
            writer.WriteLine("}");
        }

        writer.WriteLine($"static const cx_reflection_invoker " +
            $"{GetReflectionInvokersIdentifier(declaration, moduleName).ToCIdentifier()}[] = {{");
        writer.IncreaseIndent();
        for (var index = 0; index < functions.Count; index++)
        {
            var invoker = CanGenerateReflectionInvoker(declaration, functions[index])
                ? $"{GetReflectionInvokerIdentifier(declaration, moduleName, index).ToCIdentifier()}"
                : "CX_NULL";
            writer.WriteLine($"{invoker},");
        }
        writer.DecreaseIndent();
        writer.WriteLine("};");
    }

    private static bool CanGenerateReflectionInvoker(
        ClassDeclaration declaration,
        ReflectionFunctionSource function)
    {
        if (declaration.ClassType is not (ClassType.Class or ClassType.Struct) ||
            declaration.GenericTypeNames.Length != 0 ||
            function.Declaration is not { } method ||
            method.GenericTypeNames.Length != 0 ||
            method.MemberModifiers.Contains(MemberModifier.Abstract) ||
            method.Body is null)
        {
            return false;
        }

        if (method is ConstructorDeclaration &&
            (declaration.ClassType is not (ClassType.Class or ClassType.Struct) ||
             declaration.ClassType == ClassType.Class && declaration.IsAbstract ||
             !method.MemberModifiers.Contains(MemberModifier.Public)))
        {
            return false;
        }

        return function.Parameters.All(parameter =>
                IsReflectionParameterType(parameter.ParameterType)) &&
            (method is ConstructorDeclaration || function.ReturnType is VoidType ||
             IsReflectionObjectReferenceType(function.ReturnType) ||
             IsReflectionInterfaceType(function.ReturnType) ||
             IsReflectionValueType(function.ReturnType));
    }

    private static bool IsReflectionParameterType(TypeBase type) =>
        IsReflectionObjectReferenceType(type) || IsReflectionInterfaceType(type) ||
        IsReflectionValueType(type);

    private static bool IsReflectionValueType(TypeBase type)
    {
        if (type is ConstType constant)
        {
            type = constant.UnderlyingType;
        }

        return type is ValueTypeBase && type is not PtrType ||
            type is NamedType
            {
                ClassType: ClassType.Struct or ClassType.Enum,
                IsDelegate: false,
            };
    }

    private static bool IsReflectionInterfaceType(TypeBase type)
    {
        if (type is ConstType constant)
        {
            type = constant.UnderlyingType;
        }

        return type is NamedType
        {
            ClassType: ClassType.Interface,
            TypeArguments.Count: 0,
        };
    }

    private static bool IsReflectionObjectReferenceType(TypeBase type)
    {
        if (type is ConstType constant)
        {
            return IsReflectionObjectReferenceType(constant.UnderlyingType);
        }

        return type is ReferenceTypeBase or ArrayType ||
            type is NamedType
            {
                ClassType: ClassType.Class,
                IsDelegate: false,
            };
    }

    private static QualifiedIdentifier GetReflectionInvokerIdentifier(
        ClassDeclaration declaration,
        string moduleName,
        int index) =>
        new(moduleName, declaration.FullName, $"__reflection_invoke_{index}");

    private static IEnumerable<ReflectionFunctionSource> GetReflectionFunctions(
        ClassDeclaration declaration)
    {
        foreach (var function in declaration.MemberDeclarations.Declarations
            .OfType<FunctionDeclaration>()
            .Where(function => function is not ConstructorDeclaration { IsStatic: true } &&
                !function.IsGenericSpecialization))
        {
            yield return new ReflectionFunctionSource(
                function is ConstructorDeclaration ? declaration.Name : function.Name,
                function.ReturnType,
                function.Parameters,
                function.MemberModifiers,
                function.Const,
                function is ConstructorDeclaration,
                null,
                function.VirtualSlotIndex,
                Declaration: function);
        }
        foreach (var property in declaration.MemberDeclarations.Declarations.OfType<PropertyDeclaration>())
        {
            foreach (var accessor in property.PropertyAccessorDeclarations)
            {
                var parameters = accessor.Parameters.ToList();
                if (accessor.Name == "set")
                {
                    parameters.Add(new FunctionParameter("value", property.Type, null));
                }
                yield return new ReflectionFunctionSource(
                    property.Name,
                    accessor.Name == "set" ? BuiltInSystemTypes.Void : property.Type,
                    parameters,
                    property.MemberModifiers,
                    accessor.Const,
                    false,
                    accessor.Name,
                    accessor.BodyFunction?.VirtualSlotIndex,
                    accessor.Extern,
                    Declaration: null);
            }
        }
    }

    private static int GetReflectionFunctionCount(ClassDeclaration declaration)
    {
        return GetReflectionFunctions(declaration).Count();
    }

    private static QualifiedIdentifier GetReflectionFieldsIdentifier(
        ClassDeclaration declaration,
        string moduleName) =>
        new(moduleName, declaration.FullName, "__reflection_fields");

    private static QualifiedIdentifier GetReflectionFunctionsIdentifier(
        ClassDeclaration declaration,
        string moduleName) =>
        new(moduleName, declaration.FullName, "__reflection_functions");

    private static QualifiedIdentifier GetReflectionParametersIdentifier(
        ClassDeclaration declaration,
        string moduleName,
        int index) =>
        new(moduleName, declaration.FullName, $"__reflection_params_{index}");

    private static QualifiedIdentifier GetReflectionFactoryIdentifier(
        ClassDeclaration declaration,
        string moduleName) =>
        new(moduleName, declaration.FullName, "__reflection_create_instance");

    private static QualifiedIdentifier GetReflectionInvokersIdentifier(
        ClassDeclaration declaration,
        string moduleName) =>
        new(moduleName, declaration.FullName, "__reflection_invokers");

    private static string ToTypeInfoPointer(TypeBase type)
    {
        type = type is ConstType constType ? constType.UnderlyingType : type;
        if (UnwrapConst(type) is NamedType { NativeType: not null } or NativeScalarType) return "CX_NULL";
        if (type is PtrType or FunctionType or AutoType or GenericType or NullType ||
            type is NamedType { IsDelegate: true })
        {
            return "CX_NULL";
        }
        return $"&{GetTypeInfoIdentifier(type).ToCIdentifier()}";
    }

    private static string GetMemberFlags(IEnumerable<MemberModifier> modifiers)
    {
        var modifierSet = modifiers.ToHashSet();
        var flags = new List<string>();
        if (modifierSet.Contains(MemberModifier.Public)) flags.Add("CX_REFLECTION_FLAG_VISIBILITY_PUBLIC");
        else if (modifierSet.Contains(MemberModifier.Protected)) flags.Add("CX_REFLECTION_FLAG_VISIBILITY_PROTECTED");
        else if (modifierSet.Contains(MemberModifier.Internal)) flags.Add("CX_REFLECTION_FLAG_VISIBILITY_INTERNAL");
        else if (modifierSet.Contains(MemberModifier.Private)) flags.Add("CX_REFLECTION_FLAG_VISIBILITY_PRIVATE");
        else flags.Add("CX_REFLECTION_FLAG_VISIBILITY_HIDDEN");
        if (modifierSet.Contains(MemberModifier.Static)) flags.Add("CX_REFLECTION_FLAG_STATIC");
        if (modifierSet.Contains(MemberModifier.Virtual) || modifierSet.Contains(MemberModifier.Override)) flags.Add("CX_REFLECTION_FLAG_VIRTUAL");
        if (modifierSet.Contains(MemberModifier.Abstract)) flags.Add("CX_REFLECTION_FLAG_ABSTRACT");
        if (modifierSet.Contains(MemberModifier.Final)) flags.Add("CX_REFLECTION_FLAG_FINAL");
        if (modifierSet.Contains(MemberModifier.Extern)) flags.Add("CX_REFLECTION_FLAG_EXTERN");
        return string.Join(" | ", flags);
    }

    private static string GetFunctionFlags(ReflectionFunctionSource function)
    {
        var flags = new List<string> { GetMemberFlags(function.Modifiers) };
        if (function.Const) flags.Add("CX_REFLECTION_FLAG_CONST_CALL");
        if (function.Extern) flags.Add("CX_REFLECTION_FLAG_EXTERN");
        if (function.IsConstructor) flags.Add("CX_REFLECTION_FLAG_FUNCTION_CONSTRUCTOR");
        if (function.AccessorKind == "get") flags.Add("CX_REFLECTION_FLAG_FUNCTION_PROPERTY_GET");
        if (function.AccessorKind == "set") flags.Add("CX_REFLECTION_FLAG_FUNCTION_PROPERTY_SET");
        return string.Join(" | ", flags.Distinct());
    }

    private sealed record ReflectionFunctionSource(
        string Name,
        TypeBase ReturnType,
        IReadOnlyList<FunctionParameter> Parameters,
        MemberModifier[] Modifiers,
        bool Const,
        bool IsConstructor,
        string? AccessorKind,
        int? VirtualSlotIndex,
        bool Extern = false,
        FunctionDeclaration? Declaration = null);

    private static QualifiedIdentifier GetInterfaceRuntimeMapIdentifier(
        ClassDeclaration classDeclaration,
        string moduleName)
    {
        return new QualifiedIdentifier(moduleName, classDeclaration.FullName, "__interfaces");
    }

    private static QualifiedIdentifier GetRuntimeTypeInfoIdentifier(
        ClassDeclaration classDeclaration,
        string moduleName)
    {
        return new QualifiedIdentifier(moduleName, classDeclaration.FullName, "__runtime_type_info");
    }

    private static void WriteInterfaceDispatchThunks(
        IndentingWriter writer,
        IEnumerable<DeclarationBase> declarations,
        string moduleName)
    {
        foreach (var classDeclaration in EnumerateClasses(declarations)
            .Where(type => type.ClassType is ClassType.Class or ClassType.Struct))
        {
            foreach (var table in classDeclaration.InterfaceDispatchTables.Where(table =>
                !HasOpenGenericInterfaceSignature(classDeclaration, table.Interface)))
            {
                var vtableName = GetInterfaceVTableIdentifier(
                    classDeclaration,
                    table.Interface,
                    moduleName).ToCIdentifier();
                writer.WriteLine($"extern union cx_vtable_entry {vtableName}[];");
            }
        }
        writer.WriteLine();

        foreach (var classDeclaration in EnumerateClasses(declarations)
            .Where(type => type.ClassType is ClassType.Class or ClassType.Struct))
        {
            foreach (var table in classDeclaration.InterfaceDispatchTables.Where(table =>
                !HasOpenGenericInterfaceSignature(classDeclaration, table.Interface)))
            {
                foreach (var slot in table.Slots.Where(slot =>
                    slot.Implementation is not null &&
                    slot.ContractFunction is not
                        { GenericTypeNames.Length: > 0 }))
                {
                    if (table.ConstructedInterfaceType is { } constructedInterface)
                    {
                        WriteClosedGenericInterfaceDispatchThunk(
                            writer,
                            classDeclaration.ToCIdentifier(moduleName, false),
                            classDeclaration,
                            table.Interface,
                            constructedInterface,
                            slot,
                            new Dictionary<string, TypeBase>(StringComparer.Ordinal),
                            moduleName,
                            GetInterfaceThunkIdentifier(
                                classDeclaration,
                                table.Interface,
                                slot.Index,
                                moduleName).ToCIdentifier());
                    }
                    else
                    {
                        WriteInterfaceDispatchThunk(
                            writer,
                            classDeclaration,
                            table.Interface,
                            slot,
                            moduleName);
                    }
                }
            }
        }
    }

    private static void WriteInterfaceDispatchThunk(
        IndentingWriter writer,
        ClassDeclaration classDeclaration,
        ClassDeclaration interfaceDeclaration,
        InterfaceDispatchSlot slot,
        string moduleName)
    {
        EnsureConstructedGenericIdentities(slot.ReturnType);
        var thunkName = GetInterfaceThunkIdentifier(
            classDeclaration,
            interfaceDeclaration,
            slot.Index,
            moduleName).ToCIdentifier();
        var parameters = new[] { "cx_ptr __root" }.Concat(slot.Parameters.Select(
            parameter => $"{parameter.ParameterType.ToCIdentifier(false)} {parameter.Name}"));
        writer.WriteLine(
            $"static {slot.ReturnType.ToCReturnType(false)} {thunkName}(" +
            $"{string.Join(", ", parameters)}) {{");
        writer.IncreaseIndent();
        writer.WriteLine($"__cx_module_init_{GetModuleToken(moduleName)}();");

        var argumentNames = slot.Parameters.Select(parameter => parameter.Name).ToArray();
        string call;
        if (slot.ImplementationFunction is { } implementation &&
            implementation.VirtualSlotIndex is { } virtualSlotIndex)
        {
            var virtualContract = implementation.VirtualContract ?? implementation;
            var receiverConst = virtualContract.Const ? "const " : string.Empty;
            var receiverType =
                $"{receiverConst}{virtualContract.ParentClassDeclaration!.ToCIdentifier(moduleName)}*";
            var parameterTypes = new[] { receiverType }.Concat(
                virtualContract.Parameters.Select(parameter =>
                    parameter.ParameterType.ToCIdentifier(false)));
            var functionPointer =
                $"({virtualContract.ReturnType.ToCReturnType(false)} (*)({string.Join(", ", parameterTypes)}))";
            var receiver = $"({receiverType})__root";
            call = $"({functionPointer}((union cx_vtable_entry*)CX_GET_VTABLE({receiver}))[{virtualSlotIndex}].function)" +
                $"({string.Join(", ", new[] { receiver }.Concat(argumentNames))})";
        }
        else if (slot.ImplementationFunction is { } directImplementation)
        {
            var receiverConst = directImplementation.Const ? "const " : string.Empty;
            var receiver =
                $"({receiverConst}{directImplementation.ParentClassDeclaration!.ToCIdentifier(moduleName)}*)__root";
            call = $"{ToCFunctionName(directImplementation, moduleName)}(" +
                $"{string.Join(", ", new[] { receiver }.Concat(argumentNames))})";
        }
        else
        {
            var accessor = slot.ImplementationAccessor!;
            var property = accessor.ParentPropertyDeclaration;
            var receiverConst = accessor.Const ? "const " : string.Empty;
            var receiver =
                $"({receiverConst}{property.ParentClassDeclaration.ToCIdentifier(moduleName)}*)__root";
            call = $"{ToCPropertyAccessorName(accessor, moduleName)}(" +
                $"{string.Join(", ", new[] { receiver }.Concat(argumentNames))})";
        }

        writer.WriteLine(slot.ReturnType is VoidType ? $"{call};" : $"return {call};");
        writer.DecreaseIndent();
        writer.WriteLine("}");
        writer.WriteLine();
    }

    private static void WriteClosedGenericInterfaceDispatchThunk(
        IndentingWriter writer,
        string closedTypeIdentifier,
        ClassDeclaration classDeclaration,
        ClassDeclaration interfaceDeclaration,
        NamedType closedInterfaceType,
        InterfaceDispatchSlot slot,
        IReadOnlyDictionary<string, TypeBase> classArguments,
        string moduleName,
        string thunkName,
        IReadOnlyCollection<FunctionSymbol>? genericFunctions = null,
        NamedType? closedOwnerType = null)
    {
        var interfaceArguments = interfaceDeclaration.GenericTypeNames
            .Zip(closedInterfaceType.TypeArguments)
            .ToDictionary(pair => pair.First, pair => pair.Second);
        var returnType = GenericTypeSubstitution.Substitute(slot.ReturnType, interfaceArguments);
        EnsureConstructedGenericIdentities(returnType);
        var parameters = slot.Parameters.Select(parameter =>
            (Parameter: parameter,
                Type: GenericTypeSubstitution.Substitute(parameter.ParameterType, interfaceArguments)))
            .ToArray();
        var parameterDeclarations = new[] { "cx_ptr __root" }.Concat(parameters.Select(item =>
            $"{item.Type.ToCIdentifier(false)} {item.Parameter.Name}"));
        writer.WriteLine(
            $"static {returnType.ToCReturnType(false)} {thunkName}(" +
            $"{string.Join(", ", parameterDeclarations)}) {{");
        writer.IncreaseIndent();
        writer.WriteLine($"__cx_module_init_{GetModuleToken(moduleName)}();");

        string call;
        if (slot.ImplementationFunction is { } implementation)
        {
            var implementationFields = classDeclaration.MemberDeclarations.Declarations
                .OfType<FieldDeclaration>().ToArray();
            if (IsReferenceFieldGetter(implementation, implementationFields) ||
                IsClosedGenericFieldGetter(implementation, classDeclaration, implementationFields))
            {
                var field = ((ReturnStatement)implementation.Body![0]).Expression is
                    IdentifierExpression { TargetField: { } targetField }
                        ? targetField.Declaration
                        : throw new InternalCompilerException(
                            $"Supported generic getter '{implementation.FullName}' has no field target.");
                call = $"(({(implementation.Const ? "const " : string.Empty)}struct {closedTypeIdentifier}*)__root)->{field.Name}";
            }
            else if (IsClosedGenericNoOpMethod(implementation))
            {
                call = "(void)0";
            }
            else if (GetClosedGenericSimpleFieldAssignments(
                    implementation, classDeclaration, implementationFields) is
                { } simpleAssignments)
            {
                var instance = $"((struct {closedTypeIdentifier}*)__root)";
                var parameterNames = new Dictionary<string, string>(StringComparer.Ordinal);
                call = string.Join(" ", simpleAssignments.Select(assignment =>
                {
                    var fieldType = GenericTypeSubstitution.Substitute(
                        assignment.Field.Type, classArguments);
                    var value = assignment.Value is
                        LiteralExpression { SourceText: "default" }
                            ? ToCDefaultValue(fieldType)
                            : ToCClosedGenericConcreteMethodExpression(
                                assignment.Value, parameterNames, implementation,
                                closedTypeIdentifier, moduleName, instance);
                    return $"{instance}->{assignment.Field.Name} = {value};";
                }));
            }
            else if (GetGenericFieldSetterParameterIndex(implementation, implementationFields) is not null ||
                GetClosedGenericFieldSetterParameterIndex(
                    implementation, classDeclaration, implementationFields) is not null)
            {
                var assignment = (AssignmentExpression)((ExpressionStatement)implementation.Body![0]).Expression;
                var field = ((IdentifierExpression)assignment.Target).TargetField!.Declaration;
                var setterParameterIndex = GetGenericFieldSetterParameterIndex(
                        implementation, implementationFields) ??
                    GetClosedGenericFieldSetterParameterIndex(
                        implementation, classDeclaration, implementationFields) ??
                    throw new InternalCompilerException(
                        $"Supported generic setter '{implementation.FullName}' has no parameter mapping.");
                var value = parameters[setterParameterIndex].Parameter.Name;
                call = $"((struct {closedTypeIdentifier}*)__root)->{field.Name} = {value}";
            }
            else if (GetGenericFieldSetterAssignments(
                    implementation, implementationFields) is { Count: > 1 } genericAssignments)
            {
                call = string.Join("; ", genericAssignments.Select(assignment =>
                    $"((struct {closedTypeIdentifier}*)__root)->{assignment.Field.Name} = " +
                    parameters[assignment.ParameterIndex].Parameter.Name));
            }
            else if (GetClosedGenericMixedFieldAssignments(
                    implementation, classDeclaration, implementationFields) is { } mixedAssignments)
            {
                var implementationParameterNames = implementation.Parameters
                    .Select((parameter, index) =>
                        (parameter.Name, ThunkName: parameters[index].Parameter.Name))
                    .ToDictionary(item => item.Name, item => item.ThunkName,
                        StringComparer.Ordinal);
                call = string.Join("; ", mixedAssignments.Select(assignment =>
                {
                    var field = ((IdentifierExpression)assignment.Target)
                        .TargetField!.Declaration;
                    string value;
                    if (ContainsGenericParameter(
                            field.Type, classDeclaration.GenericTypeNames))
                    {
                        var parameterName = ((IdentifierExpression)assignment.Value)
                            .Identifier.ToString();
                        var parameterIndex = implementation.Parameters
                            .Select((parameter, index) => (parameter, index))
                            .First(item => item.parameter.Name == parameterName).index;
                        value = parameters[parameterIndex].Parameter.Name;
                    }
                    else
                    {
                        value = ToCClosedGenericConcreteMethodExpression(
                            assignment.Value,
                            implementationParameterNames,
                            implementation,
                            closedTypeIdentifier,
                            moduleName);
                    }
                    return $"((struct {closedTypeIdentifier}*)__root)->{field.Name} = {value}";
                }));
            }
            else if (IsClosedGenericFieldConstantSetter(
                implementation, classDeclaration, implementationFields))
            {
                var assignment = (AssignmentExpression)((ExpressionStatement)implementation.Body![0]).Expression;
                var field = ((IdentifierExpression)assignment.Target).TargetField!.Declaration;
                var value = ToCExpressionAsType(
                    assignment.Value,
                    field.Type,
                    implementation,
                    moduleName);
                call = $"((struct {closedTypeIdentifier}*)__root)->{field.Name} = {value}";
            }
            else if (GetClosedGenericDefaultFieldSetter(
                    implementation, classDeclaration, implementationFields) is { } defaultField)
            {
                var fieldType = GenericTypeSubstitution.Substitute(
                    defaultField.Type, classArguments);
                call = $"((struct {closedTypeIdentifier}*)__root)->{defaultField.Name} = " +
                    ToCDefaultValue(fieldType);
            }
            else if (GetClosedGenericDefaultFieldAssignments(
                    implementation, classDeclaration, implementationFields) is { } defaultFields)
            {
                call = string.Join("; ", defaultFields.Select(field =>
                {
                    var fieldType = GenericTypeSubstitution.Substitute(
                        field.Type, classArguments);
                    return $"((struct {closedTypeIdentifier}*)__root)->{field.Name} = " +
                        ToCDefaultValue(fieldType);
                }));
            }
            else if (GetClosedGenericConditionalDefaultFieldReset(
                    implementation, classDeclaration, implementationFields) is
                { } conditionalReset)
            {
                var instance = $"((struct {closedTypeIdentifier}*)__root)";
                var parameterNames = implementation.Parameters
                    .Select((parameter, index) =>
                        (parameter.Name, ThunkName: parameters[index].Parameter.Name))
                    .ToDictionary(item => item.Name, item => item.ThunkName,
                        StringComparer.Ordinal);
                var condition = ToCClosedGenericConcreteMethodExpression(
                    conditionalReset.Condition,
                    parameterNames,
                    implementation,
                    closedTypeIdentifier,
                    moduleName,
                    instance);
                var trueType = GenericTypeSubstitution.Substitute(
                    conditionalReset.WhenTrue.Type, classArguments);
                var falseType = GenericTypeSubstitution.Substitute(
                    conditionalReset.WhenFalse.Type, classArguments);
                call = $"if ({condition}) {{ {instance}->{conditionalReset.WhenTrue.Name} = " +
                    $"{ToCDefaultValue(trueType)}; }} else {{ " +
                    $"{instance}->{conditionalReset.WhenFalse.Name} = " +
                    $"{ToCDefaultValue(falseType)}; }}";
            }
            else if (GetClosedGenericConditionalFieldParameterAssignments(
                    implementation, classDeclaration, implementationFields) is
                { } conditionalAssignments)
            {
                var instance = $"((struct {closedTypeIdentifier}*)__root)";
                var parameterNames = implementation.Parameters
                    .Select((parameter, index) =>
                        (parameter.Name, ThunkName: parameters[index].Parameter.Name))
                    .ToDictionary(item => item.Name, item => item.ThunkName,
                        StringComparer.Ordinal);
                var condition = ToCClosedGenericConcreteMethodExpression(
                    conditionalAssignments.Condition,
                    parameterNames,
                    implementation,
                    closedTypeIdentifier,
                    moduleName,
                    instance);
                var whenTrue = string.Join(" ", conditionalAssignments.WhenTrue.Select(assignment =>
                    $"{instance}->{assignment.Field.Name} = " +
                    (assignment.ParameterIndex is { } parameterIndex
                        ? parameters[parameterIndex].Parameter.Name
                        : ToCDefaultValue(GenericTypeSubstitution.Substitute(
                            assignment.Field.Type, classArguments))) + ";"));
                var whenFalse = string.Join(" ", conditionalAssignments.WhenFalse.Select(assignment =>
                    $"{instance}->{assignment.Field.Name} = " +
                    (assignment.ParameterIndex is { } parameterIndex
                        ? parameters[parameterIndex].Parameter.Name
                        : ToCDefaultValue(GenericTypeSubstitution.Substitute(
                            assignment.Field.Type, classArguments))) + ";"));
                call = conditionalAssignments.WhenFalse.Count > 0
                    ? $"if ({condition}) {{ {whenTrue} }} else {{ {whenFalse} }}"
                    : $"if ({condition}) {{ {whenTrue} }}";
            }
            else if (GetClosedGenericConditionalConcreteFieldAssignments(
                    implementation, classDeclaration, implementationFields) is
                { } concreteAssignments)
            {
                var instance = $"((struct {closedTypeIdentifier}*)__root)";
                var parameterNames = implementation.Parameters
                    .Select((parameter, index) =>
                        (parameter.Name, ThunkName: parameters[index].Parameter.Name))
                    .ToDictionary(item => item.Name, item => item.ThunkName,
                        StringComparer.Ordinal);
                var condition = ToCClosedGenericConcreteMethodExpression(
                    concreteAssignments.Condition,
                    parameterNames,
                    implementation,
                    closedTypeIdentifier,
                    moduleName,
                    instance);
                var whenTrue = string.Join(" ", concreteAssignments.WhenTrue.Select(assignment =>
                    $"{instance}->{assignment.Field.Name} {assignment.Assignment.Operator} " +
                    ToCClosedGenericMixedFieldAssignmentValue(
                        assignment.Field,
                        assignment.Assignment,
                        implementation,
                        classArguments,
                        parameterNames,
                        closedTypeIdentifier,
                        moduleName,
                        instance) + ";"));
                var whenFalse = string.Join(" ", concreteAssignments.WhenFalse.Select(assignment =>
                    $"{instance}->{assignment.Field.Name} {assignment.Assignment.Operator} " +
                    ToCClosedGenericMixedFieldAssignmentValue(
                        assignment.Field,
                        assignment.Assignment,
                        implementation,
                        classArguments,
                        parameterNames,
                        closedTypeIdentifier,
                        moduleName,
                        instance) + ";"));
                call = concreteAssignments.WhenFalse.Count > 0
                    ? $"if ({condition}) {{ {whenTrue} }} else {{ {whenFalse} }}"
                    : $"if ({condition}) {{ {whenTrue} }}";
            }
            else if (GetClosedGenericWhileConcreteFieldAssignments(
                    implementation, classDeclaration, implementationFields) is
                { } whileAssignments)
            {
                var instance = $"((struct {closedTypeIdentifier}*)__root)";
                var parameterNames = implementation.Parameters
                    .Select((parameter, index) =>
                        (parameter.Name, ThunkName: parameters[index].Parameter.Name))
                    .ToDictionary(item => item.Name, item => item.ThunkName,
                        StringComparer.Ordinal);
                var condition = ToCClosedGenericConcreteMethodExpression(
                    whileAssignments.Condition,
                    parameterNames,
                    implementation,
                    closedTypeIdentifier,
                    moduleName,
                    instance);
                var assignments = string.Join(" ", whileAssignments.Assignments.Select(assignment =>
                    $"{instance}->{assignment.Field.Name} {assignment.Assignment.Operator} " +
                    ToCClosedGenericMixedFieldAssignmentValue(
                        assignment.Field,
                        assignment.Assignment,
                        implementation,
                        classArguments,
                        parameterNames,
                        closedTypeIdentifier,
                        moduleName,
                        instance) + ";"));
                call = $"while ({condition}) {{ cx_gc_safepoint(); {assignments} }}";
            }
            else if (GetClosedGenericDoWhileConcreteFieldAssignments(
                    implementation, classDeclaration, implementationFields) is
                { } doWhileAssignments)
            {
                var instance = $"((struct {closedTypeIdentifier}*)__root)";
                var parameterNames = implementation.Parameters
                    .Select((parameter, index) =>
                        (parameter.Name, ThunkName: parameters[index].Parameter.Name))
                    .ToDictionary(item => item.Name, item => item.ThunkName,
                        StringComparer.Ordinal);
                var assignments = string.Join(" ", doWhileAssignments.Assignments.Select(assignment =>
                    $"{instance}->{assignment.Field.Name} {assignment.Assignment.Operator} " +
                    ToCClosedGenericMixedFieldAssignmentValue(
                        assignment.Field,
                        assignment.Assignment,
                        implementation,
                        classArguments,
                        parameterNames,
                        closedTypeIdentifier,
                        moduleName,
                        instance) + ";"));
                var condition = ToCClosedGenericConcreteMethodExpression(
                    doWhileAssignments.Condition,
                    parameterNames,
                    implementation,
                    closedTypeIdentifier,
                    moduleName,
                    instance);
                call = $"do {{ cx_gc_safepoint(); {assignments} }} while ({condition})";
            }
            else if (GetClosedGenericForConcreteFieldAssignments(
                    implementation, classDeclaration, implementationFields) is
                { } forAssignments)
            {
                var instance = $"((struct {closedTypeIdentifier}*)__root)";
                var parameterNames = implementation.Parameters
                    .Select((parameter, index) =>
                        (parameter.Name, ThunkName: parameters[index].Parameter.Name))
                    .ToDictionary(item => item.Name, item => item.ThunkName,
                        StringComparer.Ordinal);
                string RenderAssignment(AssignmentExpression assignment)
                {
                    var field = ((IdentifierExpression)assignment.Target)
                        .TargetField!.Declaration;
                    var value = ToCClosedGenericConcreteMethodExpression(
                        assignment.Value,
                        parameterNames,
                        implementation,
                        closedTypeIdentifier,
                        moduleName,
                        instance);
                    return $"{instance}->{field.Name} {assignment.Operator} {value}";
                }

                var condition = ToCClosedGenericConcreteMethodExpression(
                    forAssignments.Condition,
                    parameterNames,
                    implementation,
                    closedTypeIdentifier,
                    moduleName,
                    instance);
                var assignments = string.Join(" ", forAssignments.Assignments.Select(assignment =>
                    RenderAssignment(assignment.Assignment) + ";"));
                call = $"for ({RenderAssignment(forAssignments.Initializer)}; {condition}; " +
                    $"{RenderAssignment(forAssignments.Iterator)}) {{ cx_gc_safepoint(); {assignments} }}";
            }
            else if (IsClosedGenericConcreteFieldAssignmentMethod(
                implementation, classDeclaration, implementationFields))
            {
                var implementationParameterNames = implementation.Parameters
                    .Select((parameter, index) =>
                        (parameter.Name, ThunkName: parameters[index].Parameter.Name))
                    .ToDictionary(item => item.Name, item => item.ThunkName,
                        StringComparer.Ordinal);
                var assignments = implementation.Body!
                    .OfType<ExpressionStatement>()
                    .Select(statement => (AssignmentExpression)statement.Expression)
                    .Select(assignment =>
                    {
                        var field = ((IdentifierExpression)assignment.Target)
                            .TargetField!.Declaration;
                        var value = ToCClosedGenericConcreteMethodExpression(
                            assignment.Value,
                            implementationParameterNames,
                            implementation,
                            closedTypeIdentifier,
                            moduleName);
                        return $"((struct {closedTypeIdentifier}*)__root)->{field.Name} " +
                            $"{assignment.Operator} {value}";
                    });
                call = $"({string.Join(", ", assignments)})";
            }
            else if (GetClosedGenericInterfaceReturnedParameterIndex(
                implementation, classDeclaration) is { } returnedParameterIndex)
            {
                call = parameters[returnedParameterIndex].Parameter.Name;
            }
            else if (IsClosedGenericConcreteExpressionMethod(
                implementation, classDeclaration, implementationFields))
            {
                var implementationParameterNames = implementation.Parameters
                    .Select((parameter, index) =>
                        (parameter.Name, ThunkName: parameters[index].Parameter.Name))
                    .ToDictionary(item => item.Name, item => item.ThunkName,
                        StringComparer.Ordinal);
                if (implementation.Body is
                    [ReturnStatement { Expression: { } expression }])
                {
                    call = ToCClosedGenericConcreteMethodExpression(
                        expression,
                        implementationParameterNames,
                        implementation,
                        closedTypeIdentifier,
                        moduleName);
                }
                else if (TryGetClosedGenericConcreteResultParts(
                    implementation,
                    out var conditionExpression,
                    out var trueExpression,
                    out var falseExpression))
                {
                    var instance = $"((struct {closedTypeIdentifier}*)__root)";
                    var condition = ToCClosedGenericConcreteMethodExpression(
                        conditionExpression,
                        implementationParameterNames,
                        implementation,
                        closedTypeIdentifier,
                        moduleName,
                        instance);
                    var whenTrue = ToCClosedGenericConcreteMethodExpression(
                        trueExpression,
                        implementationParameterNames,
                        implementation,
                        closedTypeIdentifier,
                        moduleName,
                        instance);
                    var whenFalse = ToCClosedGenericConcreteMethodExpression(
                        falseExpression,
                        implementationParameterNames,
                        implementation,
                        closedTypeIdentifier,
                        moduleName,
                        instance);
                    call = $"({condition} ? {whenTrue} : {whenFalse})";
                }
                else
                {
                    throw new InternalCompilerException(
                        $"Supported concrete-expression method '{implementation.FullName}' " +
                        "has no supported return expression.");
                }
            }
            else if (IsClosedGenericGenericResultExpressionMethod(
                implementation, classDeclaration, implementationFields))
            {
                if (!TryGetClosedGenericGenericResultParts(
                        implementation,
                        out var conditionExpression,
                        out var trueExpression,
                        out var falseExpression))
                {
                    throw new InternalCompilerException(
                        $"Supported generic-result method '{implementation.FullName}' " +
                        "has no conditional or return-branch expression.");
                }
                var closedResultType = GenericTypeSubstitution.Substitute(
                    implementation.ReturnType, classArguments);
                var instance = $"((struct {closedTypeIdentifier}*)__root)";
                var parameterNames = implementation.Parameters
                    .Select((parameter, index) =>
                        (parameter.Name, ThunkName: parameters[index].Parameter.Name))
                    .ToDictionary(item => item.Name, item => item.ThunkName,
                        StringComparer.Ordinal);
                var condition = ToCClosedGenericConcreteMethodExpression(
                    conditionExpression,
                    parameterNames,
                    implementation,
                    closedTypeIdentifier,
                    moduleName,
                    instance);
                call = $"({condition} ? " +
                    ToCClosedGenericGenericResultArm(
                        trueExpression, closedResultType, instance) + " : " +
                    ToCClosedGenericGenericResultArm(
                        falseExpression, closedResultType, instance) + ")";
            }
            else if (IsClosedGenericDefaultResultMethod(
                    implementation, classDeclaration))
            {
                var closedResultType = GenericTypeSubstitution.Substitute(
                    implementation.ReturnType, classArguments);
                call = ToCDefaultValue(closedResultType);
            }
            else if (closedOwnerType?.ConstructedIdentity is { } ownerIdentity &&
                (IsClosedGenericInterfaceOwnerMethod(
                    implementation, classDeclaration) ||
                 genericFunctions?.Any(candidate =>
                    ReferenceEquals(candidate.Declaration, implementation) &&
                    candidate.ClosedContainingType?.ConstructedIdentity?.CanonicalName ==
                        ownerIdentity.CanonicalName &&
                    candidate.SpecializationName is not null) == true))
            {
                var candidates = genericFunctions?.Where(candidate =>
                    ReferenceEquals(candidate.Declaration, implementation)).ToArray() ?? [];
                var specialization = candidates.SingleOrDefault(candidate =>
                    candidate.ClosedContainingType?.ConstructedIdentity?.CanonicalName ==
                        ownerIdentity.CanonicalName && candidate.SpecializationName is not null);
                if (specialization is null)
                {
                    var available = string.Join(", ", candidates.Select(candidate =>
                        $"{candidate.ClosedContainingType?.ConstructedIdentity?.CanonicalName ?? "open"}"));
                    throw new InternalCompilerException(
                        $"Closed generic interface method '{implementation.FullName}' has no " +
                        $"specialization for '{closedOwnerType.FullName}' " +
                        $"({ownerIdentity.CanonicalName}); available: [{available}].");
                }
                var receiverType = RequiresClosedValueLayout(closedOwnerType)
                    ? $"struct {ownerIdentity.CIdentifier}"
                    : ToCReceiverType(classDeclaration, moduleName, @const: false)
                        .TrimEnd('*');
                var receiverConst = implementation.Const ? "const " : string.Empty;
                var arguments = new List<string>
                {
                    $"({receiverConst}{receiverType}*)__root",
                };
                arguments.AddRange(parameters.Select(item => item.Parameter.Name));
                call = $"{specialization.SpecializationName}({string.Join(", ", arguments)})";
            }
            else
            {
                var receiverConst = implementation.Const ? "const " : string.Empty;
                var receiver = $"({receiverConst}"
                    + $"{ToCReceiverType(classDeclaration, moduleName, @const: false).TrimEnd('*')}*)__root";
                var arguments = new List<string> { receiver };
                foreach (var item in parameters)
                {
                    var parameterIndex = arguments.Count - 1;
                    var implementationParameterType = GenericTypeSubstitution.Substitute(
                        implementation.Parameters[parameterIndex].ParameterType,
                        classArguments);
                    var argument = item.Parameter.Name;
                    if (implementation.Parameters[parameterIndex].ParameterType is GenericType &&
                        IsReflectionValueType(item.Type))
                    {
                        argument = $"(void*)(size_t)({argument})";
                    }
                    else if (implementationParameterType is not GenericType &&
                        implementationParameterType.ToCIdentifier(false) !=
                            item.Type.ToCIdentifier(false))
                    {
                        argument = $"({implementationParameterType.ToCIdentifier(false)})({argument})";
                    }
                    arguments.Add(argument);
                }
                call = $"{ToCFunctionName(implementation, moduleName)}({string.Join(", ", arguments)})";
                if (implementation.ReturnType is GenericType && returnType is not GenericType &&
                    IsReflectionValueType(returnType))
                {
                    call = $"({returnType.ToCIdentifier(false)})(size_t)({call})";
                }
                else if (implementation.ReturnType is GenericType && returnType is not GenericType)
                {
                    call = $"({returnType.ToCIdentifier(false)})({call})";
                }
            }
        }
        else
        {
            var accessor = slot.ImplementationAccessor!;
            var receiverConst = accessor.Const ? "const " : string.Empty;
            var receiver = $"({receiverConst}"
                + $"{ToCReceiverType(classDeclaration, moduleName, @const: false).TrimEnd('*')}*)__root";
            if (IsClosedGenericIteratorCurrentAccessor(accessor, classDeclaration))
            {
                var instance = $"((struct {closedTypeIdentifier}*)__root)";
                var fields = classDeclaration.MemberDeclarations.Declarations
                    .OfType<FieldDeclaration>()
                    .ToArray();
                var currentField = fields.Single(field => field.Name == "__cx_current");
                var hasCurrentField = fields.Single(field => field.Name == "__cx_hasCurrent");
                var closedCurrentType = GenericTypeSubstitution.Substitute(
                    currentField.Type, classArguments);
                if (closedCurrentType.ToCIdentifier(false) != returnType.ToCIdentifier(false))
                {
                    throw new InternalCompilerException(
                        $"Closed iterator Current type '{closedCurrentType.FullName}' does not " +
                        $"match interface return type '{returnType.FullName}'.");
                }

                writer.WriteLine($"if (!{instance}->{hasCurrentField.Name}) {{");
                writer.IncreaseIndent();
                writer.WriteLine(
                    $"(void){ToCPropertyAccessorName(accessor, moduleName)}({receiver});");
                writer.DecreaseIndent();
                writer.WriteLine("}");
                call = $"{instance}->{currentField.Name}";
            }
            else if (IsClosedGenericOwnerConstructedResultAccessor(
                    accessor, classDeclaration) &&
                closedOwnerType?.ConstructedIdentity is { } ownerIdentity)
            {
                var function = accessor.BodyFunction ?? throw new InternalCompilerException(
                    $"Closed generic property '{accessor.FullName}' has no body function.");
                var specialization = genericFunctions?.SingleOrDefault(candidate =>
                    ReferenceEquals(candidate.Declaration, function) &&
                    candidate.ClosedContainingType?.ConstructedIdentity?.CanonicalName ==
                        ownerIdentity.CanonicalName && candidate.SpecializationName is not null);
                if (specialization is null)
                {
                    var available = string.Join(", ", genericFunctions?.Where(candidate =>
                        ReferenceEquals(candidate.Declaration, function)).Select(candidate =>
                        candidate.ClosedContainingType?.ConstructedIdentity?.CanonicalName ??
                            "open") ?? []);
                    throw new InternalCompilerException(
                        $"Closed generic interface property '{accessor.FullName}' has no " +
                        $"specialization for '{closedOwnerType.FullName}'; candidates: [{available}].");
                }
                var receiverType = RequiresClosedValueLayout(closedOwnerType)
                    ? $"struct {ownerIdentity.CIdentifier}"
                    : ToCReceiverType(classDeclaration, moduleName, @const: false)
                        .TrimEnd('*');
                var specializationArguments = new[]
                    { $"({receiverConst}{receiverType}*)__root" }
                    .Concat(parameters.Select(item => item.Parameter.Name));
                call = $"{specialization.SpecializationName}(" +
                    $"{string.Join(", ", specializationArguments)})";
            }
            else if (IsClosedGenericArrayPropertyGetter(
                accessor,
                classDeclaration,
                classDeclaration.MemberDeclarations.Declarations
                    .OfType<FieldDeclaration>().ToArray()))
            {
                var function = accessor.BodyFunction ?? throw new InternalCompilerException(
                    $"Supported array property '{accessor.FullName}' has no body function.");
                var arrayAccess = (ArrayAccessExpression)((ReturnStatement)function.Body![0]).Expression!;
                var instance = $"((struct {closedTypeIdentifier}*)__root)";
                var parameterNames = new Dictionary<string, string>(StringComparer.Ordinal);
                var array = ToCClosedGenericConcreteMethodExpression(
                    arrayAccess.Target,
                    parameterNames,
                    function,
                    closedTypeIdentifier,
                    moduleName,
                    instance);
                var index = ToCClosedGenericConcreteMethodExpression(
                    arrayAccess.Indices.Single(),
                    parameterNames,
                    function,
                    closedTypeIdentifier,
                    moduleName,
                    instance);
                var elementType = GenericTypeSubstitution.Substitute(
                    accessor.ParentPropertyDeclaration.Type, classArguments);
                var elementCType = elementType.ToCIdentifier(false);
                call = $"(*({elementCType}*)cx_array_at({array}, " +
                    $"(cx_uint)({index}), (cx_uint)sizeof({elementCType})))";
            }
            else if (IsClosedGenericConcreteExpressionAccessor(
                accessor,
                classDeclaration,
                classDeclaration.MemberDeclarations.Declarations
                    .OfType<FieldDeclaration>().ToArray()))
            {
                var function = accessor.BodyFunction ?? throw new InternalCompilerException(
                    $"Supported computed property '{accessor.FullName}' has no body function.");
                var expression = function.Body is
                    [ReturnStatement { Expression: { } returnExpression }]
                        ? returnExpression
                        : throw new InternalCompilerException(
                            $"Supported computed property '{accessor.FullName}' has no return expression.");
                call = ToCClosedGenericConcreteMethodExpression(
                    expression,
                    new Dictionary<string, string>(StringComparer.Ordinal),
                    function,
                    closedTypeIdentifier,
                    moduleName);
            }
            else if (IsClosedGenericGenericFieldLocalAccessor(
                accessor,
                classDeclaration,
                classDeclaration.MemberDeclarations.Declarations
                    .OfType<FieldDeclaration>().ToArray()))
            {
                var initializer = GetClosedGenericGenericFieldLocalInitializer(accessor);
                var function = accessor.BodyFunction ?? throw new InternalCompilerException(
                    $"Supported generic-result property '{accessor.FullName}' has no body function.");
                var closedResultType = GenericTypeSubstitution.Substitute(
                    accessor.ParentPropertyDeclaration.Type, classArguments);
                var instance = $"((struct {closedTypeIdentifier}*)__root)";
                call = ToCClosedGenericGenericResultExpression(
                    initializer,
                    closedResultType,
                    function,
                    closedTypeIdentifier,
                    moduleName,
                    instance);
            }
            else if (IsClosedGenericGenericResultExpressionAccessor(
                accessor,
                classDeclaration,
                classDeclaration.MemberDeclarations.Declarations
                    .OfType<FieldDeclaration>().ToArray()))
            {
                var function = accessor.BodyFunction ?? throw new InternalCompilerException(
                    $"Supported generic-result property '{accessor.FullName}' has no body function.");
                var conditional = function.Body is
                    [ReturnStatement { Expression: ConditionalExpression expression }]
                        ? expression
                        : throw new InternalCompilerException(
                            $"Supported generic-result property '{accessor.FullName}' has no conditional expression.");
                var closedResultType = GenericTypeSubstitution.Substitute(
                    accessor.ParentPropertyDeclaration.Type, classArguments);
                var instance = $"((struct {closedTypeIdentifier}*)__root)";
                var condition = ToCClosedGenericConcreteMethodExpression(
                    conditional.Condition,
                    new Dictionary<string, string>(StringComparer.Ordinal),
                    function,
                    closedTypeIdentifier,
                    moduleName,
                    instance);
                call = $"({condition} ? " +
                    ToCClosedGenericGenericResultArm(
                        conditional.WhenTrue, closedResultType, instance) + " : " +
                    ToCClosedGenericGenericResultArm(
                        conditional.WhenFalse, closedResultType, instance) + ")";
            }
            else if (IsClosedGenericConcreteExpressionSetter(
                accessor,
                classDeclaration,
                classDeclaration.MemberDeclarations.Declarations
                    .OfType<FieldDeclaration>().ToArray()))
            {
                var function = accessor.BodyFunction ?? throw new InternalCompilerException(
                    $"Supported computed setter '{accessor.FullName}' has no body function.");
                var assignment = function.Body is
                    [ExpressionStatement { Expression: AssignmentExpression bodyAssignment }]
                        ? bodyAssignment
                        : throw new InternalCompilerException(
                            $"Supported computed setter '{accessor.FullName}' has no assignment.");
                var field = ((IdentifierExpression)assignment.Target).TargetField!.Declaration;
                var parameterNames = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [function.Parameters[0].Name] = parameters.Single().Parameter.Name,
                };
                var instance = $"((struct {closedTypeIdentifier}*)__root)";
                var assignedValue = ToCClosedGenericConcreteMethodExpression(
                    assignment.Value,
                    parameterNames,
                    function,
                    closedTypeIdentifier,
                    moduleName,
                    instance);
                call = $"{instance}->{field.Name} = {assignedValue}";
            }
            else if (IsReferenceFieldPropertyAccessor(
                    accessor.ParentPropertyDeclaration,
                    accessor,
                    classDeclaration.MemberDeclarations.Declarations
                        .OfType<FieldDeclaration>().ToArray()))
            {
                var field = accessor.Name == "get"
                    ? accessor.BodyFunction?.Body is
                        [ReturnStatement { Expression: var expression }]
                        ? GetReferenceFieldDeclaration(expression)
                        : null
                    : accessor.BodyFunction?.Body is
                        [ExpressionStatement
                        {
                            Expression: AssignmentExpression { Target: var target },
                        }]
                            ? GetReferenceFieldDeclaration(target)
                            : null;
                if (field is null)
                {
                    throw new InternalCompilerException(
                        $"Supported generic property accessor '{accessor.FullName}' has no field target.");
                }
                call = $"((struct {closedTypeIdentifier}*)__root)->{field.Name}";
                if (accessor.Name == "set")
                {
                    call += $" = {parameters.Single().Parameter.Name}";
                }
            }
            else
            {
                var arguments = new List<string> { receiver };
                for (var index = 0; index < parameters.Length; index++)
                {
                    var implementationParameterType = index < accessor.Parameters.Count
                        ? accessor.Parameters[index].ParameterType
                        : accessor.ParentPropertyDeclaration.Type;
                    var argument = parameters[index].Parameter.Name;
                    if (implementationParameterType is GenericType &&
                        IsReflectionValueType(parameters[index].Type))
                    {
                        argument = $"(void*)(size_t)({argument})";
                    }
                    arguments.Add(argument);
                }
                call = $"{ToCPropertyAccessorName(accessor, moduleName)}({string.Join(", ", arguments)})";
                if (accessor.ParentPropertyDeclaration.Type is GenericType && returnType is not GenericType &&
                    IsReflectionValueType(returnType))
                {
                    call = $"({returnType.ToCIdentifier(false)})(size_t)({call})";
                }
            }
        }

        writer.WriteLine(returnType is VoidType ? $"{call};" : $"return {call};");
        writer.DecreaseIndent();
        writer.WriteLine("}");
        writer.WriteLine();
    }

    private static QualifiedIdentifier GetInterfaceVTableIdentifier(
        ClassDeclaration classDeclaration,
        ClassDeclaration interfaceDeclaration,
        string moduleName)
    {
        return GetInterfaceVTableIdentifier(
            new QualifiedIdentifier(classDeclaration.ProjectName ?? moduleName, classDeclaration.FullName),
            new QualifiedIdentifier(interfaceDeclaration.ProjectName ?? moduleName, interfaceDeclaration.FullName));
    }

    private static QualifiedIdentifier GetInterfaceVTableIdentifier(
        QualifiedIdentifier className,
        QualifiedIdentifier interfaceName)
    {
        var interfaceParts = className.Parts.FirstOrDefault() == interfaceName.Parts.FirstOrDefault()
            ? interfaceName.Parts.Skip(1).ToArray()
            : interfaceName.Parts;
        return new QualifiedIdentifier(
            className,
            new QualifiedIdentifier("__iface"),
            new QualifiedIdentifier(interfaceParts),
            new QualifiedIdentifier("__vtable"));
    }

    private static QualifiedIdentifier GetClosedGenericInterfaceVTableIdentifier(
        string closedTypeIdentifier,
        string interfaceModuleName,
        QualifiedIdentifier interfaceName)
    {
        var qualifiedInterfaceName = interfaceName.Parts.FirstOrDefault() == interfaceModuleName
            ? interfaceName
            : new QualifiedIdentifier(interfaceModuleName, interfaceName);
        return new QualifiedIdentifier(closedTypeIdentifier, "__iface") +
            qualifiedInterfaceName + new QualifiedIdentifier("__vtable");
    }

    private static QualifiedIdentifier GetClosedGenericInterfaceThunkIdentifier(
        string closedTypeIdentifier,
        string interfaceModuleName,
        QualifiedIdentifier interfaceName,
        int slotIndex)
    {
        var qualifiedInterfaceName = interfaceName.Parts.FirstOrDefault() == interfaceModuleName
            ? interfaceName
            : new QualifiedIdentifier(interfaceModuleName, interfaceName);
        return new QualifiedIdentifier(closedTypeIdentifier, "__iface_thunk") +
            qualifiedInterfaceName + new QualifiedIdentifier($"slot_{slotIndex}");
    }

    private static QualifiedIdentifier GetInterfaceThunkIdentifier(
        ClassDeclaration classDeclaration,
        ClassDeclaration interfaceDeclaration,
        int slotIndex,
        string moduleName)
    {
        var vtableName = GetInterfaceVTableIdentifier(
            classDeclaration,
            interfaceDeclaration,
            moduleName);
        return new QualifiedIdentifier(vtableName, $"slot_{slotIndex}");
    }

    private static IEnumerable<ClassDeclaration> EnumerateClasses(
        IEnumerable<DeclarationBase> declarations)
    {
        foreach (var classDeclaration in declarations.OfType<ClassDeclaration>())
        {
            yield return classDeclaration;
            foreach (var nested in EnumerateClasses(
                classDeclaration.MemberDeclarations.Declarations))
            {
                yield return nested;
            }
        }
    }

    private static IEnumerable<EnumDeclaration> EnumerateEnums(
        IEnumerable<DeclarationBase> declarations)
    {
        foreach (var declaration in declarations)
        {
            if (declaration is EnumDeclaration enumDeclaration)
            {
                yield return enumDeclaration;
            }
            else if (declaration is ClassDeclaration classDeclaration)
            {
                foreach (var nested in EnumerateEnums(classDeclaration.MemberDeclarations.Declarations))
                {
                    yield return nested;
                }
            }
        }
    }

    private static List<DeclarationBase> GetDeclarations(CxProject project)
    {
        List<DeclarationBase> declarations = [];

        foreach (var compilationContext in project.CompilationContexts
            .Where(context => !context.IsProjectReference))
        {
            foreach (var declaration in compilationContext.DeclarationScope.Declarations)
            {
                if (declaration is ClassDeclaration classDeclaration)
                {
                    if (classDeclaration.NativeType is not null) continue;
                    declarations.Add(classDeclaration);
                }
                else if (declaration is FunctionDeclaration functionDeclaration)
                {
                    declarations.Add(functionDeclaration);
                }
                else if (declaration is EnumDeclaration enumDeclaration)
                {
                    declarations.Add(enumDeclaration);
                }
                else if (declaration is DelegateDeclaration delegateDeclaration)
                {
                    declarations.Add(delegateDeclaration);
                }
                else if (declaration is ExtensionDeclaration)
                {
                    declarations.Add(declaration);
                }
                else if (declaration is ConceptDeclaration)
                {
                    // Concepts are compile-time constraints and have no runtime representation.
                }
                else if (declaration is AliasDeclaration)
                {
                    // Aliases are resolved during binding and have no runtime representation.
                }
                else
                {
                    throw new InternalCompilerException(
                        $"Unsupported declaration type: {declaration.GetType().Name} in project '{project.Name}'");
                }
            }
        }

        var cxcoreTypeOrder = new Dictionary<string, int>
        {
            { "Object", -1000 },
            { "Nullable", -900 },
            { "TypeInfo", -800 },
            { "VersionInfo", -700 }
        }; // TODO: Remove this hack for cxcore

        var result = declarations.Order(Comparer<DeclarationBase>.Create((x, y) =>
        {
            if (x is ClassDeclaration xClassDeclaration && y is ClassDeclaration yClassDeclaration)
            {
                var depthComparison = GetInheritanceDepth(xClassDeclaration)
                    .CompareTo(GetInheritanceDepth(yClassDeclaration));
                if (depthComparison != 0)
                {
                    return depthComparison;
                }
                if (project.Name == "cxcore") // TODO: Remove this hack for cxcore
                {
                    if (cxcoreTypeOrder.ContainsKey(x.Name) && !cxcoreTypeOrder.ContainsKey(y.Name))
                    {
                        return -1;
                    }
                    else if (!cxcoreTypeOrder.ContainsKey(x.Name) && cxcoreTypeOrder.ContainsKey(y.Name))
                    {
                        return 1;
                    }
                    else if (cxcoreTypeOrder.ContainsKey(x.Name) && cxcoreTypeOrder.ContainsKey(y.Name))
                    {
                        return cxcoreTypeOrder[x.Name].CompareTo(cxcoreTypeOrder[y.Name]);
                    }
                }

                if (ReferenceEquals(xClassDeclaration.BaseClassDeclaration, yClassDeclaration))
                {
                    return 1;
                }
                else if (ReferenceEquals(yClassDeclaration.BaseClassDeclaration, xClassDeclaration))
                {
                    return -1;
                }
                else
                {
                    return QualifiedIdentifier.Compare(xClassDeclaration.FullName, yClassDeclaration.FullName);
                }
            }
            else if (x.GetType() == y.GetType())
            {
                return QualifiedIdentifier.Compare(x.FullName, y.FullName);
            }
            return GetDeclarationOrder(x).CompareTo(GetDeclarationOrder(y));
        })).ToList();
        return result;

        static int GetDeclarationOrder(DeclarationBase declaration) => declaration switch
        {
            EnumDeclaration => 0,
            ClassDeclaration => 1,
            FunctionDeclaration => 2,
            _ => 3,
        };

        static int GetInheritanceDepth(ClassDeclaration declaration)
        {
            var depth = 0;
            while (declaration.BaseClassDeclaration is { } baseClass)
            {
                depth++;
                declaration = baseClass;
            }
            return depth;
        }
    }

    private static List<DeclarationBase> GetPublicApiDeclarations(
        IReadOnlyCollection<DeclarationBase> declarations,
        IReadOnlySet<QualifiedIdentifier> publicTypeNames) =>
        declarations.Where(declaration => declaration switch
        {
            FunctionDeclaration function => IsPublicApiFunction(function),
            ClassDeclaration or EnumDeclaration or DelegateDeclaration =>
                publicTypeNames.Contains(declaration.FullName),
            ExtensionDeclaration extension => extension.Visibility == Visibility.Public,
            _ => false,
        }).ToList();

    private static HashSet<QualifiedIdentifier> GetPublicApiTypeNames(
        IReadOnlyCollection<DeclarationBase> declarations,
        IReadOnlyCollection<DeclarationBase>? referencedDeclarations = null)
    {
        var allTypes = EnumerateTypes(declarations.Concat(
            referencedDeclarations ?? Array.Empty<DeclarationBase>())).ToArray();
        var publicTypeNames = allTypes
            .Where(type => type switch
            {
                ClassDeclaration classType => classType.Visibility == Visibility.Public,
                EnumDeclaration enumType => enumType.Visibility == Visibility.Public,
                DelegateDeclaration delegateType => delegateType.Visibility == Visibility.Public,
                _ => false,
            })
            .Select(type => type.FullName)
            .ToHashSet();
        var referencedTypes = new HashSet<QualifiedIdentifier>();
        var publicFunctions = EnumerateFunctions(declarations)
            .Where(IsPublicApiFunction);
        foreach (var function in publicFunctions)
        {
            AddTypeReferences(function.ReturnType, referencedTypes);
            foreach (var parameter in function.Parameters)
            {
                AddTypeReferences(parameter.ParameterType, referencedTypes);
            }
        }
        foreach (var delegateType in declarations.OfType<DelegateDeclaration>()
            .Where(delegateType => delegateType.Visibility == Visibility.Public))
        {
            AddTypeReferences(delegateType.Signature.ReturnType, referencedTypes);
            foreach (var parameterType in delegateType.Signature.ParameterTypes)
            {
                AddTypeReferences(parameterType, referencedTypes);
            }
        }
        foreach (var extension in declarations.OfType<ExtensionDeclaration>()
            .Where(extension => extension.Visibility == Visibility.Public))
        {
            AddTypeReferences(extension.TargetType, referencedTypes);
            foreach (var property in extension.Members.MemberDeclarations.Declarations
                .OfType<PropertyDeclaration>().Where(property =>
                    property.MemberModifiers.Contains(MemberModifier.Public)))
            {
                AddTypeReferences(property.Type, referencedTypes);
            }
        }

        var pendingTypes = new Queue<DeclarationBase>();
        foreach (var type in allTypes.Where(type => publicTypeNames.Contains(type.FullName)))
        {
            pendingTypes.Enqueue(type);
        }

        void EnqueueReferencedTypes()
        {
            foreach (var referencedName in referencedTypes)
            {
                if (publicTypeNames.Add(referencedName) &&
                    allTypes.FirstOrDefault(candidate => candidate.FullName == referencedName) is { } referenced)
                {
                    pendingTypes.Enqueue(referenced);
                }
            }
        }

        EnqueueReferencedTypes();

        var visitedTypes = new HashSet<QualifiedIdentifier>();
        while (pendingTypes.Count > 0)
        {
            var type = pendingTypes.Dequeue();
            if (!visitedTypes.Add(type.FullName))
            {
                continue;
            }
            if (type is not ClassDeclaration classType)
            {
                continue;
            }

            foreach (var baseType in classType.BaseTypes)
            {
                AddTypeReferences(baseType, referencedTypes);
            }
            foreach (var field in classType.MemberDeclarations.Declarations.OfType<FieldDeclaration>()
                .Where(field => !field.IsStatic ||
                    field.MemberModifiers.Contains(MemberModifier.Public)))
            {
                AddTypeReferences(field.Type, referencedTypes);
            }
            if (classType.Visibility == Visibility.Public)
            {
                foreach (var member in classType.MemberDeclarations.Declarations)
                {
                    if (!IsPublicApiMember(member, classType))
                    {
                        continue;
                    }
                    switch (member)
                    {
                        case FunctionDeclaration function:
                            AddTypeReferences(function.ReturnType, referencedTypes);
                            foreach (var parameter in function.Parameters)
                            {
                                AddTypeReferences(parameter.ParameterType, referencedTypes);
                            }
                            break;
                        case PropertyDeclaration property:
                            AddTypeReferences(property.Type, referencedTypes);
                            foreach (var accessor in property.PropertyAccessorDeclarations)
                            {
                                foreach (var parameter in accessor.Parameters)
                                {
                                    AddTypeReferences(parameter.ParameterType, referencedTypes);
                                }
                            }
                            break;
                        case ClassDeclaration nested when nested.Visibility == Visibility.Public:
                            referencedTypes.Add(nested.FullName);
                            break;
                    }
                }
            }

            EnqueueReferencedTypes();
        }

        return publicTypeNames;
    }

    private static void AddTypeReferences(
        TypeBase type,
        ISet<QualifiedIdentifier> referencedTypes)
    {
        switch (type)
        {
            case ConstType constant:
                AddTypeReferences(constant.UnderlyingType, referencedTypes);
                break;
            case ArrayType array:
                referencedTypes.Add(new QualifiedIdentifier(
                    "System", "Collections", "Array"));
                AddTypeReferences(array.ElementType, referencedTypes);
                break;
            case NullableType nullable:
                referencedTypes.Add(new QualifiedIdentifier("System", "Nullable"));
                AddTypeReferences(nullable.UnderlyingType, referencedTypes);
                break;
            case NamedType named:
                if (named.ResolvedTypeFullName.Parts.Length > 1)
                {
                    referencedTypes.Add(new QualifiedIdentifier(
                        named.ResolvedTypeFullName.Parts.Skip(1).ToArray()));
                }
                foreach (var argument in named.TypeArguments)
                {
                    AddTypeReferences(argument, referencedTypes);
                }
                break;
        }
    }

    private static IEnumerable<DeclarationBase> EnumerateTypes(
        IEnumerable<DeclarationBase> declarations)
    {
        foreach (var declaration in declarations)
        {
            if (declaration is ClassDeclaration classType)
            {
                yield return classType;
                foreach (var nestedType in EnumerateTypes(classType.MemberDeclarations.Declarations))
                {
                    yield return nestedType;
                }
            }
            else if (declaration is EnumDeclaration enumType)
            {
                yield return enumType;
            }
            else if (declaration is DelegateDeclaration delegateType)
            {
                yield return delegateType;
            }
        }
    }

    private static bool IsPublicApiType(
        DeclarationBase declaration,
        IReadOnlySet<QualifiedIdentifier> publicTypeNames) =>
        publicTypeNames.Contains(declaration.FullName);

    private static bool IsPublicApiFunction(FunctionDeclaration function) =>
        (function.MemberModifiers.Contains(MemberModifier.Public) ||
            function.ParentClassDeclaration?.ClassType == ClassType.Interface) &&
        (function.ParentClassDeclaration is null ||
            function.ParentClassDeclaration.Visibility == Visibility.Public ||
            IsCoreTaskStateAbiFunction(function));

    private static bool IsPublicApiMember(
        DeclarationBase declaration,
        ClassDeclaration parent) =>
        (parent.Visibility == Visibility.Public || IsCoreTaskStateAbiType(parent)) && declaration switch
        {
            FunctionDeclaration function =>
                function.MemberModifiers.Contains(MemberModifier.Public) ||
                parent.ClassType == ClassType.Interface,
            PropertyDeclaration property =>
                property.MemberModifiers.Contains(MemberModifier.Public) ||
                parent.ClassType == ClassType.Interface,
            ClassDeclaration nested => nested.Visibility == Visibility.Public,
            _ => false,
        };

    private static bool IsCoreTaskStateAbiType(ClassDeclaration type) =>
        type.ProjectName == "cxcore" &&
        type.FullName.ToString() == "System.Threading.Tasks.TaskState";

    private static bool IsCoreTaskStateAbiFunction(FunctionDeclaration function) =>
        function.ParentClassDeclaration is { } parent &&
        IsCoreTaskStateAbiType(parent) &&
        function.MemberModifiers.Contains(MemberModifier.Public);

    private static bool IsCoreTaskStateAbiProperty(PropertyDeclaration property) =>
        IsCoreTaskStateAbiType(property.ParentClassDeclaration) &&
        property.MemberModifiers.Contains(MemberModifier.Public);

    private static bool IsPublicApiGenericFunction(FunctionSymbol function) =>
        function.Declaration is { } declaration && IsPublicApiFunction(declaration);

    internal static IReadOnlyCollection<(NamedType Type, ClassDeclaration Declaration)>
        GetPublicApiGenericTypeInstances(CxProject project)
    {
        var publicTypeNames = GetPublicApiTypeNames(
            GetDeclarations(project), GetReferencedDeclarations(project));
        return GetPublicApiGenericTypeInstances(project, publicTypeNames);
    }

    private static IReadOnlyCollection<DeclarationBase> GetReferencedDeclarations(
        CxProject project) => project.CompilationContexts
            .Where(context => context.IsProjectReference)
            .SelectMany(context => context.DeclarationScope.Declarations)
            // Binding removes synthetic core contexts. Their closed instances
            // still carry the templates needed to discover exported support
            // layouts, including types used by public generic method bodies.
            .Concat(project.GenericTypeInstances.Select(instance => instance.Declaration))
            .Distinct()
            .ToArray();

    internal static IReadOnlyCollection<FunctionSymbol> GetPublicApiGenericFunctionInstances(
        CxProject project) =>
        project.GenericFunctionInstances.Where(IsPublicApiGenericFunction).ToArray();

    private static IReadOnlyCollection<(NamedType Type, ClassDeclaration Declaration)>
        GetPublicApiGenericTypeInstances(
            CxProject project,
            IReadOnlySet<QualifiedIdentifier> publicTypeNames)
    {
        var publicConstructedTypes = new HashSet<string>(StringComparer.Ordinal);
        var arrayDeclaration = GetDeclarations(project).OfType<ClassDeclaration>()
            .FirstOrDefault(declaration => IsPublicArrayDeclaration(declaration));
        foreach (var declaration in GetPublicApiDeclarations(GetDeclarations(project), publicTypeNames))
        {
            switch (declaration)
            {
                case FunctionDeclaration function:
                    AddConstructedTypeIdentities(function.ReturnType, publicConstructedTypes,
                        project, arrayDeclaration);
                    foreach (var parameter in function.Parameters)
                    {
                        AddConstructedTypeIdentities(parameter.ParameterType, publicConstructedTypes,
                            project, arrayDeclaration);
                    }
                    break;
                case ClassDeclaration classType:
                    foreach (var field in classType.MemberDeclarations.Declarations.OfType<FieldDeclaration>())
                    {
                        AddConstructedTypeIdentities(field.Type, publicConstructedTypes,
                            project, arrayDeclaration);
                    }
                    foreach (var functionMember in classType.MemberDeclarations.Declarations
                        .OfType<FunctionDeclaration>().Where(IsPublicApiFunction))
                    {
                        AddConstructedTypeIdentities(functionMember.ReturnType,
                            publicConstructedTypes, project, arrayDeclaration);
                        foreach (var parameter in functionMember.Parameters)
                        {
                            AddConstructedTypeIdentities(parameter.ParameterType,
                                publicConstructedTypes, project, arrayDeclaration);
                        }
                    }
                    foreach (var property in classType.MemberDeclarations.Declarations
                        .OfType<PropertyDeclaration>()
                        .Where(property => IsPublicApiMember(property, classType)))
                    {
                        AddConstructedTypeIdentities(property.Type, publicConstructedTypes,
                            project, arrayDeclaration);
                    }
                    break;
            }
        }

        foreach (var function in project.GenericFunctionInstances
            .Where(IsPublicApiGenericFunction))
        {
            AddConstructedTypeIdentities(function.ReturnType, publicConstructedTypes,
                project, arrayDeclaration);
            foreach (var parameter in function.ParameterTypes)
            {
                AddConstructedTypeIdentities(parameter, publicConstructedTypes,
                    project, arrayDeclaration);
            }
            if (function.ClosedContainingType is { } containingType)
            {
                AddConstructedTypeIdentities(containingType, publicConstructedTypes,
                    project, arrayDeclaration);
            }
        }

        var instancesByIdentity = project.GenericTypeInstances
            .Where(instance => publicTypeNames.Contains(instance.Declaration.FullName) &&
                instance.Type.ConstructedIdentity is not null)
            .ToDictionary(instance => instance.Type.ConstructedIdentity!.CanonicalName,
                StringComparer.Ordinal);
        var pendingIdentities = new Queue<string>(publicConstructedTypes);
        var expandedIdentities = new HashSet<string>(StringComparer.Ordinal);
        while (pendingIdentities.Count > 0)
        {
            var identity = pendingIdentities.Dequeue();
            if (!expandedIdentities.Add(identity) ||
                !instancesByIdentity.TryGetValue(identity, out var instance))
            {
                continue;
            }

            var arguments = instance.Declaration.GenericTypeNames
                .Zip(instance.Type.TypeArguments)
                .ToDictionary(pair => pair.First, pair => pair.Second, StringComparer.Ordinal);

            void Discover(TypeBase type)
            {
                var closedType = GenericTypeSubstitution.Substitute(type, arguments);
                EnsureConstructedGenericIdentities(closedType);
                var discovered = new HashSet<string>(StringComparer.Ordinal);
                AddConstructedTypeIdentities(closedType, discovered,
                    project, arrayDeclaration);
                foreach (var dependency in discovered)
                {
                    if (publicConstructedTypes.Add(dependency))
                    {
                        pendingIdentities.Enqueue(dependency);
                    }
                }
            }

            foreach (var field in instance.Declaration.MemberDeclarations.Declarations
                .OfType<FieldDeclaration>().Where(field => !field.IsStatic))
            {
                Discover(field.Type);
            }
            foreach (var baseType in instance.Declaration.BaseTypes)
            {
                Discover(baseType);
            }
            foreach (var member in instance.Declaration.MemberDeclarations.Declarations)
            {
                switch (member)
                {
                    case FunctionDeclaration function when IsPublicApiFunction(function):
                        Discover(function.ReturnType);
                        foreach (var parameter in function.Parameters)
                        {
                            Discover(parameter.ParameterType);
                        }
                        break;
                    case PropertyDeclaration property when
                        IsPublicApiMember(property, instance.Declaration):
                        Discover(property.Type);
                        break;
                }
            }
        }

        return project.GenericTypeInstances.Where(instance =>
            publicTypeNames.Contains(instance.Declaration.FullName) &&
            instance.Type.ConstructedIdentity is { } identity &&
            publicConstructedTypes.Contains(identity.CanonicalName)).ToArray();
    }

    private static void AddConstructedTypeIdentities(
        TypeBase type,
        ISet<string> identities,
        CxProject project,
        ClassDeclaration? arrayDeclaration)
    {
        switch (type)
        {
            case ConstType constant:
                AddConstructedTypeIdentities(constant.UnderlyingType, identities,
                    project, arrayDeclaration);
                break;
            case ArrayType array:
                if (array.ConstructedIdentity is null &&
                    !ContainsOpenGenericParameter(array.ElementType))
                {
                    var arrayModule = arrayDeclaration?.ProjectName ??
                        array.ResolvedTypeFullName.Parts[0];
                    array.ConstructedIdentity = GenericTypeIdentity.Create(
                        arrayModule, "System.Collections.Array", [array.ElementType]);
                }
                EnsureConstructedGenericIdentities(array);
                if (array.ConstructedIdentity is { } arrayIdentity)
                {
                    identities.Add(arrayIdentity.CanonicalName);
                }
                AddConstructedTypeIdentities(array.ElementType, identities,
                    project, arrayDeclaration);
                break;
            case NullableType nullable:
                AddConstructedTypeIdentities(nullable.UnderlyingType, identities,
                    project, arrayDeclaration);
                break;
            case NamedType named:
                EnsureConstructedGenericIdentities(named);
                if (named.ConstructedIdentity is { } identity)
                {
                    identities.Add(identity.CanonicalName);
                }
                foreach (var argument in named.TypeArguments)
                {
                    AddConstructedTypeIdentities(argument, identities,
                        project, arrayDeclaration);
                }
                break;
        }
    }

    private static int GetNameOverrideIndex(FunctionDeclaration functionDeclaration, IReadOnlyCollection<DeclarationBase> declarations)
    {
        var nameOverrides = declarations
            .OfType<FunctionDeclaration>()
            .Where(fd => fd.FullName == functionDeclaration.FullName)
            .ToList();
        return nameOverrides.IndexOf(functionDeclaration) + 1;
    }

    private static string ToCIdentifier(this ClassDeclaration classDeclaration, string moduleName, bool includeStruct = true)
    {
        var fullName = new QualifiedIdentifier(moduleName, classDeclaration.FullName);
        return $"{(includeStruct ? "struct " : "")}{fullName.ToCIdentifier()}";
    }

    private static bool IsPublicArrayDeclaration(ClassDeclaration declaration) =>
        declaration.GenericTypeNames.Length == 1 &&
        declaration.FullName.Parts is ["System", "Collections", "Array"];

    private static string ToCReceiverType(
        ClassDeclaration declaration,
        string moduleName,
        bool @const)
    {
        var constPrefix = @const ? "const " : string.Empty;
        return IsPublicArrayDeclaration(declaration)
            ? $"{constPrefix}struct cxcore_System_Collections_Array*"
            : $"{constPrefix}{declaration.ToCIdentifier(moduleName)}*";
    }

    private static string ToCIdentifier(this EnumDeclaration enumDeclaration, string moduleName)
    {
        return new QualifiedIdentifier(moduleName, enumDeclaration.FullName).ToCIdentifier();
    }

    private static string ToCIdentifier(this EnumMemberDeclaration member, string moduleName)
    {
        return new QualifiedIdentifier(moduleName, member.FullName).ToCIdentifier();
    }

    private static string ToCStorageType(TypeBase type)
    {
        type = type is ConstType constType ? constType.UnderlyingType : type;
        return type switch
        {
            ObjectType => "struct cxcore_System_Object",
            NamedType namedArray when ArrayType.TryGetElementType(namedArray, out _) =>
                "struct cxcore_System_Collections_Array",
            NamedType { ClassType: ClassType.Class } namedType =>
                $"struct {namedType.ResolvedTypeFullName.ToCIdentifier()}",
            _ => type.ToCIdentifier(false).TrimEnd('*', ' '),
        };
    }

    private static string ToCIdentifier(this FunctionDeclaration functionDeclaration, string moduleName, int nameOverrideIndex)
    {
        if (functionDeclaration.LocalCName is { } localCName)
        {
            return $"{functionDeclaration.ReturnType.ToCReturnType(false)} " +
                new QualifiedIdentifier(moduleName, localCName).ToCIdentifier();
        }
        var name = nameOverrideIndex > 1
            ? new QualifiedIdentifier(functionDeclaration.FullName, $"_{nameOverrideIndex}")
            : functionDeclaration.FullName;
        var fullName = new QualifiedIdentifier(moduleName, name);
        var returnType = ReturnsGenericClassReference(functionDeclaration)
            ? "void*"
            : functionDeclaration.ReturnType.ToCReturnType(@const: false);
        return $"{returnType} {fullName.ToCIdentifier()}";
    }

    private static bool ReturnsGenericClassReference(FunctionDeclaration function) =>
        function.ParentClassDeclaration is { GenericTypeNames.Length: > 0 } &&
        function.ReturnType is GenericType;

    private static string ToCFunctionName(
        FunctionDeclaration functionDeclaration,
        string moduleName)
    {
        var declarations = functionDeclaration.ParentClassDeclaration is null
            ? Array.Empty<DeclarationBase>()
            : functionDeclaration.ParentClassDeclaration.MemberDeclarations.Declarations.ToArray();
        var nameOverrideIndex = GetNameOverrideIndex(functionDeclaration, declarations);
        var name = nameOverrideIndex > 1
            ? new QualifiedIdentifier(functionDeclaration.FullName, $"_{nameOverrideIndex}")
            : functionDeclaration.FullName;
        var declaringModule = functionDeclaration.ParentClassDeclaration?.ProjectName ?? moduleName;
        return new QualifiedIdentifier(declaringModule, name).ToCIdentifier();
    }

    private static string ToCIdentifier(this PropertyAccessorDeclaration propertyAccessorDeclaration, string moduleName)
    {
        var returnType = propertyAccessorDeclaration.Name == "set"
            ? BuiltInSystemTypes.Void
            : propertyAccessorDeclaration.ParentPropertyDeclaration.Type;
        var cReturnType = ReturnsGenericClassPropertyReference(propertyAccessorDeclaration)
            ? "void*"
            : returnType.ToCReturnType(false);
        return $"{cReturnType} {ToCPropertyAccessorName(propertyAccessorDeclaration, moduleName)}";
    }

    private static bool ReturnsGenericClassPropertyReference(PropertyAccessorDeclaration accessor) =>
        accessor.Name == "get" &&
        accessor.BodyFunction is not null && accessor.Parameters.Count == 0 &&
        accessor.ParentPropertyDeclaration.ParentClassDeclaration.GenericTypeNames.Length > 0 &&
        accessor.ParentPropertyDeclaration.Type is GenericType;

    private static string ToCPropertyAccessorName(
        PropertyAccessorDeclaration propertyAccessorDeclaration,
        string moduleName)
    {
        var accessorName = $"__{(propertyAccessorDeclaration.Const ? "const_" : "")}{propertyAccessorDeclaration.Name}";
        var name = new QualifiedIdentifier(
            propertyAccessorDeclaration.ParentPropertyDeclaration.FullName,
            accessorName);
        var declaringModule =
            propertyAccessorDeclaration.ParentPropertyDeclaration.ParentClassDeclaration.ProjectName ??
            moduleName;
        return new QualifiedIdentifier(declaringModule, name).ToCIdentifier();
    }

    private static string ToCIdentifier(this TypeBase typeBase, bool @const)
    {
        var constString = @const ? "const " : "";
        return typeBase switch
        {
            NamedType { DelegateSignature: NativeFunctionType nativeFunction } => GetFunctionTypeCIdentifier(nativeFunction),
            NamedType { NativeType: { } native } => $"{constString}{native.CType}",
            NamedType { AliasTargetType: { } target } => target.ToCIdentifier(@const),
            NativeScalarType scalar => $"{constString}{scalar.CType}",
            WCharType => $"{constString}cx_wchar",
            ConstType constType => constType.UnderlyingType.ToCIdentifier(true),
            BoolType => "cx_bool",
            CharType => "cx_char",

            SByteType => "cx_sbyte",
            ShortType => "cx_short",
            IntType => "cx_int",
            LongType => "cx_long",

            ByteType => "cx_byte",
            UShortType => "cx_ushort",
            UIntType => "cx_uint",
            ULongType => "cx_ulong",

            FloatType => "cx_float",
            DoubleType => "cx_double",
            DecimalType => "cx_decimal",

            ObjectType => $"{constString}struct cxcore_System_Object*",
            StringType => $"{constString}struct cxcore_System_String*",
            PtrType { Pointee: { } pointee } => $"{NativePointeeType(pointee)}*{(@const ? " const" : "")}",
            PtrType => $"{constString}cx_ptr",

            FunctionType functionType => GetFunctionTypeCIdentifier(functionType),
            VoidType => "void",
            NullType => "cx_ptr",

            ArrayType => $"{constString}struct cxcore_System_Collections_Array*",
            NullableType => $"{constString}struct cxcore_System_Nullable",

            NamedType namedArray when ArrayType.TryGetElementType(namedArray, out _) =>
                $"{constString}struct cxcore_System_Collections_Array*",
            NamedType namedType when namedType.IsDelegate =>
                $"{constString}{namedType.ResolvedTypeFullName.ToCIdentifier()}",
            NamedType namedType when namedType.ClassType == ClassType.Struct &&
                RequiresClosedValueLayout(namedType) =>
                $"{constString}struct {namedType.ConstructedIdentity!.CIdentifier}",
            NamedType namedType when namedType.ClassType == ClassType.Struct => $"{constString}struct {namedType.ResolvedTypeFullName.ToCIdentifier()}",
            NamedType namedType when namedType.ClassType == ClassType.Class &&
                RequiresClosedValueLayout(namedType) =>
                $"{constString}struct {namedType.ConstructedIdentity!.CIdentifier}*",
            NamedType namedType when namedType.ClassType == ClassType.Class => $"{constString}struct {namedType.ResolvedTypeFullName.ToCIdentifier()}*",
            NamedType { ClassType: ClassType.Interface } => $"{constString}struct cx_iface_ref",
            NamedType namedType when namedType.ClassType == ClassType.Enum => $"{constString}{namedType.ResolvedTypeFullName.ToCIdentifier()}",

            _ => "_unknowntype_" // TODO: throw below exception for unsupported types
            //_ => throw new InternalCompilerException($"Unsupported type: {typeBase.GetType().Name}"),
        };
    }

    private static string ToCIdentifier(this QualifiedIdentifier qualifiedIdentifier)
    {
        if (qualifiedIdentifier.IsEmpty)
        {
            throw new InternalCompilerException("QualifiedIdentifier cannot be empty");
        }

        return string.Join("_", qualifiedIdentifier.Parts);
    }

    private static string ToCReturnType(this TypeBase typeBase, bool @const)
    {
        if (IsGenericValueType(typeBase))
        {
            return "void";
        }

        return typeBase.ToCIdentifier(@const);
    }

    private static string GetFunctionTypeCIdentifier(FunctionType functionType)
    {
        var signature = $"{functionType.ReturnType.ToCReturnType(false)}(" +
            string.Join(",", functionType.ParameterTypes.Select(ToCParameterType)) + ")";
        if (functionType is NativeFunctionType native)
            signature = $"native:{native.CallingConvention}:{native.ContextParameter}:" + signature;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(signature)));
        return $"CX_FN_{hash[..16]}";
    }

    private static string ToCParameterType(TypeBase typeBase)
    {
        return IsGenericValueType(typeBase)
            ? "void*"
            : typeBase.ToCIdentifier(@const: false);
    }

    private static bool IsPrimitiveExtensionReceiverType(TypeBase typeBase)
    {
        typeBase = typeBase is ConstType constant ? constant.UnderlyingType : typeBase;
        return typeBase is ArrayType or BoolType or CharType or IntTypeBase or FloatTypeBase or DecimalType or PtrType or StringType ||
            typeBase is NamedType { ClassType: ClassType.Enum };
    }

    private static bool IsGenericValueType(TypeBase typeBase)
    {
        var effectiveType = typeBase is ConstType constType
            ? constType.UnderlyingType
            : typeBase;
        return effectiveType is GenericType ||
            effectiveType is NamedType
            {
                ClassType: ClassType.Struct,
                GenericParams.Length: > 0,
                ConstructedIdentity: null,
            };
    }

    private static ulong GetTypeNameHash(
        QualifiedIdentifier typeName,
        string moduleName,
        int genericArity = 0)
    {
        var fullName = new QualifiedIdentifier(moduleName, typeName);
        var fullNameString = $"{fullName}{(genericArity == 0 ? string.Empty : $"`{genericArity}")}";
        byte[] bytes = Encoding.Unicode.GetBytes(fullNameString);
        byte[] hash = SHA256.HashData(bytes);
        return
            BitConverter.ToUInt64(hash, 0) ^
            BitConverter.ToUInt64(hash, 8) ^
            BitConverter.ToUInt64(hash, 16) ^
            BitConverter.ToUInt64(hash, 24);
    }
}

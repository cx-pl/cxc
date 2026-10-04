using CxCompiler.Model;
using CxCompiler.Model.Common;
using CxCompiler.Model.Expressions;
using CxCompiler.Model.Project;
using CxCompiler.Model.Statements;
using CxCompiler.Model.Types;
using CxCompiler.Model.Types.BuiltInTypes;
using CxCompiler.Semantics;
using System.Security.Cryptography;
using System.Text;

namespace CxCompiler.OutputGenerators;

public static partial class CCodeOutputGenerator
{
    private static StreamWriter OpenGeneratedFile(string outputFilePath) =>
        new ReproducibleStreamWriter(outputFilePath);

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
        WriteProjectSourceFile(project, Path.Combine(outputDirectory, $"{project.Name}.c"));
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
        foreach (var nativeSource in EnumerateNativeCSourceFiles(
            projectDirectory,
            Path.Combine(outputDirectory, $"{project.Name}.c")))
        {
            var relativeSourcePath = Path.GetRelativePath(outputDirectory, nativeSource);
            fileWriter.WriteLine(
                $"    \"${{CMAKE_CURRENT_LIST_DIR}}/{EscapeCMakePath(relativeSourcePath)}\"");
        }
        fileWriter.WriteLine(")");
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
        fileWriter.WriteLine("if(DEFINED CXCORE_SOURCE_DIR AND NOT TARGET cxcore)");
        fileWriter.WriteLine("    if(CX_STATIC_LINK)");
        fileWriter.WriteLine("        set(CX_BUILD_STATIC ON CACHE BOOL \"\" FORCE)");
        fileWriter.WriteLine("    else()");
        fileWriter.WriteLine("        set(CX_BUILD_STATIC OFF CACHE BOOL \"\" FORCE)");
        fileWriter.WriteLine("    endif()");
        fileWriter.WriteLine("    set(BUILD_TESTING OFF CACHE BOOL \"\" FORCE)");
        fileWriter.WriteLine("    add_subdirectory(\"${CXCORE_SOURCE_DIR}\" \"${CMAKE_CURRENT_BINARY_DIR}/cxcore\")");
        fileWriter.WriteLine("endif()");
        fileWriter.WriteLine();

        switch (project.Type)
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
        fileWriter.WriteLine($"    target_compile_options({project.Name} PRIVATE /experimental:c11atomics)");
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
        fileWriter.WriteLine($"if(TARGET cxcore)");
        fileWriter.WriteLine($"    target_link_libraries({project.Name} PRIVATE cxcore)");
        fileWriter.WriteLine($"endif()");
        var dependencyLinkVisibility = project.Type == CxProjectType.Library ? "PUBLIC" : "PRIVATE";
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
            fileWriter.WriteLine(
                $"target_include_directories({project.Name} PRIVATE \"${{CMAKE_CURRENT_LIST_DIR}}/{EscapeCMakePath(dependencyOutput)}\")");
            fileWriter.WriteLine("if(NOT CX_STATIC_LINK)");
            fileWriter.WriteLine(
                $"    add_custom_command(TARGET {project.Name} POST_BUILD COMMAND ${{CMAKE_COMMAND}} -E copy_if_different \"$<TARGET_FILE:{name}>\" \"$<TARGET_FILE_DIR:{project.Name}>\")");
            fileWriter.WriteLine("endif()");
        }

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
            [".obj", ".bin", ".git", ".vs", "CMakeFiles"],
            StringComparer.OrdinalIgnoreCase);

        return Directory.EnumerateFiles(projectDirectory, "*.c", SearchOption.AllDirectories)
            .Where(path => !string.Equals(
                Path.GetFullPath(path),
                generatedSourceFullPath,
                StringComparison.OrdinalIgnoreCase))
            .Where(path => !IsWithinDirectory(Path.GetFullPath(path), generatedOutputDirectory))
            .Where(path => Path.GetRelativePath(projectDirectory, path)
                .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                    StringSplitOptions.RemoveEmptyEntries)
                .SkipLast(1)
                .All(directory => !excludedDirectoryNames.Contains(directory)))
            .OrderBy(path => Path.GetRelativePath(projectDirectory, path),
                StringComparer.OrdinalIgnoreCase);
    }

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
        writer.WriteLine(project.Name == "cxcore"
            ? "#include <cx.h>"
            : "#include <cxcore.h>");
        writer.WriteLine();

        foreach (var dependencyName in project.ResolvedProjectNames)
        {
            writer.WriteLine($"#include \"{dependencyName}.h\"");
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

        var allDeclarations = GetDeclarations(project);
        var publicTypeNames = publicApi
            ? GetPublicApiTypeNames(allDeclarations)
            : null;
        var declarations = publicApi
            ? GetPublicApiDeclarations(allDeclarations, publicTypeNames!)
            : allDeclarations;
        var genericTypeInstances = publicApi
            ? GetPublicApiGenericTypeInstances(project, publicTypeNames!)
            : project.GenericTypeInstances;
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

        writer.WriteLine("//");
        writer.WriteLine("// Type declarations");
        writer.WriteLine("//");
        writer.WriteLine();
        WriteTypesDeclarations(writer, declarations, project.Name, publicApi, publicTypeNames);
        WriteClosedValueTypeDeclarations(writer, genericTypeInstances);
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
        IReadOnlySet<QualifiedIdentifier>? publicTypeNames)
    {
        foreach (var declaration in declarations)
        {
            if (declaration is ClassDeclaration classDeclaration)
            {
                if (classDeclaration.ClassType == ClassType.Class && classDeclaration.IsStatic)
                {
                    writer.WriteLine($"CX_STATIC_TYPE_DEF({classDeclaration.ToCIdentifier(moduleName, false)});");
                }
                else
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

                foreach (var staticField in classDeclaration.MemberDeclarations.Declarations
                    .OfType<FieldDeclaration>()
                    .Where(field => field.IsStatic &&
                        (!publicApi || field.MemberModifiers.Contains(MemberModifier.Public))))
                {
                    writer.WriteLine(
                        $"extern {staticField.Type.ToCIdentifier(false)} " +
                        $"{GetStaticFieldIdentifier(staticField, moduleName)};");
                }

                var classMemberDeclarations = classDeclaration.MemberDeclarations.Declarations
                    .OfType<ClassDeclaration>()
                    .Where(member => !publicApi || IsPublicApiType(member, publicTypeNames!))
                    .ToArray();
                if (classMemberDeclarations.Length != 0)
                {
                    WriteTypesDeclarations(
                        writer, classMemberDeclarations, moduleName, publicApi, publicTypeNames);
                }
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

                case FunctionDeclaration functionDeclaration:
                    if (publicApi && !IsPublicApiFunction(functionDeclaration))
                    {
                        break;
                    }
                    if (functionDeclaration.GenericTypeNames.Length > 0)
                    {
                        break;
                    }
                    if (functionDeclaration.ParentClassDeclaration?.ClassType == ClassType.Interface)
                    {
                        break;
                    }
                    exportable =
                        (
                            functionDeclaration.ParentClassDeclaration == null &&
                            functionDeclaration.MemberModifiers.Contains(MemberModifier.Public)
                        ) ||
                        (
                            functionDeclaration.ParentClassDeclaration?.ClassType == ClassType.Struct &&
                            functionDeclaration.MemberModifiers.Contains(MemberModifier.Public) &&
                            functionDeclaration.ParentClassDeclaration?.Visibility == Visibility.Public
                        ) ||
                        (
                            functionDeclaration.ParentClassDeclaration?.ClassType == ClassType.Class &&
                            functionDeclaration.MemberModifiers.Contains(MemberModifier.Public) &&
                            functionDeclaration.ParentClassDeclaration?.Visibility == Visibility.Public
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
                        writer.Write($"{receiverConst}{functionDeclaration.ParentClassDeclaration!.ToCIdentifier(moduleName)}* __this");
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
                    if (propertyDeclaration.ParentClassDeclaration.ClassType == ClassType.Interface)
                    {
                        break;
                    }
                    foreach (var propertyAccessorDeclaration in propertyDeclaration.PropertyAccessorDeclarations)
                    {
                        exportable =
                        (
                            propertyDeclaration.ParentClassDeclaration.ClassType == ClassType.Interface
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
                        );

                        writer.WriteIndent();
                        writer.Write($"extern {(exportable ? $"{GetModuleApiName(moduleName)} " : "")}{propertyAccessorDeclaration.ToCIdentifier(moduleName)}(");
                        writer.IncreaseIndent();

                        isFirstParameter = true;
                        if (!propertyDeclaration.IsStatic)
                        {
                            writer.WriteNewLine();
                            writer.WriteIndent();
                            var receiverConst = propertyAccessorDeclaration.Const ? "const " : string.Empty;
                            writer.Write($"{receiverConst}{propertyDeclaration.ParentClassDeclaration.ToCIdentifier(moduleName)}* __this");
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
            var parameters = GetGenericFunctionParameters(instance);
            var export = declaration.MemberModifiers.Contains(MemberModifier.Public)
                ? $"{GetModuleApiName(instance.ModuleName)} "
                : string.Empty;
            writer.WriteLine($"extern {export}{instance.ReturnType.ToCIdentifier(false)} " +
                $"{name}({string.Join(", ", parameters)});");
        }
    }

    private static IReadOnlyList<string> GetGenericFunctionParameters(
        CxCompiler.Semantics.FunctionSymbol instance)
    {
        var declaration = instance.Declaration!;
        var parameters = new List<string>();
        if (!declaration.IsStatic)
        {
            var receiverConst = declaration.Const ? "const " : string.Empty;
            var receiverType = instance.ClosedContainingType is
            { ConstructedIdentity: not null } closedType &&
                RequiresClosedValueLayout(closedType)
                    ? $"struct {closedType.ConstructedIdentity.CIdentifier}"
                    : declaration.ParentClassDeclaration!.ToCIdentifier(instance.ModuleName);
            parameters.Add($"{receiverConst}" +
                $"{receiverType}* __this");
        }
        parameters.AddRange(declaration.Parameters.Zip(instance.ParameterTypes)
            .Select(pair => $"{pair.Second.ToCIdentifier(false)} {pair.First.Name}"));
        return parameters;
    }

    private static void WriteProjectSourceFile(CxProject project, string outputFilePath)
    {
        using var fileWriter = OpenGeneratedFile(outputFilePath);
        var writer = new IndentingWriter(fileWriter);

        writer.WriteLine($"// This is an autogenerated C source file for project '{project.Name}'");
        writer.WriteLine();
        writer.WriteLine($"#include \"{project.Name}.internal.h\"");
        writer.WriteLine("#if !defined(CX_STATIC_LINK)");
        writer.WriteLine("#include <stdatomic.h>");
        writer.WriteLine("#endif");
        writer.WriteLine();

        var declarations = GetDeclarations(project);

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
        WriteClosedGenericTypeInfos(writer, project.GenericTypeInstances, project.Name);
        WriteModuleInitializationFunction(writer, project);
        writer.WriteLine();

        writer.WriteLine("//");
        writer.WriteLine("// Functions");
        writer.WriteLine("//");
        writer.WriteLine();
        WriteFunctionDefinitions(writer, declarations, project.Name);
        WriteGenericFunctionDefinitions(writer, project.GenericFunctionInstances, project.Name);

        fileWriter.Flush();
        fileWriter.Close();
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
            .SelectMany(EnumerateStringLiterals);
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

    private static void WriteModuleInitializationFunction(
        IndentingWriter writer,
        CxProject project)
    {
        var token = GetModuleToken(project.Name);
        writer.WriteLine("#if !defined(CX_STATIC_LINK)");
        writer.WriteLine($"static atomic_flag __cx_module_init_lock_{token} = ATOMIC_FLAG_INIT;");
        writer.WriteLine($"static cx_bool __cx_module_initialized_{token};");
        writer.WriteLine($"void {GetModuleApiName(project.Name)} __cx_module_init_{token}(void)");
        writer.WriteLine("{");
        writer.IncreaseIndent();
        writer.WriteLine($"while (atomic_flag_test_and_set_explicit(&__cx_module_init_lock_{token}, memory_order_acquire)) {{ }}");
        writer.WriteLine($"if (!__cx_module_initialized_{token})");
        writer.WriteLine("{");
        writer.IncreaseIndent();
        foreach (var dependencyName in project.ResolvedProjectNames)
        {
            writer.WriteLine($"__cx_module_init_{GetModuleToken(dependencyName)}();");
        }
        var declarations = GetDeclarations(project);
        foreach (var stringIdentifier in GetProjectStringIdentifiers(declarations, project.Name))
        {
            writer.WriteLine($"{stringIdentifier}.__base.__vtable = " +
                "CX_ID_4(cxcore, System, String, __vtable);");
        }
        foreach (var classDeclaration in EnumerateClasses(declarations))
        {
            WriteDynamicTypeInfoInitialization(writer, classDeclaration, project.Name);
            WriteDynamicReflectionMetadataInitialization(writer, classDeclaration, project.Name);
        }
        foreach (var instance in project.GenericTypeInstances)
        {
            var identity = instance.Type.ConstructedIdentity!;
            foreach (var stringIdentifier in new[]
            {
                $"CX_ID_2({identity.CIdentifier}, __name)",
                $"CX_ID_2({identity.CIdentifier}, __namespace)",
            })
            {
                writer.WriteLine($"{stringIdentifier}.__base.__vtable = " +
                    "CX_ID_4(cxcore, System, String, __vtable);");
            }
            WriteDynamicClosedGenericMetadataInitialization(
                writer, instance.Type, instance.Declaration, project.Name);
        }
        writer.WriteLine($"__cx_module_initialized_{token} = CX_TRUE;");
        writer.DecreaseIndent();
        writer.WriteLine("}");
        writer.WriteLine($"atomic_flag_clear_explicit(&__cx_module_init_lock_{token}, memory_order_release);");
        writer.DecreaseIndent();
        writer.WriteLine("}");
        writer.WriteLine("#endif");
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
            : "CX_ID_4(cxcore, System, Object, __typeinfo)";
        var typeInfo = new QualifiedIdentifier(
            moduleName, declaration.FullName, "__typeinfo").ToCIdentifier();
        writer.WriteLine($"{typeInfo}.BaseType._obj = &{baseTypeInfo};");
    }

    private static void WriteDynamicReflectionMetadataInitialization(
        IndentingWriter writer,
        ClassDeclaration declaration,
        string moduleName)
    {
        var fields = declaration.MemberDeclarations.Declarations.OfType<FieldDeclaration>().ToArray();
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

        var interfaces = GetReflectedInterfaces(declaration).ToArray();
        var interfaceArray = GetInterfaceRuntimeMapIdentifier(
            declaration, moduleName).ToCIdentifier();
        for (var index = 0; index < interfaces.Length; index++)
        {
            var interfaceDeclaration = interfaces[index];
            var interfaceTypeInfo = new QualifiedIdentifier(
                interfaceDeclaration.ProjectName ?? moduleName,
                interfaceDeclaration.FullName,
                "__typeinfo").ToCIdentifier();
            var vtable = declaration.ClassType == ClassType.Class
                ? GetInterfaceVTableIdentifier(
                    declaration, interfaceDeclaration, moduleName).ToCIdentifier()
                : "CX_NULL";
            writer.WriteLine($"{interfaceArray}[{index}] = (struct cx_interface_impl){{ " +
                $"&{interfaceTypeInfo}, {vtable} }};");
        }
    }

    private static void WriteDynamicClosedGenericMetadataInitialization(
        IndentingWriter writer,
        NamedType type,
        ClassDeclaration declaration,
        string moduleName)
    {
        var identity = type.ConstructedIdentity!;
        var arguments = declaration.GenericTypeNames.Zip(type.TypeArguments)
            .ToDictionary(pair => pair.First, pair => pair.Second);
        var typeInfo = $"CX_TYPEINFO_NAME({identity.CIdentifier})";
        if (declaration.ClassType == ClassType.Class && !declaration.IsStatic)
        {
            writer.WriteLine($"{typeInfo}.BaseType._obj = " +
                "&CX_ID_4(cxcore, System, Object, __typeinfo);");
        }

        var fields = declaration.MemberDeclarations.Declarations
            .OfType<FieldDeclaration>().ToArray();
        var fieldArray = $"CX_ID_2({identity.CIdentifier}, __reflection_fields)";
        for (var index = 0; index < fields.Length; index++)
        {
            var field = fields[index];
            var offset = field.IsStatic
                ? "CX_REFLECTION_NO_OFFSET"
                : $"(cx_uint)offsetof({(RequiresClosedValueLayout(type)
                    ? $"struct {identity.CIdentifier}"
                    : declaration.ToCIdentifier(moduleName))}, {field.Name})";
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
                $"CX_ID_2({identity.CIdentifier}, __reflection_parameters_{index})";
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
                $"CX_ID_2({identity.CIdentifier}, __reflection_functions)[{index}] = " +
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
            var initializer = field.Initializer is null
                ? string.Empty
                : $" = {ToCFieldInitializer(field.Initializer, moduleName)}";
            writer.WriteLine(
                $"{field.Type.ToCIdentifier(false)} {GetStaticFieldIdentifier(field, moduleName)}{initializer};");
        }
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
            foreach (var table in classDeclaration.InterfaceDispatchTables)
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
                IsReferenceFieldAssignments(constructor, fields)) &&
            (constructor.Initializer is null ||
                constructor.Initializer is
                {
                    Kind: ConstructorInitializerKind.Base,
                    Arguments.Count: 0,
                });
        var supportedFields = fields.All(field =>
        {
            if (field.IsStatic || field.Initializer is not null)
            {
                return false;
            }
            if (field.Type is ArrayType { ElementType: GenericType arrayParameter })
            {
                return declaration.GenericTypeNames.Contains(arrayParameter.Name);
            }
            if (field.Type is GenericType parameter)
            {
                var index = Array.IndexOf(declaration.GenericTypeNames, parameter.Name);
                return index >= 0 &&
                    type.TypeArguments[index] is NamedType { ClassType: ClassType.Class };
            }
            return false;
        });
        var supportedMethods = methods.All(method =>
            !method.IsStatic && method.GenericTypeNames.Length == 0 &&
            method.VirtualSlotIndex is null &&
            (method.ReturnType is VoidType && IsReferenceFieldSetter(method, fields) ||
                IsReferenceFieldGetter(method, fields)));
        var supportedProperties = properties.All(property =>
            !property.IsStatic && property.Type is GenericType &&
            property.PropertyAccessorDeclarations.Count is >= 1 and <= 2 &&
            property.PropertyAccessorDeclarations.Select(accessor => accessor.Name)
                .Distinct().Count() == property.PropertyAccessorDeclarations.Count &&
            property.PropertyAccessorDeclarations.All(accessor =>
                IsReferenceFieldPropertyAccessor(property, accessor, fields)));
        if (declaration.ClassType != ClassType.Class || declaration.IsStatic ||
            members.Count != constructors.Length + fields.Length + methods.Length + properties.Length ||
            !supportedConstructor || !supportedFields || !supportedMethods ||
            !supportedProperties ||
            declaration.BaseTypes.Count != 0 ||
            declaration.VirtualMethodSlots.Count != 0 ||
            declaration.InterfaceDispatchTables.Count != 0)
        {
            throw new CxCompiler.Model.Errors.CompilationErrorException(
                $"Closed runtime metadata for generic type '{declaration.FullName}' " +
                "currently requires an empty class, T[] fields, or T fields with " +
                "class-reference arguments; it also requires at most an empty " +
                "parameterless constructor, or one assignment per reference " +
                "constructor parameter, or a void method with a " +
                "single-field assignment, or a T getter returning a T field, " +
                "or a T property reading and writing a T field, and no explicit bases.");
        }
    }

    private static bool RequiresClosedValueLayout(NamedType type) =>
        type.ConstructedIdentity is not null &&
        type.TypeArguments.Any(IsValueTypeArgument);

    private static bool IsValueTypeArgument(TypeBase type) => type switch
    {
        ConstType constant => IsValueTypeArgument(constant.UnderlyingType),
        ArrayType or StringType or ObjectType => false,
        NamedType named => named.ClassType is ClassType.Struct or ClassType.Enum,
        _ => type is not GenericType,
    };

    private static void ValidateClosedValueLayout(ClassDeclaration declaration)
    {
        var members = declaration.MemberDeclarations.Declarations;
        var fields = members.OfType<FieldDeclaration>().ToArray();
        var constructors = members.OfType<ConstructorDeclaration>().ToArray();
        var genericMethods = members.OfType<FunctionDeclaration>()
            .Where(function => function is not ConstructorDeclaration).ToArray();
        if (declaration.ClassType != ClassType.Class || declaration.IsStatic ||
            declaration.GenericTypeNames.Length == 0 ||
            members.Count != fields.Length + constructors.Length + genericMethods.Length ||
            genericMethods.Any(method => method.IsStatic ||
                method.VirtualSlotIndex is not null ||
                (method.GenericTypeNames.Length == 0 &&
                    !IsReferenceFieldGetter(method, fields) &&
                    !IsReferenceFieldSetter(method, fields))) ||
            constructors.Length > 1 ||
            fields.Any(field => field.IsStatic || field.Initializer is not null ||
                !IsSupportedClosedValueFieldType(field.Type, declaration.GenericTypeNames)) ||
            !IsSupportedClosedValueConstructor(declaration, fields, constructors) ||
            declaration.BaseTypes.Count != 0 ||
            declaration.VirtualMethodSlots.Count != 0 ||
            declaration.InterfaceDispatchTables.Count != 0)
        {
            throw new CxCompiler.Model.Errors.CompilationErrorException(
                $"Closed value layout for generic type '{declaration.FullName}' " +
                "requires an empty class or direct generic-parameter and generic-array fields, plus " +
                "non-virtual generic methods or direct generic-field getters/setters, and either " +
                "an empty parameterless constructor or one direct assignment per " +
                "generic field constructor parameter.");
        }
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
        var constructor = constructors[0];
        if (constructor.Initializer is not null and not
            { Kind: ConstructorInitializerKind.Base, Arguments.Count: 0 })
        {
            return false;
        }
        if (fields.Count == 0)
        {
            return constructor.Parameters.Count == 0 && constructor.Body is { Count: 0 };
        }
        if (constructor.Parameters.Count == 0 && constructor.Body is { Count: 0 })
        {
            return true;
        }
        if (constructor.Parameters.Count != fields.Count ||
            constructor.Body?.Count != fields.Count)
        {
            return false;
        }

        var fieldByName = fields.ToDictionary(field => field.Name, StringComparer.Ordinal);
        var assignedFields = new HashSet<string>(StringComparer.Ordinal);
        var assignedParameters = new HashSet<string>(StringComparer.Ordinal);
        foreach (var statement in constructor.Body)
        {
            if (statement is not ExpressionStatement
                {
                    Expression: AssignmentExpression
                    {
                        Operator: "=",
                        Target: IdentifierExpression fieldIdentifier,
                        Value: IdentifierExpression parameterIdentifier,
                    },
                })
            {
                return false;
            }
            var fieldName = fieldIdentifier.Identifier.Parts[^1];
            var parameterName = parameterIdentifier.Identifier.Parts[^1];
            var parameter = constructor.Parameters.SingleOrDefault(item =>
                item.Name == parameterName);
            if (!fieldByName.TryGetValue(fieldName, out var field) ||
                parameter is null ||
                field.Type is not GenericType fieldType ||
                parameter.ParameterType is not GenericType parameterType ||
                fieldType.Name != parameterType.Name ||
                !assignedFields.Add(fieldName) || !assignedParameters.Add(parameterName))
            {
                return false;
            }
        }
        return assignedFields.Count == fields.Count &&
            assignedParameters.Count == constructor.Parameters.Count;
    }

    private static bool IsSupportedClosedValueFieldType(
        TypeBase type,
        IReadOnlyCollection<string> genericTypeNames) =>
        type switch
        {
            GenericType parameter => genericTypeNames.Contains(parameter.Name),
            ArrayType { ElementType: GenericType parameter } =>
                genericTypeNames.Contains(parameter.Name),
            _ => false,
        };

    private static void WriteClosedValueTypeDeclarations(
        IndentingWriter writer,
        IReadOnlyCollection<(NamedType Type, ClassDeclaration Declaration)> instances)
    {
        foreach (var (type, declaration) in instances.Where(item =>
            RequiresClosedValueLayout(item.Type)).OrderBy(item =>
                item.Type.ConstructedIdentity!.CanonicalName, StringComparer.Ordinal))
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
                writer.WriteLine($"{fieldType.ToCIdentifier(false)} {field.Name};");
            }
            writer.DecreaseIndent();
            writer.WriteLine("};");
        }
    }

    private static bool IsReferenceFieldPropertyAccessor(
        PropertyDeclaration property,
        PropertyAccessorDeclaration accessor,
        IReadOnlyCollection<FieldDeclaration> fields)
    {
        if (accessor.Parameters.Count != 0 || property.Type is not GenericType propertyType)
        {
            return false;
        }
        FieldDeclaration? field = accessor.BodyFunction?.Body switch
        {
            [ReturnStatement
            {
                Expression: IdentifierExpression { TargetField: { } targetField },
            }] when accessor.Name == "get" => targetField.Declaration,
            [ExpressionStatement
            {
                Expression: AssignmentExpression
                {
                    Operator: "=",
                    Target: IdentifierExpression { TargetField: { } targetField },
                    Value: IdentifierExpression input,
                },
            }] when accessor.Name == "set" && input.Identifier.ToString() == "value" =>
                targetField.Declaration,
            _ => null,
        };
        return field is not null && fields.Contains(field) &&
            field.Type is GenericType fieldType && fieldType.Name == propertyType.Name;
    }

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
            if (parameter.ParameterType is not GenericType parameterType ||
                function.Body[index] is not ExpressionStatement
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
                targetField.Declaration.Type is not GenericType fieldType ||
                fieldType.Name != parameterType.Name ||
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
        function.ReturnType is GenericType returnType &&
        function.Body is
        [ReturnStatement
        {
            Expression: IdentifierExpression { TargetField: { } targetField },
        }] &&
        fields.Contains(targetField.Declaration) &&
        targetField.Declaration.Type is GenericType fieldType &&
        fieldType.Name == returnType.Name;

    private static void WriteClosedGenericTypeInfos(
        IndentingWriter writer,
        IReadOnlyCollection<(NamedType Type, ClassDeclaration Declaration)> instances,
        string moduleName)
    {
        foreach (var (type, declaration) in instances.OrderBy(item =>
            item.Type.ConstructedIdentity!.CanonicalName, StringComparer.Ordinal))
        {
            ValidateClosedReferenceLayout(type, declaration);
            var identity = type.ConstructedIdentity!;
            var closedNameIdentifier = $"CX_ID_2({identity.CIdentifier}, __name)";
            var closedNamespaceIdentifier = $"CX_ID_2({identity.CIdentifier}, __namespace)";
            var fields = declaration.MemberDeclarations.Declarations.OfType<FieldDeclaration>().ToArray();
            var closedFields = $"CX_ID_2({identity.CIdentifier}, __reflection_fields)";
            var arguments = declaration.GenericTypeNames
                .Zip(type.TypeArguments)
                .ToDictionary(pair => pair.First, pair => pair.Second);
            var functions = GetReflectionFunctions(declaration).ToArray();
            string? closedFunctions = null;
            if (functions.Length != 0)
            {
                closedFunctions = $"CX_ID_2({identity.CIdentifier}, __reflection_functions)";
                for (var index = 0; index < functions.Length; index++)
                {
                    if (functions[index].Parameters.Count == 0)
                    {
                        continue;
                    }
                    var closedParameters =
                        $"CX_ID_2({identity.CIdentifier}, __reflection_parameters_{index})";
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
                        : $"CX_ID_2({identity.CIdentifier}, __reflection_parameters_{index})";
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
                            : declaration.ToCIdentifier(moduleName))}, {field.Name}), " +
                        $"{ToTypeInfoPointer(fieldType)}, \"{field.Name}\" }},");
                }
                writer.DecreaseIndent();
                writer.WriteLine("};");
                writer.WriteLine("#else");
                writer.WriteLine($"static struct cx_reflection_field {closedFields}[{fields.Length}];");
                writer.WriteLine("#endif");
            }
            var runtimeFunctionCount = GetReflectionFunctionCount(declaration);
            var runtimeTypeInfo = fields.Length == 0 && runtimeFunctionCount == 0
                ? "CX_NULL"
                : $"(cx_ptr)&CX_ID_2({identity.CIdentifier}, __runtime_type_info)";
            if (runtimeTypeInfo != "CX_NULL")
            {
                writer.WriteLine($"static const struct cx_runtime_type_info CX_ID_2({identity.CIdentifier}, __runtime_type_info) = {{");
                writer.IncreaseIndent();
                writer.WriteLine(".interfaces = CX_NULL,");
                writer.WriteLine(".interfaceCount = 0,");
                writer.WriteLine($".fields = {(fields.Length == 0 ? "CX_NULL" : $"(const struct cx_reflection_field*){closedFields}")},");
                writer.WriteLine($".fieldCount = {fields.Length},");
                writer.WriteLine($".functions = {(runtimeFunctionCount == 0 ? "CX_NULL" : $"(const struct cx_reflection_function*){closedFunctions ?? GetReflectionFunctionsIdentifier(declaration, moduleName).ToCIdentifier()}")},");
                writer.WriteLine($".functionCount = {runtimeFunctionCount},");
                writer.DecreaseIndent();
                writer.WriteLine("};");
            }
            writer.WriteLine($"CX_STRING_DEF({closedNameIdentifier}, \"{declaration.Name}\");");
            writer.WriteLine($"CX_STRING_DEF({closedNamespaceIdentifier}, \"{declaration.Namespace}\");");
            writer.WriteLine();
            writer.WriteLine($"CX_BEGIN_VTABLE_DEF({identity.CIdentifier})");
            writer.WriteLine("CX_END_VTABLE_DEF;");
            writer.WriteLine("#if !defined(CX_DYNAMIC_MODULE)");
            writer.WriteLine($"struct CX_ID_4(cxcore, System, Reflection, TypeInfo) " +
                $"CX_TYPEINFO_NAME({identity.CIdentifier}) = {{");
            writer.IncreaseIndent();
            writer.WriteLine($".Hash = 0x{identity.RuntimeHash:X}ULL,");
            writer.WriteLine($".Flags = {string.Join(" | ", GetTypeInfoFlags(declaration))},");
            writer.WriteLine($".Size = sizeof({(RequiresClosedValueLayout(type)
                ? $"struct {identity.CIdentifier}"
                : declaration.ToCIdentifier(moduleName))}),");
            writer.WriteLine($".Name = &{closedNameIdentifier},");
            writer.WriteLine($".Namespace = &{closedNamespaceIdentifier},");
            writer.WriteLine(".BaseType = { ._obj = &CX_ID_4(cxcore, System, Object, __typeinfo) },");
            writer.WriteLine($".RuntimeTypeInfo = {runtimeTypeInfo},");
            writer.WriteLine($".GenericArity = {declaration.GenericTypeNames.Length},");
            writer.DecreaseIndent();
            writer.WriteLine("};");
            writer.WriteLine("#else");
            writer.WriteLine($"struct CX_ID_4(cxcore, System, Reflection, TypeInfo) " +
                $"CX_TYPEINFO_NAME({identity.CIdentifier}) = {{");
            writer.IncreaseIndent();
            writer.WriteLine($".Hash = 0x{identity.RuntimeHash:X}ULL,");
            writer.WriteLine($".Flags = {string.Join(" | ", GetTypeInfoFlags(declaration))},");
            writer.WriteLine($".Size = sizeof({(RequiresClosedValueLayout(type)
                ? $"struct {identity.CIdentifier}"
                : declaration.ToCIdentifier(moduleName))}),");
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
        writer.WriteLine($"struct CX_ID_4(cxcore, System, Reflection, TypeInfo) {new QualifiedIdentifier(moduleName, declaration.FullName, "__typeinfo").ToCIdentifier()} = {{");
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

        var reflectedInterfaces = GetReflectedInterfaces(classDeclaration).ToArray();
        var fields = classDeclaration.MemberDeclarations.Declarations.OfType<FieldDeclaration>().ToArray();
        var functionCount = GetReflectionFunctionCount(classDeclaration);
        var baseType = classDeclaration.ClassType == ClassType.Class && !classDeclaration.IsStatic &&
            !(moduleName == "cxcore" && classDeclaration.Name == "Object")
            ? classDeclaration.BaseClassType switch
            {
                NamedType namedType => $"&{new QualifiedIdentifier(namedType.ResolvedTypeFullName, "__typeinfo").ToCIdentifier()}",
                _ => "&CX_ID_4(cxcore, System, Object, __typeinfo)",
            }
            : "CX_NULL";
        var size = classDeclaration.IsStatic || classDeclaration.ClassType == ClassType.Interface
            ? "0"
            : $"sizeof({classDeclaration.ToCIdentifier(moduleName)})";
        var hasRuntimeTypeInfo = reflectedInterfaces.Length != 0 || fields.Length != 0 || functionCount != 0;
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
            writer.DecreaseIndent();
            writer.WriteLine("};");
        }

        writer.WriteLine("#if !defined(CX_DYNAMIC_MODULE)");
        writer.WriteLine($"struct CX_ID_4(cxcore, System, Reflection, TypeInfo) {new QualifiedIdentifier(moduleName, classDeclaration.FullName, "__typeinfo").ToCIdentifier()} = {{");
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
        writer.WriteLine($"struct CX_ID_4(cxcore, System, Reflection, TypeInfo) {new QualifiedIdentifier(moduleName, classDeclaration.FullName, "__typeinfo").ToCIdentifier()} = {{");
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
            if (slot.Implementation is null)
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
            writer.WriteLine(slot.Implementation is null
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
        var interfaces = GetReflectedInterfaces(classDeclaration).ToArray();
        if (interfaces.Length == 0)
        {
            return;
        }

        writer.WriteLine("#if !defined(CX_DYNAMIC_MODULE)");
        writer.WriteLine($"static const struct cx_interface_impl {GetInterfaceRuntimeMapIdentifier(classDeclaration, moduleName).ToCIdentifier()}[] = {{");
        writer.IncreaseIndent();
        foreach (var @interface in interfaces)
        {
            var interfaceTypeInfo = new QualifiedIdentifier(
                @interface.ProjectName ?? moduleName,
                @interface.FullName,
                "__typeinfo").ToCIdentifier();
            var vtable = classDeclaration.ClassType == ClassType.Class
                ? GetInterfaceVTableIdentifier(classDeclaration, @interface, moduleName).ToCIdentifier()
                : "CX_NULL";
            writer.WriteLine($"{{ &{interfaceTypeInfo}, {vtable} }},");
        }
        writer.DecreaseIndent();
        writer.WriteLine("};");
        writer.WriteLine("#else");
        writer.WriteLine($"static struct cx_interface_impl {GetInterfaceRuntimeMapIdentifier(classDeclaration, moduleName).ToCIdentifier()}[{interfaces.Length}];");
        writer.WriteLine("#endif");
    }

    private static IEnumerable<ClassDeclaration> GetReflectedInterfaces(ClassDeclaration declaration)
    {
        return declaration.ClassType == ClassType.Interface
            ? declaration.InterfaceUpcastTargets
            : declaration.InterfaceDispatchTables.Select(table => table.Interface);
    }

    private static void WriteReflectionMetadata(
        IndentingWriter writer,
        ClassDeclaration declaration,
        string moduleName)
    {
        var fields = declaration.MemberDeclarations.Declarations.OfType<FieldDeclaration>().ToArray();
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

    private static IEnumerable<ReflectionFunctionSource> GetReflectionFunctions(
        ClassDeclaration declaration)
    {
        foreach (var function in declaration.MemberDeclarations.Declarations.OfType<FunctionDeclaration>())
        {
            yield return new ReflectionFunctionSource(
                function is ConstructorDeclaration ? declaration.Name : function.Name,
                function.ReturnType,
                function.Parameters,
                function.MemberModifiers,
                function.Const,
                function is ConstructorDeclaration,
                null,
                function.VirtualSlotIndex);
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
                    accessor.Extern);
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

    private static string ToTypeInfoPointer(TypeBase type)
    {
        type = type is ConstType constType ? constType.UnderlyingType : type;
        if (type is PtrType or FunctionType or AutoType or GenericType or NullType)
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
        bool Extern = false);

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
            .Where(type => type.ClassType == ClassType.Class))
        {
            foreach (var table in classDeclaration.InterfaceDispatchTables)
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
            .Where(type => type.ClassType == ClassType.Class))
        {
            foreach (var table in classDeclaration.InterfaceDispatchTables)
            {
                foreach (var slot in table.Slots.Where(slot => slot.Implementation is not null))
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

    private static void WriteInterfaceDispatchThunk(
        IndentingWriter writer,
        ClassDeclaration classDeclaration,
        ClassDeclaration interfaceDeclaration,
        InterfaceDispatchSlot slot,
        string moduleName)
    {
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
        writer.WriteLine("#if !defined(CX_STATIC_LINK)");
        writer.WriteLine($"__cx_module_init_{GetModuleToken(moduleName)}();");
        writer.WriteLine("#endif");

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
            ClassDeclaration or EnumDeclaration =>
                publicTypeNames.Contains(declaration.FullName),
            _ => false,
        }).ToList();

    private static HashSet<QualifiedIdentifier> GetPublicApiTypeNames(
        IReadOnlyCollection<DeclarationBase> declarations)
    {
        var allTypes = EnumerateTypes(declarations).ToArray();
        var publicTypeNames = allTypes
            .Where(type => type switch
            {
                ClassDeclaration classType => classType.Visibility == Visibility.Public,
                EnumDeclaration enumType => enumType.Visibility == Visibility.Public,
                _ => false,
            })
            .Select(type => type.FullName)
            .ToHashSet();
        var referencedTypes = new HashSet<QualifiedIdentifier>();
        var publicFunctions = declarations.OfType<FunctionDeclaration>()
            .Where(IsPublicApiFunction);
        foreach (var function in publicFunctions)
        {
            AddTypeReferences(function.ReturnType, referencedTypes);
            foreach (var parameter in function.Parameters)
            {
                AddTypeReferences(parameter.ParameterType, referencedTypes);
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
                AddTypeReferences(array.ElementType, referencedTypes);
                break;
            case NullableType nullable:
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
        }
    }

    private static bool IsPublicApiType(
        DeclarationBase declaration,
        IReadOnlySet<QualifiedIdentifier> publicTypeNames) =>
        publicTypeNames.Contains(declaration.FullName);

    private static bool IsPublicApiFunction(FunctionDeclaration function) =>
        function.MemberModifiers.Contains(MemberModifier.Public) &&
        (function.ParentClassDeclaration is null ||
            function.ParentClassDeclaration.Visibility == Visibility.Public);

    private static bool IsPublicApiMember(
        DeclarationBase declaration,
        ClassDeclaration parent) =>
        parent.Visibility == Visibility.Public && declaration switch
        {
            FunctionDeclaration function =>
                function.MemberModifiers.Contains(MemberModifier.Public),
            PropertyDeclaration property =>
                property.MemberModifiers.Contains(MemberModifier.Public),
            ClassDeclaration nested => nested.Visibility == Visibility.Public,
            _ => false,
        };

    private static bool IsPublicApiGenericFunction(FunctionSymbol function) =>
        function.Declaration is { } declaration && IsPublicApiFunction(declaration);

    internal static IReadOnlyCollection<(NamedType Type, ClassDeclaration Declaration)>
        GetPublicApiGenericTypeInstances(CxProject project)
    {
        var publicTypeNames = GetPublicApiTypeNames(GetDeclarations(project));
        return GetPublicApiGenericTypeInstances(project, publicTypeNames);
    }

    internal static IReadOnlyCollection<FunctionSymbol> GetPublicApiGenericFunctionInstances(
        CxProject project) =>
        project.GenericFunctionInstances.Where(IsPublicApiGenericFunction).ToArray();

    private static IReadOnlyCollection<(NamedType Type, ClassDeclaration Declaration)>
        GetPublicApiGenericTypeInstances(
            CxProject project,
            IReadOnlySet<QualifiedIdentifier> publicTypeNames)
    {
        var publicConstructedTypes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var declaration in GetPublicApiDeclarations(GetDeclarations(project), publicTypeNames))
        {
            switch (declaration)
            {
                case FunctionDeclaration function:
                    AddConstructedTypeIdentities(function.ReturnType, publicConstructedTypes);
                    foreach (var parameter in function.Parameters)
                    {
                        AddConstructedTypeIdentities(parameter.ParameterType, publicConstructedTypes);
                    }
                    break;
                case ClassDeclaration classType:
                    foreach (var field in classType.MemberDeclarations.Declarations.OfType<FieldDeclaration>())
                    {
                        AddConstructedTypeIdentities(field.Type, publicConstructedTypes);
                    }
                    foreach (var functionMember in classType.MemberDeclarations.Declarations
                        .OfType<FunctionDeclaration>().Where(IsPublicApiFunction))
                    {
                        AddConstructedTypeIdentities(functionMember.ReturnType, publicConstructedTypes);
                        foreach (var parameter in functionMember.Parameters)
                        {
                            AddConstructedTypeIdentities(parameter.ParameterType, publicConstructedTypes);
                        }
                    }
                    foreach (var property in classType.MemberDeclarations.Declarations
                        .OfType<PropertyDeclaration>()
                        .Where(property => IsPublicApiMember(property, classType)))
                    {
                        AddConstructedTypeIdentities(property.Type, publicConstructedTypes);
                    }
                    break;
            }
        }

        foreach (var function in project.GenericFunctionInstances
            .Where(IsPublicApiGenericFunction))
        {
            AddConstructedTypeIdentities(function.ReturnType, publicConstructedTypes);
            foreach (var parameter in function.ParameterTypes)
            {
                AddConstructedTypeIdentities(parameter, publicConstructedTypes);
            }
            if (function.ClosedContainingType is { } containingType)
            {
                AddConstructedTypeIdentities(containingType, publicConstructedTypes);
            }
        }

        return project.GenericTypeInstances.Where(instance =>
            publicTypeNames.Contains(instance.Declaration.FullName) &&
            instance.Type.ConstructedIdentity is { } identity &&
            publicConstructedTypes.Contains(identity.CanonicalName)).ToArray();
    }

    private static void AddConstructedTypeIdentities(
        TypeBase type,
        ISet<string> identities)
    {
        switch (type)
        {
            case ConstType constant:
                AddConstructedTypeIdentities(constant.UnderlyingType, identities);
                break;
            case ArrayType array:
                AddConstructedTypeIdentities(array.ElementType, identities);
                break;
            case NullableType nullable:
                AddConstructedTypeIdentities(nullable.UnderlyingType, identities);
                break;
            case NamedType named:
                if (named.ConstructedIdentity is { } identity)
                {
                    identities.Add(identity.CanonicalName);
                }
                foreach (var argument in named.TypeArguments)
                {
                    AddConstructedTypeIdentities(argument, identities);
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
            ObjectType => "struct CX_ID_3(cxcore, System, Object)",
            NamedType { ClassType: ClassType.Class } namedType =>
                $"struct {namedType.ResolvedTypeFullName.ToCIdentifier()}",
            _ => type.ToCIdentifier(false).TrimEnd('*', ' '),
        };
    }

    private static string ToCIdentifier(this FunctionDeclaration functionDeclaration, string moduleName, int nameOverrideIndex)
    {
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
        return new QualifiedIdentifier(moduleName, name).ToCIdentifier();
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
        return new QualifiedIdentifier(moduleName, name).ToCIdentifier();
    }

    private static string ToCIdentifier(this TypeBase typeBase, bool @const)
    {
        var constString = @const ? "const " : "";
        return typeBase switch
        {
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

            ObjectType => $"{constString}struct CX_ID_3(cxcore, System, Object)*",
            StringType => $"{constString}struct CX_ID_3(cxcore, System, String)*",
            PtrType => $"{constString}cx_ptr",

            FunctionType => throw new NotImplementedException(), // TODO
            VoidType => "void",
            NullType => "cx_ptr",

            ArrayType => $"{constString}struct CX_ID_3(cxcore, System, Array)*",
            NullableType => $"{constString}struct CX_ID_3(cxcore, System, Nullable)",

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

        if (qualifiedIdentifier.Parts.Length == 1)
        {
            return qualifiedIdentifier.Parts[0];
        }

        return $"CX_ID_{qualifiedIdentifier.Parts.Length}({string.Join(", ", qualifiedIdentifier.Parts)})";
    }

    private static string ToCReturnType(this TypeBase typeBase, bool @const)
    {
        if (IsGenericValueType(typeBase))
        {
            return "void";
        }

        return typeBase.ToCIdentifier(@const);
    }

    private static string ToCParameterType(TypeBase typeBase)
    {
        return IsGenericValueType(typeBase)
            ? "void*"
            : typeBase.ToCIdentifier(@const: false);
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

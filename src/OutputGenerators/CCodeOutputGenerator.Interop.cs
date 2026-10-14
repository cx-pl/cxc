using System.Text.RegularExpressions;
using CxCompiler.Model.Common;
using CxCompiler.Model.Errors;
using CxCompiler.Model.Project;
using CxCompiler.Model.Types;
using CxCompiler.Model.Types.BuiltInTypes;
using CxCompiler.Model.Expressions;

namespace CxCompiler.OutputGenerators;

public static partial class CCodeOutputGenerator
{
    private static string NativeSymbolConversionName(CastExpression cast) =>
        "__cx_symbol_cast_" + GetFunctionTypeCIdentifier(GetFunctionType(cast.Operand.InferredType)!) + "_" +
        GetFunctionTypeCIdentifier(GetFunctionType(cast.TargetType)!);

    private static void WriteNativeSymbolConversions(IndentingWriter writer,
        IReadOnlyCollection<DeclarationBase> declarations, IReadOnlyCollection<FunctionDeclaration> localFunctions)
    {
        foreach (var cast in EnumerateFunctions(declarations).Concat(localFunctions)
            .SelectMany(function => EnumerateBodyExpressions(function.Body ?? []))
            .OfType<CastExpression>().Where(cast => GetFunctionType(cast.Operand.InferredType) is NativeFunctionType { SymbolOnly: true } &&
                GetFunctionType(cast.TargetType) is NativeFunctionType)
            .DistinctBy(NativeSymbolConversionName))
        {
            var source = GetFunctionTypeCIdentifier(GetFunctionType(cast.Operand.InferredType)!);
            var target = GetFunctionTypeCIdentifier(GetFunctionType(cast.TargetType)!);
            writer.WriteLine($"static inline {target} {NativeSymbolConversionName(cast)}({source} symbol) {{");
            writer.IncreaseIndent();
            writer.WriteLine("cx_interop_symbol_require(&symbol, sizeof(symbol));");
            writer.WriteLine($"return ({target})symbol;");
            writer.DecreaseIndent(); writer.WriteLine("}");
        }
    }
    private static void WriteNativeFunctionType(IndentingWriter writer, NativeFunctionType native)
    {
        if (native.CallingConvention is "Stdcall" or "Winapi")
        {
            writer.WriteLine("#if !defined(_WIN32)");
            writer.WriteLine("#error \"Native Winapi/Stdcall function pointers require a Windows ABI\"");
            writer.WriteLine("#endif");
        }
        var type = GetFunctionTypeCIdentifier(native);
        var convention = native.CallingConvention is "Stdcall" or "Winapi" ? "CX_NATIVE_STDCALL"
            : native.CallingConvention == "Cdecl" ? "CX_NATIVE_CDECL" : "CX_NATIVE_DEFAULT";
        var parameters = native.SymbolOnly ? "" : native.ParameterTypes.Count == 0 ? "void"
            : string.Join(", ", native.ParameterTypes.Select(ToCParameterType));
        writer.WriteLine($"#ifndef CX_FUNCTION_TYPE_{type}");
        writer.WriteLine($"#define CX_FUNCTION_TYPE_{type}");
        writer.WriteLine($"typedef {native.ReturnType.ToCReturnType(false)} ({convention} *{type})({parameters});");
        writer.WriteLine("#endif");
    }

    private static string NativeCallbackName(CastExpression expression, string module) =>
        "__cx_native_callback_" + GetModuleToken(module) + "_" +
        GetModuleToken(expression.NativeCallbackSymbol!.FullName.ToString()) + "_" +
        GetFunctionTypeCIdentifier(GetFunctionType(expression.TargetType)!);

    private static string NativeRegistrationCallbackName(NativeFunctionType signature) =>
        "__cx_registered_callback_" + GetFunctionTypeCIdentifier(signature);

    private static void WriteNativeCallbacks(IndentingWriter writer,
        IReadOnlyCollection<DeclarationBase> declarations, string module, IReadOnlyCollection<FunctionDeclaration> localFunctions)
    {
        WriteNativeRegistrationCallbacks(writer, declarations, localFunctions);
        foreach (var cast in EnumerateFunctions(declarations).Concat(localFunctions)
            .SelectMany(function => EnumerateBodyExpressions(function.Body ?? []))
            .OfType<CastExpression>().Where(expression => expression.NativeCallbackSymbol is not null)
            .DistinctBy(expression => NativeCallbackName(expression, module)))
        {
            var signature = (NativeFunctionType)GetFunctionType(cast.TargetType)!;
            var symbol = cast.NativeCallbackSymbol!;
            var convention = signature.CallingConvention is "Stdcall" or "Winapi" ? "CX_NATIVE_STDCALL"
                : signature.CallingConvention == "Cdecl" ? "CX_NATIVE_CDECL" : "CX_NATIVE_DEFAULT";
            var parameters = signature.ParameterTypes.Select((type, index) => $"{ToCParameterType(type)} arg{index}").ToArray();
            var result = signature.ReturnType.ToCReturnType(false);
            writer.WriteLine($"static {result} {convention} {NativeCallbackName(cast, module)}({(parameters.Length == 0 ? "void" : string.Join(", ", parameters))}) {{");
            writer.IncreaseIndent();
            writer.WriteLine("struct cx_native_callback_scope scope;");
            writer.WriteLine("struct cx_exception_frame frame;");
            if (signature.ReturnType is not VoidType)
                writer.WriteLine($"{result} result = {(signature.FailureResult == "0" ? ToCDefaultValue(signature.ReturnType) : $"({result})({signature.FailureResult})")};");
            writer.WriteLine("cx_interop_callback_enter(&scope);");
            writer.WriteLine("cx_exception_push(&frame);");
            writer.WriteLine("if (setjmp(frame.environment) == 0) {");
            writer.IncreaseIndent();
            var call = ToCIdentifier(symbol) + "(" + string.Join(", ", parameters.Select((_, index) => "arg" + index)) + ")";
            writer.WriteLine(signature.ReturnType is VoidType ? call + ";" : "result = " + call + ";");
            writer.DecreaseIndent();
            writer.WriteLine("} else {");
            writer.IncreaseIndent();
            writer.WriteLine("cx_interop_callback_failed(cx_exception_current());");
            writer.DecreaseIndent();
            writer.WriteLine("}");
            writer.WriteLine("cx_exception_pop(&frame);");
            writer.WriteLine("cx_interop_callback_leave(&scope);");
            if (signature.ReturnType is not VoidType) writer.WriteLine("return result;");
            writer.DecreaseIndent(); writer.WriteLine("}");
        }
    }

    private static void WriteNativeRegistrationCallbacks(IndentingWriter writer,
        IReadOnlyCollection<DeclarationBase> declarations, IReadOnlyCollection<FunctionDeclaration> localFunctions)
    {
        var registrations = EnumerateFunctions(declarations).Concat(localFunctions)
            .SelectMany(function => EnumerateBodyExpressions(function.Body ?? []))
            .OfType<InvocationExpression>().Where(invocation => invocation.IsNativeRegistration || invocation.IsNativeRegistrationFunction)
            .DistinctBy(invocation => GetFunctionTypeCIdentifier(invocation.FunctionType!));
        foreach (var invocation in registrations)
        {
            var native = (NativeFunctionType)invocation.FunctionType!;
            var managed = invocation.ManagedCallbackSignature!;
            var resultType = native.ReturnType.ToCReturnType(false);
            var convention = native.CallingConvention is "Stdcall" or "Winapi" ? "CX_NATIVE_STDCALL"
                : native.CallingConvention == "Cdecl" ? "CX_NATIVE_CDECL" : "CX_NATIVE_DEFAULT";
            var parameters = native.ParameterTypes.Select((type, index) => $"{ToCParameterType(type)} arg{index}").ToArray();
            var context = "arg" + native.ContextParameter;
            var failure = native.FailureResult == "0" ? ToCDefaultValue(native.ReturnType) : $"({resultType})({native.FailureResult})";
            writer.WriteLine($"static {resultType} {convention} {NativeRegistrationCallbackName(native)}({string.Join(", ", parameters)}) {{");
            writer.IncreaseIndent();
            writer.WriteLine("struct cx_native_callback_scope scope;");
            writer.WriteLine("struct cx_native_registration_scope registrationScope;");
            writer.WriteLine("struct cx_exception_frame frame;");
            if (native.ReturnType is not VoidType) writer.WriteLine($"{resultType} result = {failure};");
            writer.WriteLine($"if (!cx_interop_registration_acquire({context}, &registrationScope)) {(native.ReturnType is VoidType ? "return;" : "return result;")}");
            writer.WriteLine("cx_interop_callback_enter(&scope);");
            writer.WriteLine($"{GetFunctionTypeCIdentifier(managed)} callback = *({GetFunctionTypeCIdentifier(managed)}*)cx_interop_registration_payload({context});");
            writer.WriteLine("cx_exception_push(&frame);");
            writer.WriteLine("if (setjmp(frame.environment) == 0) {");
            writer.IncreaseIndent();
            var arguments = string.Join(", ", parameters.Select((_, index) => index).Where(index => index != native.ContextParameter).Select(index => "arg" + index));
            var call = GetFunctionTypeHelperName(managed, "invoke") + "(callback" + (arguments.Length == 0 ? "" : ", " + arguments) + ")";
            writer.WriteLine(native.ReturnType is VoidType ? call + ";" : "result = " + call + ";");
            writer.DecreaseIndent(); writer.WriteLine("} else {"); writer.IncreaseIndent();
            writer.WriteLine($"cx_interop_registration_failed({context}, cx_exception_current());");
            writer.DecreaseIndent(); writer.WriteLine("}");
            writer.WriteLine("cx_exception_pop(&frame);");
            writer.WriteLine("cx_interop_callback_leave(&scope);");
            writer.WriteLine("cx_interop_registration_release(&registrationScope);");
            if (native.ReturnType is not VoidType) writer.WriteLine("return result;");
            writer.DecreaseIndent(); writer.WriteLine("}");
        }
    }
    private static string NativePointeeType(TypeBase type) => type is ConstType constant
        ? "const " + constant.UnderlyingType.ToCIdentifier(false) : type.ToCIdentifier(false);
    private static void WriteNativeDependencies(TextWriter writer, CxProject project, string projectDirectory, string outputDirectory)
    {
        foreach (var (id, dependency) in project.NativeLibraries.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (!Regex.IsMatch(id, @"^[A-Za-z_][A-Za-z0-9_.-]*$") ||
                dependency.Visibility is not ("public" or "private") ||
                dependency.Provider is not ("os" or "sdk" or "package"))
                throw new CompilationErrorException($"Invalid native dependency '{id}' or its visibility/provider.");
            writer.WriteLine($"# Native dependency: {id} ({dependency.Provider})");
            writer.WriteLine("if(CMAKE_SYSTEM_NAME STREQUAL \"Windows\")");
            WriteTarget(dependency.Windows, "Windows");
            writer.WriteLine("elseif(CMAKE_SYSTEM_NAME STREQUAL \"Linux\")");
            WriteTarget(dependency.Linux, "Linux");
            writer.WriteLine("else()");
            writer.WriteLine($"    message(FATAL_ERROR \"Native library {id} does not support target ${{CMAKE_SYSTEM_NAME}}\")");
            writer.WriteLine("endif()");

            void WriteTarget(NativeLibraryTarget? target, string os)
            {
                if (target is null)
                {
                    writer.WriteLine($"    message(FATAL_ERROR \"Native library {id} does not support target {os}\")");
                    return;
                }
                var visibility = dependency.Visibility.ToUpperInvariant();
                if (target.Linkage is not ("static" or "shared"))
                    throw new CompilationErrorException($"Native library '{id}' linkage must be static or shared.");
                var token = GetModuleToken(project.Name + "_" + id);
                writer.WriteLine("    if(NOT CMAKE_SIZEOF_VOID_P EQUAL 4 AND NOT CMAKE_SIZEOF_VOID_P EQUAL 8)");
                writer.WriteLine($"        message(FATAL_ERROR \"Native library {id} requires a supported 32/64-bit target ABI\")");
                writer.WriteLine("    endif()");
                writer.WriteLine("    set(_cx_native_arch \"${CMAKE_SYSTEM_PROCESSOR}\")");
                writer.WriteLine("    if(CMAKE_GENERATOR_PLATFORM STREQUAL \"Win32\" OR CMAKE_SYSTEM_PROCESSOR MATCHES \"^(i.86|x86)$\")");
                writer.WriteLine("        set(_cx_native_arch x86)");
                writer.WriteLine("    elseif(CMAKE_GENERATOR_PLATFORM STREQUAL \"x64\" OR CMAKE_SYSTEM_PROCESSOR MATCHES \"^(AMD64|amd64|x86_64)$\")");
                writer.WriteLine("        set(_cx_native_arch x64)");
                writer.WriteLine("    endif()");
                writer.WriteLine(os == "Windows"
                    ? "    if(NOT _cx_native_arch MATCHES \"^(x86|x64)$\" OR NOT CMAKE_C_COMPILER_ID STREQUAL \"MSVC\")"
                    : "    if(NOT _cx_native_arch STREQUAL \"x64\" OR NOT CMAKE_C_COMPILER_ID MATCHES \"^(GNU|Clang)$\")");
                writer.WriteLine($"        message(FATAL_ERROR \"Native library {id}: supported ABIs are MSVC Windows x86/x64 and GCC/Clang Linux x64\")");
                writer.WriteLine("    endif()");
                if (target.Architectures.Count > 0)
                {
                    writer.WriteLine($"    set(_cx_native_architectures {CMakeArguments(target.Architectures)})");
                    writer.WriteLine("    if(NOT _cx_native_arch IN_LIST _cx_native_architectures)");
                    writer.WriteLine($"        message(FATAL_ERROR \"Native library {id} does not support architecture ${{_cx_native_arch}}\")");
                    writer.WriteLine("    endif()");
                }
                if (target.Toolchains.Count > 0)
                {
                    writer.WriteLine($"    set(_cx_native_toolchains {CMakeArguments(target.Toolchains)})");
                    writer.WriteLine("    if(NOT CMAKE_C_COMPILER_ID IN_LIST _cx_native_toolchains)");
                    writer.WriteLine($"        message(FATAL_ERROR \"Native library {id} does not support compiler ${{CMAKE_C_COMPILER_ID}}\")");
                    writer.WriteLine("    endif()");
                }
                if (target.SdkRoot is { } sdkRoot)
                    writer.WriteLine($"    list(PREPEND CMAKE_PREFIX_PATH {NativePaths([sdkRoot])})");
                if (target.CmakePackage is { } package)
                {
                    RequireNativeToken(package);
                    writer.WriteLine($"    find_package({package} {CMakeArguments(target.Version is null ? [] : [target.Version])} CONFIG REQUIRED)");
                }
                if (target.PkgConfig is { } pkgConfig)
                {
                    RequireNativeToken(pkgConfig);
                    writer.WriteLine("    find_package(PkgConfig REQUIRED)");
                    writer.WriteLine($"    pkg_check_modules(CX_NATIVE_{token} REQUIRED IMPORTED_TARGET {CMakeArguments([pkgConfig + (target.Version is null ? "" : ">=" + target.Version)])})");
                    if (target.Linkage == "static")
                    {
                        writer.WriteLine("    set(_cx_saved_library_suffixes ${CMAKE_FIND_LIBRARY_SUFFIXES})");
                        writer.WriteLine("    if(NOT WIN32)");
                        writer.WriteLine("        set(CMAKE_FIND_LIBRARY_SUFFIXES .a)");
                        writer.WriteLine("    endif()");
                        writer.WriteLine($"    foreach(_cx_native_library IN LISTS CX_NATIVE_{token}_STATIC_LIBRARIES)");
                        writer.WriteLine($"        find_library(_cx_native_archive NAMES \"${{_cx_native_library}}\" HINTS ${{CX_NATIVE_{token}_STATIC_LIBRARY_DIRS}} REQUIRED NO_CACHE)");
                        writer.WriteLine($"        target_link_libraries({project.Name} {visibility} \"${{_cx_native_archive}}\")");
                        writer.WriteLine("        unset(_cx_native_archive)");
                        writer.WriteLine("    endforeach()");
                        writer.WriteLine("    set(CMAKE_FIND_LIBRARY_SUFFIXES ${_cx_saved_library_suffixes})");
                        writer.WriteLine($"    target_link_options({project.Name} {visibility} ${{CX_NATIVE_{token}_STATIC_LDFLAGS_OTHER}})");
                    }
                    else writer.WriteLine($"    target_link_libraries({project.Name} {visibility} PkgConfig::CX_NATIVE_{token})");
                    var pkgPrefix = $"CX_NATIVE_{token}" + (target.Linkage == "static" ? "_STATIC" : "");
                    writer.WriteLine($"    target_include_directories({project.Name} {visibility} ${{{pkgPrefix}_INCLUDE_DIRS}})");
                    writer.WriteLine($"    target_compile_options({project.Name} {visibility} ${{{pkgPrefix}_CFLAGS_OTHER}})");
                }
                if (target.LinkLibraries.Count + target.ImportedTargets.Count > 0)
                {
                    var links = string.Join(" ", target.LinkLibraries.Select(value => value.Contains('/') || value.Contains('\\')
                        ? NativePaths([value]) : CMakeArguments([value])).Concat(target.ImportedTargets.Select(value => CMakeArguments([value]))));
                    writer.WriteLine($"    target_link_libraries({project.Name} {visibility} {links})");
                }
                if (target.IncludeDirectories.Count > 0)
                    writer.WriteLine($"    target_include_directories({project.Name} {visibility} {NativePaths(target.IncludeDirectories)})");
                if (target.Definitions.Count > 0)
                    writer.WriteLine($"    target_compile_definitions({project.Name} {visibility} {CMakeArguments(target.Definitions)})");
                if (target.RuntimeSearchPaths.Count > 0)
                    writer.WriteLine($"    set_property(TARGET {project.Name} APPEND PROPERTY BUILD_RPATH {NativePaths(target.RuntimeSearchPaths)})");
                if (os == "Linux")
                    writer.WriteLine($"    set_property(TARGET {project.Name} APPEND PROPERTY BUILD_RPATH \"$ORIGIN\")");
                foreach (var artifact in target.RuntimeArtifacts)
                {
                    writer.WriteLine($"    add_custom_command(TARGET {project.Name} POST_BUILD COMMAND ${{CMAKE_COMMAND}} -E copy_if_different {NativePaths([artifact])} \"$<TARGET_FILE_DIR:{project.Name}>\")");
                    // Deployment files are required by a shared wrapper even when
                    // its native headers and link interface remain private.
                    writer.WriteLine($"    set_property(TARGET {project.Name} APPEND PROPERTY INTERFACE_CX_NATIVE_RUNTIME_FILES {NativePaths([artifact])})");
                }
                // Header diagnostics are produced by the C compiler with the selected SDK's
                // transitive include paths; no build-host filesystem probing occurs here.
            }
        }
        foreach (var name in project.ResolvedProjectNames)
        {
            writer.WriteLine($"get_target_property(_cx_native_files {name} INTERFACE_CX_NATIVE_RUNTIME_FILES)");
            writer.WriteLine("if(_cx_native_files)");
            writer.WriteLine("    if(CMAKE_SYSTEM_NAME STREQUAL \"Linux\")");
            writer.WriteLine($"        set_property(TARGET {project.Name} APPEND PROPERTY BUILD_RPATH \"$ORIGIN\")");
            writer.WriteLine("    endif()");
            writer.WriteLine("    foreach(_cx_native_file IN LISTS _cx_native_files)");
            writer.WriteLine($"        add_custom_command(TARGET {project.Name} POST_BUILD COMMAND ${{CMAKE_COMMAND}} -E copy_if_different \"${{_cx_native_file}}\" \"$<TARGET_FILE_DIR:{project.Name}>\")");
            if (project.Type == CxProjectType.Library)
                writer.WriteLine($"        set_property(TARGET {project.Name} APPEND PROPERTY INTERFACE_CX_NATIVE_RUNTIME_FILES \"${{_cx_native_file}}\")");
            writer.WriteLine("    endforeach()");
            writer.WriteLine("endif()");
        }

        string NativePaths(IEnumerable<string> paths) => CMakeArguments(paths.Select(value => (Path.IsPathRooted(value) ? value :
            "${CMAKE_CURRENT_LIST_DIR}/" + Path.GetRelativePath(outputDirectory, Path.GetFullPath(Path.Combine(projectDirectory, value))))
            .Replace("{arch}", "${_cx_native_arch}")))
            .Replace("\\${CMAKE_CURRENT_LIST_DIR}", "${CMAKE_CURRENT_LIST_DIR}")
            .Replace("\\${_cx_native_arch}", "${_cx_native_arch}");
    }

    private static void RequireNativeToken(string value)
    {
        if (!Regex.IsMatch(value, @"^[A-Za-z0-9_.+-]+$"))
            throw new CompilationErrorException($"Invalid native dependency token '{value}'.");
    }

    private static string CMakeArguments(IEnumerable<string> values) => string.Join(" ", values.Select(value =>
    {
        if (value.Contains('\n') || value.Contains('\r') || value.Contains('\0') || value.Contains(';'))
            throw new CompilationErrorException("Native dependency values cannot contain control characters or CMake list separators.");
        return "\"" + value.Replace("\\", "/").Replace("$", "\\$").Replace("\"", "\\\"") + "\"";
    }));

    private static void WriteNativeHeaders(IndentingWriter writer, CxProject project, bool publicApi)
    {
        // Preserve SDK include order: Windows OpenGL headers require windows.h first.
        foreach (var os in new[] { "Windows", "Linux" })
        {
            var headers = project.NativeLibraries.Values.Where(dependency => !publicApi || dependency.Visibility == "public")
                .Select(dependency => os == "Windows" ? dependency.Windows : dependency.Linux)
                .Where(target => target is not null).SelectMany(target => target!.Headers).Distinct().ToArray();
            if (headers.Length == 0) continue;
            writer.WriteLine(os == "Windows" ? "#if defined(_WIN32)" : "#if defined(__linux__)");
            foreach (var header in headers)
            {
                if (!Regex.IsMatch(header, @"^[A-Za-z0-9_./-]+$") || header.Contains(".."))
                    throw new CompilationErrorException($"Malformed native header '{header}'.");
                writer.WriteLine($"#include <{header}>");
            }
            writer.WriteLine("#endif");
        }
        var nativeTypes = OrderNativeLayouts(project.CompilationContexts.Where(context => !context.IsProjectReference)
            .SelectMany(context => EnumerateNativeTypes(context.DeclarationScope.Declarations))
            .Where(type => !publicApi || type.Visibility == Visibility.Public).ToArray());
        foreach (var header in nativeTypes.Select(type => type.NativeType!.Header)
            .Where(header => header.Length != 0).Distinct().Order(StringComparer.Ordinal))
            writer.WriteLine($"#include <{header}>");
        foreach (var type in nativeTypes.Where(type => type.NativeType!.OwnLayout))
            writer.WriteLine($"{type.NativeType!.CType};");
        foreach (var function in project.CompilationContexts.Where(context => !context.IsProjectReference)
            .SelectMany(context => EnumerateNativeDelegates(context.DeclarationScope.Declarations))
            .Select(declaration => declaration.Signature).OfType<NativeFunctionType>().DistinctBy(GetFunctionTypeCIdentifier))
            WriteNativeFunctionType(writer, function);
        foreach (var type in nativeTypes)
        {
            var native = type.NativeType!;
            if (native.OwnLayout)
            {
                if (native.Pack != 0) writer.WriteLine($"#pragma pack(push, {native.Pack})");
                writer.WriteLine($"{native.CType} {{");
                var first = true;
                foreach (var field in type.MemberDeclarations.Declarations.OfType<FieldDeclaration>())
                {
                    var alignment = first && native.Alignment != 0 ? $"_Alignas({native.Alignment}) " : "";
                    first = false;
                    var declaration = field.Type is FixedArrayType array
                        ? $"{array.ElementType.ToCIdentifier(false)} {field.Name}[{array.Length}]"
                        : $"{field.Type.ToCIdentifier(false)} {field.Name}";
                    writer.WriteLine($"    {alignment}{declaration};");
                }
                writer.WriteLine("};");
                if (native.Pack != 0) writer.WriteLine("#pragma pack(pop)");
            }
            if (native.Opaque)
            {
                if (native.Header.Length == 0)
                {
                    if (!native.CType.StartsWith("struct ", StringComparison.Ordinal))
                        throw new CompilationErrorException("Headerless opaque types require a 'struct Name' C type.", type.SourceSpan);
                    writer.WriteLine($"{native.CType};");
                }
                continue;
            }
            if (native.Size > 0)
                writer.WriteLine($"_Static_assert(sizeof({native.CType}) == {native.Size}, \"SDK size mismatch: {type.Name}\");");
            if (native.Alignment > 0)
                writer.WriteLine($"_Static_assert(_Alignof({native.CType}) == {native.Alignment}, \"SDK alignment mismatch: {type.Name}\");");
            foreach (var field in type.MemberDeclarations.Declarations.OfType<FieldDeclaration>())
            {
                if (field.Type is NamedType { NativeType.Opaque: true } || !IsNativeProjectionType(field.Type))
                    throw new CompilationErrorException("Native SDK records contain only native field projections.", field.SourceSpan);
                if (field.IsStatic || field.Initializer is not null)
                    throw new CompilationErrorException("Native SDK field projections cannot have static storage or initializers.", field.SourceSpan);
                var fieldType = field.Type is FixedArrayType array
                    ? $"{array.ElementType.ToCIdentifier(false)} (*)[{array.Length}]"
                    : field.Type.ToCIdentifier(false) + "*";
                writer.WriteLine($"_Static_assert(_Generic(&(({native.CType}*)0)->{field.Name}, {fieldType}: 1, default: 0), \"SDK field type mismatch: {type.Name}.{field.Name}\");");
                if (field.Annotations.SingleOrDefault(annotation => annotation.ResolvedTypeName == "cxcore.System.Interop.NativeField") is { } projection)
                {
                    if (!int.TryParse(projection.BoundArguments["offset"], out var offset) || offset < 0)
                        throw new CompilationErrorException("Native field offsets must be non-negative decimal integers.", field.SourceSpan);
                    writer.WriteLine($"_Static_assert(offsetof({native.CType}, {field.Name}) == {offset}, \"SDK field offset mismatch: {type.Name}.{field.Name}\");");
                }
            }
        }
    }

    private static IEnumerable<ClassDeclaration> EnumerateNativeTypes(IEnumerable<DeclarationBase> declarations)
    {
        foreach (var type in declarations.OfType<ClassDeclaration>())
        {
            if (type.NativeType is not null) yield return type;
            foreach (var nested in EnumerateNativeTypes(type.MemberDeclarations.Declarations)) yield return nested;
        }
    }

    private static IEnumerable<DelegateDeclaration> EnumerateNativeDelegates(IEnumerable<DeclarationBase> declarations)
    {
        foreach (var declaration in declarations)
        {
            if (declaration is DelegateDeclaration function) yield return function;
            if (declaration is ClassDeclaration type)
                foreach (var nested in EnumerateNativeDelegates(type.MemberDeclarations.Declarations)) yield return nested;
        }
    }

    private static ClassDeclaration[] OrderNativeLayouts(ClassDeclaration[] declarations)
    {
        var completed = new HashSet<ClassDeclaration>();
        var active = new HashSet<ClassDeclaration>();
        var ordered = new List<ClassDeclaration>();
        foreach (var declaration in declarations) Visit(declaration);
        return ordered.ToArray();

        void Visit(ClassDeclaration declaration)
        {
            if (completed.Contains(declaration)) return;
            if (!active.Add(declaration))
                throw new CompilationErrorException("Native layouts cannot contain cycles of fields passed by value.", declaration.SourceSpan);
            if (declaration.NativeType!.OwnLayout)
                foreach (var field in declaration.MemberDeclarations.Declarations.OfType<FieldDeclaration>())
                {
                    var type = UnwrapConst(field.Type);
                    while (type is FixedArrayType array) type = UnwrapConst(array.ElementType);
                    if (type is NamedType { NativeType.OwnLayout: true } named &&
                        declarations.FirstOrDefault(candidate => candidate.NativeType!.CType == named.NativeType.CType) is { } dependency)
                        Visit(dependency);
                }
            active.Remove(declaration); completed.Add(declaration); ordered.Add(declaration);
        }
    }

    private static bool IsNativeProjectionType(TypeBase type) => UnwrapConst(type) is
        IntTypeBase or FloatTypeBase or CharType or BoolType or PtrType or NativeFunctionType or
        NamedType { DelegateSignature: NativeFunctionType } or NamedType { NativeType.Opaque: false } or FixedArrayType;

    private static string NativeConvention(NativeImportMetadata import) => import.CallingConvention switch
    {
        "Stdcall" or "Winapi" => "CX_NATIVE_STDCALL",
        "Cdecl" => "CX_NATIVE_CDECL", _ => "CX_NATIVE_DEFAULT",
    };

    private static string NativeCallName(FunctionDeclaration function, string module) =>
        function.NativeImport!.ErrorPolicy == "None" && !function.NativeImport.Blocking ? function.NativeImport.EntryPoint
            : $"__cx_native_{GetModuleToken(module)}_{GetModuleToken(function.FullName.ToString())}";

    private static void WriteNativeImportDeclaration(IndentingWriter writer, FunctionDeclaration function, string module)
    {
        var import = function.NativeImport!;
        if (import.CallingConvention is "Stdcall" or "Winapi" || import.ErrorPolicy == "LastError")
        {
            writer.WriteLine("#if !defined(_WIN32)");
            writer.WriteLine($"#error \"Native import {import.EntryPoint} requires a Windows target ABI\"");
            writer.WriteLine("#endif");
        }
        var parameters = string.Join(", ", function.Parameters.Select(parameter =>
            $"{ToCParameterType(parameter.ParameterType)} {parameter.Name}"));
        if (parameters.Length == 0) parameters = "void";
        var returnType = function.ReturnType.ToCReturnType(false);
        if (import.Headers.Count == 0)
            writer.WriteLine($"extern {returnType} {NativeConvention(import)} {import.EntryPoint}({parameters});");
        else
            WriteNativeSignatureAssertion(writer, function, module);
        if (import.Blocking)
        {
            WriteBlockingNativeImport(writer, function, module, parameters, returnType);
            return;
        }
        if (import.ErrorPolicy == "None") return;
        writer.WriteLine($"static inline {returnType} {NativeCallName(function, module)}({parameters}) {{");
        writer.IncreaseIndent();
        var call = $"{import.EntryPoint}({string.Join(", ", function.Parameters.Select(parameter => parameter.Name))})";
        writer.WriteLine(function.ReturnType is VoidType ? $"{call};" : $"{returnType} result = {call};");
        writer.WriteLine(import.ErrorPolicy == "LastError" ? "cx_interop_capture_last_error();" : "cx_interop_capture_errno();");
        if (function.ReturnType is not VoidType) writer.WriteLine("return result;");
        writer.DecreaseIndent();
        writer.WriteLine("}");
    }

    private static void WriteNativeSignatureAssertion(IndentingWriter writer, FunctionDeclaration function, string module)
    {
        // By-value fixed-width integers allow ABI-equivalent C integer spellings.
        // Pointer pointees and SDK typedefs retain exact C identity.
        foreach (var windows in new[] { true, false })
        {
            writer.WriteLine(windows ? "#if defined(_WIN32)" : "#else");
            var types = function.Parameters.Select(parameter => parameter.ParameterType).Prepend(function.ReturnType).ToArray();
            List<string[]> alternatives = [[]];
            foreach (var type in types)
            {
                var actual = UnwrapConst(type);
                var spellings = new List<string> { type.ToCIdentifier(false) };
                if (actual is IntType) spellings.Add(windows ? "long" : "int");
                if (actual is UIntType) spellings.Add(windows ? "unsigned long" : "unsigned int");
                if (!windows && actual is LongType) spellings.Add("long");
                if (!windows && actual is ULongType) spellings.Add("unsigned long");
                // Resolve typedef-equivalent duplicates by using one spelling for ordinary int.
                if (!windows && actual is IntType or UIntType) spellings = [type.ToCIdentifier(false)];
                alternatives = alternatives.SelectMany(prefix => spellings.Distinct().Select(spelling => prefix.Append(spelling).ToArray())).ToList();
                if (alternatives.Count > 256)
                    throw new CompilationErrorException("Use exact SDK native scalar types for this native signature.", function.SourceSpan);
            }
            var name = "__cx_abi_" + GetModuleToken($"{module}_{function.FullName}");
            var associations = new List<string>();
            for (var index = 0; index < alternatives.Count; index++)
            {
                var signature = alternatives[index];
                var arguments = signature.Length == 1 ? "void" : string.Join(", ", signature.Skip(1));
                writer.WriteLine($"typedef {signature[0]} ({NativeConvention(function.NativeImport!)} *{name}_{index})({arguments});");
                associations.Add($"{name}_{index}: 1");
                if (windows && function.NativeImport!.CallingConvention == "PlatformDefault")
                    writer.WriteLine($"typedef {signature[0]} (CX_NATIVE_STDCALL *{name}_sdk_{index})({arguments});");
            }
            writer.WriteLine($"_Static_assert(_Generic(&{function.NativeImport!.EntryPoint}, {string.Join(", ", associations)}");
            if (windows && function.NativeImport.CallingConvention == "PlatformDefault")
            {
                writer.WriteLine("#if defined(_M_IX86)");
                writer.WriteLine(", " + string.Join(", ", Enumerable.Range(0, alternatives.Count).Select(index => $"{name}_sdk_{index}: 1")));
                writer.WriteLine("#endif");
            }
            writer.WriteLine($", default: 0), \"Native SDK signature/ABI mismatch: {function.NativeImport.EntryPoint}\");");
        }
        writer.WriteLine("#endif");
    }

    private static void WriteBlockingNativeImport(IndentingWriter writer, FunctionDeclaration function,
        string module, string parameters, string returnType)
    {
        parameters = function.Parameters.Count == 0 ? "void" : string.Join(", ", function.Parameters.Select((parameter, index) =>
            $"{ToCParameterType(parameter.ParameterType)} __cx_arg_{index}"));
        var import = function.NativeImport!;
        var name = NativeCallName(function, module);
        var contextType = name + "_context";
        writer.WriteLine("#include <stdlib.h>");
        writer.WriteLine($"struct {contextType} {{");
        writer.IncreaseIndent();
        foreach (var (parameter, index) in function.Parameters.Select((parameter, index) => (parameter, index)))
            writer.WriteLine($"{ToCParameterType(parameter.ParameterType)} p{index};");
        if (function.ReturnType is not VoidType) writer.WriteLine($"{returnType} result;");
        else if (function.Parameters.Count == 0) writer.WriteLine("char reserved;");
        writer.DecreaseIndent(); writer.WriteLine("};");
        writer.WriteLine($"static void {name}_run(void *storage) {{");
        writer.IncreaseIndent();
        writer.WriteLine($"struct {contextType} *context = storage;");
        var call = import.EntryPoint + "(" + string.Join(", ", function.Parameters.Select((_, index) => "context->p" + index)) + ")";
        writer.WriteLine(function.ReturnType is VoidType ? call + ";" : "context->result = " + call + ";");
        if (import.ErrorPolicy != "None")
            writer.WriteLine(import.ErrorPolicy == "LastError" ? "cx_interop_capture_last_error();" : "cx_interop_capture_errno();");
        writer.DecreaseIndent(); writer.WriteLine("}");
        writer.WriteLine($"static inline {returnType} {name}({parameters}) {{");
        writer.IncreaseIndent();
        writer.WriteLine($"struct {contextType} *context = cx_interop_context_alloc_aligned(sizeof(*context), _Alignof(struct {contextType}));");
        foreach (var (_, index) in function.Parameters.Select((parameter, index) => (parameter, index)))
            writer.WriteLine($"context->p{index} = __cx_arg_{index};");
        writer.WriteLine($"cx_gc_run_native_call({name}_run, context);");
        if (function.ReturnType is not VoidType) writer.WriteLine($"{returnType} result = context->result;");
        writer.WriteLine("cx_interop_context_free_aligned(context);");
        if (function.ReturnType is not VoidType) writer.WriteLine("return result;");
        writer.DecreaseIndent(); writer.WriteLine("}");
    }
}

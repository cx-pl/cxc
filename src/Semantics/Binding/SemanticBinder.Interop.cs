using System.Text.Json;
using System.Text.RegularExpressions;
using CxCompiler.Model.Common;
using CxCompiler.Model.Errors;
using CxCompiler.Model.Project;
using CxCompiler.Model.Types;
using CxCompiler.Model.Types.BuiltInTypes;
using CxCompiler.Model.Expressions;

namespace CxCompiler.Semantics;

public sealed partial class SemanticBinder
{
    private static readonly Dictionary<string, (string Name, TypeBase Type, string? Default)[]>
        CoreAnnotationParameters = new(StringComparer.Ordinal)
    {
        ["System.Interop.NativeImport"] = [
            ("library", BuiltInSystemTypes.String, null),
            ("entryPoint", BuiltInSystemTypes.String, "null"),
            ("callingConvention", BuiltInSystemTypes.String, "NativeCallingConvention.PlatformDefault"),
            ("captureError", BuiltInSystemTypes.String, "NativeErrorPolicy.None"),
            ("blocking", BuiltInSystemTypes.Bool, "false")],
        ["System.AnnotationUsage"] = [
            ("targets", BuiltInSystemTypes.String, "\"class,function,constructor,field,property,enum,delegate,parameter,return\""),
            ("repeatable", BuiltInSystemTypes.Bool, "false")],
        ["System.Interop.NativeType"] = [
            ("name", BuiltInSystemTypes.String, null), ("header", BuiltInSystemTypes.String, "\"\""),
            ("opaque", BuiltInSystemTypes.Bool, "false"), ("kind", BuiltInSystemTypes.String, "\"record\""),
            ("size", BuiltInSystemTypes.Int, "0"), ("alignment", BuiltInSystemTypes.Int, "0")],
        ["System.Interop.NativeLayout"] = [
            ("layout", BuiltInSystemTypes.String, "\"sequential\""), ("pack", BuiltInSystemTypes.Int, "0"),
            ("alignment", BuiltInSystemTypes.Int, "0"), ("size", BuiltInSystemTypes.Int, "0")],
        ["System.Interop.NativeField"] = [("offset", BuiltInSystemTypes.Int, null)],
        ["System.Interop.NativeFunction"] = [
            ("callingConvention", BuiltInSystemTypes.String, "NativeCallingConvention.PlatformDefault"),
            ("failureResult", BuiltInSystemTypes.String, "\"0\""),
            ("contextParameter", BuiltInSystemTypes.Int, "-1"), ("symbolOnly", BuiltInSystemTypes.Bool, "false")],
    };

    private void AddCoreAnnotationTypes()
    {
        foreach (var (name, cType, signed) in new[] {
            ("CLong", "long", true), ("CULong", "unsigned long", false),
            ("SizeT", "size_t", false), ("PtrDiffT", "ptrdiff_t", true),
            ("IntPtr", "intptr_t", true), ("UIntPtr", "uintptr_t", false),
            ("CBool", "_Bool", false), ("CChar", "char", true) })
            _aliases.Add(new AliasSymbol("cxcore", new AliasDeclaration(name,
                new QualifiedIdentifier("System", "Interop"), [], new NativeScalarType(name, cType, signed)), []));
        foreach (var fullName in CoreAnnotationParameters.Keys.Prepend("System.Annotation"))
        {
            if (FindCoreClass(fullName) is not null) continue;
            var parts = fullName.Split('.');
            var owner = new ClassDeclaration([ClassModifier.Public, ClassModifier.Abstract], false,
                ClassType.Class, null, parts[^1], [],
                new DeclarationScope(@namespace: new QualifiedIdentifier(parts[..^1])))
            { ProjectName = "cxcore" };
            var type = new NamedType(fullName, []);
            type.SetResolvedType(owner.FullName, "cxcore", ClassType.Class);
            _types.Add(new TypeSymbol("cxcore", owner, type));
            if (fullName == "System.Annotation")
            {
                var constructor = new ConstructorDeclaration(owner.FullName,
                    [MemberModifier.Protected, MemberModifier.Extern], owner);
                owner.MemberDeclarations.AddDeclaration(constructor);
                _constructors.Add(new ConstructorSymbol(type, ClassType.Class,
                    new FunctionSymbol("cxcore", constructor.FullName, [], BuiltInSystemTypes.Void,
                        declaration: constructor)));
            }
        }
        if (FindCoreClass("System.Interop.NativeError") is null)
        {
            var owner = new ClassDeclaration([ClassModifier.Public, ClassModifier.Static], false,
                ClassType.Class, null, "NativeError", [],
                new DeclarationScope(@namespace: new QualifiedIdentifier("System", "Interop")))
            { ProjectName = "cxcore" };
            var type = new NamedType("NativeError", []);
            type.SetResolvedType(owner.FullName, "cxcore", ClassType.Class);
            _types.Add(new TypeSymbol("cxcore", owner, type));
            foreach (var (name, result) in new (string, TypeBase)[] {
                ("GetLastError", BuiltInSystemTypes.UInt), ("GetErrno", BuiltInSystemTypes.Int) })
            {
                var method = new FunctionDeclaration(name, owner.FullName, result,
                    [MemberModifier.Public, MemberModifier.Static, MemberModifier.Extern], owner);
                owner.MemberDeclarations.AddDeclaration(method);
                _symbols.Add(new FunctionSymbol("cxcore", method.FullName, [], result, declaration: method));
            }
        }
    }

    private void BindAnnotations(CxProject project)
    {
        // Bind usage policies first so source order cannot alter repeatability/targets.
        foreach (var context in project.CompilationContexts)
            foreach (var declaration in WalkAnnotatedDeclarations(context.DeclarationScope.Declarations))
                foreach (var annotation in declaration.Annotations)
                    ResolveAnnotation(annotation, declaration, context.Imports);

        foreach (var context in project.CompilationContexts)
            foreach (var declaration in WalkAnnotatedDeclarations(context.DeclarationScope.Declarations))
            {
                var seen = new HashSet<(string?, string?)>();
                foreach (var annotation in declaration.Annotations)
                {
                    var annotationType = _types.Single(type =>
                        $"{type.ModuleName}.{type.Declaration.FullName}" == annotation.ResolvedTypeName);
                    var usage = annotationType.Declaration.Annotations.SingleOrDefault(item =>
                        item.ResolvedTypeName == "cxcore.System.AnnotationUsage");
                    var targets = usage is null ? "class,function,constructor,field,property,enum,delegate,parameter,return"
                        : AnnotationString(usage.BoundArguments["targets"], usage);
                    var target = AnnotationTarget(declaration, annotation);
                    if (annotation.ResolvedTypeName == "cxcore.System.Interop.NativeImport")
                        targets = "function";
                    else if (annotation.ResolvedTypeName == "cxcore.System.AnnotationUsage")
                        targets = "class";
                    else if (annotation.ResolvedTypeName == "cxcore.System.Interop.NativeType")
                        targets = "class";
                    else if (annotation.ResolvedTypeName == "cxcore.System.Interop.NativeLayout")
                        targets = "class";
                    else if (annotation.ResolvedTypeName == "cxcore.System.Interop.NativeField")
                    {
                        targets = "field";
                        if (declaration is not FieldDeclaration { ParentClassDeclaration.NativeType: not null })
                            throw AnnotationError(annotation, "NativeField requires a native record field.");
                    }
                    else if (annotation.ResolvedTypeName == "cxcore.System.Interop.NativeFunction")
                        targets = "delegate";
                    if (!targets.Split(',', StringSplitOptions.TrimEntries).Contains(target))
                        throw AnnotationError(annotation, $"Annotation '{annotation.Name}' is not valid on '{target}'.");
                    if (!seen.Add((annotation.ResolvedTypeName, annotation.Target)) &&
                        usage?.BoundArguments["repeatable"] != "true")
                        throw AnnotationError(annotation, $"Annotation '{annotation.Name}' is not repeatable.");
                    if (annotation.ResolvedTypeName == "cxcore.System.Interop.NativeImport")
                        BindNativeImport(project, context.ProjectName ?? project.Name,
                            (FunctionDeclaration)declaration, annotation);
                }
            }

        var imports = project.CompilationContexts.SelectMany(context =>
            EnumerateFunctions(context.DeclarationScope.Declarations)).Where(function => function.NativeImport is not null);
        foreach (var group in imports.GroupBy(function =>
            (function.NativeImport!.Library, function.NativeImport.EntryPoint)))
        {
            var first = group.First();
            if (group.Any(function => function.NativeImport!.CallingConvention != first.NativeImport!.CallingConvention ||
                !IsType(function.ReturnType, first.ReturnType) ||
                function.Parameters.Count != first.Parameters.Count ||
                function.Parameters.Zip(first.Parameters).Any(pair =>
                    !IsType(pair.First.ParameterType, pair.Second.ParameterType))))
                throw new CompilationErrorException($"Conflicting native ABI declarations for '{group.Key.EntryPoint}'.", first.SourceSpan);
        }
        foreach (var context in project.CompilationContexts)
            foreach (var declaration in WalkAnnotatedDeclarations(context.DeclarationScope.Declarations))
                if (declaration is FieldDeclaration field && field.ParentClassDeclaration.NativeType is not null)
                {
                    var fieldType = UnwrapConst(field.Type);
                    if (field.IsStatic || field.Initializer is not null ||
                        !(IsNativeScalar(fieldType) || fieldType is FixedArrayType))
                        throw new CompilationErrorException("Native records contain native fields only, without static storage or initializers.", field.SourceSpan);
                }
                else if (declaration is FunctionDeclaration function &&
                    (UnwrapConst(function.ReturnType) is NamedType { NativeType.Opaque: true } ||
                     function.Parameters.Any(parameter => UnwrapConst(parameter.ParameterType) is NamedType { NativeType.Opaque: true })))
                    throw new CompilationErrorException("Opaque native types are legal through pointers only.", function.SourceSpan);
                else if (declaration is FieldDeclaration managedField &&
                    (UnwrapConst(managedField.Type) is NamedType { NativeType.Opaque: true } || managedField.Type is FixedArrayType))
                    throw new CompilationErrorException("Opaque native values and fixed inline arrays cannot be stored in managed fields.", managedField.SourceSpan);
    }

    private void ResolveAnnotation(AnnotationApplication annotation, DeclarationBase owner,
        IReadOnlyList<QualifiedIdentifier> imports)
    {
        var names = GetCandidateNames(annotation.Name, owner.Namespace, imports);
        var matches = _types.Where(type => names.Contains(type.Declaration.FullName)).ToArray();
        if (matches.Length != 1)
            throw AnnotationError(annotation, $"Annotation type '{annotation.Name}' is {(matches.Length == 0 ? "unknown" : "ambiguous")}.");
        var match = matches[0];
        annotation.ResolvedTypeName = $"{match.ModuleName}.{match.Declaration.FullName}";
        var builtin = match.ModuleName == "cxcore" && CoreAnnotationParameters.TryGetValue(
            match.Declaration.FullName.ToString(), out _);
        if (!builtin && !IsAnnotationClass(match.Declaration))
            throw AnnotationError(annotation, $"Type '{annotation.Name}' must derive from System.Annotation.");
        var constructors = match.Declaration.MemberDeclarations.Declarations
            .OfType<ConstructorDeclaration>().Where(item => !item.IsStatic).ToArray();
        var candidates = builtin
            ? new[] { CoreAnnotationParameters[match.Declaration.FullName.ToString()] }
            : constructors.Select(constructor => constructor.Parameters.Select(parameter =>
                (parameter.Name, parameter.ParameterType, parameter.DefaultValueText)).ToArray()).ToArray();
        if (candidates.Length == 0) candidates = [[]];
        var successful = new List<Dictionary<string, string>>();
        string? failure = null;
        foreach (var parameters in candidates)
        {
            try { successful.Add(BindAnnotationArguments(annotation, parameters, builtin)); }
            catch (CompilationErrorException error) { failure = error.Message; }
        }
        if (successful.Count != 1)
            throw AnnotationError(annotation, successful.Count == 0 ? failure ?? "No annotation constructor matches."
                : $"Annotation constructor '{annotation.Name}' is ambiguous.");
        annotation.BoundArguments = successful[0];
    }

    private bool IsAnnotationClass(ClassDeclaration declaration)
    {
        for (ClassDeclaration? current = declaration; current is not null; current = current.BaseClassDeclaration)
            if (current.FullName.ToString() == "System.Annotation" && current.ProjectName == "cxcore") return true;
        return false;
    }

    private Dictionary<string, string> BindAnnotationArguments(AnnotationApplication annotation,
        (string Name, TypeBase Type, string? Default)[] parameters, bool builtin)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var positionalIndex = 0;
        var namedStarted = false;
        foreach (var argument in annotation.Arguments)
        {
            if (argument.Name is null && namedStarted)
                throw AnnotationError(annotation, "Positional annotation arguments must precede named arguments.");
            namedStarted |= argument.Name is not null;
            var name = argument.Name ?? (positionalIndex < parameters.Length ? parameters[positionalIndex++].Name : "");
            var parameter = parameters.SingleOrDefault(item => item.Name == name);
            if (parameter.Name is null)
                throw AnnotationError(annotation, $"Unknown or excess annotation argument '{name}'.");
            if (!values.TryAdd(name, argument.ValueText))
                throw AnnotationError(annotation, $"Duplicate annotation argument '{name}'.");
            if (builtin && name is "callingConvention" or "captureError") continue;
            ValidateAnnotationConstant(argument.ValueText, parameter.Type, annotation);
            if (UnwrapConst(parameter.Type) is IntTypeBase && TryGetIntegerConstant(argument.ValueText, out var integer))
                values[name] = integer.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        foreach (var parameter in parameters)
            if (!values.ContainsKey(parameter.Name))
            {
                if (parameter.Default is null)
                    throw AnnotationError(annotation, $"Missing annotation argument '{parameter.Name}'.");
                values[parameter.Name] = parameter.Default;
            }
        return values;
    }

    private void ValidateAnnotationConstant(string value, TypeBase type, AnnotationApplication annotation)
    {
        type = UnwrapConst(type);
        if (type is StringType) { if (value == "null") return; AnnotationString(value, annotation); return; }
        if (type is BoolType && value is "true" or "false") return;
        if (type is FloatTypeBase && double.TryParse(value.TrimEnd('f', 'F', 'd', 'D'),
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var floating) && double.IsFinite(floating)) return;
        if (type is CharType && TryGetCharacterConstant(value, out _)) return;
        if (type is IntTypeBase integer && TryGetIntegerConstant(value, out var constant))
        {
            var (minimum, maximum) = GetIntegralRange(integer) ?? (int.MinValue, int.MaxValue);
            if (constant >= minimum && constant <= maximum) return;
            throw AnnotationError(annotation, $"Annotation integer constant '{value}' is outside '{type.Name}' range.");
        }
        if (type is NamedType { ClassType: ClassType.Enum } enumType)
        {
            var declaration = _enums.SingleOrDefault(item => IsType(item.Type, enumType))?.Declaration;
            var prefix = value[..Math.Max(0, value.LastIndexOf('.'))];
            var member = declaration?.Members
                .SingleOrDefault(item => value.EndsWith("." + item.Name, StringComparison.Ordinal));
            if (member is not null && (prefix == declaration!.Name || prefix == declaration.FullName.ToString())) return;
        }
        throw AnnotationError(annotation, $"Annotation argument '{value}' must be a constant of type '{type.Name}'.");
    }

    private static IEnumerable<DeclarationBase> WalkAnnotatedDeclarations(IEnumerable<DeclarationBase> declarations)
    {
        foreach (var declaration in declarations)
        {
            yield return declaration;
            if (declaration is ClassDeclaration owner)
                foreach (var member in WalkAnnotatedDeclarations(owner.MemberDeclarations.Declarations)) yield return member;
            if (declaration is FunctionDeclaration function)
                foreach (var parameter in function.Parameters) yield return parameter;
        }
    }

    private static string AnnotationTarget(DeclarationBase declaration, AnnotationApplication annotation)
    {
        if (annotation.Target is null) return declaration switch
        {
            ConstructorDeclaration => "constructor", FunctionParameter => "parameter",
            FunctionDeclaration => "function", ClassDeclaration => "class", FieldDeclaration => "field",
            PropertyDeclaration => "property", EnumDeclaration => "enum", DelegateDeclaration => "delegate", _ => "unknown",
        };
        if (annotation.Target == "return" && declaration is FunctionDeclaration and not ConstructorDeclaration) return "return";
        throw AnnotationError(annotation, $"Invalid annotation target '{annotation.Target}'.");
    }

    private static string AnnotationString(string value, AnnotationApplication annotation)
    {
        try { return JsonSerializer.Deserialize<string>(value) ?? throw new JsonException(); }
        catch (JsonException) { throw AnnotationError(annotation, $"Expected constant string, got '{value}'."); }
    }

    private static CompilationErrorException AnnotationError(AnnotationApplication annotation, string message) =>
        new(message, annotation.SourceSpan);

    private static void BindNativeImport(CxProject project, string module,
        FunctionDeclaration function, AnnotationApplication annotation)
    {
        if (!function.IsStatic || function.Body is not null || !function.MemberModifiers.Contains(MemberModifier.Extern) ||
            function.GenericTypeNames.Length != 0 || function.ParentClassDeclaration is { GenericTypeNames.Length: > 0 } || function.IsGenericSpecialization ||
            function.MemberModifiers.Any(modifier => modifier is MemberModifier.Virtual or MemberModifier.Override or MemberModifier.Abstract))
            throw AnnotationError(annotation, "NativeImport requires a non-generic extern top-level function or static method without a body.");
        if (function.ReturnType is not VoidType && !IsNativeScalar(function.ReturnType) ||
            function.Parameters.Any(parameter => !IsNativeScalar(parameter.ParameterType)))
            throw AnnotationError(annotation, "NativeImport signatures cannot contain managed types.");
        var library = AnnotationString(annotation.BoundArguments["library"], annotation);
        var libraries = module == project.Name ? project.NativeLibraries
            : project.ReferencedNativeLibraries.GetValueOrDefault(module);
        if (libraries is null || !libraries.TryGetValue(library, out var dependency))
            throw AnnotationError(annotation, $"Unknown native library ID '{library}' in project '{module}'.");
        if (dependency.Windows is null && dependency.Linux is null)
            throw AnnotationError(annotation, $"Native library '{library}' has no supported targets.");
        if (dependency.Visibility == "private" && function.MemberModifiers.Contains(MemberModifier.Public))
            throw AnnotationError(annotation, "Public native imports require public native dependencies; put private imports behind a CX wrapper.");
        var entryPoint = annotation.BoundArguments["entryPoint"] == "null" ? function.Name
            : AnnotationString(annotation.BoundArguments["entryPoint"], annotation);
        if (!Regex.IsMatch(entryPoint, @"^[A-Za-z_][A-Za-z0-9_]*$"))
            throw AnnotationError(annotation, $"Malformed native entry point '{entryPoint}'.");
        var convention = NativeEnumArgument(annotation, "callingConvention", "NativeCallingConvention");
        if (convention is not ("Cdecl" or "Stdcall" or "Winapi" or "PlatformDefault"))
            throw AnnotationError(annotation, $"Unknown native calling convention '{convention}'.");
        var errorPolicy = NativeEnumArgument(annotation, "captureError", "NativeErrorPolicy");
        if (errorPolicy is not ("None" or "LastError" or "Errno"))
            throw AnnotationError(annotation, $"Unknown native error policy '{errorPolicy}'.");
        if (dependency.Windows is null && (convention is "Stdcall" or "Winapi" || errorPolicy == "LastError"))
            throw AnnotationError(annotation, "Winapi/Stdcall and last-error imports require a Windows-only native library entry.");
        function.NativeImport = new NativeImportMetadata(library, entryPoint, convention, errorPolicy,
            (dependency.Windows?.Headers ?? []).Concat(dependency.Linux?.Headers ?? []).Distinct().ToArray())
            { Blocking = annotation.BoundArguments["blocking"] == "true" };
    }

    private static bool IsNativeScalar(TypeBase type) => UnwrapConst(type) is
        IntTypeBase or FloatTypeBase or BoolType or CharType or PtrType or NativeFunctionType or
        NamedType { NativeType.Opaque: false } or NamedType { IsDelegate: true, DelegateSignature: NativeFunctionType } or
        NamedType { ClassType: ClassType.Enum, EnumUnderlyingType: not null };

    private void BindNativeFunctions(CxProject project)
    {
        foreach (var context in project.CompilationContexts)
            foreach (var declaration in WalkAnnotatedDeclarations(context.DeclarationScope.Declarations).OfType<DelegateDeclaration>())
                foreach (var annotation in declaration.Annotations)
                {
                    var names = GetCandidateNames(annotation.Name, declaration.Namespace, context.Imports);
                    if (!names.Contains(new QualifiedIdentifier("System", "Interop", "NativeFunction"))) continue;
                    ResolveAnnotation(annotation, declaration, context.Imports);
                    if (annotation.ResolvedTypeName != "cxcore.System.Interop.NativeFunction") continue;
                    ResolveTypeReference(declaration.Signature, declaration.Namespace, context.Imports);
                    var convention = NativeEnumArgument(annotation, "callingConvention", "NativeCallingConvention");
                    if (convention is not ("Cdecl" or "Stdcall" or "Winapi" or "PlatformDefault"))
                        throw AnnotationError(annotation, "Unknown native function calling convention.");
                    var signature = declaration.Signature;
                    if (signature.ReturnType is not VoidType && !IsNativeScalar(signature.ReturnType) ||
                        signature.ParameterTypes.Any(parameter => !IsNativeScalar(parameter)))
                        throw AnnotationError(annotation, "Native function signatures require native types only.");
                    var failure = AnnotationString(annotation.BoundArguments["failureResult"], annotation);
                    if (!Regex.IsMatch(failure, @"^-?[0-9]+$"))
                        throw AnnotationError(annotation, "Native callback failureResult must be an ABI-valid integer constant string.");
                    if (failure != "0" && (signature.ReturnType is not IntTypeBase ||
                        !TryGetIntegerConstant(failure, out var failureNumber) ||
                        failureNumber < (GetIntegralRange(signature.ReturnType) ?? (int.MinValue, int.MaxValue)).Item1 ||
                        failureNumber > (GetIntegralRange(signature.ReturnType) ?? (int.MinValue, int.MaxValue)).Item2))
                        throw AnnotationError(annotation, "Native callback failureResult is outside the result type's ABI range; non-integer results require zero.");
                    var contextParameter = int.Parse(annotation.BoundArguments["contextParameter"]);
                    if (contextParameter < -1 || contextParameter >= signature.ParameterTypes.Count ||
                        contextParameter >= 0 && UnwrapConst(signature.ParameterTypes[contextParameter]) is not PtrType { Pointee: null })
                        throw AnnotationError(annotation, "Native callback contextParameter must identify a void* (ptr) parameter or be -1.");
                    declaration.Signature = new NativeFunctionType(signature.ReturnType, signature.ParameterTypes, convention, failure)
                        { ContextParameter = contextParameter, SymbolOnly = annotation.BoundArguments["symbolOnly"] == "true" };
                }
    }

    private void BindNativeTypes(CxProject project)
    {
        foreach (var context in project.CompilationContexts)
            foreach (var declaration in WalkAnnotatedDeclarations(context.DeclarationScope.Declarations).OfType<ClassDeclaration>())
                foreach (var annotation in declaration.Annotations)
                {
                    var candidates = GetCandidateNames(annotation.Name, declaration.Namespace, context.Imports);
                    if (candidates.Any(name => name.ToString() is "System.Interop.NativeType" or "System.Interop.NativeLayout") &&
                        !context.DeclarationScope.Declarations.Contains(declaration))
                        throw AnnotationError(annotation, "Native SDK/layout types must be declared at namespace level in this ABI.");
                    if (candidates.Contains(new QualifiedIdentifier("System", "Interop", "NativeLayout")))
                    {
                        ResolveAnnotation(annotation, declaration, context.Imports);
                        if (annotation.ResolvedTypeName != "cxcore.System.Interop.NativeLayout") continue;
                        if (HasMultipleNativeLayouts(declaration, context.Imports))
                            throw AnnotationError(annotation, "NativeType and NativeLayout are mutually exclusive and cannot be repeated.");
                        var layout = AnnotationString(annotation.BoundArguments["layout"], annotation);
                        var pack = int.Parse(annotation.BoundArguments["pack"]);
                        var layoutAlignment = int.Parse(annotation.BoundArguments["alignment"]);
                        var layoutSize = int.Parse(annotation.BoundArguments["size"]);
                        if (layout is not ("sequential" or "union") || pack is not (0 or 1 or 2 or 4 or 8 or 16) ||
                            layoutAlignment is not (0 or 1 or 2 or 4 or 8 or 16 or 32 or 64) || layoutSize < 0 ||
                            declaration.ClassType != ClassType.Struct || declaration.GenericTypeNames.Length != 0 ||
                            declaration.BaseTypes.Count != 0 || declaration.MemberDeclarations.Declarations.Count == 0 ||
                            declaration.MemberDeclarations.Declarations.Any(member => member is not FieldDeclaration))
                            throw AnnotationError(annotation, "NativeLayout requires a nonempty native struct and supported layout/packing/alignment.");
                        var module = context.ProjectName ?? project.Name;
                        var tag = "__cx_native_" + module.Replace('.', '_') + "_" + declaration.FullName.ToString().Replace('.', '_');
                        declaration.NativeType = new NativeTypeMetadata((layout == "union" ? "union " : "struct ") + tag,
                            "", false, layout == "union" ? "union" : "record", layoutSize, layoutAlignment) { OwnLayout = true, Pack = pack };
                        if (_types.Single(item => item.Declaration == declaration).Type is NamedType layoutType)
                            layoutType.NativeType = declaration.NativeType;
                        continue;
                    }
                    if (!candidates.Contains(new QualifiedIdentifier("System", "Interop", "NativeType"))) continue;
                    ResolveAnnotation(annotation, declaration, context.Imports);
                    if (annotation.ResolvedTypeName != "cxcore.System.Interop.NativeType") continue;
                    if (HasMultipleNativeLayouts(declaration, context.Imports))
                        throw AnnotationError(annotation, "NativeType and NativeLayout are mutually exclusive and cannot be repeated.");
                    var name = AnnotationString(annotation.BoundArguments["name"], annotation);
                    var header = AnnotationString(annotation.BoundArguments["header"], annotation);
                    var kind = AnnotationString(annotation.BoundArguments["kind"], annotation);
                    var opaque = annotation.BoundArguments["opaque"] == "true";
                    if (!Regex.IsMatch(name, @"^(?:(?:struct|union) )?[A-Za-z_][A-Za-z0-9_]*$") ||
                        header.Length > 0 && (!Regex.IsMatch(header, @"^[A-Za-z0-9_./-]+$") || header.Contains("..")))
                        throw AnnotationError(annotation, "Malformed native SDK type or header name.");
                    if (kind is not ("record" or "union" or "scalar" or "handle") || declaration.ClassType != ClassType.Struct ||
                        declaration.GenericTypeNames.Length != 0 || declaration.BaseTypes.Count != 0 ||
                        declaration.MemberDeclarations.Declarations.Any(member => member is not FieldDeclaration) ||
                        (opaque || kind is "scalar" or "handle") && declaration.MemberDeclarations.Declarations.Count != 0)
                        throw AnnotationError(annotation, "NativeType requires a non-generic struct with native field projections only; opaque/scalar types have no members.");
                    if (header.Length == 0 && !opaque)
                        throw AnnotationError(annotation, "Non-opaque SDK types require a header.");
                    var size = int.Parse(annotation.BoundArguments["size"]);
                    var alignment = int.Parse(annotation.BoundArguments["alignment"]);
                    if (size < 0 || alignment < 0)
                        throw AnnotationError(annotation, "Native size/alignment assertions must be non-negative.");
                    declaration.NativeType = new NativeTypeMetadata(name, header, opaque, kind, size, alignment);
                    var symbol = _types.Single(item => item.Declaration == declaration);
                    if (symbol.Type is NamedType named) named.NativeType = declaration.NativeType;
                }
    }

    private static bool TryGetIntegerConstant(string text, out System.Numerics.BigInteger value)
    {
        text = text.Replace("_", "").TrimEnd('u', 'U', 'l', 'L');
        var negative = text.StartsWith('-');
        var digits = negative ? text[1..] : text;
        var radix = digits.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? 16
            : digits.StartsWith("0b", StringComparison.OrdinalIgnoreCase) ? 2
            : digits.StartsWith("0o", StringComparison.OrdinalIgnoreCase) ? 8 : 10;
        if (radix != 10)
        {
            value = 0;
            if (digits.Length <= 2) return false;
            foreach (var character in digits[2..])
            {
                var digit = character is >= '0' and <= '9' ? character - '0'
                    : char.ToLowerInvariant(character) is >= 'a' and <= 'f' ? char.ToLowerInvariant(character) - 'a' + 10 : -1;
                if (digit < 0 || digit >= radix) return false;
                value = value * radix + digit;
            }
            if (negative) value = -value;
            return true;
        }
        return System.Numerics.BigInteger.TryParse(text, out value);
    }

    private static bool HasMultipleNativeLayouts(ClassDeclaration declaration, IReadOnlyList<QualifiedIdentifier> imports) =>
        declaration.Annotations.Count(item => GetCandidateNames(item.Name, declaration.Namespace, imports)
            .Any(name => name.ToString() is "System.Interop.NativeType" or "System.Interop.NativeLayout")) > 1;

    private static bool TryGetNativeIntegerConstant(ExpressionBase expression, out System.Numerics.BigInteger value)
    {
        if (expression is LiteralExpression literal)
            return TryGetIntegerConstant(literal.SourceText, out value) || TryGetCharacterConstant(literal.SourceText, out value);
        if (expression is UnaryExpression { Operator: "+" or "-" } unary && TryGetNativeIntegerConstant(unary.Operand, out value))
        { if (unary.Operator == "-") value = -value; return true; }
        if (expression is CastExpression { TargetType: CharType } cast)
            return TryGetNativeIntegerConstant(cast.Operand, out value);
        if (expression is BinaryExpression binary && TryGetNativeIntegerConstant(binary.Left, out var left) &&
            TryGetNativeIntegerConstant(binary.Right, out var right))
        {
            switch (binary.Operator)
            {
                case "+": value = left + right; return true;
                case "-": value = left - right; return true;
                case "*": value = left * right; return true;
            }
        }
        value = 0; return false;
    }

    private static bool TryGetCharacterConstant(string text, out System.Numerics.BigInteger value)
    {
        value = 0;
        if (text.Length < 3 || text[0] != '\'' || text[^1] != '\'') return false;
        var body = text[1..^1];
        if (body.StartsWith('\\'))
        {
            if (body is "\\0" or "\\n" or "\\r" or "\\t" or "\\\\" or "\\'" or "\\\"")
            { value = body[1] switch { '0' => 0, 'n' => 10, 'r' => 13, 't' => 9, _ => body[1] }; return true; }
            if (body.StartsWith("\\u") && System.Numerics.BigInteger.TryParse("0" + body[2..],
                System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out value)) return value <= 0x10FFFF;
            return false;
        }
        var runes = body.EnumerateRunes().ToArray();
        if (runes.Length != 1) return false;
        value = runes[0].Value; return true;
    }

    private static string NativeEnumArgument(AnnotationApplication annotation, string argument, string enumName)
    {
        var text = annotation.BoundArguments[argument];
        var prefix = text[..Math.Max(0, text.LastIndexOf('.'))];
        if (prefix != enumName && prefix != "System.Interop." + enumName)
            throw AnnotationError(annotation, $"Native argument '{argument}' requires a member of System.Interop.{enumName}.");
        return text.Split('.').Last();
    }

    private void RequireUnsafe(FunctionDeclaration function, SourceSpan? span)
    {
        if (!function.MemberModifiers.Contains(MemberModifier.Unsafe) && _unsafeDepth == 0)
            throw new CompilationErrorException("Raw pointer operations require an unsafe function or context.", span);
    }

    private static bool BorrowsStackStorage(ExpressionBase expression) => expression switch {
        NativeStackAllocationExpression => true,
        UnaryExpression { Operator: "&", Operand: IdentifierExpression { TargetLocal: not null } } => true,
        IdentifierExpression { TargetLocal: { } local } => local.BorrowsStackStorage,
        LambdaExpression lambda => lambda.Captures.Any(capture => capture.Local.BorrowsStackStorage),
        CastExpression cast => BorrowsStackStorage(cast.Operand),
        ConditionalExpression condition => BorrowsStackStorage(condition.WhenTrue) || BorrowsStackStorage(condition.WhenFalse),
        _ => false,
    };
}

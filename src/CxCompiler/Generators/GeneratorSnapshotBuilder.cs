using CxCompiler.Model.Common;
using CxCompiler.Model.Project;
using CxCompiler.Model.Types;

namespace CxCompiler.Generators;

/// <summary>
/// Projects bound declarations into deterministic values that can cross the
/// generator worker boundary without retaining mutable compiler symbols.
/// </summary>
public static class GeneratorSnapshotBuilder
{
    public static GeneratorInputSnapshot Create(
        CxProject project,
        string projectDirectory,
        IReadOnlyDictionary<string, string>? sourceTextByPath = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectDirectory);

        var root = Path.GetFullPath(projectDirectory);
        var syntax = new List<GeneratorSyntaxSnapshot>();
        var annotations = new List<GeneratorAnnotationSnapshot>();
        var types = new List<GeneratorTypeSnapshot>();

        foreach (var context in project.CompilationContexts)
        {
            var contextModuleName = context.ProjectName ?? project.Name;
            foreach (var (declaration, parentIdentity) in WalkDeclarations(
                context.DeclarationScope.Declarations, contextModuleName,
                publicApiOnly: context.IsProjectReference))
            {
                var identity = GetDeclarationIdentity(contextModuleName, declaration, parentIdentity);
                if (declaration.SourceSpan is { } span)
                {
                    var location = ToLocation(span, root);
                    if (!context.IsProjectReference)
                    {
                        syntax.Add(new GeneratorSyntaxSnapshot(
                            declaration.DeclarationType,
                            ReadSourceLines(span, root, sourceTextByPath),
                            identity,
                            location));
                    }

                    foreach (var annotation in declaration.Annotations
                        .OrderBy(item => item.ResolvedTypeName, StringComparer.Ordinal)
                        .ThenBy(item => item.SourceSpan?.StartLine ?? 0)
                        .ThenBy(item => item.SourceSpan?.StartColumn ?? 0))
                    {
                        if (annotation.ResolvedTypeName is not { } metadataName)
                            continue;
                        var annotationLocation = annotation.SourceSpan is { } annotationSpan
                            ? ToLocation(annotationSpan, root)
                            : location;
                        var bound = annotation.BoundArguments
                            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                            .ToArray();
                        annotations.Add(new GeneratorAnnotationSnapshot(
                            metadataName,
                            identity,
                            Freeze(bound.Select(pair => pair.Key).ToArray()),
                            Freeze(bound.Select(pair => pair.Value).ToArray()),
                            annotationLocation));
                    }
                }

                if (declaration is ClassDeclaration type)
                {
                    var moduleName = type.ProjectName ?? context.ProjectName ?? project.Name;
                    var typeIdentity = GetTypeDefinitionIdentity(moduleName, type);
                    var members = type.MemberDeclarations.Declarations
                        .Where(member => !context.IsProjectReference || IsPublicApiDeclaration(member))
                        .Select(member => CreateMemberSnapshot(moduleName, typeIdentity, member, root))
                        .Where(member => member is not null)
                        .Cast<GeneratorMemberSnapshot>()
                        .OrderBy(member => member.Identity, StringComparer.Ordinal)
                        .ToArray();
                    var typeLocation = type.SourceSpan is { } typeSpan
                        ? ToLocation(typeSpan, root)
                        : EmptyLocation;
                    types.Add(new GeneratorTypeSnapshot(
                        typeIdentity,
                        GetMetadataName(type),
                        moduleName,
                        type.Namespace.ToString(),
                        type.Visibility.ToString(),
                        type.ClassType.ToString(),
                        type.IsPartial,
                        type.IsAbstract,
                        type.IsFinal,
                        type.GenericTypeNames.Length != 0,
                        Freeze(type.GenericTypeNames.ToArray()),
                        Freeze(type.BaseTypes.Select(GetTypeIdentity).ToArray()),
                        Freeze(CreateGenericConstraints(type)),
                        Freeze(members),
                        typeLocation));
                }
                else if (declaration is EnumDeclaration enumType)
                {
                    var typeIdentity = $"{contextModuleName}.{enumType.FullName}";
                    var typeLocation = enumType.SourceSpan is { } enumSpan
                        ? ToLocation(enumSpan, root)
                        : EmptyLocation;
                    var enumMembers = enumType.Members.Select(member =>
                    {
                        var memberLocation = member.SourceSpan is { } memberSpan
                            ? ToLocation(memberSpan, root)
                            : EmptyLocation;
                        return new GeneratorMemberSnapshot(
                            $"{typeIdentity}.{member.Name}",
                            member.Name,
                            enumType.UnderlyingType is { } underlying
                                ? GetTypeIdentity(underlying)
                                : string.Empty,
                            enumType.Visibility.ToString(),
                            "EnumValue",
                            false,
                            true,
                            false,
                            Freeze(Array.Empty<GeneratorParameterSnapshot>()),
                            memberLocation,
                            member.NativeConstantValue?.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    }).OrderBy(member => member.Identity, StringComparer.Ordinal).ToArray();
                    types.Add(new GeneratorTypeSnapshot(
                        typeIdentity,
                        enumType.FullName.ToString(),
                        contextModuleName,
                        enumType.Namespace.ToString(),
                        enumType.Visibility.ToString(),
                        "Enum",
                        false,
                        false,
                        false,
                        false,
                        Array.Empty<string>(),
                        Array.Empty<string>(),
                        Array.Empty<GeneratorGenericConstraintSnapshot>(),
                        Freeze(enumMembers),
                        typeLocation));
                }
                else if (declaration is DelegateDeclaration delegateType)
                {
                    var typeIdentity = $"{contextModuleName}.{delegateType.FullName}";
                    var signature = $"{GetTypeIdentity(delegateType.Signature.ReturnType)}(" +
                        string.Join(",", delegateType.Signature.ParameterTypes.Select(GetTypeIdentity)) + ")";
                    var typeLocation = delegateType.SourceSpan is { } delegateSpan
                        ? ToLocation(delegateSpan, root)
                        : EmptyLocation;
                    types.Add(new GeneratorTypeSnapshot(
                        typeIdentity,
                        delegateType.FullName.ToString(),
                        contextModuleName,
                        delegateType.Namespace.ToString(),
                        delegateType.Visibility.ToString(),
                        $"Delegate:{signature}",
                        false,
                        false,
                        false,
                        false,
                        Freeze(Array.Empty<string>()),
                        Freeze(Array.Empty<string>()),
                        Freeze(Array.Empty<GeneratorGenericConstraintSnapshot>()),
                        Freeze(Array.Empty<GeneratorMemberSnapshot>()),
                        typeLocation));
                }
            }
        }

        var genericRequests = project.GenericTypeInstances
            .Where(instance => instance.Type.ConstructedIdentity is not null)
            .Select(instance =>
            {
                var type = instance.Declaration;
                var moduleName = type.ProjectName ?? project.Name;
                var definitionIdentity = GetTypeDefinitionIdentity(moduleName, type);
                var typeArguments = instance.Type.TypeArguments
                    .Select(GetTypeIdentity).ToArray();
                var location = type.SourceSpan is { } span ? ToLocation(span, root) : EmptyLocation;
                return new GeneratorClosedGenericSnapshot(
                    definitionIdentity,
                    instance.Type.ConstructedIdentity!.CanonicalName,
                    moduleName,
                    Freeze(typeArguments),
                    location);
            })
            .OrderBy(request => request.ClosedIdentity, StringComparer.Ordinal)
            .ToArray();

        return new GeneratorInputSnapshot(
            project.Name,
            Freeze(syntax.OrderBy(item => item.Identity, StringComparer.Ordinal)
                .ThenBy(item => item.Location.Path, StringComparer.Ordinal)
                .ThenBy(item => item.Location.StartLine)
                .ThenBy(item => item.Location.StartColumn)
                .ToArray()),
            Freeze(annotations.OrderBy(item => item.MetadataName, StringComparer.Ordinal)
                .ThenBy(item => item.TargetIdentity, StringComparer.Ordinal)
                .ThenBy(item => item.Location.Path, StringComparer.Ordinal)
                .ThenBy(item => item.Location.StartLine)
                .ThenBy(item => item.Location.StartColumn)
                .ToArray()),
            Freeze(types.OrderBy(item => item.Identity, StringComparer.Ordinal).ToArray()),
            Freeze(genericRequests));
    }

    private static GeneratorMemberSnapshot? CreateMemberSnapshot(
        string moduleName,
        string containingTypeIdentity,
        DeclarationBase declaration,
        string projectRoot)
    {
        // Compiler-created backing fields and accessors have no source span and
        // are implementation details, not source generator inputs.
        if (declaration.SourceSpan is null) return null;

        var (kind, type, isStatic, isReadOnly, parameters, visibility) = declaration switch
        {
            FieldDeclaration field => (
                "Field", field.Type, field.IsStatic, field.Type is ConstType,
                Freeze(Array.Empty<GeneratorParameterSnapshot>()),
                GetVisibility(field.MemberModifiers)),
            PropertyDeclaration property => (
                "Property", property.Type, property.IsStatic,
                !property.PropertyAccessorDeclarations.Any(accessor => accessor.Name == "set"),
                Freeze(Array.Empty<GeneratorParameterSnapshot>()),
                GetVisibility(property.MemberModifiers)),
            ConstructorDeclaration constructor => (
                "Constructor", constructor.ReturnType, false, false,
                CreateParameters(constructor.Parameters, projectRoot),
                GetVisibility(constructor.MemberModifiers)),
            FunctionDeclaration function => (
                "Method", function.ReturnType, function.IsStatic, false,
                CreateParameters(function.Parameters, projectRoot),
                GetVisibility(function.MemberModifiers)),
            _ => default,
        };
        if (kind is null) return null;

        var sourceLocation = declaration.SourceSpan is { } span
            ? ToLocation(span, projectRoot)
            : EmptyLocation;
        var memberName = declaration is ConstructorDeclaration ? ".ctor" : declaration.Name;
        return new GeneratorMemberSnapshot(
            GetDeclarationIdentity(moduleName, declaration, containingTypeIdentity),
            memberName,
            GetTypeIdentity(type!),
            visibility!,
            kind,
            isStatic,
            isReadOnly,
            IsNullable(type!),
            parameters!,
            sourceLocation);
    }

    private static IReadOnlyList<GeneratorParameterSnapshot> CreateParameters(
        IReadOnlyList<FunctionParameter> parameters,
        string projectRoot) => Freeze(parameters.Select(parameter => new GeneratorParameterSnapshot(
            parameter.Name,
            GetTypeIdentity(parameter.ParameterType),
            IsNullable(parameter.ParameterType),
            parameter.DefaultValueText,
            parameter.SourceSpan is { } span ? ToLocation(span, projectRoot) : EmptyLocation))
        .ToArray());

    private static GeneratorGenericConstraintSnapshot[] CreateGenericConstraints(
        ClassDeclaration type) => type.GenericConstraints.Select(constraint =>
            new GeneratorGenericConstraintSnapshot(
                constraint.ParameterName,
                constraint.Kind.ToString(),
                Freeze(constraint.Types.Select(GetTypeIdentity).ToArray()),
                Freeze(Array.Empty<string>())))
            .Concat(type.ConceptConstraints.Select(constraint =>
                new GeneratorGenericConstraintSnapshot(
                    constraint.ParameterName,
                    "Concept",
                    Freeze(Array.Empty<string>()),
                    Freeze(constraint.Concepts.Select(concept => concept.ToString()).ToArray()))))
            .OrderBy(constraint => constraint.ParameterName, StringComparer.Ordinal)
            .ThenBy(constraint => constraint.Kind, StringComparer.Ordinal)
            .ToArray();

    private static string GetVisibility(IEnumerable<MemberModifier> modifiers) =>
        modifiers.FirstOrDefault(modifier => modifier is MemberModifier.Public or
            MemberModifier.Protected or MemberModifier.Internal or MemberModifier.Private)
            .ToString();

    private static bool IsNullable(TypeBase type) => type is NullableType;

    private static string GetTypeIdentity(TypeBase type) => type switch
    {
        ConstType constant => $"const {GetTypeIdentity(constant.UnderlyingType)}",
        NullableType nullable => $"{GetTypeIdentity(nullable.UnderlyingType)}?",
        ArrayType array => $"{GetTypeIdentity(array.ElementType)}[]",
        FixedArrayType array => $"{GetTypeIdentity(array.ElementType)}[{array.Length}]",
        GenericType generic => $"!{generic.Name}",
        NamedType named => GetNamedTypeIdentity(named),
        _ => type.FullName.ToString(),
    };

    private static string GetNamedTypeIdentity(NamedType named)
    {
        var typeName = named.ResolvedTypeFullName.ToString();
        if (typeName == "void") typeName = named.Name;
        return named.TypeArguments.Count == 0
            ? typeName
            : $"{typeName}<{string.Join(",", named.TypeArguments.Select(GetTypeIdentity))}>";
    }

    private static string GetTypeDefinitionIdentity(string moduleName, ClassDeclaration type) =>
        type.GenericTypeNames.Length == 0
            ? $"{moduleName}.{type.FullName}"
            : $"{moduleName}.{type.FullName}`{type.GenericTypeNames.Length}";

    private static string GetMetadataName(ClassDeclaration type) =>
        type.GenericTypeNames.Length == 0
            ? type.FullName.ToString()
            : $"{type.FullName}`{type.GenericTypeNames.Length}";

    private static string GetDeclarationIdentity(
        string moduleName,
        DeclarationBase declaration,
        string? parentIdentity = null)
    {
        if (declaration is ClassDeclaration type)
            return GetTypeDefinitionIdentity(moduleName, type);
        if (declaration is EnumDeclaration or DelegateDeclaration)
            return $"{moduleName}.{declaration.FullName}";
        if (declaration is FunctionParameter parameter)
            return $"{parentIdentity ?? moduleName}.parameter:{parameter.Name}";
        if (declaration is FunctionDeclaration function)
        {
            var name = declaration is ConstructorDeclaration ? ".ctor" : declaration.Name;
            var prefix = parentIdentity is null
                ? $"{moduleName}.{declaration.FullName}"
                : $"{parentIdentity}.{name}";
            return $"{prefix}({string.Join(",", function.Parameters.Select(parameter => GetTypeIdentity(parameter.ParameterType)))})";
        }
        if (parentIdentity is not null)
            return $"{parentIdentity}.{declaration.Name}";
        return $"{moduleName}.{declaration.FullName}:{declaration.DeclarationType}";
    }

    private static IEnumerable<(DeclarationBase Declaration, string? ParentIdentity)> WalkDeclarations(
        IEnumerable<DeclarationBase> declarations,
        string moduleName,
        string? parentIdentity = null,
        bool publicApiOnly = false)
    {
        foreach (var declaration in declarations)
        {
            if (publicApiOnly && !IsPublicApiDeclaration(declaration)) continue;
            yield return (declaration, parentIdentity);
            var identity = GetDeclarationIdentity(moduleName, declaration, parentIdentity);
            if (declaration is ClassDeclaration owner)
                foreach (var member in WalkDeclarations(
                    owner.MemberDeclarations.Declarations, moduleName, identity, publicApiOnly))
                    yield return member;
            if (declaration is FunctionDeclaration function)
                foreach (var parameter in function.Parameters)
                    yield return (parameter, identity);
        }
    }

    private static GeneratorLocationSnapshot ToLocation(SourceSpan span, string root)
    {
        string path;
        try
        {
            path = Path.GetRelativePath(root, Path.GetFullPath(span.FilePath))
                .Replace(Path.DirectorySeparatorChar, '/');
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or
            NotSupportedException)
        {
            path = span.FilePath.Replace('\\', '/');
        }
        if (path == ".." || path.StartsWith("../", StringComparison.Ordinal) ||
            Path.IsPathRooted(path))
            path = Path.GetFileName(span.FilePath);
        return new GeneratorLocationSnapshot(
            path, span.StartLine, span.StartColumn, span.EndLine, span.EndColumn);
    }

    private static string ReadSourceLines(
        SourceSpan span,
        string projectRoot,
        IReadOnlyDictionary<string, string>? sourceTextByPath)
    {
        string? source = null;
        if (sourceTextByPath is not null)
        {
            var fullPath = Path.GetFullPath(span.FilePath);
            var relativePath = Path.GetRelativePath(projectRoot, fullPath)
                .Replace(Path.DirectorySeparatorChar, '/');
            if (!sourceTextByPath.TryGetValue(span.FilePath, out source) &&
                !sourceTextByPath.TryGetValue(fullPath, out source))
                sourceTextByPath.TryGetValue(relativePath, out source);
        }
        if (source is null)
        {
            if (!File.Exists(span.FilePath)) return string.Empty;
            try
            {
                source = File.ReadAllText(span.FilePath);
            }
            catch (IOException)
            {
                return string.Empty;
            }
            catch (UnauthorizedAccessException)
            {
                return string.Empty;
            }
        }

        try
        {
            var lines = source.Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n').Split('\n');
            var start = Math.Clamp(span.StartLine - 1, 0, lines.Length);
            var end = Math.Clamp(span.EndLine, start, lines.Length);
            return string.Join("\n", lines[start..end]);
        }
        catch (ArgumentException) { return string.Empty; }
    }

    private static readonly GeneratorLocationSnapshot EmptyLocation = new(string.Empty, 0, 0, 0, 0);

    private static IReadOnlyList<T> Freeze<T>(T[] values) => Array.AsReadOnly(values);

    private static bool IsPublicApiDeclaration(DeclarationBase declaration) => declaration switch
    {
        ClassDeclaration type => type.Visibility == Visibility.Public,
        EnumDeclaration type => type.Visibility == Visibility.Public,
        DelegateDeclaration type => type.Visibility == Visibility.Public,
        FieldDeclaration field => GetVisibility(field.MemberModifiers) == "Public",
        PropertyDeclaration property => GetVisibility(property.MemberModifiers) == "Public",
        ConstructorDeclaration constructor => GetVisibility(constructor.MemberModifiers) == "Public",
        FunctionDeclaration function => GetVisibility(function.MemberModifiers) == "Public",
        FunctionParameter => true,
        _ => false,
    };
}

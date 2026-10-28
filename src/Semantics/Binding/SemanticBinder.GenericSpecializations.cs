using CxCompiler.Model;
using CxCompiler.Model.Common;
using CxCompiler.Model.Errors;
using CxCompiler.Model.Types;
using CxCompiler.Model.Types.BuiltInTypes;

namespace CxCompiler.Semantics;

public sealed partial class SemanticBinder
{

    private void ClassifyFunctionGenericTypeLists(CompilationContext context)
    {
        var moduleName = context.ProjectName ?? _project!.Name;
        var functions = EnumerateFunctions(context.DeclarationScope.Declarations)
            .Concat(context.DeclarationScope.Declarations
                .OfType<ExtensionDeclaration>()
                .SelectMany(extension => EnumerateFunctions(
                    extension.Members.MemberDeclarations.Declarations)))
            .Concat(context.DeclarationScope.Declarations
                .OfType<ConceptDeclaration>()
                .SelectMany(concept => concept.DeclaredMethodRequirements
                    .Concat(concept.DeclaredOperatorRequirements)));

        foreach (var function in functions.Where(function =>
            function.HasUnclassifiedGenericTypeArguments))
        {
            ClassifyFunctionGenericTypeList(function, context.Namespace, context.Imports);
        }
    }

    private void ClassifyFunctionGenericTypeList(
        FunctionDeclaration function,
        QualifiedIdentifier currentNamespace,
        IReadOnlyList<QualifiedIdentifier> imports)
    {
        var arguments = function.UnclassifiedGenericTypeArguments;
        var hasNonIdentifierTypeForm = arguments.Any(argument =>
            argument.SimpleIdentifier is null);
        var hasVisibleTypeName = arguments.Any(argument =>
            argument.SimpleIdentifier is { } name &&
            IsVisibleFunctionTypeName(name, currentNamespace, imports, argument.Type.SourceSpan));

        if (!hasNonIdentifierTypeForm && !hasVisibleTypeName)
        {
            function.ClassifyGenericTypeArgumentsAsParameters(
                arguments.Select(argument => argument.SimpleIdentifier!));
            return;
        }

        var specializationArguments = arguments.Select(argument =>
            GenericTypeSubstitution.BindParameters(
                argument.Type,
                function.ParentClassDeclaration?.GenericTypeNames ?? []))
            .ToArray();
        foreach (var type in specializationArguments)
        {
            ResolveTypeReference(type, currentNamespace, imports);
            if (FindUnresolvedTypeName(type) is { } unresolvedName)
            {
                throw new CompilationErrorException(
                    $"Cannot resolve specialization type argument '{unresolvedName}' " +
                    $"for function '{function.FullName}'.",
                    type.SourceSpan ?? function.SourceSpan);
            }
            if (!IsClosedSpecializationType(type) || ContainsOpenTypeParameter(type))
            {
                throw new CompilationErrorException(
                    $"Specialization arguments for '{function.FullName}' must be closed types.",
                    type.SourceSpan ?? function.SourceSpan);
            }
        }
        function.ClassifyGenericTypeArgumentsAsSpecialization(specializationArguments);
    }

    private static bool IsClosedSpecializationType(TypeBase type, bool allowVoid = false) =>
        type switch
        {
            AutoType or NullType => false,
            VoidType => allowVoid,
            GenericType => false,
            ConstType constant => IsClosedSpecializationType(
                constant.UnderlyingType, allowVoid),
            ArrayType array => IsClosedSpecializationType(array.ElementType),
            NullableType nullable => IsClosedSpecializationType(
                nullable.UnderlyingType),
            FunctionType function => IsClosedSpecializationType(
                    function.ReturnType, allowVoid: true) &&
                function.ParameterTypes.All(parameter =>
                    IsClosedSpecializationType(parameter)),
            NamedType named => named.ResolvedTypeFullName.ToString() != "void" &&
                named.TypeArguments.All(argument =>
                    IsClosedSpecializationType(argument)),
            _ => true,
        };

    private bool IsVisibleFunctionTypeName(
        string name,
        QualifiedIdentifier currentNamespace,
        IReadOnlyList<QualifiedIdentifier> imports,
        SourceSpan? sourceSpan)
    {
        var knownSystemType = new NamedType(name, []);
        if (knownSystemType.ResolvedTypeFullName.ToString() != "void")
        {
            return true;
        }

        var sourceName = new QualifiedIdentifier(name.Split('.',
            StringSplitOptions.RemoveEmptyEntries));
        var candidateNames = GetCandidateNames(
            sourceName, currentNamespace, imports);
        var candidateCount = _types.Count(candidate =>
                candidateNames.Contains(candidate.Declaration.FullName)) +
            _enums.Count(candidate =>
                candidateNames.Contains(candidate.Declaration.FullName)) +
            _delegates.Count(candidate =>
                candidateNames.Contains(candidate.Declaration.FullName)) +
            _aliases.Count(candidate =>
                candidateNames.Contains(candidate.Declaration.FullName));
        if (candidateCount > 1)
        {
            throw new CompilationErrorException(
                $"Type name '{name}' is ambiguous while classifying a generic function declaration.",
                sourceSpan);
        }
        return candidateCount == 1;
    }

    private static string? FindUnresolvedTypeName(TypeBase type) => type switch
    {
        ConstType constant => FindUnresolvedTypeName(constant.UnderlyingType),
        ArrayType array => FindUnresolvedTypeName(array.ElementType),
        NullableType nullable => FindUnresolvedTypeName(nullable.UnderlyingType),
        FunctionType function => FindUnresolvedTypeName(function.ReturnType) ??
            function.ParameterTypes.Select(FindUnresolvedTypeName)
                .FirstOrDefault(name => name is not null),
        NamedType named when named.ResolvedTypeFullName.ToString() == "void" => named.Name,
        NamedType named => named.TypeArguments.Select(FindUnresolvedTypeName)
            .FirstOrDefault(name => name is not null),
        _ => null,
    };

    private void RegisterGenericFunctionSpecializations(
        IReadOnlyList<CompilationContext> contexts)
    {
        foreach (var context in contexts)
        {
            var moduleName = context.ProjectName ?? _project!.Name;
            foreach (var specialization in EnumerateFunctions(
                context.DeclarationScope.Declarations)
                .Concat(context.DeclarationScope.Declarations
                    .OfType<ExtensionDeclaration>()
                    .Where(extension => !IsExternalReference(context) ||
                        extension.Visibility == Visibility.Public)
                    .SelectMany(extension => EnumerateFunctions(
                        extension.Members.MemberDeclarations.Declarations)))
                .Where(function => function.IsGenericSpecialization))
            {
                if (IsExternalReference(context) && !IsPublicApiFunction(specialization))
                {
                    continue;
                }
                var genericContainingType = specialization.ParentClassDeclaration is
                    { GenericTypeNames.Length: > 0 };
                if (specialization.ParentClassDeclaration is { } containingType &&
                    containingType.ClassType is not (ClassType.Class or ClassType.Struct) &&
                    !specialization.IsExtension)
                {
                    throw new CompilationErrorException(
                        "Generic specializations are supported only for top-level functions and methods on classes or structs.",
                        specialization.SourceSpan);
                }
                if (specialization.Body is null ||
                    specialization.MemberModifiers.Contains(MemberModifier.Extern))
                {
                    throw new CompilationErrorException(
                        "A generic function specialization must provide a CX body.",
                        specialization.SourceSpan);
                }
                if (specialization.GenericTypeNames.Length != 0 ||
                    specialization.GenericConstraints.Count != 0 ||
                    specialization.ConceptConstraints.Count != 0)
                {
                    throw new CompilationErrorException(
                        "A generic function specialization cannot declare generic parameters or constraints.",
                        specialization.SourceSpan);
                }
                if (specialization.SpecializationTypeArguments.Any(ContainsOpenTypeParameter))
                {
                    throw new CompilationErrorException(
                        "Generic function specialization type arguments must be closed.",
                        specialization.SourceSpan);
                }
                if (specialization.Parameters.Any(parameter =>
                    parameter.DefaultValueText is not null))
                {
                    throw new CompilationErrorException(
                        "A generic function specialization inherits default arguments from its primary declaration.",
                        specialization.SourceSpan);
                }

                var potentialPrimaries = _symbols
                    .Where(symbol => symbol.Declaration is
                        { GenericTypeNames.Length: > 0 } primaryDeclaration &&
                        (symbol.ModuleName == moduleName ||
                         !IsExternalReference(context) &&
                         specialization.ParentClassDeclaration is null &&
                         primaryDeclaration.ParentClassDeclaration is null &&
                         IsReferencedModule(symbol.ModuleName) &&
                         IsPublicApiFunction(primaryDeclaration)) &&
                        SameGenericSpecializationOwner(
                            primaryDeclaration.ParentClassDeclaration,
                            specialization.ParentClassDeclaration) &&
                        primaryDeclaration.Name == specialization.Name &&
                        primaryDeclaration.Namespace == specialization.Namespace &&
                        primaryDeclaration.GenericTypeNames.Length ==
                            specialization.SpecializationTypeArguments.Count)
                    .ToArray();
                var exactPrimaries = new List<(FunctionSymbol Symbol,
                    TypeBase[] Parameters, TypeBase ReturnType)>();
                foreach (var symbol in potentialPrimaries)
                {
                    var primary = symbol.Declaration!;
                    var mapping = primary.GenericTypeNames.Zip(
                            specialization.SpecializationTypeArguments)
                        .ToDictionary(pair => pair.First, pair => pair.Second,
                            StringComparer.Ordinal);
                    var parameterTypes = primary.Parameters.Select(parameter =>
                        GenericTypeSubstitution.Substitute(
                            parameter.ParameterType, mapping)).ToArray();
                    var returnType = GenericTypeSubstitution.Substitute(
                        primary.ReturnType, mapping);
                    if (parameterTypes.Length == specialization.Parameters.Count &&
                        parameterTypes.Zip(specialization.Parameters).All(pair =>
                            IsType(pair.First, pair.Second.ParameterType)) &&
                        IsType(returnType, specialization.ReturnType))
                    {
                        exactPrimaries.Add((symbol, parameterTypes, returnType));
                    }
                }

                if (exactPrimaries.Count == 0)
                {
                    throw new CompilationErrorException(
                        $"No generic function matches specialization '{specialization.Name}'.",
                        specialization.SourceSpan);
                }
                if (exactPrimaries.Count > 1)
                {
                    throw new CompilationErrorException(
                        $"Generic function specialization '{specialization.Name}' is ambiguous.",
                        specialization.SourceSpan);
                }

                var (primarySymbol, closedParameters, closedReturnType) = exactPrimaries[0];
                var primaryDeclaration = primarySymbol.Declaration!;
                if (primaryDeclaration.MemberModifiers.Contains(MemberModifier.Abstract))
                {
                    throw new CompilationErrorException(
                        "A generic specialization cannot provide an implementation for an " +
                        "abstract generic method.",
                        specialization.SourceSpan);
                }
                if (!primaryDeclaration.MemberModifiers.SequenceEqual(specialization.MemberModifiers) ||
                    primaryDeclaration.Const != specialization.Const ||
                    primaryDeclaration.Parameters.Count != specialization.Parameters.Count)
                {
                    throw new CompilationErrorException(
                        $"Generic function specialization '{specialization.Name}' must preserve the primary declaration's modifiers and signature.",
                        specialization.SourceSpan);
                }

                // Close the primary before registering the replacement so its constraints
                // remain authoritative and cannot be bypassed by a custom implementation.
                FunctionSymbol? primaryInstance = null;
                if (!genericContainingType)
                {
                    primaryInstance = CloseGenericFunction(
                        primarySymbol,
                        closedParameters,
                        specialization.SpecializationTypeArguments);
                    if (primaryInstance is null)
                    {
                        throw new CompilationErrorException(
                            _genericConstraintFailure ??
                                $"The type arguments do not satisfy '{primaryDeclaration.FullName}'.",
                            specialization.SourceSpan);
                    }
                }

                if (genericContainingType)
                {
                    var templateKey = GetGenericMethodSpecializationTypeArgumentsKey(
                        specialization.SpecializationTypeArguments);
                    if (!_genericMethodSpecializationTemplates.TryAdd(
                        (primaryDeclaration, templateKey), specialization))
                    {
                        throw new CompilationErrorException(
                            $"Duplicate generic function specialization for '{primaryDeclaration.FullName}'.",
                            specialization.SourceSpan);
                    }
                    continue;
                }

                var specializationIdentity = GenericTypeIdentity.CreateFunction(
                    moduleName,
                    primaryDeclaration.FullName.ToString() +
                        (primaryDeclaration.IsStatic ? string.Empty : "#instance"),
                    specialization.SpecializationTypeArguments,
                    closedParameters,
                    closedReturnType);
                if (!_genericFunctionSpecializations.TryAdd(
                    specializationIdentity.CanonicalName, specialization))
                {
                    throw new CompilationErrorException(
                        $"Duplicate generic function specialization for '{primaryDeclaration.FullName}'.",
                        specialization.SourceSpan);
                }
                if (!IsExternalReference(context) && IsPublicApiFunction(specialization))
                {
                    _project!.AddGenericFunctionInstance(new FunctionSymbol(
                        moduleName,
                        primaryInstance!.FullName,
                        primaryInstance.ParameterTypes,
                        primaryInstance.ReturnType,
                        primaryInstance.OverloadIndex,
                        primaryInstance.Declaration,
                        specializationIdentity.CIdentifier,
                        primaryInstance.ClosedContainingType,
                        primaryInstance.ConceptOperatorBindings,
                        primaryInstance.GenericTypeArguments,
                        specialization));
                }
            }
        }
    }

    private bool SameGenericSpecializationOwner(
        ClassDeclaration? primary,
        ClassDeclaration? specialization)
    {
        if (primary is null || specialization is null)
        {
            return primary is null && specialization is null;
        }

        return ReferenceEquals(
            GetCanonicalPartialTypeDeclaration(primary),
            GetCanonicalPartialTypeDeclaration(specialization));
    }

    private ClassDeclaration GetCanonicalPartialTypeDeclaration(ClassDeclaration declaration)
    {
        var canonical = declaration;
        while (_canonicalPartialTypeDeclarations.TryGetValue(canonical, out var next) &&
            !ReferenceEquals(canonical, next))
        {
            canonical = next;
        }
        return canonical;
    }

    private bool IsReferencedModule(string moduleName) =>
        _project?.CompilationContexts.Any(context => IsExternalReference(context) &&
            context.ProjectName == moduleName) == true;

    private static bool IsExternalReference(CompilationContext context) =>
        context.IsProjectReference && !context.IsSyntheticCore;

    private static bool IsPublicApiFunction(FunctionDeclaration function) =>
        function.ParentClassDeclaration is { } parent
            ? parent.Visibility == Visibility.Public &&
                (parent.ClassType == ClassType.Interface ||
                 function.MemberModifiers.Contains(MemberModifier.Public))
            : function.MemberModifiers.Contains(MemberModifier.Public);

    private FunctionSymbol SelectGenericFunctionSpecialization(FunctionSymbol symbol)
    {
        if (symbol.ImplementationDeclaration is not null ||
            symbol.Declaration is not { GenericTypeNames.Length: > 0 } declaration ||
            symbol.SpecializationName is null)
        {
            return symbol;
        }

        var typeArguments = declaration.GenericTypeNames
            .Select(name => symbol.GenericTypeArguments[name]).ToArray();
        var primaryModule = _symbols.FirstOrDefault(candidate =>
            ReferenceEquals(candidate.Declaration, declaration))?.ModuleName ??
            symbol.ModuleName;
        if (symbol.ClosedContainingType?.ConstructedIdentity is not null &&
            declaration.ParentClassDeclaration is
                { GenericTypeNames.Length: > 0 } &&
            _genericMethodSpecializationTemplates.TryGetValue(
                (declaration, GetGenericMethodSpecializationTypeArgumentsKey(typeArguments)),
                out var templateImplementation))
        {
            return new FunctionSymbol(
                symbol.ModuleName,
                symbol.FullName,
                symbol.ParameterTypes,
                symbol.ReturnType,
                symbol.OverloadIndex,
                symbol.Declaration,
                symbol.SpecializationName,
                symbol.ClosedContainingType,
                symbol.ConceptOperatorBindings,
                symbol.GenericTypeArguments,
                templateImplementation);
        }

        var projectIdentity = GenericTypeIdentity.CreateFunction(
            _project!.Name,
            symbol.FullName.ToString() +
                (declaration.IsStatic ? string.Empty : "#instance") +
                (symbol.ClosedContainingType?.ConstructedIdentity is { } projectOwnerIdentity
                    ? $"@{projectOwnerIdentity.CanonicalName}"
                    : string.Empty),
            typeArguments,
            symbol.ParameterTypes,
            symbol.ReturnType);
        var primaryIdentity = GenericTypeIdentity.CreateFunction(
            primaryModule,
            symbol.FullName.ToString() +
                (declaration.IsStatic ? string.Empty : "#instance") +
                (symbol.ClosedContainingType?.ConstructedIdentity is { } ownerIdentity
                    ? $"@{ownerIdentity.CanonicalName}"
                    : string.Empty),
            typeArguments,
            symbol.ParameterTypes,
            symbol.ReturnType);
        if (projectIdentity.CanonicalName != primaryIdentity.CanonicalName &&
            _genericFunctionSpecializations.TryGetValue(
                projectIdentity.CanonicalName, out var projectImplementation))
        {
            return new FunctionSymbol(
                _project.Name,
                symbol.FullName,
                symbol.ParameterTypes,
                symbol.ReturnType,
                symbol.OverloadIndex,
                symbol.Declaration,
                projectIdentity.CIdentifier,
                symbol.ClosedContainingType,
                symbol.ConceptOperatorBindings,
                symbol.GenericTypeArguments,
                projectImplementation);
        }

        var identity = GenericTypeIdentity.CreateFunction(
            symbol.ModuleName,
            symbol.FullName.ToString() +
                (declaration.IsStatic ? string.Empty : "#instance") +
                (symbol.ClosedContainingType?.ConstructedIdentity is { } containingIdentity
                    ? $"@{containingIdentity.CanonicalName}"
                    : string.Empty),
            typeArguments,
            symbol.ParameterTypes,
            symbol.ReturnType).CanonicalName;
        if (!_genericFunctionSpecializations.TryGetValue(identity, out var implementation))
        {
            return symbol;
        }

        return new FunctionSymbol(
            symbol.ModuleName,
            symbol.FullName,
            symbol.ParameterTypes,
            symbol.ReturnType,
            symbol.OverloadIndex,
            symbol.Declaration,
            symbol.SpecializationName,
            symbol.ClosedContainingType,
            symbol.ConceptOperatorBindings,
            symbol.GenericTypeArguments,
            implementation);
    }

    private static string GetGenericMethodSpecializationTypeArgumentsKey(
        IReadOnlyList<TypeBase> typeArguments) =>
        GenericTypeIdentity.CreateFunction(
            "cx-specialization-template",
            "generic-method",
            typeArguments,
            [],
            BuiltInSystemTypes.Void).CanonicalName;
}

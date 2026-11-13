using CxCompiler.Model;
using CxCompiler.Model.Common;
using CxCompiler.Model.Errors;
using CxCompiler.Model.Types;
using CxCompiler.Model.Types.BuiltInTypes;

namespace CxCompiler.Semantics;

public sealed partial class SemanticBinder
{

    private void IndexConcepts(IReadOnlyList<CompilationContext> contexts)
    {
        foreach (var context in contexts)
        {
            var moduleName = context.ProjectName ?? _project!.Name;
            foreach (var declaration in context.DeclarationScope.Declarations
                .OfType<ConceptDeclaration>())
            {
                if (IsExternalReference(context) && declaration.Visibility != Visibility.Public)
                {
                    continue;
                }
                if (_concepts.Any(existing =>
                    existing.Declaration.FullName == declaration.FullName))
                {
                    throw new CompilationErrorException(
                        $"Concept '{declaration.FullName}' is declared more than once.",
                        declaration.SourceSpan);
                }
                foreach (var requirement in declaration.OperatorRequirements)
                {
                    var isComparison = requirement.OperatorToken is
                        "==" or "!=" or "<" or "<=" or ">" or ">=";
                    var isUnaryNegation = requirement.OperatorToken == "-" &&
                        requirement.Parameters.Count == 0;
                    var isArithmetic = requirement.OperatorToken is
                        "+" or "-" or "*" or "/" or "%" && !isUnaryNegation;
                    var isUnary = requirement.OperatorToken is "!" or "~" ||
                        isUnaryNegation;
                    var expectedParameterCount = isUnary ? 0 : 1;
                    var returnsSelf = requirement.ReturnType is NamedType selfType &&
                        selfType.Name == declaration.Name;
                    if ((!isComparison && !isArithmetic && !isUnary) ||
                        requirement.Parameters.Count != expectedParameterCount ||
                        (isComparison || requirement.OperatorToken == "!") &&
                            requirement.ReturnType is not BoolType ||
                        (isArithmetic || requirement.OperatorToken is "~" ||
                            isUnaryNegation) &&
                            !returnsSelf ||
                        !requirement.Const)
                    {
                        throw new CompilationErrorException(
                            $"Concept '{declaration.Name}' currently supports only const " +
                            "comparison, arithmetic, or unary !/~/- operator requirements " +
                            "with supported signatures.",
                            requirement.SourceSpan ?? declaration.SourceSpan);
                    }
                }
                foreach (var requirement in declaration.MethodRequirements)
                {
                    var hasDefaultParameter = false;
                    foreach (var parameter in requirement.Parameters)
                    {
                        if (parameter.DefaultValueText is not null)
                        {
                            hasDefaultParameter = true;
                        }
                        else if (hasDefaultParameter)
                        {
                            throw new CompilationErrorException(
                                $"Required parameter '{parameter.Name}' cannot follow a " +
                                "parameter with a default value.",
                                parameter.SourceSpan ?? requirement.SourceSpan ??
                                    declaration.SourceSpan);
                        }
                    }
                    requirement.ReturnType = GenericTypeSubstitution.BindParameters(
                        requirement.ReturnType, requirement.GenericTypeNames);
                    foreach (var parameter in requirement.Parameters)
                    {
                        parameter.ParameterType = GenericTypeSubstitution.BindParameters(
                            parameter.ParameterType, requirement.GenericTypeNames);
                    }
                    requirement.SetGenericConstraints(requirement.GenericConstraints
                        .Select(constraint => constraint with
                        {
                            Types = constraint.Types.Select(type =>
                                GenericTypeSubstitution.BindParameters(
                                    type, requirement.GenericTypeNames)).ToArray(),
                        }).ToArray());
                    if (ContainsUndeclaredConceptMethodParameter(
                            requirement.ReturnType, requirement.GenericTypeNames) ||
                        requirement.GenericConstraints.Any(constraint =>
                            constraint.Types.Any(type =>
                                ContainsUndeclaredConceptMethodParameter(
                                    type, requirement.GenericTypeNames))) ||
                        requirement.Parameters.Any(parameter =>
                            ContainsUndeclaredConceptMethodParameter(
                                parameter.ParameterType, requirement.GenericTypeNames)) ||
                        requirement.MemberModifiers.Contains(MemberModifier.Static) ||
                        requirement.MemberModifiers.Contains(MemberModifier.Extern))
                    {
                        throw new CompilationErrorException(
                            $"Concept '{declaration.Name}' currently supports only " +
                            "instance method requirements with method type parameters " +
                            "used in their signatures and standard type constraints.",
                            requirement.SourceSpan ?? declaration.SourceSpan);
                    }
                    foreach (var constraint in requirement.GenericConstraints)
                    {
                        if (!requirement.GenericTypeNames.Contains(
                                constraint.ParameterName, StringComparer.Ordinal))
                        {
                            throw new CompilationErrorException(
                                $"Constraint refers to unknown generic parameter " +
                                $"'{constraint.ParameterName}'.",
                                requirement.SourceSpan ?? declaration.SourceSpan);
                        }
                    }
                }
                if (declaration.MethodRequirements.GroupBy(
                        requirement => ConceptDeclaration.GetMethodBindingKey(requirement))
                    .Any(group => group.Count() > 1))
                {
                    throw new CompilationErrorException(
                        $"Concept '{declaration.Name}' contains a duplicate method " +
                        "requirement signature.",
                        declaration.SourceSpan);
                }
                foreach (var requirement in declaration.PropertyRequirements)
                {
                    if (requirement.HasGetter && !requirement.GetterConst ||
                        !requirement.HasGetter && !requirement.HasSetter ||
                        !requirement.HasGetter && requirement.GetterConst ||
                        requirement.Type is VoidType ||
                        ContainsUnboundGenericParameter(requirement.Type))
                    {
                        throw new CompilationErrorException(
                            $"Concept '{declaration.Name}' currently supports only " +
                            "const getter and/or setter requirements with concrete " +
                            "non-generic " +
                            "property types.",
                            declaration.SourceSpan);
                    }
                }
                if (declaration.PropertyRequirements
                    .GroupBy(requirement => requirement.Name)
                    .Any(group => group.Count() > 1))
                {
                    throw new CompilationErrorException(
                        $"Concept '{declaration.Name}' contains a duplicate property " +
                        "requirement.",
                        declaration.SourceSpan);
                }
                _concepts.Add((moduleName, declaration));
            }
        }

        foreach (var context in contexts)
        {
            foreach (var concept in context.DeclarationScope.Declarations
                .OfType<ConceptDeclaration>())
            {
                foreach (var requirement in concept.OperatorRequirements)
                {
                    foreach (var parameter in requirement.Parameters)
                    {
                        var type = parameter.ParameterType is ConstType constant
                            ? constant.UnderlyingType
                            : parameter.ParameterType;
                        if (type is not NamedType namedType ||
                            FindConcept(namedType.Name, context.Namespace, context.Imports) is null)
                        {
                            throw new CompilationErrorException(
                                $"Concept operator requirement parameters must name a concept " +
                                $"self type; found '{GetTypeName(parameter.ParameterType)}'.",
                                parameter.SourceSpan ?? requirement.SourceSpan ?? concept.SourceSpan);
                        }
                    }
                }
            }
        }

        if (_concepts.Count == 0)
        {
            return;
        }

        foreach (var context in contexts)
        {
            foreach (var classDeclaration in EnumerateClasses(
                context.DeclarationScope.Declarations))
            {
                foreach (var function in classDeclaration.MemberDeclarations.Declarations
                    .OfType<FunctionDeclaration>().Where(function =>
                        function.OperatorToken is not null))
                {
                    foreach (var parameter in function.Parameters)
                    {
                        var isConst = parameter.ParameterType is ConstType;
                        var parameterType = isConst
                            ? ((ConstType)parameter.ParameterType).UnderlyingType
                            : parameter.ParameterType;
                        if (parameterType is not NamedType namedType)
                        {
                            continue;
                        }
                        var matchingConcept = FindConcept(
                            namedType.Name, classDeclaration.Namespace, context.Imports);
                        if (matchingConcept is null)
                        {
                            continue;
                        }

                        var selfType = NamedType.CreateWithTypeArguments(
                            classDeclaration.FullName.ToString(),
                            classDeclaration.GenericTypeNames
                                .Select(name => (TypeBase)new GenericType(name)).ToArray());
                        parameter.ParameterType = isConst
                            ? new ConstType(selfType)
                            : selfType;
                    }
                }
            }
        }
    }

    private void ResolveConceptBases(IReadOnlyList<CompilationContext> contexts)
    {
        var declarations = contexts
            .SelectMany(context => context.DeclarationScope.Declarations
                .OfType<ConceptDeclaration>()
                .Select(concept => (Context: context, Concept: concept)))
            .ToArray();
        foreach (var (context, concept) in declarations)
        {
            if (concept.BaseConceptNames.Count == 0)
            {
                continue;
            }

            var baseNames = new HashSet<string>(StringComparer.Ordinal);
            var baseConcepts = new List<ConceptDeclaration>();
            foreach (var baseName in concept.BaseConceptNames)
            {
                if (!baseNames.Add(baseName.ToString()))
                {
                    throw new CompilationErrorException(
                        $"Concept '{concept.FullName}' lists base concept '{baseName}' more than once.",
                        concept.SourceSpan);
                }
                var baseConcept = FindConcept(
                    baseName.ToString(), context.Namespace, context.Imports);
                if (baseConcept is null)
                {
                    throw new CompilationErrorException(
                        $"Cannot resolve base concept '{baseName}' for '{concept.FullName}'.",
                        concept.SourceSpan);
                }
                baseConcepts.Add(baseConcept.Value.Declaration);
            }
            _conceptBases.Add(concept, baseConcepts);
        }

        var visiting = new HashSet<ConceptDeclaration>();
        var flattened = new HashSet<ConceptDeclaration>();
        void Flatten(ConceptDeclaration concept)
        {
            if (flattened.Contains(concept))
            {
                return;
            }
            if (!visiting.Add(concept))
            {
                throw new CompilationErrorException(
                    $"Concept inheritance cycle includes '{concept.FullName}'.",
                    concept.SourceSpan);
            }

            if (_conceptBases.TryGetValue(concept, out var baseConcepts))
            {
                foreach (var baseConcept in baseConcepts)
                {
                    Flatten(baseConcept);
                }
                ValidateInheritedRequirements(concept, baseConcepts);
                concept.InheritRequirements(baseConcepts);
            }

            visiting.Remove(concept);
            flattened.Add(concept);
        }

        foreach (var (_, concept) in declarations)
        {
            Flatten(concept);
        }
    }

    private static void ValidateInheritedRequirements(
        ConceptDeclaration concept,
        IReadOnlyList<ConceptDeclaration> baseConcepts)
    {
        string OperatorSignature(FunctionDeclaration requirement) =>
            $"{requirement.OperatorToken}({string.Join(",", requirement.Parameters.Select(
                parameter => GetTypeName(parameter.ParameterType)))})";

        var inheritedOperators = baseConcepts
            .SelectMany(baseConcept => baseConcept.OperatorRequirements)
            .Distinct().ToArray();
        if (inheritedOperators.GroupBy(OperatorSignature, StringComparer.Ordinal)
            .Any(group => group.Count() > 1))
        {
            throw new CompilationErrorException(
                $"Concept '{concept.FullName}' inherits conflicting operator requirements.",
                concept.SourceSpan);
        }
        var inheritedOperatorSignatures = inheritedOperators
            .Select(OperatorSignature).ToHashSet(StringComparer.Ordinal);
        if (concept.DeclaredOperatorRequirements.Any(requirement =>
                inheritedOperatorSignatures.Contains(OperatorSignature(requirement))))
        {
            throw new CompilationErrorException(
                $"Concept '{concept.FullName}' redeclares an inherited operator requirement.",
                concept.SourceSpan);
        }

        var inheritedMethods = baseConcepts
            .SelectMany(baseConcept => baseConcept.MethodRequirements)
            .Distinct().ToArray();
        if (inheritedMethods.GroupBy(
                requirement => ConceptDeclaration.GetMethodBindingKey(requirement),
                StringComparer.Ordinal).Any(group => group.Count() > 1))
        {
            throw new CompilationErrorException(
                $"Concept '{concept.FullName}' inherits conflicting method requirements.",
                concept.SourceSpan);
        }
        var inheritedMethodSignatures = inheritedMethods
            .Select(requirement => ConceptDeclaration.GetMethodBindingKey(requirement))
            .ToHashSet(StringComparer.Ordinal);
        if (concept.DeclaredMethodRequirements.Any(requirement =>
                inheritedMethodSignatures.Contains(
                    ConceptDeclaration.GetMethodBindingKey(requirement))))
        {
            throw new CompilationErrorException(
                $"Concept '{concept.FullName}' redeclares an inherited method requirement.",
                concept.SourceSpan);
        }

        var inheritedProperties = baseConcepts
            .SelectMany(baseConcept => baseConcept.PropertyRequirements)
            .Distinct<ConceptPropertyRequirement>(ReferenceEqualityComparer.Instance)
            .ToArray();
        if (inheritedProperties.GroupBy(
                requirement => requirement.Name, StringComparer.Ordinal)
            .Any(group => group.Count() > 1))
        {
            throw new CompilationErrorException(
                $"Concept '{concept.FullName}' inherits conflicting property requirements.",
                concept.SourceSpan);
        }
        var inheritedPropertyNames = inheritedProperties
            .Select(requirement => requirement.Name)
            .ToHashSet(StringComparer.Ordinal);
        if (concept.DeclaredPropertyRequirements.Any(requirement =>
                inheritedPropertyNames.Contains(requirement.Name)))
        {
            throw new CompilationErrorException(
                $"Concept '{concept.FullName}' redeclares an inherited property requirement.",
                concept.SourceSpan);
        }
    }

    private (string ModuleName, ConceptDeclaration Declaration)? FindConcept(
        string sourceName,
        QualifiedIdentifier currentNamespace,
        IReadOnlyList<QualifiedIdentifier> imports)
    {
        var name = new QualifiedIdentifier(
            sourceName.Split('.', StringSplitOptions.RemoveEmptyEntries));
        var candidateNames = GetCandidateNames(name, currentNamespace, imports);
        var candidates = _concepts
            .Where(concept => candidateNames.Contains(concept.Declaration.FullName))
            .ToArray();
        if (candidates.Length > 1)
        {
            throw new CompilationErrorException($"Concept name '{sourceName}' is ambiguous.");
        }
        return candidates.Length == 0 ? null : candidates[0];
    }

    private void ResolveConceptConstraints(
        FunctionDeclaration function,
        QualifiedIdentifier currentNamespace,
        IReadOnlyList<QualifiedIdentifier> imports)
    {
        if (function.ConceptConstraints.Count == 0)
        {
            return;
        }
        if (function.GenericTypeNames.Length == 0)
        {
            throw new CompilationErrorException(
                "Concept constraints are valid only on generic functions.",
                function.SourceSpan);
        }

        var resolved = new List<(
            string ParameterName,
            string ModuleName,
            ConceptDeclaration Declaration)>();
        foreach (var constraint in function.ConceptConstraints)
        {
            if (!function.GenericTypeNames.Contains(
                constraint.ParameterName, StringComparer.Ordinal))
            {
                throw new CompilationErrorException(
                    $"Concept constraint refers to unknown generic parameter " +
                    $"'{constraint.ParameterName}'.",
                    function.SourceSpan);
            }
            foreach (var conceptName in constraint.Concepts)
            {
                var concept = FindConcept(
                    conceptName.ToString(), currentNamespace, imports);
                if (concept is null)
                {
                    throw new CompilationErrorException(
                        $"Cannot resolve concept '{conceptName}'.",
                        function.SourceSpan);
                }
                if (resolved.Any(existing =>
                    existing.ParameterName == constraint.ParameterName &&
                    existing.Declaration.FullName == concept.Value.Declaration.FullName))
                {
                    throw new CompilationErrorException(
                        $"Concept '{conceptName}' is listed more than once on " +
                        $"generic parameter '{constraint.ParameterName}'.",
                        function.SourceSpan);
                }
                resolved.Add((constraint.ParameterName,
                    concept.Value.ModuleName, concept.Value.Declaration));
            }
        }
        _resolvedConceptConstraints[function] = resolved;
    }

    private void ResolveClassConceptConstraints(
        ClassDeclaration declaration,
        IReadOnlyList<QualifiedIdentifier> imports)
    {
        if (declaration.ConceptConstraints.Count == 0)
        {
            return;
        }
        if (declaration.GenericTypeNames.Length == 0)
        {
            throw new CompilationErrorException(
                "Concept constraints are valid only on generic classes, structs, and interfaces.",
                declaration.SourceSpan);
        }

        var resolved = new List<(
            string ParameterName,
            string ModuleName,
            ConceptDeclaration Declaration)>();
        foreach (var constraint in declaration.ConceptConstraints)
        {
            if (!declaration.GenericTypeNames.Contains(
                constraint.ParameterName, StringComparer.Ordinal))
            {
                throw new CompilationErrorException(
                    $"Concept constraint refers to unknown generic parameter " +
                    $"'{constraint.ParameterName}'.",
                    declaration.SourceSpan);
            }
            foreach (var conceptName in constraint.Concepts)
            {
                var concept = FindConcept(
                    conceptName.ToString(), declaration.Namespace, imports);
                if (concept is null)
                {
                    throw new CompilationErrorException(
                        $"Cannot resolve concept '{conceptName}'.",
                        declaration.SourceSpan);
                }
                if (resolved.Any(existing =>
                    existing.ParameterName == constraint.ParameterName &&
                    existing.Declaration.FullName == concept.Value.Declaration.FullName))
                {
                    throw new CompilationErrorException(
                        $"Concept '{conceptName}' is listed more than once on " +
                        $"generic parameter '{constraint.ParameterName}'.",
                        declaration.SourceSpan);
                }
                resolved.Add((constraint.ParameterName,
                    concept.Value.ModuleName, concept.Value.Declaration));
            }
        }
        _resolvedClassConceptConstraints[declaration] = resolved;
    }

    private IReadOnlyList<(string ParameterName, ConceptDeclaration Declaration)>
        GetAvailableConceptConstraints(
            ClassDeclaration? classDeclaration,
            FunctionDeclaration? function)
    {
        var functionParameters = function?.GenericTypeNames ?? [];
        var available = new List<(string ParameterName, ConceptDeclaration Declaration)>();
        if (classDeclaration is not null &&
            _resolvedClassConceptConstraints.TryGetValue(
                classDeclaration, out var classConstraints))
        {
            available.AddRange(classConstraints
                .Where(constraint => !functionParameters.Contains(
                    constraint.ParameterName, StringComparer.Ordinal))
                .Select(constraint =>
                    (constraint.ParameterName, constraint.Declaration)));
        }
        if (function is not null &&
            _resolvedConceptConstraints.TryGetValue(function, out var functionConstraints))
        {
            available.AddRange(functionConstraints.Select(constraint =>
                (constraint.ParameterName, constraint.Declaration)));
        }
        return available;
    }

    private bool SatisfiesConcept(TypeBase argumentType, ConceptDeclaration concept)
    {
        var typeDeclaration = GetClassDeclaration(argumentType);
        if (typeDeclaration is null)
        {
            return false;
        }

        foreach (var requirement in concept.OperatorRequirements)
        {
            var requiredParameters = requirement.Parameters
                .Select(parameter => SubstituteConceptSelfType(
                    parameter.ParameterType, argumentType))
                .ToArray();
            var requiredReturnType = SubstituteConceptSelfType(
                requirement.ReturnType, argumentType);
            var candidate = FindOperator(
                argumentType, requirement.OperatorToken!, requiredParameters);
            if (candidate is null ||
                !IsType(candidate.Value.Symbol.ReturnType, requiredReturnType) ||
                candidate.Value.Symbol.Declaration?.Const != requirement.Const ||
                candidate.Value.Symbol.ParameterTypes.Count != requiredParameters.Length ||
                !candidate.Value.Symbol.ParameterTypes.Zip(requiredParameters)
                    .All(pair => IsType(pair.First, pair.Second)))
            {
                return false;
            }
        }
        foreach (var requirement in concept.MethodRequirements)
        {
            if (FindConceptMethodImplementation(argumentType, requirement) is null)
            {
                return false;
            }
        }
        foreach (var requirement in concept.PropertyRequirements)
        {
            if (requirement.HasGetter &&
                FindConceptPropertyGetterImplementation(argumentType, requirement) is null ||
                requirement.HasSetter &&
                FindConceptPropertySetterImplementation(argumentType, requirement) is null)
            {
                return false;
            }
        }
        return true;
    }

    private static string GetConceptPropertyBindingKey(
        ConceptPropertyRequirement requirement) => $"property:{requirement.Name}";

    private static string GetConceptPropertySetterBindingKey(
        ConceptPropertyRequirement requirement) => $"property:{requirement.Name}:set";

    private (PropertySymbol Symbol, PropertyDeclaration Declaration)? FindConceptProperty(
        TypeBase argumentType,
        ConceptPropertyRequirement requirement)
    {
        var typeDeclaration = GetClassDeclaration(argumentType);
        if (typeDeclaration is null ||
            typeDeclaration.ClassType is not (ClassType.Class or ClassType.Struct) ||
            typeDeclaration.GenericTypeNames.Length != 0)
        {
            return null;
        }

        var declaration = typeDeclaration.MemberDeclarations.Declarations
            .OfType<PropertyDeclaration>()
            .SingleOrDefault(property => property.Name == requirement.Name);
        if (declaration is null ||
            declaration.IsStatic ||
            !declaration.MemberModifiers.Contains(MemberModifier.Public) ||
            declaration.MemberModifiers.Contains(MemberModifier.Abstract) ||
            declaration.MemberModifiers.Contains(MemberModifier.Virtual) ||
            declaration.MemberModifiers.Contains(MemberModifier.Override) ||
            !IsType(declaration.Type, requirement.Type))
        {
            return null;
        }

        var property = FindProperty(typeDeclaration, requirement.Name, staticOnly: false);
        if (property is not { } propertyMatch || propertyMatch.BaseDepth != 0)
        {
            return null;
        }

        return (propertyMatch.Symbol, declaration);
    }

    private FunctionSymbol? FindConceptPropertyGetterImplementation(
        TypeBase argumentType,
        ConceptPropertyRequirement requirement)
    {
        var property = FindConceptProperty(argumentType, requirement);
        if (property is not { } propertyMatch || !requirement.HasGetter ||
            !propertyMatch.Declaration.PropertyAccessorDeclarations.Any(accessor =>
                accessor.Name == "get" && accessor.Const && !accessor.Extern &&
                accessor.BodyFunction is not null && accessor.Parameters.Count == 0) ||
            !propertyMatch.Symbol.Accessors.Any(accessor =>
                accessor.Name == "get" && accessor.Const &&
                accessor.ParameterTypes.Count == 0))
        {
            return null;
        }

        return new FunctionSymbol(
            propertyMatch.Symbol.ModuleName,
            new QualifiedIdentifier(propertyMatch.Symbol.FullName, "__const_get"),
            [],
            propertyMatch.Symbol.Type);
    }

    private FunctionSymbol? FindConceptPropertySetterImplementation(
        TypeBase argumentType,
        ConceptPropertyRequirement requirement)
    {
        var property = FindConceptProperty(argumentType, requirement);
        if (property is not { } propertyMatch || !requirement.HasSetter ||
            !propertyMatch.Declaration.PropertyAccessorDeclarations.Any(accessor =>
                accessor.Name == "set" && !accessor.Extern &&
                accessor.BodyFunction is not null && accessor.Parameters.Count == 0) ||
            !propertyMatch.Symbol.Accessors.Any(accessor =>
                accessor.Name == "set" && accessor.ParameterTypes.Count == 0))
        {
            return null;
        }

        return new FunctionSymbol(
            propertyMatch.Symbol.ModuleName,
            new QualifiedIdentifier(propertyMatch.Symbol.FullName, "__set"),
            [propertyMatch.Symbol.Type],
            BuiltInSystemTypes.Void);
    }

    private FunctionSymbol? FindConceptMethodImplementation(
        TypeBase argumentType,
        FunctionDeclaration requirement,
        IReadOnlyList<TypeBase>? methodTypeArguments = null)
    {
        var typeDeclaration = GetClassDeclaration(argumentType);
        if (typeDeclaration is null ||
            typeDeclaration.ClassType is not (ClassType.Class or ClassType.Struct) ||
            typeDeclaration.GenericTypeNames.Length != 0)
        {
            return null;
        }

        var matches = FindFunctions(typeDeclaration, requirement.Name)
            .Select(candidate => MatchConceptMethodImplementation(
                argumentType, typeDeclaration, requirement, candidate.Symbol,
                methodTypeArguments))
            .Where(candidate => candidate is not null)
            .Select(candidate => candidate!)
            .Distinct()
            .ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private FunctionSymbol? MatchConceptMethodImplementation(
        TypeBase selfType,
        ClassDeclaration typeDeclaration,
        FunctionDeclaration requirement,
        FunctionSymbol candidate,
        IReadOnlyList<TypeBase>? methodTypeArguments)
    {
        if (candidate.Declaration is not { IsStatic: false } implementation ||
            implementation.Const != requirement.Const ||
            !ReferenceEquals(implementation.ParentClassDeclaration, typeDeclaration) ||
            implementation.VirtualSlotIndex is not null || implementation.IsExtension ||
            !implementation.MemberModifiers.Contains(MemberModifier.Public) ||
            implementation.GenericTypeNames.Length != requirement.GenericTypeNames.Length ||
            !ConceptMethodConstraintsMatch(requirement, implementation) ||
            candidate.ParameterTypes.Count != requirement.Parameters.Count)
        {
            return null;
        }

        IReadOnlyList<TypeBase> requiredParameters;
        TypeBase requiredReturnType;
        if (requirement.GenericTypeNames.Length == 0)
        {
            if (methodTypeArguments is { Count: > 0 })
            {
                return null;
            }
            requiredParameters = requirement.Parameters
                .Select(parameter => parameter.ParameterType).ToArray();
            requiredReturnType = requirement.ReturnType;
        }
        else if (methodTypeArguments is null)
        {
            var genericMapping = requirement.GenericTypeNames.Zip(
                    implementation.GenericTypeNames)
                .ToDictionary(pair => pair.First, pair => (TypeBase)new GenericType(pair.Second),
                    StringComparer.Ordinal);
            requiredParameters = requirement.Parameters.Select(parameter =>
                GenericTypeSubstitution.Substitute(parameter.ParameterType, genericMapping))
                .ToArray();
            requiredReturnType = GenericTypeSubstitution.Substitute(
                requirement.ReturnType, genericMapping);
        }
        else
        {
            if (methodTypeArguments.Count != requirement.GenericTypeNames.Length)
            {
                return null;
            }
            var genericMapping = requirement.GenericTypeNames.Zip(methodTypeArguments)
                .ToDictionary(pair => pair.First, pair => pair.Second, StringComparer.Ordinal);
            requiredParameters = requirement.Parameters.Select(parameter =>
                GenericTypeSubstitution.Substitute(parameter.ParameterType, genericMapping))
                .ToArray();
            requiredReturnType = GenericTypeSubstitution.Substitute(
                requirement.ReturnType, genericMapping);
            candidate = CloseGenericFunction(candidate, requiredParameters, methodTypeArguments)!;
            if (candidate is null)
            {
                return null;
            }
        }

        requiredParameters = requiredParameters.Select(parameter =>
            SubstituteConceptSelfType(parameter, selfType)).ToArray();
        requiredReturnType = SubstituteConceptSelfType(
            requiredReturnType, selfType);
        if (!candidate.ParameterTypes.Zip(requiredParameters)
                .All(pair => IsType(pair.First, pair.Second)) ||
            !IsType(candidate.ReturnType, requiredReturnType))
        {
            return null;
        }
        if (candidate.SpecializationName is not null)
        {
            _project!.AddGenericFunctionInstance(candidate);
        }
        return candidate;
    }

    private bool ConceptMethodTypeArgumentsSatisfyConstraints(
        FunctionDeclaration requirement,
        IReadOnlyList<TypeBase> methodTypeArguments)
    {
        if (requirement.GenericTypeNames.Length != methodTypeArguments.Count)
        {
            return false;
        }

        var arguments = requirement.GenericTypeNames.Zip(methodTypeArguments)
            .ToDictionary(pair => pair.First, pair => pair.Second, StringComparer.Ordinal);
        foreach (var constraint in requirement.GenericConstraints)
        {
            var argumentType = arguments[constraint.ParameterName];
            if (!SatisfiesSpecialConstraint(argumentType, constraint))
            {
                _genericConstraintFailure =
                    $"Type '{GetTypeName(argumentType)}' does not satisfy the " +
                    $"{GetConstraintKindName(constraint.Kind)} constraint for parameter " +
                    $"'{constraint.ParameterName}' on concept method '{requirement.Name}'.";
                return false;
            }
            foreach (var requiredType in constraint.Types)
            {
                var closedRequiredType = GenericTypeSubstitution.Substitute(
                    requiredType, arguments);
                if (SatisfiesGenericTypeConstraint(closedRequiredType, argumentType))
                {
                    continue;
                }
                _genericConstraintFailure =
                    $"Type '{GetTypeName(argumentType)}' does not satisfy generic " +
                    $"constraint '{GetTypeName(closedRequiredType)}' for parameter " +
                    $"'{constraint.ParameterName}' on concept method '{requirement.Name}'.";
                return false;
            }
        }
        if (_resolvedConceptConstraints.TryGetValue(requirement, out var concepts))
        {
            foreach (var conceptConstraint in concepts)
            {
                var argumentType = arguments[conceptConstraint.ParameterName];
                if (SatisfiesConcept(argumentType, conceptConstraint.Declaration))
                {
                    continue;
                }
                _genericConstraintFailure =
                    $"Type '{GetTypeName(argumentType)}' does not satisfy concept " +
                    $"'{conceptConstraint.Declaration.FullName}' for parameter " +
                    $"'{conceptConstraint.ParameterName}' on concept method " +
                    $"'{requirement.Name}'.";
                return false;
            }
        }
        return true;
    }

    private bool ConceptMethodConstraintsMatch(
        FunctionDeclaration requirement,
        FunctionDeclaration implementation)
    {
        if (requirement.GenericConstraints.Count != implementation.GenericConstraints.Count)
        {
            return false;
        }

        var parameterNames = requirement.GenericTypeNames.Zip(
                implementation.GenericTypeNames)
            .ToDictionary(pair => pair.First, pair => pair.Second, StringComparer.Ordinal);
        foreach (var requiredConstraint in requirement.GenericConstraints)
        {
            if (!parameterNames.TryGetValue(
                    requiredConstraint.ParameterName, out var implementationParameter))
            {
                return false;
            }
            var implementationConstraint = implementation.GenericConstraints
                .SingleOrDefault(constraint =>
                    constraint.ParameterName == implementationParameter);
            if (implementationConstraint is null ||
                implementationConstraint.Kind != requiredConstraint.Kind ||
                implementationConstraint.Types.Count != requiredConstraint.Types.Count)
            {
                return false;
            }

            var unmatchedTypes = implementationConstraint.Types.ToList();
            foreach (var requiredType in requiredConstraint.Types)
            {
                var closedRequiredType = GenericTypeSubstitution.Substitute(
                    requiredType,
                    parameterNames.ToDictionary(
                        pair => pair.Key,
                        pair => (TypeBase)new GenericType(pair.Value),
                        StringComparer.Ordinal));
                var matchIndex = unmatchedTypes.FindIndex(candidateType =>
                    IsType(candidateType, closedRequiredType));
                if (matchIndex < 0)
                {
                    return false;
                }
                unmatchedTypes.RemoveAt(matchIndex);
            }
        }

        var requiredConcepts = _resolvedConceptConstraints.TryGetValue(
            requirement, out var requirementConcepts)
            ? requirementConcepts
            : [];
        var implementationConcepts = _resolvedConceptConstraints.TryGetValue(
            implementation, out var implementationConceptRequirements)
            ? implementationConceptRequirements
            : [];
        if (requiredConcepts.Count != implementationConcepts.Count)
        {
            return false;
        }
        foreach (var requiredConcept in requiredConcepts)
        {
            var parameterIndex = Array.IndexOf(
                requirement.GenericTypeNames, requiredConcept.ParameterName);
            if (parameterIndex < 0 ||
                parameterIndex >= implementation.GenericTypeNames.Length)
            {
                return false;
            }
            var implementationParameter = implementation.GenericTypeNames[parameterIndex];
            if (!implementationConcepts.Any(candidate =>
                    candidate.ParameterName == implementationParameter &&
                    candidate.Declaration.FullName == requiredConcept.Declaration.FullName))
            {
                return false;
            }
        }
        return true;
    }

    private TypeBase SubstituteConceptSelfType(TypeBase type, TypeBase selfType)
    {
        if (type is ConstType constant)
        {
            return new ConstType(SubstituteConceptSelfType(
                constant.UnderlyingType, selfType));
        }
        if (type is ArrayType arrayType)
        {
            return new ArrayType(SubstituteConceptSelfType(
                arrayType.ElementType, selfType));
        }
        if (type is NamedType namedType && namedType.TypeArguments.Count == 0 &&
            _concepts.Any(concept =>
                concept.Declaration.Name == namedType.Name ||
                concept.Declaration.FullName.ToString() == namedType.Name))
        {
            return selfType;
        }
        return type;
    }
}

using CxCompiler.Model.Common;
using CxCompiler.Model.Errors;
using CxCompiler.Model.Types;
using CxCompiler.Model.Types.BuiltInTypes;

namespace CxCompiler.Semantics;

public sealed partial class SemanticBinder
{

    private void ResolveGenericTypeConstraints(
        IEnumerable<DeclarationBase> declarations,
        IReadOnlyList<QualifiedIdentifier> imports)
    {
        foreach (var classDeclaration in EnumerateClasses(declarations))
        {
            foreach (var constraint in classDeclaration.GenericConstraints)
            {
                BindAtSourceSpan(classDeclaration, () =>
                {
                    if (!classDeclaration.GenericTypeNames.Contains(
                            constraint.ParameterName, StringComparer.Ordinal))
                    {
                        throw new CompilationErrorException(
                            $"Constraint refers to unknown generic parameter " +
                            $"'{constraint.ParameterName}'.");
                    }
                    foreach (var type in constraint.Types)
                    {
                        ResolveTypeReference(type, classDeclaration.Namespace, imports);
                    }
                    ValidateGenericConstraintTypes(constraint);
                });
            }
            BindAtSourceSpan(classDeclaration, () =>
                ValidateGenericConstraintSet(classDeclaration.GenericConstraints));
            ResolveClassConceptConstraints(classDeclaration, imports);
        }
    }

    private void ValidateGenericBaseTypeConstraints(
        IEnumerable<DeclarationBase> declarations)
    {
        foreach (var classDeclaration in EnumerateClasses(declarations))
        {
            if (classDeclaration.BaseClassType is { } baseType)
            {
                BindAtSourceSpan(classDeclaration, () =>
                    ValidateOpenGenericTypeConstraints(
                        baseType,
                        classDeclaration.GenericConstraints,
                        GetAvailableConceptConstraints(classDeclaration, null)));
            }
        }
    }

    private static void ValidateGenericConstraintSet(
        IReadOnlyList<GenericTypeConstraint> constraints)
    {
        var duplicate = constraints.GroupBy(
                constraint => constraint.ParameterName, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new CompilationErrorException(
                $"Generic parameter '{duplicate.Key}' cannot have multiple constraint clauses.");
        }
    }

    private void ValidateGenericConstraintTypes(GenericTypeConstraint constraint)
    {
        foreach (var type in constraint.Types)
        {
            if (UnwrapConst(type) is not NamedType
                { ClassType: ClassType.Class or ClassType.Interface })
            {
                throw new CompilationErrorException(
                    $"Generic constraint '{GetTypeName(type)}' must be a class or interface.");
            }
        }

        if (constraint.Types.Count(type =>
            UnwrapConst(type) is NamedType { ClassType: ClassType.Class }) > 1)
        {
            throw new CompilationErrorException(
                $"Generic constraint for '{constraint.ParameterName}' cannot name more than one class.");
        }

        if (constraint.Kind == GenericConstraintKind.ReferenceType &&
            constraint.Types.Any(type =>
                UnwrapConst(type) is NamedType { ClassType: ClassType.Class }))
        {
            throw new CompilationErrorException(
                $"Reference-type constraint for '{constraint.ParameterName}' cannot also name a base class.");
        }

        if (constraint.Kind == GenericConstraintKind.ValueType &&
            constraint.Types.Any(type =>
                UnwrapConst(type) is NamedType { ClassType: ClassType.Class }))
        {
            throw new CompilationErrorException(
                $"Value-type constraint for '{constraint.ParameterName}' cannot name a base class.");
        }
    }

    private static bool IsReferenceType(TypeBase type)
    {
        type = UnwrapConst(type);
        return type is ReferenceTypeBase or ArrayType ||
            type is NamedType { ClassType: ClassType.Class or ClassType.Interface };
    }

    private static bool IsValueType(TypeBase type)
    {
        type = UnwrapConst(type);
        return type is ValueTypeBase ||
            type is NamedType { ClassType: ClassType.Struct or ClassType.Enum };
    }

    private static bool SatisfiesSpecialConstraint(
        TypeBase argumentType,
        GenericTypeConstraint constraint,
        IReadOnlyList<GenericTypeConstraint>? availableConstraints = null)
    {
        if (constraint.Kind == GenericConstraintKind.Type)
        {
            return true;
        }

        if (argumentType is GenericType genericArgument)
        {
            return availableConstraints?.Any(available =>
                available.ParameterName == genericArgument.Name &&
                (available.Kind == constraint.Kind ||
                    constraint.Kind == GenericConstraintKind.ReferenceType &&
                    available.Kind == GenericConstraintKind.Type &&
                    available.Types.Any(type => UnwrapConst(type) is NamedType
                    {
                        ClassType: ClassType.Class or ClassType.Interface,
                    }))) == true;
        }

        return constraint.Kind switch
        {
            GenericConstraintKind.ReferenceType => IsReferenceType(argumentType),
            GenericConstraintKind.ValueType => IsValueType(argumentType),
            _ => true,
        };
    }

    private static string GetConstraintKindName(GenericConstraintKind kind) => kind switch
    {
        GenericConstraintKind.ReferenceType => "reference-type",
        GenericConstraintKind.ValueType => "value-type",
        _ => "type",
    };

    private void ValidateGenericTypeConstraints(
        ClassDeclaration declaration,
        NamedType constructedType)
    {
        if ((declaration.GenericConstraints.Count == 0 &&
             declaration.ConceptConstraints.Count == 0))
        {
            return;
        }

        var arguments = declaration.GenericTypeNames
            .Zip(constructedType.TypeArguments)
            .ToDictionary(pair => pair.First, pair => pair.Second, StringComparer.Ordinal);
        foreach (var constraint in declaration.GenericConstraints)
        {
            var argument = arguments[constraint.ParameterName];
            if (ContainsOpenTypeParameter(argument))
            {
                continue;
            }
            if (!SatisfiesSpecialConstraint(argument, constraint))
            {
                throw new CompilationErrorException(
                    $"Type '{GetTypeName(argument)}' does not satisfy the " +
                    $"{GetConstraintKindName(constraint.Kind)} constraint for parameter " +
                    $"'{constraint.ParameterName}' on '{declaration.FullName}'.",
                    constructedType.SourceSpan);
            }
            foreach (var constraintType in constraint.Types)
            {
                var requiredType = GenericTypeSubstitution.Substitute(constraintType, arguments);
                if (!SatisfiesGenericTypeConstraint(requiredType, argument))
                {
                    throw new CompilationErrorException(
                        $"Type '{GetTypeName(argument)}' does not satisfy generic type " +
                        $"constraint '{GetTypeName(requiredType)}' for parameter " +
                        $"'{constraint.ParameterName}' on '{declaration.FullName}'.",
                        constructedType.SourceSpan);
                }
            }
        }
        if (_resolvedClassConceptConstraints.TryGetValue(
            declaration, out var conceptConstraints))
        {
            foreach (var conceptConstraint in conceptConstraints)
            {
                var argument = arguments[conceptConstraint.ParameterName];
                if (argument is GenericType genericArgument)
                {
                    var available = _activeFunction is null
                        ? []
                        : GetAvailableConceptConstraints(
                            _activeFunction.ParentClassDeclaration, _activeFunction);
                    if (available.Any(candidate =>
                        candidate.ParameterName == genericArgument.Name &&
                        ConceptConstraintImplies(
                            candidate.Declaration, conceptConstraint.Declaration)))
                    {
                        continue;
                    }
                    if (_activeFunction is null)
                    {
                        continue;
                    }
                    throw new CompilationErrorException(
                        $"Type parameter '{genericArgument.Name}' does not satisfy concept " +
                        $"'{conceptConstraint.Declaration.FullName}' for parameter " +
                        $"'{conceptConstraint.ParameterName}' on '{declaration.FullName}'.",
                        constructedType.SourceSpan);
                }
                if (ContainsOpenTypeParameter(argument))
                {
                    throw new CompilationErrorException(
                        $"Type '{GetTypeName(argument)}' does not satisfy concept " +
                        $"'{conceptConstraint.Declaration.FullName}' for parameter " +
                        $"'{conceptConstraint.ParameterName}' on '{declaration.FullName}'.",
                        constructedType.SourceSpan);
                }
                if (!SatisfiesConcept(argument, conceptConstraint.Declaration))
                {
                    throw new CompilationErrorException(
                        $"Type '{GetTypeName(argument)}' does not satisfy concept " +
                        $"'{conceptConstraint.Declaration.FullName}' for parameter " +
                        $"'{conceptConstraint.ParameterName}' on '{declaration.FullName}'.",
                        constructedType.SourceSpan);
                }
            }
        }
    }

    private void ValidateOpenGenericTypeConstraints(
        TypeBase type,
        IReadOnlyList<GenericTypeConstraint> availableConstraints,
        IReadOnlyList<(string ParameterName, ConceptDeclaration Declaration)>?
            availableConceptConstraints = null)
    {
        switch (type)
        {
            case ConstType constant:
                ValidateOpenGenericTypeConstraints(
                    constant.UnderlyingType, availableConstraints, availableConceptConstraints);
                return;
            case ArrayType array:
                ValidateOpenGenericTypeConstraints(
                    array.ElementType, availableConstraints, availableConceptConstraints);
                return;
            case NullableType nullable:
                ValidateOpenGenericTypeConstraints(
                    nullable.UnderlyingType, availableConstraints, availableConceptConstraints);
                return;
            case FunctionType function:
                ValidateOpenGenericTypeConstraints(
                    function.ReturnType, availableConstraints, availableConceptConstraints);
                foreach (var parameter in function.ParameterTypes)
                {
                    ValidateOpenGenericTypeConstraints(
                        parameter, availableConstraints, availableConceptConstraints);
                }
                return;
            case NamedType:
                break;
            default:
                return;
        }

        var namedType = (NamedType)type;
        var declaration = _types
            .Where(candidate => new QualifiedIdentifier(
                candidate.ModuleName, candidate.Declaration.FullName) ==
                namedType.ResolvedTypeFullName)
            .Where(candidate => candidate.Declaration.GenericTypeNames.Length ==
                namedType.TypeArguments.Count)
            .Select(candidate => candidate.Declaration)
            .SingleOrDefault();
        if (declaration is not null && declaration.GenericConstraints.Count != 0)
        {
            var arguments = declaration.GenericTypeNames
                .Zip(namedType.TypeArguments)
                .ToDictionary(pair => pair.First, pair => pair.Second, StringComparer.Ordinal);
            foreach (var constraint in declaration.GenericConstraints)
            {
                var argument = arguments[constraint.ParameterName];
                if (!SatisfiesSpecialConstraint(
                    argument, constraint, availableConstraints))
                {
                    throw new CompilationErrorException(
                        $"Type '{GetTypeName(argument)}' does not satisfy the " +
                        $"{GetConstraintKindName(constraint.Kind)} constraint for parameter " +
                        $"'{constraint.ParameterName}' on '{declaration.FullName}'.",
                        namedType.SourceSpan);
                }
                foreach (var constraintType in constraint.Types)
                {
                    var requiredType = GenericTypeSubstitution.Substitute(
                        constraintType, arguments);
                    var satisfies = SatisfiesGenericTypeConstraint(requiredType, argument);
                    if (!satisfies && argument is GenericType genericArgument)
                    {
                        satisfies = availableConstraints
                            .Where(available => available.ParameterName == genericArgument.Name)
                            .SelectMany(available => available.Types)
                            .Any(availableType =>
                                SatisfiesGenericTypeConstraint(requiredType, availableType));
                    }
                    if (!satisfies)
                    {
                        throw new CompilationErrorException(
                            $"Type '{GetTypeName(argument)}' does not satisfy generic type " +
                            $"constraint '{GetTypeName(requiredType)}' for parameter " +
                            $"'{constraint.ParameterName}' on '{declaration.FullName}'.",
                            namedType.SourceSpan);
                    }
                }
            }
        }

        if (declaration is not null &&
            _resolvedClassConceptConstraints.TryGetValue(
                declaration, out var requiredConcepts))
        {
            var arguments = declaration.GenericTypeNames
                .Zip(namedType.TypeArguments)
                .ToDictionary(pair => pair.First, pair => pair.Second, StringComparer.Ordinal);
            foreach (var constraint in requiredConcepts)
            {
                if (arguments[constraint.ParameterName] is not GenericType genericArgument)
                {
                    throw new CompilationErrorException(
                        $"Type '{GetTypeName(arguments[constraint.ParameterName])}' does not " +
                        $"satisfy concept '{constraint.Declaration.FullName}' for parameter " +
                        $"'{constraint.ParameterName}' on '{declaration.FullName}'.",
                        namedType.SourceSpan);
                }
                if (availableConceptConstraints?.Any(available =>
                    available.ParameterName == genericArgument.Name &&
                    ConceptConstraintImplies(available.Declaration, constraint.Declaration)) != true)
                {
                    throw new CompilationErrorException(
                        $"Type parameter '{genericArgument.Name}' does not satisfy concept " +
                        $"'{constraint.Declaration.FullName}' required by generic type " +
                        $"'{declaration.FullName}'.",
                        namedType.SourceSpan);
                }
            }
        }

        foreach (var argument in namedType.TypeArguments)
        {
            ValidateOpenGenericTypeConstraints(
                argument, availableConstraints, availableConceptConstraints);
        }
    }

    private bool ConceptConstraintImplies(
        ConceptDeclaration available,
        ConceptDeclaration required) =>
        ReferenceEquals(available, required) ||
        _conceptBases.TryGetValue(available, out var bases) &&
        bases.Any(baseConcept => ConceptConstraintImplies(baseConcept, required));

    private bool IsConstrainedGenericReferenceType(TypeBase type, FunctionDeclaration function)
    {
        type = UnwrapConst(type);
        if (type is ArrayType array &&
            ContainsFunctionTypeParameter(array.ElementType, function.GenericTypeNames))
        {
            return true;
        }
        if (type is not GenericType genericType ||
            !function.GenericTypeNames.Contains(genericType.Name))
        {
            return false;
        }

        return function.GenericConstraints.Any(constraint =>
            constraint.ParameterName == genericType.Name &&
            (constraint.Kind == GenericConstraintKind.ReferenceType ||
             constraint.Kind == GenericConstraintKind.Type &&
             constraint.Types.Any(constraintType =>
                 constraintType is NamedType
                 {
                     ClassType: ClassType.Class or ClassType.Interface,
                 })));
    }

    private static bool IsReferenceConstrainedGenericType(
        TypeBase type,
        FunctionDeclaration function)
    {
        if (UnwrapConst(type) is not GenericType parameter)
        {
            return false;
        }

        var constraints = function.GenericConstraints.Concat(
            function.ParentClassDeclaration?.GenericConstraints ?? []);
        return constraints.Any(constraint =>
            constraint.ParameterName == parameter.Name &&
            (constraint.Kind == GenericConstraintKind.ReferenceType ||
             constraint.Types.Any(required =>
                UnwrapConst(required) is NamedType { ClassType: ClassType.Class })));
    }

    private bool SatisfiesGenericTypeConstraint(TypeBase requiredType, TypeBase argumentType)
    {
        if (CanAssign(requiredType, argumentType))
        {
            return true;
        }

        var required = UnwrapConst(requiredType);
        var argument = UnwrapConst(argumentType);
        if (required is not NamedType
            {
                ClassType: ClassType.Interface,
                TypeArguments.Count: 0,
            } ||
            argument is not NamedType
            {
                ClassType: ClassType.Struct,
            } ||
            GetClassDeclaration(required) is not { ClassType: ClassType.Interface } interfaceType ||
            GetMemberClassDeclaration(argument) is not { ClassType: ClassType.Struct } valueType)
        {
            return false;
        }

        return GetImplementedInterfaces(valueType).Any(candidate =>
            ReferenceEquals(candidate, interfaceType));
    }

    private ClassDeclaration? GetActiveClassConstraint(GenericType genericParameter)
    {
        if (_activeFunction is null)
        {
            return null;
        }

        var constraints = _activeFunction.GenericConstraints.Concat(
            _activeFunction.ParentClassDeclaration?.GenericConstraints ?? []);
        foreach (var constraint in constraints.Where(item =>
            item.ParameterName == genericParameter.Name))
        {
            if (constraint.Kind == GenericConstraintKind.ReferenceType &&
                GetClassDeclarationIncludingConstructed(BuiltInSystemTypes.Object) is
                    { ClassType: ClassType.Class } rootObjectClass)
            {
                return rootObjectClass;
            }
            foreach (var type in constraint.Types)
            {
                if (type is NamedType { ClassType: ClassType.Class } &&
                    GetClassDeclarationIncludingConstructed(type) is
                        { ClassType: ClassType.Class } classDeclaration)
                {
                    return classDeclaration;
                }
            }
        }
        return null;
    }

    private IReadOnlyList<ClassDeclaration> GetActiveInterfaceConstraints(
        GenericType genericParameter) =>
        GetActiveInterfaceConstraintTypes(genericParameter)
            .Select(GetClassDeclarationIncludingConstructed)
            .OfType<ClassDeclaration>()
            .Distinct()
            .ToArray();

    private IReadOnlyList<NamedType> GetActiveTypeConstraintTypes(
        GenericType genericParameter)
    {
        if (_activeFunction is null)
        {
            return [];
        }

        return _activeFunction.GenericConstraints.Concat(
                _activeFunction.ParentClassDeclaration?.GenericConstraints ?? [])
            .Where(constraint => constraint.ParameterName == genericParameter.Name &&
                constraint.Kind == GenericConstraintKind.Type)
            .SelectMany(constraint => constraint.Types)
            .OfType<NamedType>()
            .Where(type => type.ClassType is ClassType.Class or ClassType.Interface)
            .Distinct()
            .ToArray();
    }

    private TypeBase? GetConstrainedMemberReceiverType(
        TypeBase? receiverType,
        FunctionSymbol symbol)
    {
        if (receiverType is not null &&
            UnwrapConst(receiverType) is GenericType genericParameter &&
            symbol.Declaration?.ParentClassDeclaration is { } declaringType)
        {
            return GetActiveTypeConstraintTypes(genericParameter).FirstOrDefault(type =>
                ReferenceEquals(
                    GetClassDeclarationIncludingConstructed(type), declaringType)) ?? receiverType;
        }

        return receiverType;
    }

    private IReadOnlyList<NamedType> GetActiveInterfaceConstraintTypes(
        GenericType genericParameter)
    {
        if (_activeFunction is null)
        {
            return [];
        }

        var types = new List<NamedType>();
        var constraints = _activeFunction.GenericConstraints.Concat(
            _activeFunction.ParentClassDeclaration?.GenericConstraints ?? []);
        foreach (var constraint in constraints.Where(item =>
            item.ParameterName == genericParameter.Name))
        {
            foreach (var type in constraint.Types)
            {
                if (type is NamedType { ClassType: ClassType.Interface } interfaceType &&
                    GetClassDeclarationIncludingConstructed(type) is
                        { ClassType: ClassType.Interface })
                {
                    types.Add(interfaceType);
                }
            }
        }
        return types.Distinct().ToArray();
    }
}

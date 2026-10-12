using CxCompiler.Model;
using CxCompiler.Model.Common;
using CxCompiler.Model.Errors;
using CxCompiler.Model.Expressions;
using CxCompiler.Model.Statements;
using CxCompiler.Model.Types;
using CxCompiler.Model.Types.BuiltInTypes;

namespace CxCompiler.Semantics;

public sealed partial class SemanticBinder
{

    private static bool IsGenericFieldGetter(PropertyAccessorDeclaration accessor)
    {
        if (accessor.Name != "get" || accessor.Parameters.Count != 0 ||
            accessor.ParentPropertyDeclaration.IsStatic ||
            !IsPropertyGenericTypePattern(accessor.ParentPropertyDeclaration.Type,
                accessor.ParentPropertyDeclaration.ParentClassDeclaration?.GenericTypeNames ?? []) ||
            accessor.Body is not [ReturnStatement { Expression: var expression }])
        {
            return false;
        }
        var fieldName = GetPropertyAccessorFieldName(expression);
        if (fieldName is null)
        {
            return false;
        }
        return accessor.ParentPropertyDeclaration.ParentClassDeclaration?.MemberDeclarations
            .Declarations.OfType<FieldDeclaration>().Any(field =>
                !field.IsStatic && field.Name == fieldName &&
                IsDirectGenericIdentityType(field.Type, accessor.ParentPropertyDeclaration.Type)) == true;
    }

    private static bool IsGenericArrayGetter(PropertyAccessorDeclaration accessor)
    {
        if (accessor.Name != "get" || accessor.Parameters.Count != 0 ||
            accessor.ParentPropertyDeclaration.IsStatic ||
            !IsPropertyGenericTypePattern(
                accessor.ParentPropertyDeclaration.Type,
                accessor.ParentPropertyDeclaration.ParentClassDeclaration?.GenericTypeNames ?? []) ||
            accessor.Body is not
                [ReturnStatement
                {
                    Expression: ArrayAccessExpression
                    {
                        Target: IdentifierExpression { Identifier.Parts: [var fieldName] },
                        Indices.Count: 1,
                    },
                }])
        {
            return false;
        }

        return accessor.ParentPropertyDeclaration.ParentClassDeclaration?.MemberDeclarations
            .Declarations.OfType<FieldDeclaration>().Any(field =>
                !field.IsStatic && field.Name == fieldName &&
                ArrayType.TryGetElementType(field.Type, out var elementType) &&
                IsDirectGenericIdentityType(
                    elementType, accessor.ParentPropertyDeclaration.Type)) == true;
    }

    private static bool IsGenericFieldLocalGetter(PropertyAccessorDeclaration accessor)
    {
        if (accessor.Name != "get" || accessor.Parameters.Count != 0 ||
            accessor.ParentPropertyDeclaration.IsStatic ||
            !IsPropertyGenericTypePattern(accessor.ParentPropertyDeclaration.Type,
                accessor.ParentPropertyDeclaration.ParentClassDeclaration?.GenericTypeNames ?? []) ||
            accessor.Body is not
                [LocalVariableDeclarationStatement
                    { Declarators: [var declarator] } local,
                 ReturnStatement { Expression: IdentifierExpression returned }])
        {
            return false;
        }

        var fields = accessor.ParentPropertyDeclaration.ParentClassDeclaration?
            .MemberDeclarations.Declarations.OfType<FieldDeclaration>().ToArray() ?? [];
        bool IsMatchingField(ExpressionBase expression) =>
            GetPropertyAccessorFieldName(expression) is { } fieldName &&
            fields.Any(field => !field.IsStatic && field.Name == fieldName &&
                IsDirectGenericIdentityType(
                    field.Type, accessor.ParentPropertyDeclaration.Type));
        var supportedInitializer = declarator.Initializer switch
        {
            ConditionalExpression conditional =>
                IsSupportedGenericPropertyBooleanCondition(
                    conditional.Condition, fields) &&
                (IsMatchingField(conditional.WhenTrue) ||
                    IsMatchingField(conditional.WhenFalse)) &&
                new[] { conditional.WhenTrue, conditional.WhenFalse }.All(arm =>
                    arm is LiteralExpression { SourceText: "default" } ||
                    IsMatchingField(arm)),
            { } expression when IsMatchingField(expression) => true,
            _ => false,
        };
        if (!supportedInitializer || returned.Identifier.Parts[^1] != declarator.Name ||
            local.DeclaredType is not AutoType &&
            !IsDirectGenericIdentityType(
                local.DeclaredType, accessor.ParentPropertyDeclaration.Type))
        {
            return false;
        }
        return true;
    }

    private static bool IsSupportedGenericPropertyBooleanCondition(
        ExpressionBase expression,
        IReadOnlyCollection<FieldDeclaration> fields) => expression switch
    {
        LiteralExpression { SourceText: "true" or "false" } => true,
        _ when GetPropertyAccessorFieldName(expression) is { } fieldName =>
            fields.Any(field => !field.IsStatic && field.Name == fieldName &&
                IsType(field.Type, BuiltInSystemTypes.Bool)),
        UnaryExpression { Operator: "!", Operand: var operand } =>
            IsSupportedGenericPropertyBooleanCondition(operand, fields),
        BinaryExpression { Operator: "&&" or "||", Left: var left, Right: var right } =>
            IsSupportedGenericPropertyBooleanCondition(left, fields) &&
            IsSupportedGenericPropertyBooleanCondition(right, fields),
        _ => false,
    };

    private static bool IsGenericConditionalFieldGetter(PropertyAccessorDeclaration accessor)
    {
        if (accessor.Name != "get" || !accessor.Const || accessor.Parameters.Count != 0 ||
            accessor.ParentPropertyDeclaration.IsStatic ||
            !IsPropertyGenericTypePattern(accessor.ParentPropertyDeclaration.Type,
                accessor.ParentPropertyDeclaration.ParentClassDeclaration?.GenericTypeNames ?? []) ||
            accessor.Body is not
                [ReturnStatement { Expression: ConditionalExpression conditional }])
        {
            return false;
        }

        var declaration = accessor.ParentPropertyDeclaration.ParentClassDeclaration;
        var conditionFieldName = GetPropertyAccessorFieldName(conditional.Condition);
        if (declaration is null || conditionFieldName is null)
        {
            return false;
        }

        var fields = declaration.MemberDeclarations.Declarations
            .OfType<FieldDeclaration>().Where(field => !field.IsStatic).ToArray();
        bool IsMatchingFieldOrDefault(ExpressionBase expression) =>
            expression is LiteralExpression { SourceText: "default" } ||
            GetPropertyAccessorFieldName(expression) is { } fieldName &&
            fields.Any(field => field.Name == fieldName &&
                IsDirectGenericIdentityType(field.Type, accessor.ParentPropertyDeclaration.Type));
        var hasMatchingField = new[] { conditional.WhenTrue, conditional.WhenFalse }
            .Any(expression => GetPropertyAccessorFieldName(expression) is { } fieldName &&
                fields.Any(field => field.Name == fieldName &&
                    IsDirectGenericIdentityType(
                        field.Type, accessor.ParentPropertyDeclaration.Type)));
        return hasMatchingField &&
            IsMatchingFieldOrDefault(conditional.WhenTrue) &&
            IsMatchingFieldOrDefault(conditional.WhenFalse) &&
            fields.Any(field => field.Name == conditionFieldName &&
                IsType(field.Type, BuiltInSystemTypes.Bool));
    }

    private static bool IsGenericFieldSetter(PropertyAccessorDeclaration accessor)
    {
        if (accessor.Name != "set" || accessor.Parameters.Count != 0 ||
            accessor.ParentPropertyDeclaration.IsStatic ||
            !IsPropertyGenericTypePattern(accessor.ParentPropertyDeclaration.Type,
                accessor.ParentPropertyDeclaration.ParentClassDeclaration?.GenericTypeNames ?? []) ||
            accessor.Body is not
            [ExpressionStatement
            {
                Expression: AssignmentExpression
                {
                    Operator: "=",
                    Target: var target,
                    Value: IdentifierExpression input,
                },
            }] || input.Identifier.ToString() != "value")
        {
            return false;
        }

        var fieldName = GetPropertyAccessorFieldName(target);
        return fieldName is not null && IsGenericFieldSetterTarget(accessor, fieldName);
    }

    private static bool IsGenericFieldSetterTarget(
        PropertyAccessorDeclaration accessor,
        string fieldName) => accessor.ParentPropertyDeclaration.ParentClassDeclaration?.MemberDeclarations
            .Declarations.OfType<FieldDeclaration>().Any(field =>
                !field.IsStatic && field.Name == fieldName &&
                IsDirectGenericIdentityType(field.Type, accessor.ParentPropertyDeclaration.Type)) == true;

    private static string? GetPropertyAccessorFieldName(ExpressionBase? expression) => expression switch
    {
        IdentifierExpression identifier => identifier.Identifier.ToString(),
        MemberAccessExpression { Target: ThisExpression } memberAccess => memberAccess.MemberName,
        _ => null,
    };

    private static bool IsPropertyGenericTypePattern(
        TypeBase type,
        IReadOnlyCollection<string> genericTypeNames) => type switch
    {
        ConstType constant => IsPropertyGenericTypePattern(constant.UnderlyingType, genericTypeNames),
        GenericType generic => genericTypeNames.Contains(generic.Name),
        ArrayType array => IsPropertyGenericTypePattern(array.ElementType, genericTypeNames),
        NamedType { ClassType: ClassType.Class, TypeArguments.Count: > 0 } named =>
            named.TypeArguments.All(argument =>
                IsPropertyGenericTypePattern(argument, genericTypeNames)),
        _ => false,
    };

    private PropertyReference? ResolvePropertyReference(
        ExpressionBase expression,
        FunctionDeclaration function,
        IReadOnlyList<QualifiedIdentifier> imports,
        LocalScope scope)
    {
        if (expression is IdentifierExpression identifier &&
            identifier.Identifier.Parts.Length == 1 &&
            function.ParentClassDeclaration is { } parentClass)
        {
            if (scope.TryLookup(identifier.Identifier.Parts[0], out _))
            {
                return null;
            }
            var propertyMatch = FindProperty(
                parentClass,
                identifier.Identifier.Parts[0],
                staticOnly: null);
            if (propertyMatch is null)
            {
                return null;
            }
            var property = propertyMatch.Value.Symbol;
            if (!property.IsStatic && function.IsStatic)
            {
                throw new CompilationErrorException(
                    $"Instance property '{identifier.Identifier}' cannot be used from a static function.");
            }
            return new PropertyReference(
                property,
                null,
                !property.IsStatic && IsFunctionThisConst(function),
                property.IsStatic ? 0 : propertyMatch.Value.BaseDepth,
                null);
        }

        if (expression is not MemberAccessExpression memberAccess)
        {
            return null;
        }

        var currentNamespace = GetCurrentNamespace(function);
        var staticTarget = TryResolveTypeExpression(
            memberAccess.Target,
            currentNamespace,
            imports,
            scope);
        if (staticTarget is not null)
        {
            var targetClass = staticTarget.Declaration as ClassDeclaration;
            var property = targetClass is null
                ? null
                : FindProperty(targetClass, memberAccess.MemberName, staticOnly: true);
            return property is null
                ? null
                : new PropertyReference(property.Value.Symbol, null, false, 0, null);
        }

        var receiverType = BindExpression(memberAccess.Target, function, imports, scope);
        if (UnwrapConst(receiverType) is GenericType)
        {
            TryBindConceptPropertyAccess(
                memberAccess,
                receiverType,
                function,
                imports);
            if (memberAccess.TargetProperty is { } conceptProperty)
            {
                return new PropertyReference(
                    conceptProperty,
                    memberAccess.Target,
                    receiverType is ConstType,
                    0,
                    receiverType);
            }
        }
        var receiverClass = GetMemberClassDeclaration(receiverType);
        (PropertySymbol Symbol, int BaseDepth)? instanceProperty = null;
        if (UnwrapConst(receiverType) is ArrayType)
        {
            var matches = GetMemberClassDeclarations(receiverType)
                .Select(owner => (
                    Owner: owner,
                    Match: FindProperty(owner, memberAccess.MemberName, staticOnly: false)))
                .Where(candidate => candidate.Match is not null)
                .GroupBy(candidate => candidate.Match!.Value.Symbol.FullName)
                .Select(group => group.First())
                .ToArray();
            if (matches.Length > 1)
            {
                throw new CompilationErrorException(
                    $"Array property '{memberAccess.MemberName}' is ambiguous for " +
                    $"'{GetTypeName(receiverType)}'.");
            }
            if (matches.Length == 1)
            {
                receiverClass = matches[0].Owner;
                instanceProperty = matches[0].Match;
            }
        }
        else if (receiverClass is not null)
        {
            instanceProperty = FindProperty(
                receiverClass, memberAccess.MemberName, staticOnly: false);
        }
        else if (UnwrapConst(receiverType) is GenericType)
        {
            var matches = GetMemberClassDeclarations(receiverType)
                .Select(owner => (
                    Owner: owner,
                    Match: FindProperty(owner, memberAccess.MemberName, staticOnly: false)))
                .Where(candidate => candidate.Match is not null)
                .ToArray();
            if (matches.Length > 0)
            {
                receiverClass = matches[0].Owner;
                instanceProperty = matches[0].Match;
            }
        }
        else
        {
            instanceProperty = _properties
                .Where(candidate =>
                    IsType(candidate.ContainingType, receiverType) &&
                    candidate.FullName.Parts[^1] == memberAccess.MemberName &&
                    !candidate.IsStatic)
                .Select(candidate => ((PropertySymbol Symbol, int BaseDepth)?)(candidate, 0))
                .SingleOrDefault();
        }
        return instanceProperty is null
            ? null
            : new PropertyReference(
                CloseContainingClassProperty(
                    instanceProperty.Value.Symbol, receiverClass, receiverType),
                memberAccess.Target,
                receiverType is ConstType,
                instanceProperty.Value.BaseDepth,
                receiverType);
    }

    private (int? SlotIndex, string? TemporaryName, NamedType? InterfaceType)
        GetInterfacePropertyDispatch(
        TypeBase? receiverType,
        PropertySymbol property,
        PropertyAccessorSymbol accessor)
    {
        if (receiverType is null || _extensionPropertyOwners.ContainsKey(property))
        {
            return (null, null, null);
        }

        var kind = accessor.Name == "get"
            ? InterfaceDispatchSlotKind.PropertyGetter
            : InterfaceDispatchSlotKind.PropertySetter;
        var interfaceType = UnwrapConst(receiverType) is NamedType
            { ClassType: ClassType.Interface } directInterface
                ? directInterface
                : UnwrapConst(receiverType) is GenericType genericParameter
                    ? GetActiveInterfaceConstraintTypes(genericParameter).FirstOrDefault(type =>
                        GetClassDeclarationIncludingConstructed(type)?.InterfaceDispatchSlots.Any(candidate =>
                            IsInterfacePropertySlot(candidate, kind, property, accessor, type)) == true)
                    : null;
        var receiverInterface = interfaceType is null
            ? null
            : GetClassDeclarationIncludingConstructed(interfaceType);
        if (receiverInterface is null)
        {
            return (null, null, null);
        }
        var slot = receiverInterface.InterfaceDispatchSlots.Single(candidate =>
        {
            return IsInterfacePropertySlot(candidate, kind, property, accessor, interfaceType!);
        });
        return (slot.Index, $"__cx_iface_receiver_{_interfaceReceiverIndex++}", interfaceType);
    }

    private static bool IsInterfacePropertySlot(
        InterfaceDispatchSlot slot,
        InterfaceDispatchSlotKind kind,
        PropertySymbol property,
        PropertyAccessorSymbol accessor,
        NamedType interfaceType)
    {
        if (slot.Kind != kind || slot.ContractAccessor is not { } contract ||
            contract.ParentPropertyDeclaration.FullName != property.FullName ||
            contract.Const != accessor.Const ||
            contract.Parameters.Count != accessor.ParameterTypes.Count)
        {
            return false;
        }

        var interfaceDeclaration = contract.ParentPropertyDeclaration.ParentClassDeclaration;
        var typeArguments = interfaceDeclaration.GenericTypeNames
            .Zip(interfaceType.TypeArguments)
            .ToDictionary(pair => pair.First, pair => pair.Second);
        return contract.Parameters.Zip(accessor.ParameterTypes).All(pair =>
            IsType(
                GenericTypeSubstitution.Substitute(pair.First.ParameterType, typeArguments),
                pair.Second));
    }

    private PropertyAccessorSymbol SelectPropertyAccessor(
        PropertySymbol property,
        string accessorName,
        IReadOnlyList<TypeBase> argumentTypes,
        bool receiverIsConst)
    {
        if (receiverIsConst && accessorName == "set")
        {
            throw new CompilationErrorException(
                $"Property '{property.FullName}' cannot be assigned through a const receiver.");
        }

        var candidates = property.Accessors
            .Where(accessor => accessor.Name == accessorName &&
                ParametersMatch(accessor.ParameterTypes, argumentTypes))
            .ToArray();
        if (accessorName == "get")
        {
            candidates = receiverIsConst
                ? candidates.Where(accessor => accessor.Const).ToArray()
                : candidates.OrderBy(accessor => accessor.Const).ToArray();
        }
        if (candidates.Length == 0)
        {
            var operation = accessorName == "get" ? "readable" : "writable";
            throw new CompilationErrorException(
                $"Property '{property.FullName}' is not {operation} with " +
                $"({string.Join(", ", argumentTypes.Select(GetTypeName))}).");
        }

        return candidates[0];
    }

    private static void EnsurePropertyValueIsSupported(PropertySymbol property)
    {
        if (ContainsUnboundGenericParameter(property.Type))
        {
            throw new CompilationErrorException(
                $"Generic property '{property.FullName}' cannot be used in expressions yet.");
        }
    }

    private TypeBase BindPropertyMutation(
        UnaryExpression unary,
        FunctionDeclaration function,
        IReadOnlyList<QualifiedIdentifier> imports,
        LocalScope scope,
        TypeBase operandType)
    {
        var propertyExpression = unary.Operand is ArrayAccessExpression indexed
            ? indexed.Target
            : unary.Operand;
        var propertyReference = ResolvePropertyReference(
            propertyExpression,
            function,
            imports,
            scope) ?? throw new InternalCompilerException(
                "Bound property mutation has no property reference.");
        var indexExpressions = unary.Operand is ArrayAccessExpression arrayAccess
            ? arrayAccess.Indices
            : [];
        var indexTypes = indexExpressions.Select(index => index.InferredType ??
            throw new InternalCompilerException("Property index is not bound.")).ToArray();
        EnsurePropertyValueIsSupported(propertyReference.Property);
        var getter = SelectPropertyAccessor(
            propertyReference.Property,
            "get",
            indexTypes,
            propertyReference.ReceiverIsConst);
        var setter = SelectPropertyAccessor(
            propertyReference.Property,
            "set",
            indexTypes,
            propertyReference.ReceiverIsConst);

        var operatorSymbol = FindOperator(operandType, unary.Operator, []);
        if (operatorSymbol is null && !IsNumeric(UnwrapConst(operandType)))
        {
            throw new CompilationErrorException(
                $"Operator '{unary.Operator}' cannot be applied to '{GetTypeName(operandType)}'.");
        }

        var dispatchGetter = GetInterfacePropertyDispatch(
            propertyReference.ReceiverType,
            propertyReference.Property,
            getter);
        var dispatchSetter = GetInterfacePropertyDispatch(
            propertyReference.ReceiverType,
            propertyReference.Property,
            setter);
        var temporaryIndex = _propertyAssignmentIndex++;
        if (operatorSymbol is { } op)
        {
            unary.BindOperator(op.Symbol, op.BaseDepth);
        }
        unary.BindPropertyMutation(
            propertyReference.Property,
            getter,
            setter,
            $"__cx_property_mutation_value_{temporaryIndex}",
            unary.Postfix ? $"__cx_property_mutation_old_{temporaryIndex}" : null,
            propertyReference.ReceiverBaseDepth,
            propertyReference.Receiver is null
                ? null
                : $"__cx_property_mutation_receiver_{temporaryIndex}",
            indexExpressions.Select((_, index) =>
                $"__cx_property_mutation_index_{temporaryIndex}_{index}").ToArray(),
            dispatchGetter.SlotIndex,
            dispatchGetter.SlotIndex is null
                ? null
                : $"__cx_property_mutation_iface_get_{temporaryIndex}",
            dispatchGetter.InterfaceType,
            dispatchSetter.SlotIndex,
            dispatchSetter.SlotIndex is null
                ? null
                : $"__cx_property_mutation_iface_set_{temporaryIndex}",
            dispatchSetter.InterfaceType);
        return operandType;
    }

    private TypeBase? TryBindPropertyAssignment(
        AssignmentExpression assignment,
        FunctionDeclaration function,
        IReadOnlyList<QualifiedIdentifier> imports,
        LocalScope scope)
    {
        var propertyExpression = assignment.Target is ArrayAccessExpression arrayAccess
            ? arrayAccess.Target
            : assignment.Target;
        var propertyReference = ResolvePropertyReference(
            propertyExpression,
            function,
            imports,
            scope);
        if (propertyReference is null)
        {
            return null;
        }

        var indexExpressions = assignment.Target is ArrayAccessExpression indexed
            ? indexed.Indices
            : [];
        if (assignment.Target is ArrayAccessExpression &&
            !propertyReference.Property.Accessors.Any(accessor =>
                accessor.ParameterTypes.Count > 0))
        {
            return null;
        }
        EnsurePropertyValueIsSupported(propertyReference.Property);
        var argumentTypes = indexExpressions
            .Select(index => BindExpression(index, function, imports, scope))
            .ToArray();
        if (assignment.Operator != "=")
        {
            var getter = SelectPropertyAccessor(
                propertyReference.Property,
                "get",
                argumentTypes,
                propertyReference.ReceiverIsConst);
            var compoundSetter = SelectPropertyAccessor(
                propertyReference.Property,
                "set",
                argumentTypes,
                propertyReference.ReceiverIsConst);
            var targetType = propertyReference.Property.Type;
            var functionType = GetFunctionSignature(targetType);
            var isDelegateCompound = functionType is not null &&
                assignment.Operator is ("+=" or "-=");
            var compoundValueType = BindExpression(
                assignment.Value,
                function,
                imports,
                scope,
                isDelegateCompound ? targetType : null);
            if (isDelegateCompound)
            {
                if (compoundValueType is not NullType &&
                    (GetFunctionSignature(compoundValueType) is not { } valueFunctionType ||
                    !IsType(functionType!, valueFunctionType) ||
                     UnwrapConst(targetType) is NamedType { IsDelegate: true } targetDelegate &&
                     UnwrapConst(compoundValueType) is NamedType { IsDelegate: true } valueDelegate &&
                     !IsType(targetDelegate, valueDelegate)))
                {
                    throw new CompilationErrorException(
                        $"Compound assignment '{assignment.Operator}' requires a function value " +
                        $"of type '{GetTypeName(targetType)}'.");
                }
                ApplyContextualType(assignment.Value, targetType);
            }
            else
            {
                var overloadedOperator = FindOperator(
                    targetType,
                    assignment.Operator,
                    [compoundValueType]);
                if (overloadedOperator is { } op)
                {
                    if (!IsType(op.Symbol.ReturnType, BuiltInSystemTypes.Void) &&
                        !CanAssign(targetType, op.Symbol.ReturnType))
                    {
                        throw new CompilationErrorException(
                            $"Compound assignment '{assignment.Operator}' changes the property type.");
                    }
                    assignment.BindOperator(op.Symbol, op.BaseDepth);
                }
                else
                {
                    var resultType = GetBinaryResultType(
                        assignment.Operator[..^1],
                        targetType,
                        compoundValueType);
                    if (!CanAssign(targetType, resultType))
                    {
                        throw new CompilationErrorException(
                            $"Compound assignment '{assignment.Operator}' changes the property type.");
                    }
                }
            }

            var getterDispatch = GetInterfacePropertyDispatch(
                propertyReference.ReceiverType,
                propertyReference.Property,
                getter);
            var setterDispatch = GetInterfacePropertyDispatch(
                propertyReference.ReceiverType,
                propertyReference.Property,
                compoundSetter);
            var temporaryIndex = _propertyAssignmentIndex++;
            assignment.BindPropertyCompound(
                propertyReference.Property,
                getter,
                compoundSetter,
                $"__cx_property_value_{temporaryIndex}",
                propertyReference.ReceiverBaseDepth,
                propertyReference.Receiver is null
                    ? null
                    : $"__cx_property_receiver_{temporaryIndex}",
                getterDispatch.SlotIndex,
                getterDispatch.SlotIndex is null
                    ? null
                    : $"__cx_property_iface_get_{temporaryIndex}",
                getterDispatch.InterfaceType,
                setterDispatch.SlotIndex,
                setterDispatch.SlotIndex is null
                    ? null
                    : $"__cx_property_iface_set_{temporaryIndex}",
                setterDispatch.InterfaceType,
                indexExpressions.Select((_, index) =>
                    $"__cx_property_index_{temporaryIndex}_{index}").ToArray());
            return isDelegateCompound ? BuiltInSystemTypes.Void : targetType;
        }

        var setter = SelectPropertyAccessor(
            propertyReference.Property,
            "set",
            argumentTypes,
            propertyReference.ReceiverIsConst);
        var valueType = BindExpression(
            assignment.Value,
            function,
            imports,
            scope,
            propertyReference.Property.Type);
        if (!CanAssign(propertyReference.Property.Type, valueType))
        {
            throw new CompilationErrorException(
                $"Cannot assign '{GetTypeName(valueType)}' to property " +
                $"'{propertyReference.Property.FullName}' of type " +
                $"'{GetTypeName(propertyReference.Property.Type)}'.");
        }

        ApplyContextualType(assignment.Value, propertyReference.Property.Type);
        var dispatch = GetInterfacePropertyDispatch(
            propertyReference.ReceiverType,
            propertyReference.Property,
            setter);
        var setterTemporaryIndex = _propertyAssignmentIndex++;
        assignment.BindPropertySetter(
            propertyReference.Property,
            setter,
            $"__cx_property_value_{setterTemporaryIndex}",
            propertyReference.ReceiverBaseDepth,
            dispatch.SlotIndex,
            dispatch.TemporaryName,
            dispatch.InterfaceType,
            propertyReference.Receiver is null
                ? null
                : $"__cx_property_receiver_{setterTemporaryIndex}",
            indexExpressions.Select((_, index) =>
                $"__cx_property_index_{setterTemporaryIndex}_{index}").ToArray());
        return propertyReference.Property.Type;
    }

    private static PropertySymbol CloseContainingClassProperty(
        PropertySymbol property,
        ClassDeclaration? owner,
        TypeBase receiverType)
    {
        receiverType = UnwrapConst(receiverType);
        if (receiverType is not NamedType { ConstructedIdentity: not null } closedType ||
            owner is null || owner.GenericTypeNames.Length != closedType.TypeArguments.Count ||
            closedType.ResolvedTypeFullName !=
                new QualifiedIdentifier(property.ModuleName, owner.FullName))
        {
            return property;
        }
        var arguments = owner.GenericTypeNames.Zip(closedType.TypeArguments)
            .ToDictionary(pair => pair.First, pair => pair.Second);
        return new PropertySymbol(
            property.ModuleName,
            property.FullName,
            closedType,
            property.ContainingClassType,
            GenericTypeSubstitution.Substitute(property.Type, arguments),
            property.IsStatic,
            property.Accessors.Select(accessor => accessor with
            {
                ParameterTypes = accessor.ParameterTypes.Select(parameter =>
                    GenericTypeSubstitution.Substitute(parameter, arguments)).ToArray()
            }).ToArray());
    }

    private TypeBase CloseContainingClassFieldType(
        FieldSymbol field,
        ClassDeclaration? owner,
        TypeBase receiverType)
    {
        receiverType = UnwrapConst(receiverType);
        if (receiverType is not NamedType { ConstructedIdentity: not null } closedType ||
            owner is null || owner.GenericTypeNames.Length != closedType.TypeArguments.Count ||
            closedType.ResolvedTypeFullName !=
                new QualifiedIdentifier(field.ModuleName, owner.FullName))
        {
            return field.Declaration.Type;
        }
        var arguments = owner.GenericTypeNames.Zip(closedType.TypeArguments)
            .ToDictionary(pair => pair.First, pair => pair.Second);
        return CloseConstructedTypes(
            GenericTypeSubstitution.Substitute(field.Declaration.Type, arguments));
    }
}

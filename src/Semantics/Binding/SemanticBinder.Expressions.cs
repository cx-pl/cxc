using CxCompiler.Model.Common;
using CxCompiler.Model.Errors;
using CxCompiler.Model.Expressions;
using CxCompiler.Model.Types;
using CxCompiler.Model.Types.BuiltInTypes;

namespace CxCompiler.Semantics;

public sealed partial class SemanticBinder
{

    private TypeBase BindSwitchExpression(
        SwitchExpression expression,
        FunctionDeclaration function,
        IReadOnlyList<QualifiedIdentifier> imports,
        LocalScope scope,
        TypeBase? expectedType)
    {
        var selectorType = BindExpression(expression.Selector, function, imports, scope);
        if (!IsSwitchType(selectorType))
        {
            throw new CompilationErrorException(
                $"Switch expression type '{GetTypeName(selectorType)}' is not supported.");
        }

        var hasDefault = false;
        var caseValues = new HashSet<string>(StringComparer.Ordinal);
        var valueTypes = new List<TypeBase>(expression.Arms.Count);
        for (var index = 0; index < expression.Arms.Count; index++)
        {
            var arm = expression.Arms[index];
            if (arm.IsDefault)
            {
                if (hasDefault || arm.Filter is not null || index != expression.Arms.Count - 1)
                {
                    throw new CompilationErrorException(
                        "A switch expression must have one unfiltered discard arm, and it must be last.");
                }
                hasDefault = true;
            }
            else
            {
                var labelType = BindExpression(arm.Label!, function, imports, scope);
                var caseValue = arm.Label switch
                {
                    LiteralExpression literal => literal.SourceText,
                    MemberAccessExpression { TargetEnumMember: not null } memberAccess =>
                        memberAccess.TargetEnumMember.Declaration.FullName.ToString(),
                    _ => throw new CompilationErrorException(
                        "Switch expression labels must be literals, enum members, or '_'."),
                };
                var isNullStringLabel = arm.Label is LiteralExpression { SourceText: "null" } &&
                    IsStringSwitchType(selectorType);
                if (!isNullStringLabel && !IsType(selectorType, labelType))
                {
                    throw new CompilationErrorException(
                        $"Switch case type '{GetTypeName(labelType)}' does not match " +
                        $"selector type '{GetTypeName(selectorType)}'.");
                }
                var identity = $"{GetTypeName(labelType)}:{caseValue}";
                if (arm.Filter is null && !caseValues.Add(identity))
                {
                    throw new CompilationErrorException(
                        $"Switch case label '{caseValue}' is duplicated.");
                }
            }

            if (arm.Filter is not null)
            {
                var filterType = BindExpression(arm.Filter, function, imports, scope);
                if (!IsType(filterType, BuiltInSystemTypes.Bool))
                {
                    throw new CompilationErrorException(
                        $"Switch case filter must have type 'System.Bool', but found '{GetTypeName(filterType)}'.");
                }
            }
            valueTypes.Add(BindExpression(
                arm.Value,
                function,
                imports,
                scope,
                arm.Value is LambdaExpression ? expectedType : null));
        }

        if (!hasDefault)
        {
            throw new CompilationErrorException(
                "A switch expression must end with an unfiltered discard arm '_'.");
        }

        TypeBase? resultType = expectedType;
        if (resultType is not null)
        {
            foreach (var (arm, valueType) in expression.Arms.Zip(valueTypes))
            {
                if (!CanAssign(resultType, valueType))
                {
                    throw new CompilationErrorException(
                        $"Switch expression arms have incompatible types: arm type " +
                        $"'{GetTypeName(valueType)}' cannot be assigned to expected type " +
                        $"'{GetTypeName(resultType)}'.");
                }
                ApplyContextualType(arm.Value, resultType);
            }
        }
        else
        {
            foreach (var valueType in valueTypes)
            {
                if (resultType is null || resultType is NullType && IsNullAssignable(valueType))
                {
                    resultType = valueType;
                }
                else if (valueType is NullType && IsNullAssignable(resultType))
                {
                    continue;
                }
                else if (CanAssign(resultType, valueType))
                {
                    continue;
                }
                else if (CanAssign(valueType, resultType))
                {
                    resultType = valueType;
                }
                else
                {
                    throw new CompilationErrorException(
                        $"Switch expression arms have incompatible types '{GetTypeName(resultType)}' " +
                        $"and '{GetTypeName(valueType)}'.");
                }
            }
        }

        if (resultType is null or NullType)
        {
            throw new CompilationErrorException(
                "A switch expression must produce a value with a determined type.");
        }
        foreach (var arm in expression.Arms)
        {
            var armType = arm.Value.InferredType!;
            if (!CanAssign(resultType, armType))
            {
                throw new CompilationErrorException(
                    $"Switch expression arm type '{GetTypeName(armType)}' cannot convert to " +
                    $"'{GetTypeName(resultType)}'.");
            }
            ApplyContextualType(arm.Value, resultType);
        }

        expression.BindTemporary($"__cx_switch_value_{++_switchTemporaryIndex}");
        return resultType;
    }

    private TypeBase BindExpression(
        ExpressionBase expression,
        FunctionDeclaration function,
        IReadOnlyList<QualifiedIdentifier> imports,
        LocalScope scope,
        TypeBase? expectedType = null)
    {
        try
        {
            return BindExpressionCore(expression, function, imports, scope, expectedType);
        }
        catch (CompilationErrorException exception) when (
            exception.SourceSpan is null && expression.SourceSpan is not null)
        {
            throw exception.WithSourceSpan(expression.SourceSpan);
        }
    }

    private TypeBase BindExpressionCore(
        ExpressionBase expression,
        FunctionDeclaration function,
        IReadOnlyList<QualifiedIdentifier> imports,
        LocalScope scope,
        TypeBase? expectedType)
    {
        TypeBase type;
        switch (expression)
        {
            case LiteralExpression { SourceText: "default" }:
                type = expectedType ?? throw new CompilationErrorException(
                    "The default literal requires a target type.",
                    expression.SourceSpan);
                break;

            case LiteralExpression literal:
                type = BindLiteral(literal);
                break;

            case LambdaExpression lambda:
                type = BindLambda(lambda, function, imports, scope, expectedType);
                break;

            case IdentifierExpression identifier:
                type = BindIdentifier(identifier, function, imports, scope, expectedType);
                break;

            case ThisExpression:
                type = BindThis((ThisExpression)expression, function);
                break;

            case MemberAccessExpression memberAccess:
                type = BindMemberAccess(memberAccess, function, imports, scope, expectedType);
                break;

            case InvocationExpression invocation:
                type = BindInvocation(invocation, function, imports, scope);
                break;

            case BinaryExpression binary:
                type = BindBinary(binary, function, imports, scope, expectedType);
                break;

            case CastExpression cast:
                type = BindCast(cast, function, imports, scope);
                break;

            case TypeTestExpression typeTest:
                type = BindTypeTest(typeTest, function, imports, scope);
                break;

            case ConditionalExpression conditional:
                type = BindConditional(conditional, function, imports, scope, expectedType);
                break;

            case SwitchExpression switchExpression:
                type = BindSwitchExpression(
                    switchExpression,
                    function,
                    imports,
                    scope,
                    expectedType);
                break;

            case NullCoalescingExpression coalescing:
                type = BindNullCoalescing(coalescing, function, imports, scope, expectedType);
                break;

            case UnaryExpression unary:
                type = BindUnary(unary, function, imports, scope, expectedType);
                break;

            case AssignmentExpression assignment:
                type = BindAssignment(assignment, function, imports, scope);
                break;

            case ArrayCreationExpression arrayCreation:
                type = BindArrayCreation(arrayCreation, function, imports, scope);
                break;

            case ArrayAccessExpression arrayAccess:
                type = BindArrayAccess(arrayAccess, function, imports, scope);
                break;

            case ObjectCreationExpression objectCreation:
                type = BindObjectCreation(objectCreation, function, imports, scope);
                break;

            default:
                throw new CompilationErrorException(
                    $"Cannot determine the type of expression '{expression.GetType().Name}'.");
        }

        type = ArrayType.Canonicalize(type);
        expression.SetInferredType(type);
        return type;
    }

    private TypeBase BindBinary(
        BinaryExpression binary,
        FunctionDeclaration function,
        IReadOnlyList<QualifiedIdentifier> imports,
        LocalScope scope,
        TypeBase? expectedType)
    {
        var expectedFunctionType = expectedType is null
            ? null
            : GetFunctionSignature(expectedType);
        var leftType = BindExpression(binary.Left, function, imports, scope, expectedFunctionType);
        var leftFunctionType = GetFunctionSignature(leftType);
        TypeBase? expectedRightType = leftFunctionType ?? expectedFunctionType;
        if (binary.Operator == "+" && IsType(UnwrapConst(leftType), BuiltInSystemTypes.String))
        {
            expectedRightType = BuiltInSystemTypes.String;
        }
        var rightType = BindExpression(
            binary.Right,
            function,
            imports,
            scope,
            expectedRightType);
        if (binary.Operator == "+" && leftType is NullType &&
            IsType(UnwrapConst(rightType), BuiltInSystemTypes.String))
        {
            ApplyContextualType(binary.Left, BuiltInSystemTypes.String);
            leftType = BuiltInSystemTypes.String;
        }
        if (binary.Operator == "+" && rightType is NullType &&
            IsType(UnwrapConst(leftType), BuiltInSystemTypes.String))
        {
            ApplyContextualType(binary.Right, BuiltInSystemTypes.String);
            rightType = BuiltInSystemTypes.String;
        }
        if (binary.Operator is "==" or "!=" or "<" or "<=" or ">" or ">=")
        {
            if (IsType(UnwrapConst(leftType), BuiltInSystemTypes.UInt) &&
                TryApplyUnsignedLiteralType(binary.Right))
            {
                rightType = BuiltInSystemTypes.UInt;
            }
            else if (IsType(UnwrapConst(rightType), BuiltInSystemTypes.UInt) &&
                TryApplyUnsignedLiteralType(binary.Left))
            {
                leftType = BuiltInSystemTypes.UInt;
            }
        }
        var rightFunctionType = GetFunctionSignature(rightType);
        if (binary.Operator is "+" or "-" &&
            AreCompatibleDelegateValues(leftType, rightType))
        {
            return leftFunctionType is null ? rightType : leftType;
        }
        if ((binary.Operator is "==" or "!=") &&
                IsConstrainedGenericReferenceType(leftType, function) && rightType is NullType ||
            (binary.Operator is "==" or "!=") &&
                leftType is NullType && IsConstrainedGenericReferenceType(rightType, function))
        {
            return BuiltInSystemTypes.Bool;
        }
        if (IsRequiredConceptOperator(
            binary.Operator, leftType, rightType, function, imports) is { } conceptReturnType)
        {
            return conceptReturnType;
        }
        if (FindOperator(leftType, binary.Operator, [rightType]) is { } op)
        {
            var receiverType = UnwrapConst(leftType);
            var temporary = GetMemberClassDeclaration(receiverType)?.ClassType == ClassType.Struct
                ? $"__cx_operator_receiver_{++_operatorTemporaryIndex}"
                : null;
            binary.BindOperator(op.Symbol, op.BaseDepth, temporary);
            return op.Symbol.ReturnType;
        }
        return GetBinaryResultType(binary.Operator, leftType, rightType);
    }

    private static bool TryApplyUnsignedLiteralType(ExpressionBase expression)
    {
        if (expression is not LiteralExpression literal ||
            literal.InferredType is not IntType ||
            !uint.TryParse(literal.SourceText, out _))
        {
            return false;
        }

        literal.SetInferredType(BuiltInSystemTypes.UInt);
        return true;
    }

    private TypeBase? IsRequiredConceptOperator(
        string operatorToken,
        TypeBase leftType,
        TypeBase rightType,
        FunctionDeclaration function,
        IReadOnlyList<QualifiedIdentifier> imports)
    {
        if (UnwrapConst(leftType) is not GenericType leftGeneric ||
            UnwrapConst(rightType) is not GenericType rightGeneric ||
            leftGeneric.Name != rightGeneric.Name)
        {
            return null;
        }

        foreach (var constraint in GetAvailableConceptConstraints(
            function.ParentClassDeclaration, function).Where(constraint =>
                constraint.ParameterName == leftGeneric.Name))
        {
            var requirement = constraint.Declaration.OperatorRequirements
                .SingleOrDefault(requirement =>
                    requirement.OperatorToken == operatorToken);
            if (requirement is not null && requirement.Parameters.Count == 1)
            {
                return SubstituteConceptSelfType(requirement.ReturnType, leftType);
            }
        }
        return null;
    }

    private TypeBase? IsRequiredConceptUnaryOperator(
        string operatorToken,
        TypeBase operandType,
        FunctionDeclaration function,
        IReadOnlyList<QualifiedIdentifier> imports)
    {
        if (UnwrapConst(operandType) is not GenericType generic ||
            !IsGenericTypeParameterInScope(function, generic.Name))
        {
            return null;
        }

        foreach (var constraint in GetAvailableConceptConstraints(
            function.ParentClassDeclaration, function).Where(constraint =>
                constraint.ParameterName == generic.Name))
        {
            var requirement = constraint.Declaration.OperatorRequirements
                .FirstOrDefault(requirement =>
                    requirement.OperatorToken == operatorToken &&
                    requirement.Parameters.Count == 0);
            if (requirement is not null)
            {
                return SubstituteConceptSelfType(requirement.ReturnType, operandType);
            }
        }
        return null;
    }

    private TypeBase BindCast(
        CastExpression cast,
        FunctionDeclaration function,
        IReadOnlyList<QualifiedIdentifier> imports,
        LocalScope scope)
    {
        var currentNamespace = GetCurrentNamespace(function);
        ResolveTypeReference(cast.TargetType, currentNamespace, imports);
        var sourceType = BindExpression(cast.Operand, function, imports, scope);
        var unwrappedSource = UnwrapConst(sourceType);
        var unwrappedTarget = UnwrapConst(cast.TargetType);
        var boxesToImplementedInterface =
            unwrappedSource is NamedType { ClassType: ClassType.Struct } &&
            GetClassDeclarationIncludingConstructed(unwrappedTarget)?.ClassType ==
                ClassType.Interface &&
            CanAssign(unwrappedTarget, unwrappedSource);
        if (!(unwrappedSource is ObjectType && IsBoxableValueType(unwrappedTarget)) &&
            !boxesToImplementedInterface &&
            !IsPossibleExplicitReferenceConversion(sourceType, cast.TargetType))
        {
            throw new CompilationErrorException(
                $"Cannot explicitly convert '{GetTypeName(sourceType)}' to " +
                $"'{GetTypeName(cast.TargetType)}'.");
        }
        return cast.TargetType;
    }

    private TypeBase BindTypeTest(
        TypeTestExpression typeTest,
        FunctionDeclaration function,
        IReadOnlyList<QualifiedIdentifier> imports,
        LocalScope scope)
    {
        var currentNamespace = GetCurrentNamespace(function);
        ResolveTypeReference(typeTest.TargetType, currentNamespace, imports);
        var sourceType = BindExpression(typeTest.Operand, function, imports, scope);
        if (!IsPossibleExplicitReferenceConversion(sourceType, typeTest.TargetType))
        {
            throw new CompilationErrorException(
                $"Type test from '{GetTypeName(sourceType)}' to " +
                $"'{GetTypeName(typeTest.TargetType)}' can never succeed.");
        }
        return BuiltInSystemTypes.Bool;
    }

    private bool IsPossibleExplicitReferenceConversion(TypeBase sourceType, TypeBase targetType)
    {
        sourceType = UnwrapConst(sourceType);
        targetType = UnwrapConst(targetType);
        if (ArrayType.TryGetElementType(sourceType, out _) && targetType is ObjectType)
        {
            return true;
        }
        if (sourceType is NullType)
        {
            return GetClassDeclarationIncludingConstructed(targetType)?.ClassType is
                ClassType.Class or ClassType.Interface;
        }

        var source = GetClassDeclarationIncludingConstructed(sourceType);
        var target = GetClassDeclarationIncludingConstructed(targetType);
        if (source?.ClassType is not (ClassType.Class or ClassType.Interface) ||
            target?.ClassType is not (ClassType.Class or ClassType.Interface))
        {
            return false;
        }
        if (IsType(sourceType, targetType) || CanAssign(targetType, sourceType) ||
            CanAssign(sourceType, targetType))
        {
            return true;
        }
        if (source.ClassType == ClassType.Interface && target.ClassType == ClassType.Interface)
        {
            return true;
        }
        if (source.ClassType == ClassType.Class && target.ClassType == ClassType.Interface)
        {
            return !source.IsFinal;
        }
        if (source.ClassType == ClassType.Interface && target.ClassType == ClassType.Class)
        {
            return !target.IsFinal;
        }
        return false;
    }

    private TypeBase BindConditional(
        ConditionalExpression conditional,
        FunctionDeclaration function,
        IReadOnlyList<QualifiedIdentifier> imports,
        LocalScope scope,
        TypeBase? expectedType)
    {
        var conditionType = BindExpression(conditional.Condition, function, imports, scope);
        if (!IsType(conditionType, BuiltInSystemTypes.Bool))
        {
            throw new CompilationErrorException(
                $"Conditional expression requires a bool condition, but found '{GetTypeName(conditionType)}'.");
        }

        var whenTrueType = BindExpression(
            conditional.WhenTrue,
            function,
            imports,
            scope,
            conditional.WhenTrue is LambdaExpression or
                LiteralExpression { SourceText: "default" } ? expectedType : null);
        var whenFalseType = BindExpression(
            conditional.WhenFalse,
            function,
            imports,
            scope,
            conditional.WhenFalse is LambdaExpression or
                LiteralExpression { SourceText: "default" } ? expectedType : null);
        if (whenTrueType is NullType && IsNullAssignable(whenFalseType))
        {
            ApplyContextualType(conditional.WhenTrue, whenFalseType);
            return whenFalseType;
        }
        if (whenFalseType is NullType && IsNullAssignable(whenTrueType))
        {
            ApplyContextualType(conditional.WhenFalse, whenTrueType);
            return whenTrueType;
        }
        if (!IsType(whenTrueType, whenFalseType))
        {
            if (CanAssign(whenTrueType, whenFalseType))
            {
                ApplyContextualType(conditional.WhenFalse, whenTrueType);
                return whenTrueType;
            }
            if (CanAssign(whenFalseType, whenTrueType))
            {
                ApplyContextualType(conditional.WhenTrue, whenFalseType);
                return whenFalseType;
            }
            throw new CompilationErrorException(
                $"Conditional expression branches must have the same type, but found " +
                $"'{GetTypeName(whenTrueType)}' and '{GetTypeName(whenFalseType)}'.");
        }

        return whenTrueType;
    }

    private TypeBase BindNullCoalescing(
        NullCoalescingExpression coalescing,
        FunctionDeclaration function,
        IReadOnlyList<QualifiedIdentifier> imports,
        LocalScope scope,
        TypeBase? expectedType)
    {
        var leftType = BindExpression(coalescing.Left, function, imports, scope);

        if (coalescing.Left is not IdentifierExpression and
            not LiteralExpression { SourceText: "null" })
        {
            throw new CompilationErrorException(
                "The left operand of '??' must currently be a local or parameter so it is evaluated only once.");
        }

        if (leftType is NullType)
        {
            var nullFallbackType = BindExpression(
                coalescing.Right,
                function,
                imports,
                scope,
                coalescing.Right is LambdaExpression ? expectedType : null);
            if (!IsNullAssignable(nullFallbackType))
            {
                throw new CompilationErrorException(
                    $"Operator '??' cannot use '{GetTypeName(nullFallbackType)}' as a null fallback.");
            }
            ApplyContextualType(coalescing.Left, nullFallbackType);
            return nullFallbackType;
        }

        var effectiveLeftType = UnwrapConst(leftType);
        var resultType = effectiveLeftType is NullableType nullableType
            ? nullableType.UnderlyingType
            : leftType;
        if (!IsNullAssignable(leftType) &&
            !IsReferenceConstrainedGenericType(leftType, function))
        {
            throw new CompilationErrorException(
                $"Operator '??' requires a nullable or reference left operand, but found '{GetTypeName(leftType)}'.");
        }
        var rightType = BindExpression(
            coalescing.Right,
            function,
            imports,
            scope,
            coalescing.Right is LambdaExpression ? resultType : null);
        if (!CanAssign(resultType, rightType))
        {
            throw new CompilationErrorException(
                $"Operator '??' fallback has type '{GetTypeName(rightType)}', " +
                $"but '{GetTypeName(resultType)}' is required.");
        }

        ApplyContextualType(coalescing.Right, resultType);
        return resultType;
    }

    private TypeBase BindUnary(
        UnaryExpression unary,
        FunctionDeclaration function,
        IReadOnlyList<QualifiedIdentifier> imports,
        LocalScope scope,
        TypeBase? expectedType)
    {
        var operandType = BindExpression(unary.Operand, function, imports, scope);
        var valueType = UnwrapConst(operandType);
        if (unary.Operator is "++" or "--")
        {
            if (unary.Operand is not IdentifierExpression and
                not ArrayAccessExpression and
                not MemberAccessExpression)
            {
                throw new CompilationErrorException(
                    $"Operator '{unary.Operator}' requires an assignable value.");
            }
            if (operandType is ConstType)
            {
                throw new CompilationErrorException(
                    $"Operator '{unary.Operator}' requires a writable value.");
            }
            if (unary.Operand is IdentifierExpression { TargetProperty: not null } or
                MemberAccessExpression { TargetProperty: not null } or
                ArrayAccessExpression { TargetProperty: not null })
            {
                return BindPropertyMutation(unary, function, imports, scope, operandType);
            }
            ValidateWritableField(unary.Operand);
        }

        if (IsRequiredConceptUnaryOperator(
            unary.Operator, operandType, function, imports) is { } conceptReturnType)
        {
            return conceptReturnType;
        }

        if (FindOperator(
                operandType,
                unary.Operator,
                [],
                unary.Operator is "++" or "--" ? null : expectedType) is { } op)
        {
            var isMutating = unary.Operator is "++" or "--";
            var operandClass = GetMemberClassDeclaration(valueType)?.ClassType;
            var temporary = isMutating
                ? $"__cx_operator_receiver_{++_operatorTemporaryIndex}"
                : operandClass == ClassType.Struct
                    ? $"__cx_operator_value_{++_operatorTemporaryIndex}"
                    : null;
            var isValueExtensionOperator = op.Symbol.Declaration?.IsExtension == true;
            var oldValueTemporary = isMutating && unary.Postfix &&
                (operandClass == ClassType.Struct || isValueExtensionOperator)
                    ? $"__cx_operator_old_{++_operatorTemporaryIndex}"
                    : null;
            unary.BindOperator(
                op.Symbol,
                op.BaseDepth,
                temporary,
                oldValueTemporary);
            return isMutating ? operandType : op.Symbol.ReturnType;
        }

        return unary.Operator switch
        {
            "+" or "-" when IsNumeric(valueType) => valueType,
            "~" when IsInteger(valueType) => valueType,
            "!" when IsType(valueType, BuiltInSystemTypes.Bool) => BuiltInSystemTypes.Bool,
            "++" or "--" when IsNumeric(valueType) => valueType,
            _ => throw new CompilationErrorException(
                $"Operator '{unary.Operator}' cannot be applied to '{GetTypeName(operandType)}'."),
        };
    }

    private TypeBase BindAssignment(
        AssignmentExpression assignment,
        FunctionDeclaration function,
        IReadOnlyList<QualifiedIdentifier> imports,
        LocalScope scope)
    {
        var propertyAssignmentType = TryBindPropertyAssignment(
            assignment,
            function,
            imports,
            scope);
        if (propertyAssignmentType is not null)
        {
            return propertyAssignmentType;
        }

        if (assignment.Target is not IdentifierExpression and
            not ArrayAccessExpression and
            not MemberAccessExpression)
        {
            throw new CompilationErrorException(
                $"Expression '{assignment.Target.GetType().Name}' cannot be assigned to.");
        }

        var targetType = BindExpression(assignment.Target, function, imports, scope);
        ValidateWritableField(assignment.Target);
        var functionType = GetFunctionSignature(targetType);
        var isDelegateCompound = functionType is not null &&
            assignment.Operator is "+=" or "-=";
        var valueType = BindExpression(
            assignment.Value,
            function,
            imports,
            scope,
            assignment.Operator == "=" || isDelegateCompound ? targetType : null);

        if (assignment.Operator == "=")
        {
            if (!CanAssign(targetType, valueType))
            {
                throw new CompilationErrorException(
                    $"Cannot assign '{GetTypeName(valueType)}' to '{GetTypeName(targetType)}'.");
            }
            ApplyContextualType(assignment.Value, targetType);
        }
        else
        {
            if (isDelegateCompound)
            {
                if (valueType is not NullType &&
                    (GetFunctionSignature(valueType) is not { } valueFunctionType ||
                     !IsType(functionType!, valueFunctionType) ||
                     UnwrapConst(targetType) is NamedType { IsDelegate: true } targetDelegate &&
                     UnwrapConst(valueType) is NamedType { IsDelegate: true } valueDelegate &&
                     !IsType(targetDelegate, valueDelegate)))
                {
                    throw new CompilationErrorException(
                        $"Compound assignment '{assignment.Operator}' requires a function value " +
                        $"of type '{GetTypeName(targetType)}'.");
                }
                assignment.BindDelegateCompound(
                    $"__cx_delegate_assignment_{++_operatorTemporaryIndex}");
                return BuiltInSystemTypes.Void;
            }

            var operatorToken = assignment.Operator;
            if (FindOperator(targetType, operatorToken, [valueType]) is { } op)
            {
                var extensionTemporary = op.Symbol.Declaration?.IsExtension == true
                    ? $"__cx_extension_assignment_{++_operatorTemporaryIndex}"
                    : null;
                assignment.BindOperator(op.Symbol, op.BaseDepth, extensionTemporary);
                return BuiltInSystemTypes.Void;
            }

            var resultType = GetBinaryResultType(operatorToken[..^1], targetType, valueType);
            if (!IsType(resultType, targetType))
            {
                throw new CompilationErrorException(
                    $"Compound assignment '{assignment.Operator}' changes the target type.");
            }
        }

        return targetType;
    }

    private static void ValidateWritableField(ExpressionBase expression)
    {
        if (expression is MemberAccessExpression { TargetEnumMember: not null } enumMember)
        {
            throw new CompilationErrorException(
                $"Enum member '{enumMember.TargetEnumMember.Declaration.FullName}' cannot be assigned to.");
        }

        var field = expression switch
        {
            IdentifierExpression identifier => identifier.TargetField,
            MemberAccessExpression memberAccess => memberAccess.TargetField,
            _ => null,
        };
        if (field?.Declaration.Type is ConstType)
        {
            throw new CompilationErrorException(
                $"Const field '{field.Declaration.FullName}' cannot be assigned to.");
        }
    }

    private TypeBase BindArrayCreation(
        ArrayCreationExpression expression,
        FunctionDeclaration function,
        IReadOnlyList<QualifiedIdentifier> imports,
        LocalScope scope)
    {
        ResolveTypeReference(
            expression.ElementType,
            GetCurrentNamespace(function),
            imports);
        if (expression.ElementType is VoidType or AutoType)
        {
            throw new CompilationErrorException(
                $"Arrays of type '{GetTypeName(expression.ElementType)}' cannot be created.");
        }

        var lengthType = BindExpression(expression.Length, function, imports, scope);
        if (!IsInteger(lengthType))
        {
            throw new CompilationErrorException(
                $"Array length must be an integer, but found '{GetTypeName(lengthType)}'.");
        }
        if (expression.Length is UnaryExpression
            {
                Operator: "-",
                Operand: LiteralExpression
            })
        {
            throw new CompilationErrorException("Array length cannot be negative.");
        }

        return new ArrayType(expression.ElementType);
    }

    private TypeBase BindArrayAccess(
        ArrayAccessExpression expression,
        FunctionDeclaration function,
        IReadOnlyList<QualifiedIdentifier> imports,
        LocalScope scope)
    {
        var propertyReference = ResolvePropertyReference(
            expression.Target,
            function,
            imports,
            scope);
        if (propertyReference is not null &&
            propertyReference.Property.Accessors.Any(accessor =>
                accessor.Name == "get" && accessor.ParameterTypes.Count > 0))
        {
            EnsurePropertyValueIsSupported(propertyReference.Property);
            var argumentTypes = expression.Indices
                .Select(index => BindExpression(index, function, imports, scope))
                .ToArray();
            var getter = SelectPropertyAccessor(
                propertyReference.Property,
                "get",
                argumentTypes,
                propertyReference.ReceiverIsConst);
            var dispatch = GetInterfacePropertyDispatch(
                propertyReference.ReceiverType,
                propertyReference.Property,
                getter);
            expression.BindProperty(
                propertyReference.Property,
                getter,
                propertyReference.ReceiverBaseDepth,
                dispatch.SlotIndex,
                dispatch.TemporaryName,
                dispatch.InterfaceType);
            return propertyReference.Property.Type;
        }

        var targetType = UnwrapConst(
            BindExpression(expression.Target, function, imports, scope));
        if (!ArrayType.TryGetElementType(targetType, out var elementType))
        {
            throw new CompilationErrorException(
                $"Cannot index expression of type '{GetTypeName(targetType)}'.");
        }
        if (expression.Indices.Count != 1)
        {
            throw new CompilationErrorException("Only one-dimensional array indexing is supported.");
        }

        var indexType = BindExpression(expression.Indices[0], function, imports, scope);
        if (!IsInteger(indexType))
        {
            throw new CompilationErrorException(
                $"Array index must be an integer, but found '{GetTypeName(indexType)}'.");
        }

        return elementType;
    }

    private static TypeBase BindLiteral(LiteralExpression literal)
    {
        if (literal.IsString)
        {
            return BuiltInSystemTypes.String;
        }
        if (literal.SourceText.StartsWith('\''))
        {
            return BuiltInSystemTypes.Char;
        }
        if (literal.SourceText is "true" or "false")
        {
            return BuiltInSystemTypes.Bool;
        }
        if (literal.SourceText.Contains('.') ||
            literal.SourceText.Contains('e', StringComparison.OrdinalIgnoreCase))
        {
            return literal.SourceText.EndsWith("f", StringComparison.OrdinalIgnoreCase)
                ? BuiltInSystemTypes.Float
                : BuiltInSystemTypes.Double;
        }
        if (literal.SourceText == "null")
        {
            return BuiltInSystemTypes.Null;
        }

        var suffix = literal.SourceText.TrimEnd('l', 'L', 'u', 'U');
        var isUnsigned = literal.SourceText.EndsWith("u", StringComparison.OrdinalIgnoreCase) ||
            literal.SourceText.EndsWith("ul", StringComparison.OrdinalIgnoreCase) ||
            literal.SourceText.EndsWith("lu", StringComparison.OrdinalIgnoreCase);
        var isLong = suffix.Length != literal.SourceText.Length &&
            literal.SourceText.Contains('l', StringComparison.OrdinalIgnoreCase);
        return (isUnsigned, isLong) switch
        {
            (true, true) => BuiltInSystemTypes.ULong,
            (true, false) => BuiltInSystemTypes.UInt,
            (false, true) => BuiltInSystemTypes.Long,
            _ => BuiltInSystemTypes.Int,
        };
    }

    private static TypeBase GetBinaryResultType(
        string @operator,
        TypeBase leftType,
        TypeBase rightType)
    {
        if (@operator is "==" or "!=" &&
            (leftType is NullType && IsNullAssignable(rightType) ||
             rightType is NullType && IsNullAssignable(leftType)))
        {
            return BuiltInSystemTypes.Bool;
        }

        var operandType = GetCommonNumericType(leftType, rightType);
        if (!IsType(leftType, rightType) && operandType is null)
        {
            throw new CompilationErrorException(
                $"Operator '{@operator}' cannot combine '{GetTypeName(leftType)}' and '{GetTypeName(rightType)}'.");
        }

        operandType ??= leftType;

        return @operator switch
        {
            "+" or "-" or "*" or "/" or "%" when IsNumeric(operandType) => operandType,
            "<<" or ">>" when IsInteger(operandType) => operandType,
            "&" or "|" or "^" when IsInteger(operandType) || IsType(operandType, BuiltInSystemTypes.Bool) => operandType,
            "&&" or "||" when IsType(operandType, BuiltInSystemTypes.Bool) => BuiltInSystemTypes.Bool,
            "==" or "!=" => BuiltInSystemTypes.Bool,
            "<" or "<=" or ">" or ">=" when IsNumeric(operandType) => BuiltInSystemTypes.Bool,
            _ => throw new CompilationErrorException(
                $"Operator '{@operator}' cannot be applied to '{GetTypeName(operandType)}'."),
        };
    }

    private static TypeBase? GetCommonNumericType(TypeBase leftType, TypeBase rightType)
    {
        if (!IsNumeric(leftType) || !IsNumeric(rightType))
        {
            return null;
        }
        if (IsImplicitNumericConversion(rightType, leftType))
        {
            return leftType;
        }
        if (IsImplicitNumericConversion(leftType, rightType))
        {
            return rightType;
        }

        return null;
    }

    private static TypeBase ResolveValue(
        QualifiedIdentifier identifier,
        LocalScope scope)
    {
        if (identifier.Parts.Length != 1 ||
            !scope.TryLookup(identifier.Parts[0], out var type))
        {
            throw new CompilationErrorException($"Cannot resolve value '{identifier}'.");
        }

        return type;
    }

    private static bool IsNumeric(TypeBase type)
    {
        return
            IsType(type, BuiltInSystemTypes.Char) ||
            IsInteger(type) ||
            IsType(type, BuiltInSystemTypes.Float) ||
            IsType(type, BuiltInSystemTypes.Double);
    }

    private static bool IsInteger(TypeBase type)
    {
        return UnwrapConst(type) is IntTypeBase;
    }

    private static bool IsSwitchType(TypeBase type)
    {
        return IsInteger(type) ||
            IsType(type, BuiltInSystemTypes.Bool) ||
            IsType(type, BuiltInSystemTypes.Char) ||
            IsStringSwitchType(type) ||
            UnwrapConst(type) is NamedType { ClassType: ClassType.Enum };
    }

    private static bool IsStringSwitchType(TypeBase type) => UnwrapConst(type) is StringType;
}

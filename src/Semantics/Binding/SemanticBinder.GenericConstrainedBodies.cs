using CxCompiler.Model.Expressions;
using CxCompiler.Model.Statements;
using CxCompiler.Model.Types;
using CxCompiler.Model.Types.BuiltInTypes;

namespace CxCompiler.Semantics;

public sealed partial class SemanticBinder
{

    private static bool IsGenericConstrainedMemberReturnBody(FunctionDeclaration function)
    {
        if (function.Body is not
            [ReturnStatement
            {
                Expression: { } expression,
            }])
        {
            return false;
        }

        return ContainsGenericConstrainedMember(expression, function) &&
            IsGenericConstrainedMemberReturnExpression(expression, function);
    }

    private static bool IsGenericConstrainedMemberLocalReturnBody(
        FunctionDeclaration function)
    {
        if (function.Body is not
            [
                LocalVariableDeclarationStatement
                {
                    DeclaredType: var localType,
                    Declarators:
                    [
                        { Name: var localName, Initializer: { } initializer },
                    ],
                },
                ReturnStatement
                {
                    Expression: IdentifierExpression returned,
                },
            ] || returned.Identifier.ToString() != localName ||
            localType is AutoType ||
            ContainsFunctionTypeParameter(localType, function.GenericTypeNames))
        {
            return false;
        }

        return ContainsGenericConstrainedMember(initializer, function) &&
            IsGenericConstrainedMemberReturnExpression(initializer, function);
    }

    private static bool IsGenericConstrainedReceiver(
        ExpressionBase expression,
        FunctionDeclaration function) =>
        expression is IdentifierExpression receiver &&
        function.Parameters.Any(parameter =>
            parameter.Name == receiver.Identifier.ToString() &&
            parameter.ParameterType is GenericType genericParameter &&
            function.GenericTypeNames.Contains(genericParameter.Name) &&
            HasSimpleClassConstraint(function, genericParameter.Name));

    private static bool ContainsGenericConstrainedMember(
        ExpressionBase expression,
        FunctionDeclaration function) => expression switch
    {
        InvocationExpression
        {
            Target: MemberAccessExpression { Target: var receiver },
        } => IsGenericConstrainedReceiver(receiver, function),
        MemberAccessExpression { Target: var receiver } =>
            IsGenericConstrainedReceiver(receiver, function),
        ArrayAccessExpression
        {
            Target: MemberAccessExpression { Target: var receiver },
        } => IsGenericConstrainedReceiver(receiver, function),
        BinaryExpression binary =>
            ContainsGenericConstrainedMember(binary.Left, function) ||
            ContainsGenericConstrainedMember(binary.Right, function),
        ConditionalExpression conditional =>
            ContainsGenericConstrainedMember(conditional.Condition, function) ||
            ContainsGenericConstrainedMember(conditional.WhenTrue, function) ||
            ContainsGenericConstrainedMember(conditional.WhenFalse, function),
        _ => false,
    };

    private static bool IsGenericConstrainedMemberReturnExpression(
        ExpressionBase expression,
        FunctionDeclaration function) => expression switch
    {
        InvocationExpression
        {
            Target: MemberAccessExpression { Target: var receiver },
            Arguments: var arguments,
        } => IsGenericConstrainedReceiver(receiver, function) &&
            arguments.All(argument =>
                IsSupportedGenericConstrainedValueExpression(argument, function)),
        MemberAccessExpression { Target: var receiver } =>
            IsGenericConstrainedReceiver(receiver, function),
        ArrayAccessExpression
        {
            Target: MemberAccessExpression { Target: var receiver },
            Indices: var indices,
        } => IsGenericConstrainedReceiver(receiver, function) &&
            indices.All(index =>
                IsSupportedGenericConstrainedValueExpression(index, function)),
        BinaryExpression
        {
            Operator: "+" or "-" or "*" or "/" or "%" or
                "==" or "!=" or "<" or "<=" or ">" or ">=" or
                "&&" or "||" or "&" or "|" or "^" or "<<" or ">>",
            OperatorSymbol: null,
        } binary =>
            IsGenericConstrainedMemberReturnExpression(binary.Left, function) &&
            IsGenericConstrainedMemberReturnExpression(binary.Right, function),
        ConditionalExpression conditional =>
            IsGenericConstrainedMemberReturnExpression(conditional.Condition, function) &&
            IsGenericConstrainedMemberReturnExpression(conditional.WhenTrue, function) &&
            IsGenericConstrainedMemberReturnExpression(conditional.WhenFalse, function),
        _ => false,
    };

    private static string? GetGenericConstrainedMemberReceiverName(ExpressionBase expression) =>
        expression switch
        {
            InvocationExpression
            {
                Target: MemberAccessExpression
                {
                    Target: IdentifierExpression receiver,
                },
            } => receiver.Identifier.ToString(),
            MemberAccessExpression
            {
                Target: IdentifierExpression receiver,
            } => receiver.Identifier.ToString(),
            ArrayAccessExpression
            {
                Target: MemberAccessExpression
                {
                    Target: IdentifierExpression receiver,
                },
            } => receiver.Identifier.ToString(),
            BinaryExpression binary =>
                GetGenericConstrainedMemberReceiverName(binary.Left) ??
                GetGenericConstrainedMemberReceiverName(binary.Right),
            ConditionalExpression conditional =>
                GetGenericConstrainedMemberReceiverName(conditional.WhenTrue) ??
                GetGenericConstrainedMemberReceiverName(conditional.WhenFalse),
            _ => null,
        };

    private static bool ContainsGenericConstrainedMemberInvocation(
        ExpressionBase expression,
        string receiverName) => expression switch
    {
        InvocationExpression
        {
            Target: MemberAccessExpression
            {
                Target: IdentifierExpression receiver,
            },
        } => receiver.Identifier.ToString() == receiverName,
        MemberAccessExpression
        {
            Target: IdentifierExpression receiver,
        } => receiver.Identifier.ToString() == receiverName,
        ArrayAccessExpression
        {
            Target: MemberAccessExpression
            {
                Target: IdentifierExpression receiver,
            },
        } => receiver.Identifier.ToString() == receiverName,
        BinaryExpression binary =>
            ContainsGenericConstrainedMemberInvocation(binary.Left, receiverName) ||
            ContainsGenericConstrainedMemberInvocation(binary.Right, receiverName),
        ConditionalExpression conditional =>
            ContainsGenericConstrainedMemberInvocation(conditional.Condition, receiverName) ||
            ContainsGenericConstrainedMemberInvocation(conditional.WhenTrue, receiverName) ||
            ContainsGenericConstrainedMemberInvocation(conditional.WhenFalse, receiverName),
        _ => false,
    };

    private static bool IsGenericConstrainedMemberReturnExpression(
        ExpressionBase expression,
        FunctionDeclaration function,
        string receiverName) => expression switch
    {
        InvocationExpression
        {
            Target: MemberAccessExpression
            {
                Target: IdentifierExpression receiver,
            },
            Arguments: var arguments,
        } => receiver.Identifier.ToString() == receiverName &&
            arguments.All(argument =>
                IsSupportedGenericConstrainedValueExpression(argument, function)),
        MemberAccessExpression
        {
            Target: IdentifierExpression receiver,
        } => receiver.Identifier.ToString() == receiverName,
        ArrayAccessExpression
        {
            Target: MemberAccessExpression
            {
                Target: IdentifierExpression receiver,
            },
            Indices: var indices,
        } => receiver.Identifier.ToString() == receiverName &&
            indices.All(index =>
                IsSupportedGenericConstrainedValueExpression(index, function)),
        BinaryExpression
        {
            Operator: "+" or "-" or "*" or "/" or "%" or
                "==" or "!=" or "<" or "<=" or ">" or ">=" or
                "&&" or "||" or "&" or "|" or "^" or "<<" or ">>",
            OperatorSymbol: null,
        } binary =>
            IsGenericConstrainedMemberReturnExpression(binary.Left, function, receiverName) &&
            IsGenericConstrainedMemberReturnExpression(binary.Right, function, receiverName),
        ConditionalExpression conditional =>
            IsGenericConstrainedMemberReturnExpression(
                conditional.Condition, function, receiverName) &&
            IsGenericConstrainedMemberReturnExpression(
                conditional.WhenTrue, function, receiverName) &&
            IsGenericConstrainedMemberReturnExpression(
                conditional.WhenFalse, function, receiverName),
        _ => false,
    };

    private static bool IsGenericConstrainedFieldReturnBody(FunctionDeclaration function)
    {
        if (function.Body is not [ReturnStatement { Expression: { } expression }])
        {
            return false;
        }

        var receiverName = expression switch
        {
            MemberAccessExpression
            {
                Target: IdentifierExpression receiver,
            } => receiver.Identifier.ToString(),
            ArrayAccessExpression
            {
                Target: MemberAccessExpression
                {
                    Target: IdentifierExpression receiver,
                },
            } => receiver.Identifier.ToString(),
            _ => null,
        };
        var parameter = function.Parameters.SingleOrDefault(item =>
            item.Name == receiverName);
        return parameter?.ParameterType is GenericType genericParameter &&
            function.GenericTypeNames.Contains(genericParameter.Name) &&
            HasSimpleClassConstraint(function, genericParameter.Name);
    }

    private static bool IsGenericConstrainedPropertyAssignmentBody(
        FunctionDeclaration function)
    {
        if (function.ReturnType is not VoidType ||
            function.Body is not { Count: > 0 } body)
        {
            return false;
        }

        foreach (var statement in body)
        {
            if (statement is not ExpressionStatement
            {
                Expression: AssignmentExpression
                {
                    Operator: "=" or "+=" or "-=" or "*=" or "/=" or "%=",
                    Target: var target,
                    Value: var value,
                },
            })
            {
                return false;
            }

            var receiverName = target switch
            {
                MemberAccessExpression
                {
                    Target: IdentifierExpression receiver,
                } => receiver.Identifier.ToString(),
                ArrayAccessExpression
                {
                    Target: MemberAccessExpression
                    {
                        Target: IdentifierExpression receiver,
                    },
                } => receiver.Identifier.ToString(),
                _ => null,
            };
            var receiverParameter = function.Parameters.SingleOrDefault(parameter =>
                parameter.Name == receiverName &&
                parameter.ParameterType is GenericType genericParameter &&
                function.GenericTypeNames.Contains(genericParameter.Name));
            if (receiverParameter?.ParameterType is not GenericType receiverType ||
                !function.GenericConstraints.Any(constraint =>
                    constraint.ParameterName == receiverType.Name &&
                    constraint.Kind == GenericConstraintKind.Type &&
                    constraint.Types.Any(type => type is NamedType)) ||
                !IsSupportedGenericConstrainedAssignmentValueExpression(
                    value, function, receiverName!))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsGenericConstrainedVoidMethodCallBody(
        FunctionDeclaration function)
    {
        if (function.ReturnType is not VoidType ||
            function.Body is not { Count: > 0 } body ||
            body.Any(statement => !IsGenericConstrainedVoidMethodCallStatement(
                statement, function)))
        {
            return false;
        }

        return true;
    }

    private static bool IsGenericConstrainedVoidMethodCallStatement(
        StatementBase statement,
        FunctionDeclaration function) => statement switch
    {
        BlockStatement block => block.Statements.Count > 0 && block.Statements.All(
            nested => IsGenericConstrainedVoidMethodCallStatement(nested, function)),
        IfStatement conditional =>
            IsSupportedGenericConstrainedValueExpression(conditional.Condition, function) &&
            IsGenericConstrainedVoidMethodCallStatement(
                conditional.ThenStatement, function) &&
            (conditional.ElseStatement is null ||
             IsGenericConstrainedVoidMethodCallStatement(
                 conditional.ElseStatement, function)),
        ExpressionStatement
        {
            Expression: InvocationExpression
            {
                Target: MemberAccessExpression
                {
                    Target: IdentifierExpression receiver,
                },
                Arguments: var arguments,
            },
        } => function.Parameters.Any(parameter =>
                parameter.Name == receiver.Identifier.ToString() &&
                parameter.ParameterType is GenericType genericParameter &&
                function.GenericTypeNames.Contains(genericParameter.Name) &&
                function.GenericConstraints.Any(constraint =>
                    constraint.ParameterName == genericParameter.Name &&
                    constraint.Kind == GenericConstraintKind.Type &&
                    constraint.Types.Count > 0)) &&
            arguments.All(argument =>
                IsSupportedGenericConstrainedValueExpression(argument, function)),
        _ => false,
    };

    private static bool IsSupportedGenericConstrainedAssignmentValueExpression(
        ExpressionBase expression,
        FunctionDeclaration function,
        string receiverName) => expression switch
    {
        LiteralExpression => true,
        IdentifierExpression => IsSupportedGenericConstrainedValueExpression(
            expression, function),
        InvocationExpression
        {
            Target: MemberAccessExpression
            {
                Target: IdentifierExpression receiver,
            },
            Arguments: var arguments,
        } => receiver.Identifier.ToString() == receiverName &&
            arguments.All(argument =>
                IsSupportedGenericConstrainedValueExpression(argument, function)),
        MemberAccessExpression
        {
            Target: IdentifierExpression receiver,
        } => receiver.Identifier.ToString() == receiverName,
        ArrayAccessExpression
        {
            Target: MemberAccessExpression
            {
                Target: IdentifierExpression receiver,
            },
            Indices: var indices,
        } => receiver.Identifier.ToString() == receiverName &&
            indices.All(index =>
                IsSupportedGenericConstrainedValueExpression(index, function)),
        BinaryExpression
        {
            Operator: "+" or "-" or "*" or "/" or "%" or
                "==" or "!=" or "<" or "<=" or ">" or ">=" or
                "&&" or "||" or "&" or "|" or "^" or "<<" or ">>",
            OperatorSymbol: null,
        } binary =>
            IsSupportedGenericConstrainedAssignmentValueExpression(
                binary.Left, function, receiverName) &&
            IsSupportedGenericConstrainedAssignmentValueExpression(
                binary.Right, function, receiverName),
        ConditionalExpression conditional =>
            IsSupportedGenericConstrainedAssignmentValueExpression(
                conditional.Condition, function, receiverName) &&
            IsSupportedGenericConstrainedAssignmentValueExpression(
                conditional.WhenTrue, function, receiverName) &&
            IsSupportedGenericConstrainedAssignmentValueExpression(
                conditional.WhenFalse, function, receiverName),
        _ => false,
    };

    private static bool IsSupportedGenericConstrainedValueExpression(
        ExpressionBase expression,
        FunctionDeclaration function) => expression switch
    {
        LiteralExpression => true,
        IdentifierExpression identifier => function.Parameters.Any(parameter =>
            parameter.Name == identifier.Identifier.ToString() &&
            !ContainsFunctionTypeParameter(
                parameter.ParameterType, function.GenericTypeNames)),
        BinaryExpression
        {
            Operator: "+" or "-" or "*" or "/" or "%" or
                "==" or "!=" or "<" or "<=" or ">" or ">=" or
                "&&" or "||" or "&" or "|" or "^" or "<<" or ">>",
            OperatorSymbol: null,
        } binary =>
            IsSupportedGenericConstrainedValueExpression(binary.Left, function) &&
            IsSupportedGenericConstrainedValueExpression(binary.Right, function),
        ConditionalExpression conditional =>
            IsSupportedGenericConstrainedValueExpression(conditional.Condition, function) &&
            IsSupportedGenericConstrainedValueExpression(conditional.WhenTrue, function) &&
            IsSupportedGenericConstrainedValueExpression(conditional.WhenFalse, function),
        _ => false,
    };

    private static bool HasSimpleClassConstraint(
        FunctionDeclaration function,
        string genericParameterName) =>
        function.GenericConstraints.Any(constraint =>
            constraint.ParameterName == genericParameterName &&
            constraint.Kind == GenericConstraintKind.Type &&
            constraint.Types.Any(type =>
                type is NamedType
                {
                    ClassType: ClassType.Class or ClassType.Interface,
                }));

    private static bool IsGenericArrayLengthReturnBody(FunctionDeclaration function)
    {
        if (function.ReturnType is not UIntType ||
            function.Body is not
                [ReturnStatement
                {
                    Expression: MemberAccessExpression
                    {
                        Target: { } target,
                        MemberName: "Length",
                    },
                }])
        {
            return false;
        }

        return IsGenericArrayLengthExpression(target, function);
    }

    private static bool IsGenericArrayLengthExpression(
        ExpressionBase expression,
        FunctionDeclaration function)
    {
        TypeBase? arrayType = expression switch
        {
            IdentifierExpression identifier => function.Parameters.SingleOrDefault(parameter =>
                parameter.Name == identifier.Identifier.ToString())?.ParameterType,
            ArrayAccessExpression access when TryGetGenericArrayElementType(
                function, access, out _, out var elementType, out _) => elementType,
            _ => null,
        };

        return arrayType is ArrayType array &&
            ContainsFunctionTypeParameter(array.ElementType, function.GenericTypeNames);
    }

    private static bool IsGenericArrayLengthComparisonBody(FunctionDeclaration function)
    {
        if (function.ReturnType is not BoolType ||
            function.Body is not
                [ReturnStatement { Expression: { } expression }])
        {
            return false;
        }

        bool IsLengthOfGenericArrayParameter(ExpressionBase expression) =>
            expression is MemberAccessExpression
            {
                Target: { } target,
                MemberName: "Length",
            } && IsGenericArrayLengthExpression(target, function);

        bool IsUnsignedComparisonOperand(ExpressionBase expression) =>
            expression is LiteralExpression literal &&
                (literal.SourceText.EndsWith('u') ||
                 uint.TryParse(literal.SourceText, out _)) ||
            expression is IdentifierExpression identifier &&
                function.Parameters.Any(parameter =>
                    parameter.Name == identifier.Identifier.ToString() &&
                    parameter.ParameterType is UIntType);

        bool IsLengthComparison(ExpressionBase candidate) =>
            candidate is BinaryExpression
            {
                Operator: "==" or "!=" or "<" or "<=" or ">" or ">=",
                OperatorSymbol: null,
            } comparison &&
            (IsLengthOfGenericArrayParameter(comparison.Left) &&
                IsUnsignedComparisonOperand(comparison.Right) ||
             IsUnsignedComparisonOperand(comparison.Left) &&
                IsLengthOfGenericArrayParameter(comparison.Right));

        bool IsSupportedBooleanExpression(ExpressionBase candidate) => candidate switch
        {
            UnaryExpression { Operator: "!", OperatorSymbol: null } unary =>
                IsSupportedBooleanExpression(unary.Operand),
            BinaryExpression
            {
                Operator: "&&" or "||",
                OperatorSymbol: null,
            } logical => IsSupportedBooleanExpression(logical.Left) &&
                IsSupportedBooleanExpression(logical.Right),
            ConditionalExpression conditional =>
                IsSupportedBooleanExpression(conditional.Condition) &&
                IsSupportedBooleanExpression(conditional.WhenTrue) &&
                IsSupportedBooleanExpression(conditional.WhenFalse),
            _ => IsLengthComparison(candidate),
        };

        return IsSupportedBooleanExpression(expression);
    }

    private static bool ContainsNonVoidGenericConstrainedCall(
        StatementBase statement,
        FunctionDeclaration function) => statement switch
    {
        BlockStatement block => block.Statements.Any(nested =>
            ContainsNonVoidGenericConstrainedCall(nested, function)),
        IfStatement conditional =>
            ContainsNonVoidGenericConstrainedCall(conditional.ThenStatement, function) ||
            conditional.ElseStatement is { } elseStatement &&
                ContainsNonVoidGenericConstrainedCall(elseStatement, function),
        ExpressionStatement
        {
            Expression: InvocationExpression
            {
                Target: MemberAccessExpression
                {
                    Target: IdentifierExpression receiver,
                },
                TargetSymbol.ReturnType: not VoidType,
            },
        } => function.Parameters.Any(parameter =>
            parameter.Name == receiver.Identifier.ToString() &&
            parameter.ParameterType is GenericType genericParameter &&
            function.GenericTypeNames.Contains(genericParameter.Name)),
        _ => false,
    };
}

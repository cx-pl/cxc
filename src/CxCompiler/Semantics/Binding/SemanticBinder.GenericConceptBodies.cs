using CxCompiler.Model.Expressions;
using CxCompiler.Model.Statements;
using CxCompiler.Model.Types;
using CxCompiler.Model.Types.BuiltInTypes;

namespace CxCompiler.Semantics;

public sealed partial class SemanticBinder
{

    private static bool IsClosedClassConceptMethodReturnBody(
        FunctionDeclaration declaration) =>
        declaration.ParentClassDeclaration is
            { ClassType: ClassType.Class or ClassType.Struct, ConceptConstraints.Count: > 0 } &&
        declaration.Body is
        [ReturnStatement
        {
            Expression: InvocationExpression { IsConceptMethodInvocation: true },
        }];

    private static bool IsClosedClassConceptVoidCallBody(
        FunctionDeclaration declaration) =>
        declaration.ReturnType is VoidType &&
        declaration.ParentClassDeclaration is
            { ClassType: ClassType.Class or ClassType.Struct, ConceptConstraints.Count: > 0 } parent &&
        declaration.Body is { Count: > 0 } body &&
        IsSupportedStatementSequence(body, parent, declaration, null);

    private static bool IsSupportedStatementSequence(
        IEnumerable<StatementBase> statements,
        ClassDeclaration parent,
        FunctionDeclaration declaration,
        IReadOnlySet<string>? enclosingLocals)
    {
        var localNames = enclosingLocals is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : new HashSet<string>(enclosingLocals, StringComparer.Ordinal);
        foreach (var statement in statements)
        {
            if (statement is LocalVariableDeclarationStatement local)
            {
                if (!IsSupportedLocalDeclaration(local, parent, declaration, localNames))
                {
                    return false;
                }

                foreach (var declarator in local.Declarators)
                {
                    localNames.Add(declarator.Name);
                }
                continue;
            }

            if (!IsSupportedStatement(statement, parent, declaration, localNames))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsSupportedLocalDeclaration(
        LocalVariableDeclarationStatement local,
        ClassDeclaration parent,
        FunctionDeclaration declaration,
        IReadOnlySet<string>? localNames) =>
        (local.DeclaredType is IntTypeBase or BoolType or StringType ||
         local.DeclaredType is GenericType genericType &&
            parent.ConceptConstraints.Any(constraint =>
                constraint.ParameterName == genericType.Name)) &&
        local.Declarators.Count == 1 &&
        local.Declarators[0].Initializer is { } initializer &&
        IsClosedClassConceptExpression(initializer, parent, declaration, localNames);

    private static bool IsSupportedStatement(
        StatementBase statement,
        ClassDeclaration parent,
        FunctionDeclaration declaration,
        IReadOnlySet<string>? localNames = null) => statement switch
    {
        BlockStatement block => IsSupportedStatementSequence(
            block.Statements, parent, declaration, localNames),
        IfStatement conditional =>
            IsClosedClassConceptExpression(
                conditional.Condition, parent, declaration, localNames) &&
            IsSupportedStatement(conditional.ThenStatement, parent, declaration, localNames) &&
            (conditional.ElseStatement is null ||
             IsSupportedStatement(conditional.ElseStatement, parent, declaration, localNames)),
        WhileStatement loop =>
            IsClosedClassConceptExpression(loop.Condition, parent, declaration, localNames) &&
            IsSupportedStatement(loop.Body, parent, declaration, localNames),
        DoWhileStatement loop =>
            IsClosedClassConceptExpression(loop.Condition, parent, declaration, localNames) &&
            IsSupportedStatement(loop.Body, parent, declaration, localNames),
        ForStatement loop => IsSupportedForStatement(loop, parent, declaration, localNames),
        ForeachStatement loop => IsSupportedForeachStatement(
            loop, parent, declaration, localNames),
        SwitchStatement switchStatement =>
            IsClosedClassConceptExpression(
                switchStatement.Expression, parent, declaration, localNames) &&
            switchStatement.Expression.InferredType is not null &&
            switchStatement.Sections.SelectMany(section => section.Labels)
                .All(label => label.Filter is null ||
                    IsClosedClassConceptExpression(
                        label.Filter, parent, declaration, localNames)) &&
            switchStatement.Sections.All(section => IsSupportedStatementSequence(
                section.Statements, parent, declaration, localNames)),
        ReturnStatement { Expression: null } => true,
        BreakStatement or ContinueStatement => true,
        LocalVariableDeclarationStatement local =>
            IsSupportedLocalDeclaration(local, parent, declaration, localNames),
        ExpressionStatement
        {
            Expression: InvocationExpression
            {
                IsConceptMethodInvocation: true,
                Target: MemberAccessExpression { Target: IdentifierExpression },
            } invocation
        } => invocation.Arguments.All(argument =>
            IsClosedClassConceptExpression(argument, parent, declaration, localNames)),
        ExpressionStatement
        {
            Expression: AssignmentExpression
            {
                Operator: "=" or "+=" or "-=" or "*=" or "/=" or "%=",
                Target: MemberAccessExpression
                {
                    Target: IdentifierExpression receiver,
                    TargetProperty: not null,
                },
            } assignment
        } => receiver.InferredType is GenericType generic &&
                parent.ConceptConstraints.Any(constraint =>
                    constraint.ParameterName == generic.Name) &&
                (assignment.Operator == "="
                    ? IsClosedClassConceptExpression(
                        assignment.Value, parent, declaration, localNames)
                    : assignment.OperatorSymbol is null &&
                        IsClosedClassConceptSimpleValue(
                            assignment.Value, declaration, localNames)),
        ExpressionStatement
        {
            Expression: AssignmentExpression
            {
                Operator: "=" or "+=" or "-=" or "*=" or "/=" or "%=",
                Target: IdentifierExpression { TargetLocal: not null } identifier,
            } assignment,
        } => localNames?.Contains(identifier.Identifier.ToString()) == true &&
            (assignment.Operator == "="
                ? IsClosedClassConceptExpression(
                    assignment.Value, parent, declaration, localNames)
                : identifier.InferredType is IntTypeBase &&
                    assignment.OperatorSymbol is null &&
                    IsClosedClassConceptSimpleValue(
                        assignment.Value, declaration, localNames)),
        ExpressionStatement
        {
            Expression: UnaryExpression
            {
                Operator: "++" or "--",
                OperatorSymbol: null,
                Operand: IdentifierExpression { TargetLocal: not null } identifier,
            },
        } => identifier.InferredType is IntTypeBase &&
            localNames?.Contains(identifier.Identifier.ToString()) == true,
        _ => false,
    };

    private static bool IsSupportedForStatement(
        ForStatement loop,
        ClassDeclaration parent,
        FunctionDeclaration declaration,
        IReadOnlySet<string>? enclosingLocals)
    {
        if (loop.DeclarationInitializer is not
            { Declarators.Count: 1, DeclaredType: IntType } initializer ||
            loop.InitializerExpressions.Count != 0 ||
            initializer.Declarators[0] is not { Initializer: { } startValue } counter ||
            loop.Condition is null || loop.Iterators.Count != 1 ||
            loop.Iterators[0] is not UnaryExpression
            {
                Operator: "++" or "--",
                OperatorSymbol: null,
                Operand: IdentifierExpression iterator,
            } || iterator.Identifier.ToString() != counter.Name)
        {
            return false;
        }

        var localNames = enclosingLocals is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : new HashSet<string>(enclosingLocals, StringComparer.Ordinal);
        localNames.Add(counter.Name);
        return IsClosedClassConceptExpression(
                startValue, parent, declaration, enclosingLocals) &&
            IsClosedClassConceptExpression(
                loop.Condition, parent, declaration, localNames) &&
            IsSupportedStatement(loop.Body, parent, declaration, localNames);
    }

    private static bool IsSupportedForeachStatement(
        ForeachStatement loop,
        ClassDeclaration parent,
        FunctionDeclaration declaration,
        IReadOnlySet<string>? enclosingLocals)
    {
        if (loop.Collection.InferredType is not ArrayType { ElementType: GenericType elementType } ||
            loop.VariableType is not GenericType variableType ||
            loop.VariableSymbol?.IsCaptured == true ||
            elementType.Name != variableType.Name ||
            !parent.ConceptConstraints.Any(constraint =>
                constraint.ParameterName == variableType.Name))
        {
            return false;
        }

        var localNames = enclosingLocals is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : new HashSet<string>(enclosingLocals, StringComparer.Ordinal);
        localNames.Add(loop.VariableName);
        return IsClosedClassConceptExpression(
                loop.Collection, parent, declaration, enclosingLocals) &&
            IsSupportedStatement(loop.Body, parent, declaration, localNames);
    }

    private static bool IsClosedClassConceptSimpleValue(
        ExpressionBase expression,
        FunctionDeclaration declaration,
        IReadOnlySet<string>? localNames = null) =>
        expression is LiteralExpression ||
        expression is IdentifierExpression identifier &&
            (declaration.Parameters.Any(parameter => parameter.Name ==
                identifier.Identifier.ToString()) ||
             localNames?.Contains(identifier.Identifier.ToString()) == true);

    private static bool IsClosedClassConceptExpression(
        ExpressionBase expression,
        ClassDeclaration declaration,
        FunctionDeclaration function,
        IReadOnlySet<string>? localNames = null) => expression switch
    {
        LiteralExpression => true,
        IdentifierExpression identifier =>
            function.Parameters.Any(parameter => parameter.Name ==
                identifier.Identifier.ToString()) ||
            localNames?.Contains(identifier.Identifier.ToString()) == true ||
            identifier.InferredType is GenericType generic &&
                declaration.ConceptConstraints.Any(constraint =>
                    constraint.ParameterName == generic.Name),
        MemberAccessExpression
        {
            Target: IdentifierExpression receiver,
            TargetProperty: not null,
        } => receiver.InferredType is GenericType generic &&
            declaration.ConceptConstraints.Any(constraint =>
                constraint.ParameterName == generic.Name),
        InvocationExpression
        {
            IsConceptMethodInvocation: true,
            Target: MemberAccessExpression { Target: IdentifierExpression },
        } invocation => invocation.Arguments.All(argument =>
            IsClosedClassConceptExpression(argument, declaration, function, localNames)),
        BinaryExpression { OperatorSymbol: null } binary =>
            IsClosedClassConceptExpression(binary.Left, declaration, function, localNames) &&
            IsClosedClassConceptExpression(binary.Right, declaration, function, localNames),
        UnaryExpression { OperatorSymbol: null, Postfix: false } unary =>
            IsClosedClassConceptExpression(unary.Operand, declaration, function, localNames),
        ConditionalExpression conditional =>
            IsClosedClassConceptExpression(conditional.Condition, declaration, function, localNames) &&
            IsClosedClassConceptExpression(conditional.WhenTrue, declaration, function, localNames) &&
            IsClosedClassConceptExpression(conditional.WhenFalse, declaration, function, localNames),
        _ => false,
    };

    private static bool IsGenericConceptConditionalReturnBody(FunctionDeclaration function)
    {
        if (function.GenericTypeNames.Length != 1 ||
            function.ReturnType is not GenericType returnType ||
            !function.ConceptConstraints.Any(constraint =>
                constraint.ParameterName == returnType.Name) ||
            function.Body is not
                [ReturnStatement
                {
                    Expression: ConditionalExpression
                    {
                        Condition: BinaryExpression
                        {
                            Left: IdentifierExpression left,
                            Right: IdentifierExpression right,
                        },
                        WhenTrue: IdentifierExpression whenTrue,
                        WhenFalse: IdentifierExpression whenFalse,
                    },
                }])
        {
            return false;
        }

        var parameters = function.Parameters
            .Where(parameter => IsDirectGenericIdentityType(
                returnType, parameter.ParameterType))
            .Select(parameter => parameter.Name)
            .ToHashSet(StringComparer.Ordinal);
        var leftName = left.Identifier.ToString();
        var rightName = right.Identifier.ToString();
        return leftName != rightName && parameters.Contains(leftName) &&
            parameters.Contains(rightName) &&
            whenTrue.Identifier.ToString() == leftName &&
            whenFalse.Identifier.ToString() == rightName &&
            function.Parameters.Count == 2;
    }

    private static bool IsGenericConceptOperatorReturnBody(FunctionDeclaration function)
    {
        if (function.GenericTypeNames.Length != 1 ||
            function.Body is not
                [ReturnStatement
                {
                    Expression: BinaryExpression
                    {
                        Left: IdentifierExpression left,
                        Right: IdentifierExpression right,
                    },
                }])
        {
            return false;
        }

        var typeParameter = function.GenericTypeNames[0];
        return (function.ReturnType is BoolType ||
                function.ReturnType is GenericType returnType &&
                returnType.Name == typeParameter) &&
            function.ConceptConstraints.Any(constraint =>
                constraint.ParameterName == typeParameter) &&
            function.Parameters.Count == 2 &&
            function.Parameters.All(parameter =>
                parameter.ParameterType is GenericType generic &&
                generic.Name == typeParameter) &&
            left.Identifier.ToString() != right.Identifier.ToString() &&
            function.Parameters.Any(parameter =>
                parameter.Name == left.Identifier.ToString()) &&
            function.Parameters.Any(parameter =>
                parameter.Name == right.Identifier.ToString());
    }

    private static bool IsGenericConceptUnaryReturnBody(FunctionDeclaration function)
    {
        if (function.GenericTypeNames.Length != 1 ||
            function.Body is not
                [ReturnStatement { Expression: UnaryExpression
                {
                    Operand: IdentifierExpression operand,
                } unary }])
        {
            return false;
        }

        var typeParameter = function.GenericTypeNames[0];
        return (unary.Operator is "!" or "~" or "-") &&
            function.ConceptConstraints.Any(constraint =>
                constraint.ParameterName == typeParameter) &&
            function.Parameters.Count == 1 &&
            function.Parameters[0].ParameterType is GenericType parameterType &&
            parameterType.Name == typeParameter &&
            function.Parameters[0].Name == operand.Identifier.ToString() &&
            (unary.Operator == "!"
                ? function.ReturnType is BoolType
                : function.ReturnType is GenericType returnType &&
                    returnType.Name == typeParameter);
    }

    private static IEnumerable<InvocationExpression> EnumerateDirectConceptMethodInvocations(
        FunctionDeclaration function)
    {
        foreach (var statement in function.Body ?? [])
        {
            switch (statement)
            {
                case ReturnStatement { Expression: InvocationExpression invocation }:
                    yield return invocation;
                    break;
                case ExpressionStatement { Expression: InvocationExpression invocation }:
                    yield return invocation;
                    break;
            }
        }
    }

    private static bool IsGenericConceptMethodReturnBody(FunctionDeclaration function)
    {
        if (function.GenericTypeNames.Length == 0 ||
            function.Body is not
                [ReturnStatement
                {
                    Expression: InvocationExpression
                    {
                        Target: MemberAccessExpression
                        {
                            Target: IdentifierExpression receiver,
                        } member,
                        Arguments: _,
                    } invocation,
                }])
        {
            return false;
        }

        var typeParameter = function.GenericTypeNames[0];
        return function.ConceptConstraints.Any(constraint =>
                constraint.ParameterName == typeParameter) &&
            function.Parameters.Count >= 1 &&
            function.Parameters[0].ParameterType is GenericType parameterType &&
            parameterType.Name == typeParameter &&
            function.Parameters[0].Name == receiver.Identifier.ToString() &&
            member.MemberName.Length > 0 && function.ReturnType is not VoidType;
    }

    private static bool IsGenericConceptMethodCallBody(FunctionDeclaration function)
    {
        if (function.GenericTypeNames.Length == 0 ||
            function.ReturnType is not VoidType ||
            function.Body is not { Count: > 0 } body ||
            body.Any(statement => statement is not ExpressionStatement
                {
                    Expression: InvocationExpression
                    {
                        Target: MemberAccessExpression
                        {
                            Target: IdentifierExpression,
                        },
                    },
                }))
        {
            return false;
        }

        var typeParameter = function.GenericTypeNames[0];
        var receiverMatches = body.Cast<ExpressionStatement>()
            .Select(statement => (InvocationExpression)statement.Expression)
            .All(invocation => invocation.Target is MemberAccessExpression
                { Target: IdentifierExpression receiver } &&
                function.Parameters.Count > 0 &&
                function.Parameters[0].Name == receiver.Identifier.ToString());
        return function.ConceptConstraints.Any(constraint =>
                constraint.ParameterName == typeParameter) &&
            function.Parameters.Count >= 1 &&
            function.Parameters[0].ParameterType is GenericType parameterType &&
            parameterType.Name == typeParameter &&
            receiverMatches;
    }

    private static bool IsGenericConceptPropertyReturnBody(FunctionDeclaration function)
    {
        if (function.GenericTypeNames.Length != 1 ||
            function.Body is not
                [ReturnStatement
                {
                    Expression: { } expression,
                }])
        {
            return false;
        }

        var typeParameter = function.GenericTypeNames[0];
        var receiverName = function.Parameters.FirstOrDefault()?.Name;
        return function.ConceptConstraints.Any(constraint =>
                constraint.ParameterName == typeParameter) &&
            function.Parameters.Count >= 1 &&
            function.Parameters[0].ParameterType is GenericType parameterType &&
            parameterType.Name == typeParameter &&
            function.Parameters.Skip(1).All(parameter =>
                !ContainsFunctionTypeParameter(
                    parameter.ParameterType, function.GenericTypeNames)) &&
            receiverName is not null &&
            IsGenericConceptPropertyExpression(expression, receiverName) &&
            ContainsGenericConceptPropertyAccess(expression, receiverName) &&
            function.ReturnType is not VoidType;
    }

    private static bool IsGenericConceptPropertyExpression(
        ExpressionBase expression,
        string receiverName) => expression switch
    {
        MemberAccessExpression
        {
            Target: IdentifierExpression receiver,
            MemberName.Length: > 0,
        } => receiver.Identifier.ToString() == receiverName,
        InvocationExpression
        {
            Target: MemberAccessExpression
            {
                Target: IdentifierExpression receiver,
                MemberName.Length: > 0,
            },
        } invocation => receiver.Identifier.ToString() == receiverName &&
            invocation.Arguments.All(argument =>
                IsGenericConceptMethodArgumentExpression(argument, receiverName)),
        LiteralExpression => true,
        IdentifierExpression => true,
        ConditionalExpression conditional =>
            IsGenericConceptPropertyExpression(conditional.Condition, receiverName) &&
            IsGenericConceptPropertyExpression(conditional.WhenTrue, receiverName) &&
            IsGenericConceptPropertyExpression(conditional.WhenFalse, receiverName),
        BinaryExpression
        {
            Operator: "+" or "-" or "*" or "/" or "%" or
                "==" or "!=" or "<" or "<=" or ">" or ">=" or
                "&&" or "||" or "&" or "|" or "^" or "<<" or ">>",
            OperatorSymbol: null,
        } binary => IsGenericConceptPropertyExpression(binary.Left, receiverName) &&
            IsGenericConceptPropertyExpression(binary.Right, receiverName),
        _ => false,
    };

    private static bool ContainsGenericConceptPropertyAccess(
        ExpressionBase expression,
        string receiverName) => expression switch
    {
        MemberAccessExpression { Target: IdentifierExpression receiver } =>
            receiver.Identifier.ToString() == receiverName,
        InvocationExpression
        {
            Target: MemberAccessExpression
            {
                Target: IdentifierExpression receiver,
            },
        } invocation => receiver.Identifier.ToString() == receiverName ||
            invocation.Arguments.Any(argument =>
                ContainsGenericConceptPropertyAccess(argument, receiverName)),
        ConditionalExpression conditional =>
            ContainsGenericConceptPropertyAccess(conditional.Condition, receiverName) ||
            ContainsGenericConceptPropertyAccess(conditional.WhenTrue, receiverName) ||
            ContainsGenericConceptPropertyAccess(conditional.WhenFalse, receiverName),
        BinaryExpression binary =>
            ContainsGenericConceptPropertyAccess(binary.Left, receiverName) ||
            ContainsGenericConceptPropertyAccess(binary.Right, receiverName),
        _ => false,
    };

    private static bool IsGenericConceptPropertyAssignmentBody(
        FunctionDeclaration function)
    {
        if (function.GenericTypeNames.Length != 1 ||
            function.ReturnType is not VoidType ||
            function.Body is not { Count: > 0 } body)
        {
            return false;
        }

        var typeParameter = function.GenericTypeNames[0];
        var receiverName = function.Parameters.FirstOrDefault()?.Name;
        return function.ConceptConstraints.Any(constraint =>
                constraint.ParameterName == typeParameter) &&
            function.Parameters.Count >= 1 &&
            function.Parameters[0].ParameterType is GenericType parameterType &&
            parameterType.Name == typeParameter &&
            receiverName is not null &&
            body.All(statement => statement is ExpressionStatement
                {
                    Expression: AssignmentExpression
                    {
                        Operator: "=" or "+=" or "-=" or "*=" or "/=" or "%=",
                        Target: MemberAccessExpression
                        {
                            Target: IdentifierExpression receiver,
                        } member,
                    },
                } && function.Parameters[0].Name == receiver.Identifier.ToString() &&
                member.MemberName.Length > 0) &&
            body.Cast<ExpressionStatement>().Select(statement =>
                (AssignmentExpression)statement.Expression).All(assignment =>
                    assignment.Operator == "="
                        ? !ContainsGenericConceptPropertyAccess(assignment.Value, receiverName) ||
                            IsGenericConceptPropertyExpression(assignment.Value, receiverName)
                        : ContainsGenericConceptPropertyAccess(assignment.Value, receiverName)
                            ? IsGenericConceptPropertyExpression(assignment.Value, receiverName)
                            : IsGenericConceptCompoundValueExpression(assignment.Value));
    }

    private static bool IsGenericConceptCompoundValueExpression(ExpressionBase expression) =>
        expression switch
        {
            LiteralExpression or IdentifierExpression => true,
            UnaryExpression
            {
                OperatorSymbol: null,
                Postfix: false,
                Operator: "+" or "-" or "~" or "!",
            } unary => IsGenericConceptCompoundValueExpression(unary.Operand),
            BinaryExpression
            {
                OperatorSymbol: null,
                Operator: "+" or "-" or "*" or "/" or "%" or
                    "==" or "!=" or "<" or "<=" or ">" or ">=" or
                    "&&" or "||" or "&" or "|" or "^" or "<<" or ">>",
            } binary => IsGenericConceptCompoundValueExpression(binary.Left) &&
                IsGenericConceptCompoundValueExpression(binary.Right),
            ConditionalExpression conditional =>
                IsGenericConceptCompoundValueExpression(conditional.Condition) &&
                IsGenericConceptCompoundValueExpression(conditional.WhenTrue) &&
                IsGenericConceptCompoundValueExpression(conditional.WhenFalse),
            _ => false,
        };

    private static bool IsGenericConceptMethodArgumentExpression(
        ExpressionBase expression,
        string receiverName) => expression switch
    {
        LiteralExpression or IdentifierExpression => true,
        MemberAccessExpression
        {
            Target: IdentifierExpression receiver,
            MemberName.Length: > 0,
        } => receiver.Identifier.ToString() == receiverName,
        InvocationExpression
        {
            Target: MemberAccessExpression
            {
                Target: IdentifierExpression receiver,
                MemberName.Length: > 0,
            },
        } invocation => receiver.Identifier.ToString() == receiverName &&
            invocation.Arguments.All(argument =>
                IsGenericConceptMethodArgumentExpression(argument, receiverName)),
        UnaryExpression
        {
            OperatorSymbol: null,
            Postfix: false,
            Operator: "+" or "-" or "~" or "!",
        } unary => IsGenericConceptMethodArgumentExpression(unary.Operand, receiverName),
        BinaryExpression
        {
            OperatorSymbol: null,
            Operator: "+" or "-" or "*" or "/" or "%" or
                "==" or "!=" or "<" or "<=" or ">" or ">=" or
                "&&" or "||" or "&" or "|" or "^" or "<<" or ">>",
        } binary => IsGenericConceptMethodArgumentExpression(binary.Left, receiverName) &&
            IsGenericConceptMethodArgumentExpression(binary.Right, receiverName),
        ConditionalExpression conditional =>
            IsGenericConceptMethodArgumentExpression(conditional.Condition, receiverName) &&
            IsGenericConceptMethodArgumentExpression(conditional.WhenTrue, receiverName) &&
            IsGenericConceptMethodArgumentExpression(conditional.WhenFalse, receiverName),
        _ => false,
    };

    private static bool IsGenericConceptMixedMutationBody(
        FunctionDeclaration function)
    {
        if (function.GenericTypeNames.Length != 1 ||
            function.ReturnType is not VoidType ||
            function.Body is not { Count: > 0 } body)
        {
            return false;
        }

        var typeParameter = function.GenericTypeNames[0];
        var receiverName = function.Parameters.FirstOrDefault()?.Name;
        if (receiverName is null ||
            !function.ConceptConstraints.Any(constraint =>
                constraint.ParameterName == typeParameter) ||
            function.Parameters[0].ParameterType is not GenericType parameterType ||
            parameterType.Name != typeParameter)
        {
            return false;
        }

        bool IsMutationStatement(
            StatementBase statement,
            bool insideLoop = false,
            bool insideSwitch = false) =>
            statement switch
        {
            ExpressionStatement
            {
                Expression: AssignmentExpression
                {
                    Operator: "=",
                    Target: MemberAccessExpression
                    {
                        Target: IdentifierExpression receiver,
                    } member,
                } assignment,
            } => receiver.Identifier.ToString() == receiverName &&
                member.MemberName.Length > 0 &&
                (!ContainsGenericConceptPropertyAccess(assignment.Value, receiverName) ||
                 IsGenericConceptPropertyExpression(assignment.Value, receiverName)),
            ExpressionStatement
            {
                Expression: InvocationExpression
                {
                    Target: MemberAccessExpression
                    {
                        Target: IdentifierExpression receiver,
                    },
                },
            } => receiver.Identifier.ToString() == receiverName,
            ExpressionStatement
            {
                Expression: AssignmentExpression
                {
                    Operator: "=",
                    Target: IdentifierExpression,
                } assignment,
            } => IsGenericConceptPropertyExpression(
                assignment.Value, receiverName),
            LocalVariableDeclarationStatement local =>
                local.DeclaredType is not GenericType &&
                local.Declarators.Count > 0 &&
                local.Declarators.All(declarator =>
                    declarator.Initializer is { } initializer &&
                    IsGenericConceptPropertyExpression(initializer, receiverName)),
            BlockStatement block => block.Statements.All(
                nested => IsMutationStatement(nested, insideLoop, insideSwitch)),
            IfStatement conditional =>
                IsGenericConceptMutationCondition(conditional.Condition, function) &&
                IsMutationStatement(
                    conditional.ThenStatement, insideLoop, insideSwitch) &&
                (conditional.ElseStatement is null ||
                 IsMutationStatement(
                    conditional.ElseStatement, insideLoop, insideSwitch)),
            WhileStatement loop =>
                IsGenericConceptMutationCondition(loop.Condition, function) &&
                IsMutationStatement(loop.Body, insideLoop: true, insideSwitch),
            DoWhileStatement loop =>
                IsMutationStatement(loop.Body, insideLoop: true, insideSwitch) &&
                IsGenericConceptMutationCondition(loop.Condition, function),
            ForStatement loop =>
                loop.DeclarationInitializer is { } initializer &&
                loop.InitializerExpressions.Count == 0 &&
                loop.Condition is { } condition &&
                loop.Iterators.Count > 0 &&
                loop.Iterators.All(IsGenericConceptMutationLocalAssignment) &&
                IsMutationStatement(initializer, insideLoop, insideSwitch) &&
                IsGenericConceptMutationCondition(condition, function) &&
                IsMutationStatement(loop.Body, insideLoop: true, insideSwitch),
            ForeachStatement loop =>
                IsGenericConceptMutationForeachCollection(
                    loop.Collection, function) &&
                IsMutationStatement(loop.Body, insideLoop: true, insideSwitch),
            SwitchStatement switchStatement =>
                IsGenericConceptMutationSwitchSelector(
                    switchStatement.Expression, function) &&
                switchStatement.Sections.Count > 0 &&
                switchStatement.Sections.All(section =>
                    section.Labels.Count > 0 &&
                    section.Labels.All(label =>
                        label.Filter is null &&
                        (label.IsDefault ||
                         label.Value is LiteralExpression)) &&
                    section.Statements.All(nested =>
                        IsMutationStatement(nested, insideLoop, insideSwitch: true))),
            BreakStatement => insideLoop || insideSwitch,
            ContinueStatement => insideLoop,
            ReturnStatement { Expression: null } => true,
            _ => false,
        };

        return body.All(statement => IsMutationStatement(statement));

        bool IsGenericConceptMutationLocalAssignment(ExpressionBase expression) =>
            expression is AssignmentExpression
            {
                Operator: "=",
                Target: IdentifierExpression,
            } assignment &&
            IsGenericConceptPropertyExpression(assignment.Value, receiverName);

        static bool IsGenericConceptMutationSwitchSelector(
            ExpressionBase expression,
            FunctionDeclaration declaration) =>
            expression is IdentifierExpression identifier &&
            declaration.Parameters.Any(parameter =>
                parameter.Name == identifier.Identifier.ToString() &&
                (parameter.ParameterType is IntTypeBase or BoolType or CharType ||
                 parameter.ParameterType is NamedType { ClassType: ClassType.Enum }));

        static bool IsGenericConceptMutationForeachCollection(
            ExpressionBase expression,
            FunctionDeclaration declaration)
        {
            static bool IsSupportedElementType(
                TypeBase elementType,
                IReadOnlyList<string> genericTypeNames) =>
                !ContainsFunctionTypeParameter(elementType, genericTypeNames) ||
                elementType is GenericType genericElement &&
                    genericElement.Name == genericTypeNames[0] ||
                elementType is ArrayType nestedArray &&
                    IsSupportedElementType(nestedArray.ElementType, genericTypeNames);

            return expression is IdentifierExpression identifier &&
                declaration.Parameters.Any(parameter =>
                parameter.Name == identifier.Identifier.ToString() &&
                parameter.ParameterType is ArrayType array &&
                IsSupportedElementType(array.ElementType, declaration.GenericTypeNames));
        }
    }

    private static bool IsGenericConceptMutationCondition(
        ExpressionBase expression,
        FunctionDeclaration function)
    {
        var localName = expression is IdentifierExpression identifierExpression
            ? identifierExpression.Identifier.ToString()
            : string.Empty;

        bool DeclaresBooleanLocal(StatementBase statement) => statement switch
        {
            LocalVariableDeclarationStatement
            {
                DeclaredType: BoolType,
            } local => local.Declarators.Any(declarator =>
                declarator.Name == localName),
            BlockStatement block => block.Statements.Any(DeclaresBooleanLocal),
            IfStatement conditional =>
                DeclaresBooleanLocal(conditional.ThenStatement) ||
                conditional.ElseStatement is { } elseStatement &&
                DeclaresBooleanLocal(elseStatement),
            WhileStatement loop => DeclaresBooleanLocal(loop.Body),
            DoWhileStatement loop => DeclaresBooleanLocal(loop.Body),
            ForStatement loop =>
                loop.DeclarationInitializer is { } initializer &&
                DeclaresBooleanLocal(initializer) ||
                DeclaresBooleanLocal(loop.Body),
            _ => false,
        };

        return expression switch
        {
            LiteralExpression { SourceText: "true" or "false" } => true,
            IdentifierExpression identifier =>
                function.Parameters.Any(parameter =>
                    parameter.Name == identifier.Identifier.ToString() &&
                    parameter.ParameterType is BoolType) ||
                function.Body?.Any(DeclaresBooleanLocal) == true,
            UnaryExpression { Operator: "!", OperatorSymbol: null } unary =>
                IsGenericConceptMutationCondition(unary.Operand, function),
            BinaryExpression
            {
                Operator: "&&" or "||",
                OperatorSymbol: null,
            } binary =>
                IsGenericConceptMutationCondition(binary.Left, function) &&
                IsGenericConceptMutationCondition(binary.Right, function),
            BinaryExpression
            {
                Operator: "==" or "!=" or "<" or "<=" or ">" or ">=",
                OperatorSymbol: null,
            } binary =>
                IsGenericConceptPropertyExpression(
                    binary.Left, function.Parameters[0].Name) &&
                IsGenericConceptPropertyExpression(
                    binary.Right, function.Parameters[0].Name),
            _ => false,
        };
    }
}

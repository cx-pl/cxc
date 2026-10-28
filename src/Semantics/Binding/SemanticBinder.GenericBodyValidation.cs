using CxCompiler.Model.Errors;
using CxCompiler.Model.Expressions;
using CxCompiler.Model.Statements;
using CxCompiler.Model.Types;
using CxCompiler.Model.Types.BuiltInTypes;

namespace CxCompiler.Semantics;

public sealed partial class SemanticBinder
{

    private static void ValidateGenericFunctionShape(FunctionDeclaration function)
    {
        // Async lowering runs after binding, so recognize supported pending-await
        // source forms before generic body validation examines them.
        if (function.IsAsync && function.Body is
            [ReturnStatement { Expression: AwaitExpression }])
        {
            return;
        }
        if (function.IsAsync && IsGenericAsyncAwaitLocalReturnBody(function))
        {
            return;
        }
        if (function.IsAsync && IsGenericAsyncExplicitAwaitSourceReturnBody(function))
        {
            return;
        }
        if (function.GenericTypeNames.Length == 0)
        {
            return;
        }
        if (function.Body is [ReturnStatement { Expression: TypeOfExpression or NameOfExpression }] ||
            function.Body is [ReturnStatement { Expression: CastExpression { IsSafe: true, Operand: IdentifierExpression } }])
        {
            return;
        }
        if (function.Body is null)
        {
            if (function.ParentClassDeclaration?.ClassType == ClassType.Interface)
            {
                return;
            }
            if (function.MemberModifiers.Contains(MemberModifier.Extern) ||
                function.MemberModifiers.Contains(MemberModifier.Abstract))
            {
                return;
            }
        }
        else if (!function.MemberModifiers.Contains(MemberModifier.Extern) &&
            function.Body is [ReturnStatement { Expression: IdentifierExpression identifier }] &&
            function.Parameters.Any(parameter =>
                parameter.Name == identifier.Identifier.ToString() &&
                IsDirectGenericIdentityType(function.ReturnType, parameter.ParameterType)))
        {
            return;
        }
        else if (!function.MemberModifiers.Contains(MemberModifier.Extern) &&
            IsGenericForwardCallReturnBody(function))
        {
            return;
        }
        else if (!function.MemberModifiers.Contains(MemberModifier.Extern) &&
            IsGenericVoidForwardCallBody(function))
        {
            return;
        }
        else if (!function.MemberModifiers.Contains(MemberModifier.Extern) &&
            IsGenericDefaultReturnBody(function))
        {
            return;
        }
        else if (!function.MemberModifiers.Contains(MemberModifier.Extern) &&
            IsGenericDefaultLocalReturnBody(function))
        {
            return;
        }
        else if (!function.MemberModifiers.Contains(MemberModifier.Extern) &&
            IsGenericConceptConditionalReturnBody(function))
        {
            return;
        }
        else if (!function.MemberModifiers.Contains(MemberModifier.Extern) &&
            IsGenericConceptOperatorReturnBody(function))
        {
            return;
        }
        else if (!function.MemberModifiers.Contains(MemberModifier.Extern) &&
            IsGenericConceptUnaryReturnBody(function))
        {
            return;
        }
        else if (!function.MemberModifiers.Contains(MemberModifier.Extern) &&
            IsGenericConceptMethodReturnBody(function))
        {
            return;
        }
        else if (!function.MemberModifiers.Contains(MemberModifier.Extern) &&
            IsGenericConceptMethodCallBody(function))
        {
            return;
        }
        else if (!function.MemberModifiers.Contains(MemberModifier.Extern) &&
            IsGenericConceptPropertyReturnBody(function))
        {
            return;
        }
        else if (!function.MemberModifiers.Contains(MemberModifier.Extern) &&
            IsGenericConceptPropertyAssignmentBody(function))
        {
            return;
        }
        else if (!function.MemberModifiers.Contains(MemberModifier.Extern) &&
            IsGenericConceptMixedMutationBody(function))
        {
            return;
        }
        else if (!function.MemberModifiers.Contains(MemberModifier.Extern) &&
            IsGenericConstrainedMemberReturnBody(function))
        {
            return;
        }
        else if (!function.MemberModifiers.Contains(MemberModifier.Extern) &&
            IsGenericConstrainedMemberLocalReturnBody(function))
        {
            return;
        }
        else if (!function.MemberModifiers.Contains(MemberModifier.Extern) &&
            IsGenericConstrainedFieldReturnBody(function))
        {
            return;
        }
        else if (!function.MemberModifiers.Contains(MemberModifier.Extern) &&
            IsGenericConstrainedPropertyAssignmentBody(function))
        {
            return;
        }
        else if (!function.MemberModifiers.Contains(MemberModifier.Extern) &&
            IsGenericConstrainedVoidMethodCallBody(function))
        {
            return;
        }
        else if (!function.MemberModifiers.Contains(MemberModifier.Extern) &&
            IsGenericArrayElementReturnBody(function))
        {
            return;
        }
        else if (!function.MemberModifiers.Contains(MemberModifier.Extern) &&
            IsGenericArrayGetReturnBody(function))
        {
            return;
        }
        else if (!function.MemberModifiers.Contains(MemberModifier.Extern) &&
            IsGenericArrayElementLocalReturnBody(function))
        {
            return;
        }
        else if (!function.MemberModifiers.Contains(MemberModifier.Extern) &&
            IsGenericArrayLengthReturnBody(function))
        {
            return;
        }
        else if (!function.MemberModifiers.Contains(MemberModifier.Extern) &&
            IsGenericArrayLengthComparisonBody(function))
        {
            return;
        }
        else if (!function.MemberModifiers.Contains(MemberModifier.Extern) &&
            IsGenericReferenceNullComparisonBody(function))
        {
            return;
        }
        else if (!function.MemberModifiers.Contains(MemberModifier.Extern) &&
            IsGenericNullCoalescingReturnBody(function))
        {
            return;
        }
        else if (!function.MemberModifiers.Contains(MemberModifier.Extern) &&
            IsGenericArrayElementCopyBody(function))
        {
            return;
        }
        else if (!function.MemberModifiers.Contains(MemberModifier.Extern) &&
            IsGenericArrayElementAssignmentBody(function))
        {
            return;
        }
        else if (!function.MemberModifiers.Contains(MemberModifier.Extern) &&
            IsGenericArrayConstructionBody(function))
        {
            return;
        }
        else if (!function.MemberModifiers.Contains(MemberModifier.Extern) &&
            IsGenericArrayCopyBody(function))
        {
            return;
        }
        else if (!function.MemberModifiers.Contains(MemberModifier.Extern) &&
            IsGenericArrayCopyWhileBody(function))
        {
            return;
        }
        else if (!function.MemberModifiers.Contains(MemberModifier.Extern) &&
            IsGenericArrayCopyForeachBody(function))
        {
            return;
        }
        else if (!function.MemberModifiers.Contains(MemberModifier.Extern) &&
            IsGenericLocalCopyBody(function))
        {
            return;
        }
        else if (!function.MemberModifiers.Contains(MemberModifier.Extern) &&
            IsGenericLocalCopyAssignmentBody(function))
        {
            return;
        }
        else if (!function.MemberModifiers.Contains(MemberModifier.Extern) &&
            IsGenericLocalSequenceBody(function))
        {
            return;
        }
        else if (!function.MemberModifiers.Contains(MemberModifier.Extern) &&
            IsGenericConditionalLocalReturnBody(function))
        {
            return;
        }
        else if (!function.MemberModifiers.Contains(MemberModifier.Extern) &&
            IsGenericConditionalReturnBody(function))
        {
            return;
        }
        throw new CompilationErrorException(
            $"Generic function '{function.FullName}' currently supports only an extern " +
            "declaration, a direct type-parameter or default return, a default-initialized " +
            "matching local copy, one local copy, or a " +
            "single assignment to a local copy, a matching array element or array length, or " +
            "a comparison against a matching generic array length, one matching " +
            "generic array element read through indexing or Get, assignment or copy " +
            "or construction, a matching " +
            "indexed generic array copy loop, matching generic values selected by a " +
            "conditional expression, a constrained class member result, " +
            "constraint, or return-only boolean conditional control flow that returns " +
            "matching type-parameter values or arrays.");
    }

    private static bool IsGenericAsyncAwaitLocalReturnBody(FunctionDeclaration function)
    {
        if (function.Body is not { Count: >= 2 } body ||
            body[^1] is not ReturnStatement
            {
                Expression: { } resultExpression,
            } || resultExpression is AwaitExpression ||
            body.Take(body.Count - 1).Any(statement => statement is ReturnStatement))
        {
            return false;
        }

        return body.Take(body.Count - 1).Any(statement => statement is
            LocalVariableDeclarationStatement
            {
                DeclaredType: AutoType { Const: false },
                Declarators:
                [
                    {
                        Initializer: AwaitExpression,
                    },
                ],
            });
    }

    private static bool IsGenericAsyncExplicitAwaitSourceReturnBody(
        FunctionDeclaration function)
    {
        if (function.Body is not { Count: >= 2 } body ||
            body[^1] is not ReturnStatement
            {
                Expression: AwaitExpression
                {
                    Operand: IdentifierExpression awaitedSource,
                },
            })
        {
            return false;
        }

        var prefix = body.Take(body.Count - 1).ToArray();
        if (prefix.Any(statement =>
                statement is not LocalVariableDeclarationStatement
                {
                    Declarators: [{ Initializer: not null }],
                } || CoroutineSyntaxAnalysis.StatementContainsAwait(statement)))
        {
            return false;
        }

        return prefix[^1] is LocalVariableDeclarationStatement
            {
                Declarators: [{ } sourceDeclarator],
            } && sourceDeclarator.Name == awaitedSource.Identifier.ToString();
    }

    private static bool IsGenericDefaultReturnBody(FunctionDeclaration function) =>
        function.Body is
            [ReturnStatement { Expression: LiteralExpression { SourceText: "default" } }] &&
        ContainsFunctionTypeParameter(function.ReturnType, function.GenericTypeNames);

    private static bool IsGenericForwardCallReturnBody(FunctionDeclaration function)
    {
        var invocation = GetGenericForwardCallInvocation(function);
        if (function.ParentClassDeclaration is { } parent &&
                (parent.ClassType is not (ClassType.Class or ClassType.Struct) ||
                    parent.IsStatic) ||
            function.LambdaOwner is not null ||
            function.IsExtension ||
            function.GenericTypeNames.Length == 0 || invocation is null ||
            invocation.Arguments.Count == 0 ||
            invocation.Arguments.Any(argument =>
                !IsPotentialGenericForwardArgument(argument, function)) ||
            function.ReturnType is not GenericType returnType ||
            !function.GenericTypeNames.Contains(returnType.Name, StringComparer.Ordinal))
        {
            return false;
        }

        return invocation.Arguments.OfType<IdentifierExpression>().Any(argument =>
            function.Parameters.Any(parameter =>
                parameter.Name == argument.Identifier.ToString() &&
                parameter.ParameterType is GenericType parameterType &&
                parameterType.Name == returnType.Name) ||
            GetGenericForwardAliasTypeParameter(
                function, argument.Identifier.ToString()) == returnType.Name);
    }

    private static InvocationExpression? GetGenericForwardCallInvocation(
        FunctionDeclaration function)
    {
        if (function.Body is [ReturnStatement
            { Expression: InvocationExpression returnedInvocation }])
        {
            return returnedInvocation;
        }
        if (function.Body is [ExpressionStatement
            { Expression: InvocationExpression expressionInvocation }])
        {
            return expressionInvocation;
        }
        var aliasInvocation = function.Body switch
        {
            [LocalVariableDeclarationStatement,
                ReturnStatement { Expression: InvocationExpression returnAliasInvocation }] =>
                returnAliasInvocation,
            [LocalVariableDeclarationStatement,
                ExpressionStatement { Expression: InvocationExpression statementAliasInvocation }] =>
                statementAliasInvocation,
            _ => null,
        };
        if (aliasInvocation is not null &&
            aliasInvocation.Arguments.OfType<IdentifierExpression>().Any(argument =>
                GetGenericForwardAliasTypeParameter(
                    function, argument.Identifier.ToString()) is not null))
        {
            return aliasInvocation;
        }
        if (function.Body is
                [LocalVariableDeclarationStatement,
                    ReturnStatement { Expression: ConditionalExpression conditional }])
        {
            if (conditional.WhenTrue is InvocationExpression whenTrue &&
                conditional.WhenFalse is IdentifierExpression falseFallback &&
                GetGenericForwardAliasTypeParameter(
                    function, falseFallback.Identifier.ToString()) is not null &&
                whenTrue.Arguments.OfType<IdentifierExpression>().Any(argument =>
                    GetGenericForwardAliasTypeParameter(
                        function, argument.Identifier.ToString()) is not null))
            {
                return whenTrue;
            }
            if (conditional.WhenFalse is InvocationExpression whenFalse &&
                conditional.WhenTrue is IdentifierExpression trueFallback &&
                GetGenericForwardAliasTypeParameter(
                    function, trueFallback.Identifier.ToString()) is not null &&
                whenFalse.Arguments.OfType<IdentifierExpression>().Any(argument =>
                    GetGenericForwardAliasTypeParameter(
                        function, argument.Identifier.ToString()) is not null))
            {
                return whenFalse;
            }
        }
        if (function.Body is
            [LocalVariableDeclarationStatement local,
                ReturnStatement { Expression: IdentifierExpression returned }] &&
            local.Declarators is [var declarator] &&
            declarator.Name == returned.Identifier.ToString() &&
            declarator.Initializer is InvocationExpression invocation &&
            (IsDirectGenericIdentityType(function.ReturnType,
                declarator.Type ?? local.DeclaredType) ||
                local.DeclaredType is AutoType &&
                function.ReturnType is GenericType returnType &&
                function.GenericTypeNames.Contains(
                    returnType.Name, StringComparer.Ordinal)))
        {
            return invocation;
        }
        if (function.Body is [ReturnStatement
            { Expression: ConditionalExpression simpleConditional }])
        {
            static bool IsMatchingReturnParameter(
                FunctionDeclaration declaration,
                ExpressionBase expression) =>
                expression is IdentifierExpression identifier &&
                declaration.Parameters.Any(parameter =>
                    parameter.Name == identifier.Identifier.ToString() &&
                    IsDirectGenericIdentityType(
                        declaration.ReturnType, parameter.ParameterType));

            if (simpleConditional.WhenTrue is InvocationExpression whenTrue &&
                IsMatchingReturnParameter(function, simpleConditional.WhenFalse))
            {
                return whenTrue;
            }
            if (simpleConditional.WhenFalse is InvocationExpression whenFalse &&
                IsMatchingReturnParameter(function, simpleConditional.WhenTrue))
            {
                return whenFalse;
            }
        }
        return GetGenericForwardStatementCallInvocation(function);
    }

    private static InvocationExpression? GetGenericForwardStatementCallInvocation(
        FunctionDeclaration function)
    {
        if (function.Body is not { } body)
        {
            return null;
        }

        var booleanLocals = new HashSet<string>(StringComparer.Ordinal);
        var flowStart = 0;
        var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
        while (flowStart < body.Count &&
            body[flowStart] is LocalVariableDeclarationStatement local)
        {
            var isInferredBooleanLocal = local.DeclaredType is AutoType &&
                local.Declarators.Count > 0 && local.Declarators.All(declarator =>
                    declarator.Initializer is { } initializer &&
                    IsBooleanExpression(initializer, function, booleanLocals));
            if (local.DeclaredType is BoolType || isInferredBooleanLocal)
            {
                if (local.Declarators.Count == 0)
                {
                    return null;
                }
                foreach (var declarator in local.Declarators)
                {
                    if (declarator.Initializer is not { } initializer ||
                        !IsBooleanExpression(initializer, function, booleanLocals) ||
                        !booleanLocals.Add(declarator.Name))
                    {
                        return null;
                    }
                }
            }
            else if (local.Declarators is [var aliasDeclarator] &&
                GetGenericForwardAliasSourceTypeParameter(
                    function, aliasDeclarator.Name) is { } aliasTypeParameter &&
                function.ReturnType is GenericType returnType &&
                aliasTypeParameter == returnType.Name)
            {
                aliases.Add(aliasDeclarator.Name, aliasTypeParameter);
            }
            else
            {
                return null;
            }
            flowStart++;
        }

        var flow = body.Skip(flowStart).ToArray();
        if (flow is [IfStatement statementIf,
            ReturnStatement { Expression: IdentifierExpression fallback }] &&
            statementIf.ElseStatement is null &&
            IsBooleanExpression(statementIf.Condition, function, booleanLocals) &&
            GetSingleForwardReturnStatement(statementIf.ThenStatement) is
                { Expression: InvocationExpression thenInvocation } &&
            (IsMatchingForwardReturnParameter(function, fallback) ||
                IsMatchingForwardReturnAlias(function, fallback, aliases)))
        {
            return thenInvocation;
        }
        if (flow is [IfStatement statementIfElse] &&
            IsBooleanExpression(statementIfElse.Condition, function, booleanLocals) &&
            GetSingleForwardReturnStatement(statementIfElse.ElseStatement) is
            { Expression: IdentifierExpression elseFallback } &&
            GetSingleForwardReturnStatement(statementIfElse.ThenStatement) is
            { Expression: InvocationExpression ifElseThenInvocation } &&
            (IsMatchingForwardReturnParameter(function, elseFallback) ||
                IsMatchingForwardReturnAlias(function, elseFallback, aliases)))
        {
            return ifElseThenInvocation;
        }
        if (flow is [IfStatement reverseStatementIf] &&
            IsBooleanExpression(reverseStatementIf.Condition, function, booleanLocals) &&
            GetSingleForwardReturnStatement(reverseStatementIf.ThenStatement) is
            { Expression: IdentifierExpression thenFallback } &&
            GetSingleForwardReturnStatement(reverseStatementIf.ElseStatement) is
            { Expression: InvocationExpression elseInvocation } &&
            (IsMatchingForwardReturnParameter(function, thenFallback) ||
                IsMatchingForwardReturnAlias(function, thenFallback, aliases)))
        {
            return elseInvocation;
        }
        return null;
    }

    private static bool IsMatchingForwardReturnAlias(
        FunctionDeclaration function,
        IdentifierExpression identifier,
        IReadOnlyDictionary<string, string> aliases) =>
        aliases.TryGetValue(identifier.Identifier.ToString(), out var aliasTypeParameter) &&
        function.ReturnType is GenericType returnType &&
        returnType.Name == aliasTypeParameter;

    private static ReturnStatement? GetSingleForwardReturnStatement(
        StatementBase? statement) => statement switch
    {
        ReturnStatement returnStatement => returnStatement,
        BlockStatement { Statements: [ReturnStatement returnStatement] } => returnStatement,
        _ => null,
    };

    private static bool IsMatchingForwardReturnParameter(
        FunctionDeclaration function,
        IdentifierExpression identifier) =>
        function.ReturnType is GenericType returnType &&
        function.Parameters.Any(parameter =>
            parameter.Name == identifier.Identifier.ToString() &&
            parameter.ParameterType is GenericType parameterType &&
            parameterType.Name == returnType.Name);

    private static bool IsGenericVoidForwardCallBody(FunctionDeclaration function)
    {
        if (function.ReturnType is not VoidType ||
            function.ParentClassDeclaration is { } parent &&
                (parent.ClassType is not (ClassType.Class or ClassType.Struct) ||
                    parent.IsStatic) ||
            function.LambdaOwner is not null || function.IsExtension ||
            function.GenericTypeNames.Length == 0)
        {
            return false;
        }

        var invocation = function.Body switch
        {
            [ExpressionStatement { Expression: InvocationExpression directInvocation }] =>
                directInvocation,
            [LocalVariableDeclarationStatement,
                ExpressionStatement { Expression: InvocationExpression aliasInvocation }] =>
                aliasInvocation.Arguments.OfType<IdentifierExpression>().Any(argument =>
                    GetGenericForwardAliasTypeParameter(
                        function, argument.Identifier.ToString()) is not null)
                    ? aliasInvocation
                    : null,
            _ => null,
        };
        if (invocation is null ||
            invocation.Arguments.Count == 0 ||
            invocation.Arguments.Any(argument =>
                !IsPotentialGenericForwardArgument(argument, function)))
        {
            return false;
        }

        return invocation.Arguments.OfType<IdentifierExpression>().Any(argument =>
            function.Parameters.SingleOrDefault(candidate =>
                candidate.Name == argument.Identifier.ToString())?.ParameterType is
                GenericType genericParameter &&
                function.GenericTypeNames.Contains(
                    genericParameter.Name, StringComparer.Ordinal) ||
            GetGenericForwardAliasTypeParameter(
                function, argument.Identifier.ToString()) is { } aliasTypeParameter &&
                function.GenericTypeNames.Contains(
                    aliasTypeParameter, StringComparer.Ordinal));
    }

    private static string? GetGenericForwardAliasTypeParameter(
        FunctionDeclaration function,
        string aliasName)
    {
        var sourceTypeParameter = GetGenericForwardAliasSourceTypeParameter(
            function, aliasName);
        if (sourceTypeParameter is null)
        {
            return null;
        }

        if (function.Body is { Count: 2 } body)
        {
            var terminal = body[1];
            var (invocation, fallback) = terminal switch
            {
                ReturnStatement { Expression: InvocationExpression directInvocation } =>
                    (directInvocation, (ExpressionBase?)null),
                ExpressionStatement { Expression: InvocationExpression voidInvocation } =>
                    (voidInvocation, null),
                ReturnStatement { Expression: ConditionalExpression trueConditional }
                    when trueConditional.WhenTrue is InvocationExpression trueInvocation =>
                    (trueInvocation, trueConditional.WhenFalse),
                ReturnStatement { Expression: ConditionalExpression falseConditional }
                    when falseConditional.WhenFalse is InvocationExpression falseInvocation =>
                    (falseInvocation, falseConditional.WhenTrue),
                _ => (null, null),
            };
            if (invocation is not null &&
                invocation.Arguments.OfType<IdentifierExpression>().Any(argument =>
                    argument.Identifier.ToString() == aliasName) &&
                (fallback is null || fallback is IdentifierExpression fallbackIdentifier &&
                    (fallbackIdentifier.Identifier.ToString() == aliasName ||
                        function.Parameters.Any(parameter =>
                            parameter.Name == fallbackIdentifier.Identifier.ToString() &&
                            parameter.ParameterType is GenericType fallbackType &&
                            fallbackType.Name == sourceTypeParameter) ||
                        GetGenericForwardAliasSourceTypeParameter(
                            function, fallbackIdentifier.Identifier.ToString()) ==
                            sourceTypeParameter)))
            {
                return sourceTypeParameter;
            }
        }

        var statementInvocation = GetGenericForwardStatementCallInvocation(function);
        if (statementInvocation is not null &&
            statementInvocation.Arguments.OfType<IdentifierExpression>().Any(argument =>
                argument.Identifier.ToString() == aliasName))
        {
            return sourceTypeParameter;
        }
        return null;
    }

    private static string? GetGenericForwardAliasSourceTypeParameter(
        FunctionDeclaration function,
        string aliasName)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var localTypes = new List<TypeBase>();
        var currentName = aliasName;
        while (visited.Add(currentName))
        {
            var matchingLocals = function.Body?
                .OfType<LocalVariableDeclarationStatement>()
                .Where(local => local.Declarators is [var declarator] &&
                    declarator.Name == currentName)
                .ToArray();
            if (matchingLocals is not [var local] ||
                local.Declarators is not [var declarator] ||
                declarator.Initializer is not IdentifierExpression source)
            {
                return null;
            }
            localTypes.Add(local.DeclaredType);

            var sourceName = source.Identifier.ToString();
            if (function.Parameters.SingleOrDefault(parameter =>
                    parameter.Name == sourceName)?.ParameterType is GenericType parameterType)
            {
                return localTypes.All(localType =>
                    localType is AutoType ||
                    IsDirectGenericIdentityType(localType, parameterType))
                    ? parameterType.Name
                    : null;
            }
            currentName = sourceName;
        }
        return null;
    }

    private static bool IsPotentialGenericForwardArgument(
        ExpressionBase expression,
        FunctionDeclaration function) => expression switch
    {
        IdentifierExpression identifier => function.Parameters.Any(parameter =>
            parameter.Name == identifier.Identifier.ToString()) ||
            GetGenericForwardAliasTypeParameter(
                function, identifier.Identifier.ToString()) is not null,
        LiteralExpression { SourceText: not "default" } => true,
        UnaryExpression unary when !unary.Postfix && unary.Operator is not ("++" or "--") =>
            IsPotentialGenericForwardArgument(unary.Operand, function),
        BinaryExpression binary =>
            IsPotentialGenericForwardArgument(binary.Left, function) &&
            IsPotentialGenericForwardArgument(binary.Right, function),
        ConditionalExpression conditional =>
            IsPotentialGenericForwardArgument(conditional.Condition, function) &&
            IsPotentialGenericForwardArgument(conditional.WhenTrue, function) &&
            IsPotentialGenericForwardArgument(conditional.WhenFalse, function),
        _ => false,
    };

    private static bool IsSupportedClosedGenericForwardArgument(
        ExpressionBase expression,
        FunctionDeclaration function) => expression switch
    {
        IdentifierExpression identifier => function.Parameters
            .SingleOrDefault(parameter =>
                parameter.Name == identifier.Identifier.ToString()) is { } parameter &&
            !ContainsFunctionTypeParameter(parameter.ParameterType, function.GenericTypeNames),
        LiteralExpression { SourceText: not "default" } literal =>
            literal.InferredType is { } literalType &&
            !ContainsFunctionTypeParameter(literalType, function.GenericTypeNames),
        UnaryExpression unary =>
            unary.OperatorSymbol is null && !unary.Postfix &&
            unary.Operator is not ("++" or "--") &&
            unary.InferredType is { } unaryType &&
            !ContainsFunctionTypeParameter(unaryType, function.GenericTypeNames) &&
            IsSupportedClosedGenericForwardArgument(unary.Operand, function),
        BinaryExpression binary =>
            binary.OperatorSymbol is null &&
            binary.InferredType is { } binaryType &&
            !ContainsFunctionTypeParameter(binaryType, function.GenericTypeNames) &&
            IsSupportedClosedGenericForwardArgument(binary.Left, function) &&
            IsSupportedClosedGenericForwardArgument(binary.Right, function),
        ConditionalExpression conditional =>
            conditional.InferredType is { } conditionalType &&
            !ContainsFunctionTypeParameter(conditionalType, function.GenericTypeNames) &&
            IsSupportedClosedGenericForwardArgument(conditional.Condition, function) &&
            IsSupportedClosedGenericForwardArgument(conditional.WhenTrue, function) &&
            IsSupportedClosedGenericForwardArgument(conditional.WhenFalse, function),
        _ => false,
    };

    private static bool IsSupportedGenericForwardArgument(
        ExpressionBase expression,
        FunctionDeclaration function) =>
        expression is IdentifierExpression identifier &&
        (function.Parameters.SingleOrDefault(parameter =>
            parameter.Name == identifier.Identifier.ToString())?.ParameterType is
            GenericType genericParameter &&
            function.GenericTypeNames.Contains(genericParameter.Name, StringComparer.Ordinal) ||
            GetGenericForwardAliasTypeParameter(
                function, identifier.Identifier.ToString()) is { } aliasTypeParameter &&
            function.GenericTypeNames.Contains(aliasTypeParameter, StringComparer.Ordinal)) ||
        IsSupportedClosedGenericForwardArgument(expression, function);

    private static bool IsGenericNullCoalescingReturnBody(FunctionDeclaration function)
    {
        if (!ContainsFunctionTypeParameter(function.ReturnType, function.GenericTypeNames) ||
            function.Body is not
                [ReturnStatement { Expression: NullCoalescingExpression coalescing }])
        {
            return false;
        }

        bool IsMatchingParameter(ExpressionBase expression) =>
            expression is IdentifierExpression identifier &&
            function.Parameters.Any(parameter =>
                parameter.Name == identifier.Identifier.ToString() &&
                IsDirectGenericIdentityType(function.ReturnType, parameter.ParameterType));

        bool IsSupportedOperand(ExpressionBase expression) => expression switch
        {
            LiteralExpression { SourceText: "null" } => true,
            NullCoalescingExpression nested =>
                IsSupportedOperand(nested.Left) && IsSupportedOperand(nested.Right),
            _ => IsMatchingParameter(expression),
        };

        bool ContainsMatchingParameter(ExpressionBase expression) => expression switch
        {
            NullCoalescingExpression nested =>
                ContainsMatchingParameter(nested.Left) ||
                ContainsMatchingParameter(nested.Right),
            _ => IsMatchingParameter(expression),
        };

        return IsSupportedOperand(coalescing) && ContainsMatchingParameter(coalescing);
    }

    private static bool IsGenericDefaultLocalReturnBody(FunctionDeclaration function)
    {
        if (function.Body is not
                [LocalVariableDeclarationStatement local,
                    ReturnStatement { Expression: IdentifierExpression returned }] ||
            local.DeclaredType is null ||
            local.Declarators is not [var declarator] ||
            declarator.Initializer is not LiteralExpression { SourceText: "default" } ||
            returned.Identifier.ToString() != declarator.Name ||
            !ContainsFunctionTypeParameter(function.ReturnType, function.GenericTypeNames))
        {
            return false;
        }

        return IsDirectGenericIdentityType(function.ReturnType, local.DeclaredType);
    }

    private static bool IsGenericArrayElementReturnBody(FunctionDeclaration function)
    {
        if (function.Body is not [ReturnStatement { Expression: { } expression }] ||
            !ContainsFunctionTypeParameter(function.ReturnType, function.GenericTypeNames))
        {
            return false;
        }

        var depth = 0;
        while (expression is ArrayAccessExpression { Indices.Count: 1 } access)
        {
            depth++;
            expression = access.Target;
        }

        if (depth == 0 || expression is not IdentifierExpression target)
        {
            return false;
        }

        var parameter = function.Parameters.SingleOrDefault(item =>
            item.Name == target.Identifier.ToString());
        var parameterType = parameter?.ParameterType;
        for (var index = 0; index < depth; index++)
        {
            if (parameterType is not ArrayType array)
            {
                return false;
            }
            parameterType = array.ElementType;
        }

        return parameterType is not null &&
            IsDirectGenericIdentityType(function.ReturnType, parameterType);
    }

    private static bool IsGenericArrayGetReturnBody(FunctionDeclaration function)
    {
        if (function.Body is not
                [ReturnStatement
                {
                    Expression: InvocationExpression
                    {
                        Target: MemberAccessExpression
                        {
                            MemberName: "Get",
                            Target: IdentifierExpression arrayIdentifier,
                        },
                        Arguments.Count: 1,
                    },
                } invocation] ||
            !ContainsFunctionTypeParameter(function.ReturnType, function.GenericTypeNames))
        {
            return false;
        }

        var parameter = function.Parameters.SingleOrDefault(item =>
            item.Name == arrayIdentifier.Identifier.ToString());
        return parameter is not null &&
            TryGetArrayElementTypeForGenericShape(
                parameter.ParameterType, out var elementType) &&
            ContainsFunctionTypeParameter(elementType, function.GenericTypeNames) &&
            IsDirectGenericIdentityType(function.ReturnType, elementType);
    }

    private static bool TryGetArrayElementTypeForGenericShape(
        TypeBase type,
        out TypeBase elementType)
    {
        if (ArrayType.TryGetElementType(type, out elementType))
        {
            return true;
        }
        if (type is NamedType
            {
                TypeArguments.Count: 1,
            } namedArray &&
            namedArray.Name is "Array" or "System.Collections.Array")
        {
            elementType = namedArray.TypeArguments[0];
            return true;
        }
        elementType = null!;
        return false;
    }

    private static bool IsGenericArrayElementLocalReturnBody(FunctionDeclaration function)
    {
        if (function.Body is not { Count: >= 2 } body ||
            body[^1] is not ReturnStatement
            {
                Expression: IdentifierExpression returnIdentifier,
            } ||
            body[0] is not LocalVariableDeclarationStatement
            {
                DeclaredType: { } declaredType,
                Declarators:
                [
                    {
                        Initializer: ArrayAccessExpression access,
                    } firstDeclarator,
                ],
            } ||
            !TryGetGenericArrayElementType(
                function, access, out _, out var elementType, out _))
        {
            return false;
        }

        if (elementType is null)
        {
            return false;
        }

        var boundLocalType = declaredType is AutoType
            ? elementType
            : GenericTypeSubstitution.BindParameters(declaredType, function.GenericTypeNames);
        if (
            !ContainsFunctionTypeParameter(boundLocalType, function.GenericTypeNames) ||
            !IsDirectGenericIdentityType(function.ReturnType, boundLocalType) ||
            !IsDirectGenericIdentityType(boundLocalType, elementType) ||
            !ContainsFunctionTypeParameter(elementType, function.GenericTypeNames))
        {
            return false;
        }

        var values = function.Parameters.ToDictionary(
            parameter => parameter.Name,
            parameter => parameter.ParameterType,
            StringComparer.Ordinal);
        var localNames = new HashSet<string>(StringComparer.Ordinal);
        values.Add(firstDeclarator.Name, boundLocalType);
        localNames.Add(firstDeclarator.Name);
        for (var index = 1; index < body.Count - 1; index++)
        {
            switch (body[index])
            {
                case LocalVariableDeclarationStatement local when local.DeclaredType is not null:
                {
                    if (local.Declarators.Count == 0)
                    {
                        return false;
                    }
                    foreach (var declarator in local.Declarators)
                    {
                        if (declarator.Initializer is not IdentifierExpression source ||
                            !values.TryGetValue(source.Identifier.ToString(), out var sourceType) ||
                            !IsDirectGenericIdentityType(function.ReturnType, sourceType) ||
                            values.ContainsKey(declarator.Name))
                        {
                            return false;
                        }
                        var localType = local.DeclaredType is AutoType
                            ? sourceType
                            : GenericTypeSubstitution.BindParameters(
                                local.DeclaredType,
                                function.GenericTypeNames);
                        if (!IsDirectGenericIdentityType(function.ReturnType, localType))
                        {
                            return false;
                        }
                        values.Add(declarator.Name, localType);
                        localNames.Add(declarator.Name);
                    }
                    break;
                }
                case ExpressionStatement
                {
                    Expression: AssignmentExpression
                    {
                        Target: IdentifierExpression target,
                        Operator: "=",
                        Value: IdentifierExpression replacement,
                    },
                } when localNames.Contains(target.Identifier.ToString()) &&
                    values.TryGetValue(replacement.Identifier.ToString(), out var replacementType) &&
                    IsDirectGenericIdentityType(function.ReturnType, replacementType):
                    break;
                default:
                    return false;
            }
        }

        return localNames.Contains(returnIdentifier.Identifier.ToString()) &&
            values.TryGetValue(returnIdentifier.Identifier.ToString(), out var returnType) &&
            IsDirectGenericIdentityType(function.ReturnType, returnType);
    }

    private static bool IsGenericReferenceNullComparisonBody(FunctionDeclaration function)
    {
        if (function.ReturnType is not BoolType ||
            function.Body is not
                [ReturnStatement { Expression: { } expression }])
        {
            return false;
        }

        bool IsConstrainedGenericParameter(ExpressionBase expression) =>
            expression is IdentifierExpression identifier &&
            function.Parameters.Any(parameter =>
            {
                if (parameter.Name != identifier.Identifier.ToString())
                {
                    return false;
                }
                if (parameter.ParameterType is ArrayType array)
                {
                    return ContainsFunctionTypeParameter(
                        array.ElementType, function.GenericTypeNames);
                }
                if (parameter.ParameterType is not GenericType genericType ||
                    !function.GenericTypeNames.Contains(genericType.Name))
                {
                    return false;
                }
                return function.GenericConstraints.Any(constraint =>
                    constraint.ParameterName == genericType.Name &&
                    (constraint.Kind == GenericConstraintKind.ReferenceType ||
                     constraint.Kind == GenericConstraintKind.Type &&
                     constraint.Types.Any(type => type is NamedType
                     {
                         ClassType: ClassType.Class or ClassType.Interface,
                     })));
            });

        static bool IsNull(ExpressionBase expression) =>
            expression is LiteralExpression { SourceText: "null" };

        bool IsNullComparison(ExpressionBase candidate) =>
            candidate is BinaryExpression
            {
                Operator: "==" or "!=",
                OperatorSymbol: null,
            } comparison &&
            (IsConstrainedGenericParameter(comparison.Left) && IsNull(comparison.Right) ||
             IsNull(comparison.Left) && IsConstrainedGenericParameter(comparison.Right));

        bool IsGenericArrayLength(ExpressionBase candidate) =>
            candidate is MemberAccessExpression
            {
                Target: { } target,
                MemberName: "Length",
            } && IsGenericArrayLengthExpression(target, function);

        bool IsUnsignedOperand(ExpressionBase candidate) => candidate switch
        {
            LiteralExpression literal =>
                literal.SourceText.EndsWith('u') ||
                uint.TryParse(literal.SourceText, out _),
            IdentifierExpression identifier => function.Parameters.Any(parameter =>
                parameter.Name == identifier.Identifier.ToString() &&
                parameter.ParameterType is UIntType),
            BinaryExpression
            {
                Operator: "+" or "-" or "*" or "/" or "%",
                OperatorSymbol: null,
            } arithmetic => IsUnsignedOperand(arithmetic.Left) &&
                IsUnsignedOperand(arithmetic.Right),
            _ => false,
        };

        bool IsGenericArrayLengthComparison(ExpressionBase candidate) =>
            candidate is BinaryExpression
            {
                Operator: "==" or "!=" or "<" or "<=" or ">" or ">=",
                OperatorSymbol: null,
            } comparison &&
            (IsGenericArrayLength(comparison.Left) &&
                IsUnsignedOperand(comparison.Right) ||
             IsUnsignedOperand(comparison.Left) &&
                IsGenericArrayLength(comparison.Right) ||
             IsGenericArrayLength(comparison.Left) &&
                IsGenericArrayLength(comparison.Right) &&
                IsMatchingGenericArrayLengthPair(
                    ((MemberAccessExpression)comparison.Left).Target!,
                    ((MemberAccessExpression)comparison.Right).Target!,
                    function));

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
            _ => IsNullComparison(candidate) ||
                IsGenericArrayLengthComparison(candidate),
        };

        return IsSupportedBooleanExpression(expression);
    }

    private static bool IsGenericArrayElementAssignmentBody(FunctionDeclaration function)
    {
        if (function.ReturnType is not VoidType ||
            function.Body is not
                [ExpressionStatement
                {
                    Expression: AssignmentExpression
                    {
                        Operator: "=",
                        Target: ArrayAccessExpression
                        {
                            Indices.Count: 1,
                        } arrayAccess,
                        Value: IdentifierExpression valueIdentifier,
                    },
                }] ||
            arrayAccess.Indices.Count != 1)
        {
            return false;
        }

        if (!TryGetGenericArrayElementType(
                function, arrayAccess, out _, out var elementType, out _))
        {
            return false;
        }

        var valueParameter = function.Parameters.SingleOrDefault(parameter =>
            parameter.Name == valueIdentifier.Identifier.ToString());
        return elementType is not null && valueParameter is not null &&
            IsDirectGenericIdentityType(elementType, valueParameter.ParameterType) &&
            ContainsFunctionTypeParameter(elementType, function.GenericTypeNames);
    }

    private static bool TryGetGenericArrayElementType(
        FunctionDeclaration function,
        ArrayAccessExpression access,
        out FunctionParameter? parameter,
        out TypeBase? elementType,
        out int depth)
    {
        var rootAccess = access;
        depth = 0;
        while (rootAccess.Indices.Count == 1)
        {
            depth++;
            if (rootAccess.Target is ArrayAccessExpression nestedAccess)
            {
                rootAccess = nestedAccess;
                continue;
            }
            if (rootAccess.Target is not IdentifierExpression identifier)
            {
                break;
            }

            parameter = function.Parameters.SingleOrDefault(candidate =>
                candidate.Name == identifier.Identifier.ToString());
            elementType = parameter?.ParameterType;
            for (var index = 0; index < depth; index++)
            {
                if (elementType is not ArrayType array)
                {
                    parameter = null;
                    elementType = null;
                    return false;
                }
                elementType = array.ElementType;
            }
            return parameter is not null;
        }

        parameter = null;
        elementType = null;
        return false;
    }

    private static bool IsGenericArrayElementCopyBody(FunctionDeclaration function)
    {
        if (function.ReturnType is not VoidType ||
            function.Body is not
                [ExpressionStatement
                {
                    Expression: AssignmentExpression
                    {
                        Operator: "=",
                        Target: ArrayAccessExpression
                        {
                            Indices.Count: 1,
                        } targetAccess,
                        Value: ArrayAccessExpression
                        {
                            Indices.Count: 1,
                        } sourceAccess,
                    },
                }] ||
            targetAccess.Indices.Count != 1 || sourceAccess.Indices.Count != 1)
        {
            return false;
        }

        return TryGetGenericArrayElementType(
                function, targetAccess, out _, out var targetElementType, out var targetDepth) &&
            TryGetGenericArrayElementType(
                function, sourceAccess, out _, out var sourceElementType, out var sourceDepth) &&
            targetDepth == sourceDepth && targetElementType is not null &&
            sourceElementType is not null &&
            IsDirectGenericIdentityType(targetElementType, sourceElementType) &&
            ContainsFunctionTypeParameter(targetElementType, function.GenericTypeNames);
    }

    private static bool IsGenericArrayConstructionBody(FunctionDeclaration function)
    {
        if (function.ReturnType is not ArrayType returnArray ||
            function.Body is not
                [LocalVariableDeclarationStatement
                {
                    DeclaredType: ArrayType localArray,
                    Declarators:
                    [
                        {
                            Initializer: ArrayCreationExpression creation,
                        } local,
                    ],
                },
                ExpressionStatement
                {
                    Expression: AssignmentExpression
                    {
                        Operator: "=",
                        Target: ArrayAccessExpression
                        {
                            Target: IdentifierExpression target,
                            Indices.Count: 1,
                        },
                        Value: IdentifierExpression value,
                    },
                },
                ReturnStatement { Expression: IdentifierExpression returned }] ||
            target.Identifier.ToString() != local.Name ||
            returned.Identifier.ToString() != local.Name ||
            !IsDirectGenericIdentityType(returnArray.ElementType, localArray.ElementType) ||
            !IsDirectGenericIdentityType(returnArray.ElementType, creation.ElementType) ||
            !ContainsFunctionTypeParameter(returnArray.ElementType, function.GenericTypeNames))
        {
            return false;
        }

        return function.Parameters.Any(parameter =>
            parameter.Name == value.Identifier.ToString() &&
            IsDirectGenericIdentityType(returnArray.ElementType, parameter.ParameterType));
    }

    private static bool IsGenericArrayCopyBody(FunctionDeclaration function)
    {
        if (function.GenericTypeNames.Length != 1 ||
            function.ReturnType is not ArrayType returnArray ||
            function.Parameters is not [var sourceParameter] ||
            sourceParameter.ParameterType is not ArrayType sourceArray ||
            function.Body is not
            [
                LocalVariableDeclarationStatement
                {
                    DeclaredType: ArrayType localArray,
                    Declarators: [{ Initializer: ArrayCreationExpression creation } local],
                },
                ForStatement
                {
                    DeclarationInitializer: LocalVariableDeclarationStatement
                    {
                        DeclaredType: UIntType,
                        Declarators: [{ Initializer: LiteralExpression { SourceText: "0u" } } indexLocal],
                    },
                    Condition: BinaryExpression
                    {
                        Operator: "<",
                        Left: IdentifierExpression conditionIndex,
                        Right: MemberAccessExpression
                        {
                            Target: IdentifierExpression lengthSource,
                            MemberName: "Length",
                        },
                    },
                    Iterators: [UnaryExpression
                    {
                        Operator: "++",
                        Postfix: true,
                        Operand: IdentifierExpression incrementIndex,
                    }],
                    Body: BlockStatement
                    {
                        Statements:
                        [
                            ExpressionStatement
                            {
                                Expression: AssignmentExpression
                                {
                                    Operator: "=",
                                    Target: ArrayAccessExpression
                                    {
                                        Target: IdentifierExpression destination,
                                        Indices: [IdentifierExpression destinationIndex],
                                    },
                                    Value: ArrayAccessExpression
                                    {
                                        Target: IdentifierExpression copySource,
                                        Indices: [IdentifierExpression sourceIndex],
                                    },
                                },
                            },
                        ],
                    },
                } loop,
                ReturnStatement { Expression: IdentifierExpression returned },
            ] ||
            !IsDirectGenericIdentityType(returnArray, sourceArray) ||
            !IsDirectGenericIdentityType(returnArray, localArray) ||
            !IsDirectGenericIdentityType(returnArray.ElementType, creation.ElementType) ||
            !ContainsFunctionTypeParameter(returnArray.ElementType, function.GenericTypeNames))
        {
            return false;
        }

        var sourceName = sourceParameter.Name;
        var destinationName = local.Name;
        var indexName = indexLocal.Name;
        return indexName != sourceName && indexName != destinationName &&
            lengthSource.Identifier.ToString() == sourceName &&
            creation.Length is MemberAccessExpression
            {
                Target: IdentifierExpression allocationLengthSource,
                MemberName: "Length",
            } && allocationLengthSource.Identifier.ToString() == sourceName &&
            conditionIndex.Identifier.ToString() == indexName &&
            incrementIndex.Identifier.ToString() == indexName &&
            destination.Identifier.ToString() == destinationName &&
            destinationIndex.Identifier.ToString() == indexName &&
            copySource.Identifier.ToString() == sourceName &&
            sourceIndex.Identifier.ToString() == indexName &&
            returned.Identifier.ToString() == destinationName;
    }

    private static bool IsGenericArrayCopyWhileBody(FunctionDeclaration function)
    {
        if (function.GenericTypeNames.Length != 1 ||
            function.ReturnType is not ArrayType returnArray ||
            function.Parameters is not [var sourceParameter] ||
            sourceParameter.ParameterType is not ArrayType sourceArray ||
            function.Body is not
            [
                LocalVariableDeclarationStatement
                {
                    DeclaredType: ArrayType localArray,
                    Declarators: [{ Initializer: ArrayCreationExpression creation } local],
                },
                LocalVariableDeclarationStatement
                {
                    DeclaredType: UIntType,
                    Declarators: [{ Initializer: LiteralExpression { SourceText: "0u" } } indexLocal],
                },
                WhileStatement
                {
                    Condition: BinaryExpression
                    {
                        Operator: "<",
                        Left: IdentifierExpression conditionIndex,
                        Right: MemberAccessExpression
                        {
                            Target: IdentifierExpression lengthSource,
                            MemberName: "Length",
                        },
                    },
                    Body: BlockStatement
                    {
                        Statements:
                        [
                            ExpressionStatement
                            {
                                Expression: AssignmentExpression
                                {
                                    Operator: "=",
                                    Target: ArrayAccessExpression
                                    {
                                        Target: IdentifierExpression destination,
                                        Indices: [IdentifierExpression destinationIndex],
                                    },
                                    Value: ArrayAccessExpression
                                    {
                                        Target: IdentifierExpression copySource,
                                        Indices: [IdentifierExpression sourceIndex],
                                    },
                                },
                            },
                            ExpressionStatement
                            {
                                Expression: { } incrementExpression,
                            },
                        ],
                    },
                } loop,
                ReturnStatement { Expression: IdentifierExpression returned },
            ] ||
            !IsDirectGenericIdentityType(returnArray, sourceArray) ||
            !IsDirectGenericIdentityType(returnArray, localArray) ||
            !IsDirectGenericIdentityType(returnArray.ElementType, creation.ElementType))
        {
            return false;
        }

        var sourceName = sourceParameter.Name;
        var destinationName = local.Name;
        var indexName = indexLocal.Name;
        var canonicalIncrement = IsCanonicalGenericArrayCopyIncrement(
            incrementExpression, indexName);
        return ContainsFunctionTypeParameter(returnArray.ElementType, function.GenericTypeNames) &&
            indexName != sourceName && indexName != destinationName &&
            canonicalIncrement &&
            lengthSource.Identifier.ToString() == sourceName &&
            creation.Length is MemberAccessExpression
            {
                Target: IdentifierExpression allocationLengthSource,
                MemberName: "Length",
            } && allocationLengthSource.Identifier.ToString() == sourceName &&
            conditionIndex.Identifier.ToString() == indexName &&
            destination.Identifier.ToString() == destinationName &&
            destinationIndex.Identifier.ToString() == indexName &&
            copySource.Identifier.ToString() == sourceName &&
            sourceIndex.Identifier.ToString() == indexName &&
            returned.Identifier.ToString() == destinationName;
    }

    private static bool IsGenericArrayCopyForeachBody(FunctionDeclaration function)
    {
        if (function.GenericTypeNames.Length != 1 ||
            function.ReturnType is not ArrayType returnArray ||
            function.Parameters is not [var sourceParameter] ||
            sourceParameter.ParameterType is not ArrayType sourceArray ||
            function.Body is not
            [
                LocalVariableDeclarationStatement
                {
                    DeclaredType: ArrayType localArray,
                    Declarators: [{ Initializer: ArrayCreationExpression creation } local],
                },
                LocalVariableDeclarationStatement
                {
                    DeclaredType: UIntType,
                    Declarators: [{ Initializer: LiteralExpression { SourceText: "0u" } } indexLocal],
                },
                ForeachStatement
                {
                    VariableName: var itemName,
                    Collection: IdentifierExpression foreachSource,
                    Body: BlockStatement
                    {
                        Statements:
                        [
                            ExpressionStatement
                            {
                                Expression: AssignmentExpression
                                {
                                    Operator: "=",
                                    Target: ArrayAccessExpression
                                    {
                                        Target: IdentifierExpression destination,
                                        Indices: [IdentifierExpression destinationIndex],
                                    },
                                    Value: IdentifierExpression copiedItem,
                                },
                            },
                            ExpressionStatement { Expression: { } incrementExpression },
                        ],
                    },
                } loop,
                ReturnStatement { Expression: IdentifierExpression returned },
            ] ||
            !IsDirectGenericIdentityType(returnArray, sourceArray) ||
            !IsDirectGenericIdentityType(returnArray, localArray) ||
            !IsDirectGenericIdentityType(returnArray.ElementType, creation.ElementType))
        {
            return false;
        }

        var sourceName = sourceParameter.Name;
        var destinationName = local.Name;
        var indexName = indexLocal.Name;
        var itemType = loop.VariableType ?? loop.DeclaredType;
        return ContainsFunctionTypeParameter(returnArray.ElementType, function.GenericTypeNames) &&
            IsDirectGenericIdentityType(returnArray.ElementType, itemType) &&
            foreachSource.Identifier.ToString() == sourceName &&
            creation.Length is MemberAccessExpression
            {
                Target: IdentifierExpression allocationLengthSource,
                MemberName: "Length",
            } && allocationLengthSource.Identifier.ToString() == sourceName &&
            destination.Identifier.ToString() == destinationName &&
            destinationIndex.Identifier.ToString() == indexName &&
            copiedItem.Identifier.ToString() == itemName &&
            IsCanonicalGenericArrayCopyIncrement(incrementExpression, indexName) &&
            returned.Identifier.ToString() == destinationName;
    }

    private static bool IsCanonicalGenericArrayCopyIncrement(
        ExpressionBase expression,
        string indexName) => expression switch
    {
        UnaryExpression
        {
            Operator: "++",
            Postfix: true,
            Operand: IdentifierExpression incrementIndex,
        } => incrementIndex.Identifier.ToString() == indexName,
        AssignmentExpression
        {
            Operator: "=",
            Target: IdentifierExpression incrementTarget,
            Value: BinaryExpression
            {
                Operator: "+",
                Left: IdentifierExpression incrementSource,
                Right: LiteralExpression { SourceText: "1u" },
            },
        } => incrementTarget.Identifier.ToString() == indexName &&
            incrementSource.Identifier.ToString() == indexName,
        _ => false,
    };

    private static bool IsDirectGenericIdentityType(TypeBase returnType, TypeBase parameterType)
    {
        var returnIsArray = TryGetArrayIdentityElementType(returnType, out var returnElementType);
        var parameterIsArray = TryGetArrayIdentityElementType(parameterType, out var parameterElementType);
        if (returnIsArray || parameterIsArray)
        {
            return returnIsArray && parameterIsArray &&
                IsDirectGenericIdentityType(returnElementType, parameterElementType);
        }
        if (returnType is ConstType returnConst && parameterType is ConstType parameterConst)
        {
            return IsDirectGenericIdentityType(
                returnConst.UnderlyingType, parameterConst.UnderlyingType);
        }
        if (returnType is GenericType returned && parameterType is GenericType argument)
        {
            return returned.Name == argument.Name;
        }
        if (returnType is NullableType returnNullable &&
            parameterType is NullableType parameterNullable)
        {
            return IsDirectGenericIdentityType(
                returnNullable.UnderlyingType, parameterNullable.UnderlyingType);
        }
        if (returnType is NamedType returnNamed && parameterType is NamedType parameterNamed)
        {
            return returnNamed.ResolvedTypeFullName == parameterNamed.ResolvedTypeFullName &&
                returnNamed.TypeArguments.Count == parameterNamed.TypeArguments.Count &&
                returnNamed.TypeArguments.Zip(parameterNamed.TypeArguments)
                    .All(pair => IsDirectGenericIdentityType(pair.First, pair.Second));
        }
        return returnType.FullName == parameterType.FullName;
    }

    private static bool TryGetArrayIdentityElementType(
        TypeBase type,
        out TypeBase elementType)
    {
        if (ArrayType.TryGetElementType(type, out elementType))
        {
            return true;
        }

        type = type is ConstType constant ? constant.UnderlyingType : type;
        if (type is NamedType { TypeArguments.Count: 1 } namedArray &&
            namedArray.Name is "Array" or "System.Collections.Array")
        {
            elementType = namedArray.TypeArguments[0];
            return true;
        }

        elementType = null!;
        return false;
    }

    private static bool IsGenericLocalCopyBody(FunctionDeclaration function)
    {
        if (function.Body is not
            [LocalVariableDeclarationStatement local,
                ReturnStatement { Expression: IdentifierExpression returned }] ||
            local.Declarators is not [var declarator] ||
            function.ReturnType is not GenericType returnParameter ||
            declarator.Initializer is not IdentifierExpression source ||
            returned.Identifier.ToString() != declarator.Name)
        {
            return false;
        }
        if (local.DeclaredType is NamedType declared &&
                (declared.GenericParams.Length != 0 ||
                    declared.Name != returnParameter.Name) ||
            local.DeclaredType is not (AutoType or NamedType))
        {
            return false;
        }
        return function.Parameters.Any(parameter =>
            parameter.Name == source.Identifier.ToString() &&
            parameter.ParameterType is GenericType argumentParameter &&
            argumentParameter.Name == returnParameter.Name);
    }

    private static bool IsGenericLocalCopyAssignmentBody(FunctionDeclaration function)
    {
        if (function.Body is not
            [LocalVariableDeclarationStatement local,
                ExpressionStatement
            {
                Expression: AssignmentExpression
                {
                    Target: IdentifierExpression target,
                    Operator: "=",
                    Value: IdentifierExpression replacement,
                },
            },
                ReturnStatement { Expression: IdentifierExpression returned }] ||
            local.Declarators is not [var declarator] ||
            local.DeclaredType is not NamedType declared ||
            declared.GenericParams.Length != 0 ||
            !function.GenericTypeNames.Contains(declared.Name) ||
            function.ReturnType is not GenericType returnParameter ||
            returnParameter.Name != declared.Name ||
            declarator.Initializer is not IdentifierExpression initial ||
            target.Identifier.ToString() != declarator.Name ||
            returned.Identifier.ToString() != declarator.Name)
        {
            return false;
        }

        bool IsMatchingParameter(string name) => function.Parameters.Any(parameter =>
            parameter.Name == name && parameter.ParameterType is GenericType parameterType &&
            parameterType.Name == declared.Name);

        return IsMatchingParameter(initial.Identifier.ToString()) &&
            IsMatchingParameter(replacement.Identifier.ToString());
    }

    private static bool IsGenericLocalSequenceBody(FunctionDeclaration function)
    {
        if (function.Body is not { Count: >= 2 } body ||
            body[^1] is not ReturnStatement { Expression: IdentifierExpression returned } ||
            !ContainsFunctionTypeParameter(function.ReturnType, function.GenericTypeNames))
        {
            return false;
        }

        var values = function.Parameters.ToDictionary(
            parameter => parameter.Name,
            parameter => parameter.ParameterType,
            StringComparer.Ordinal);
        var localNames = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < body.Count - 1; index++)
        {
            switch (body[index])
            {
                case LocalVariableDeclarationStatement local:
                    if (local.Declarators.Count == 0 ||
                        local.DeclaredType is not (AutoType or NamedType) ||
                        !IsDirectGenericIdentityType(
                            function.ReturnType,
                            local.DeclaredType is AutoType
                                ? function.ReturnType
                                : GenericTypeSubstitution.BindParameters(
                                    local.DeclaredType, function.GenericTypeNames)))
                    {
                        return false;
                    }
                    foreach (var declarator in local.Declarators)
                    {
                        if (declarator.Initializer is not IdentifierExpression source ||
                            !HasMatchingValue(
                                source.Identifier.ToString(), values, function.ReturnType) ||
                            values.ContainsKey(declarator.Name))
                        {
                            return false;
                        }
                        values.Add(declarator.Name, function.ReturnType);
                        localNames.Add(declarator.Name);
                    }
                    break;
                case ExpressionStatement
                {
                    Expression: AssignmentExpression
                    {
                        Target: IdentifierExpression target,
                        Operator: "=",
                        Value: IdentifierExpression replacement,
                    },
                } when localNames.Contains(target.Identifier.ToString()) &&
                    HasMatchingValue(replacement.Identifier.ToString(), values, function.ReturnType):
                    break;
                default:
                    return false;
            }
        }

        return localNames.Contains(returned.Identifier.ToString()) &&
            HasMatchingValue(returned.Identifier.ToString(), values, function.ReturnType);
    }

    private static bool HasMatchingValue(
        string name,
        IReadOnlyDictionary<string, TypeBase> values,
        TypeBase expectedType) =>
        values.TryGetValue(name, out var valueType) &&
        IsDirectGenericIdentityType(expectedType, valueType);

    private static bool IsGenericConditionalReturnBody(FunctionDeclaration function)
    {
        if (function.Body is not { Count: > 0 } body ||
            !ContainsFunctionTypeParameter(function.ReturnType, function.GenericTypeNames))
        {
            return false;
        }

        var booleanLocals = new HashSet<string>(StringComparer.Ordinal);
        var flowStart = 0;
        while (flowStart < body.Count && body[flowStart] is LocalVariableDeclarationStatement local)
        {
            if (local.DeclaredType is not (BoolType or AutoType) ||
                local.Declarators.Count == 0)
            {
                return false;
            }
            foreach (var declarator in local.Declarators)
            {
                if (declarator.Initializer is not { } initializer ||
                    !IsBooleanExpression(initializer, function, booleanLocals) ||
                    !booleanLocals.Add(declarator.Name))
                {
                    return false;
                }
            }
            flowStart++;
        }

        var returnFlow = body.Skip(flowStart).ToArray();
        return returnFlow.Length > 0 &&
            returnFlow.All(statement => IsGenericReturnFlow(statement, function, booleanLocals)) &&
            GenericReturnFlowAlwaysReturns(returnFlow);
    }

    private static bool IsGenericConditionalLocalReturnBody(FunctionDeclaration function)
    {
        if (function.Body is not
            [
                LocalVariableDeclarationStatement local,
                ReturnStatement { Expression: IdentifierExpression returned },
            ] ||
            local.Declarators is not
                [{ Initializer: ConditionalExpression conditional } declarator] ||
            returned.Identifier.ToString() != declarator.Name ||
            !ContainsFunctionTypeParameter(function.ReturnType, function.GenericTypeNames) ||
            !IsBooleanExpression(conditional.Condition, function) ||
            !IsGenericValueSelectionExpression(conditional.WhenTrue, function, null) ||
            !IsGenericValueSelectionExpression(conditional.WhenFalse, function, null))
        {
            return false;
        }

        var localType = local.DeclaredType is AutoType
            ? function.ReturnType
            : GenericTypeSubstitution.BindParameters(
                local.DeclaredType, function.GenericTypeNames);
        return IsDirectGenericIdentityType(function.ReturnType, localType);
    }

    private static bool GenericReturnFlowAlwaysReturns(IReadOnlyList<StatementBase> statements) =>
        statements.Count > 0 && GenericReturnFlowAlwaysReturns(statements[^1]);

    private static bool GenericReturnFlowAlwaysReturns(StatementBase statement) => statement switch
    {
        ReturnStatement => true,
        BlockStatement block => GenericReturnFlowAlwaysReturns(block.Statements),
        IfStatement { ElseStatement: not null } conditional =>
            GenericReturnFlowAlwaysReturns(conditional.ThenStatement) &&
            GenericReturnFlowAlwaysReturns(conditional.ElseStatement),
        _ => false,
    };

    private static bool IsGenericReturnFlow(
        StatementBase statement,
        FunctionDeclaration function,
        IReadOnlySet<string>? booleanLocals = null) =>
        statement switch
        {
            ReturnStatement { Expression: { } expression } =>
                IsGenericValueSelectionExpression(
                    expression, function, booleanLocals),
            BlockStatement block => block.Statements.All(item =>
                IsGenericReturnFlow(item, function, booleanLocals)),
            IfStatement conditional =>
                IsBooleanExpression(conditional.Condition, function, booleanLocals) &&
                IsGenericReturnFlow(conditional.ThenStatement, function, booleanLocals) &&
                (conditional.ElseStatement is null ||
                    IsGenericReturnFlow(conditional.ElseStatement, function, booleanLocals)),
            _ => false,
        };

    private static bool IsGenericValueSelectionExpression(
        ExpressionBase expression,
        FunctionDeclaration function,
        IReadOnlySet<string>? booleanLocals) =>
        expression switch
        {
            IdentifierExpression identifier => function.Parameters.Any(parameter =>
                parameter.Name == identifier.Identifier.ToString() &&
                IsDirectGenericIdentityType(function.ReturnType, parameter.ParameterType)),
            ConditionalExpression conditional =>
                IsBooleanExpression(conditional.Condition, function, booleanLocals) &&
                IsGenericValueSelectionExpression(
                    conditional.WhenTrue, function, booleanLocals) &&
                IsGenericValueSelectionExpression(
                    conditional.WhenFalse, function, booleanLocals),
            _ => false,
        };

    private static bool IsBooleanExpression(
        ExpressionBase expression,
        FunctionDeclaration function,
        IReadOnlySet<string>? booleanLocals = null) =>
        expression switch
        {
            IdentifierExpression identifier => function.Parameters.Any(parameter =>
                parameter.Name == identifier.Identifier.ToString() && parameter.ParameterType is BoolType) ||
                booleanLocals?.Contains(identifier.Identifier.ToString()) == true,
            LiteralExpression { SourceText: "true" or "false" } => true,
            UnaryExpression { Operator: "!", Operand: var operand } =>
                IsBooleanExpression(operand, function, booleanLocals),
            BinaryExpression { Operator: "&&" or "||", Left: var left, Right: var right } =>
                IsBooleanExpression(left, function, booleanLocals) &&
                IsBooleanExpression(right, function, booleanLocals),
            BinaryExpression { Operator: "==" or "!=", Left: var left, Right: var right } =>
                IsBooleanExpression(left, function, booleanLocals) &&
                IsBooleanExpression(right, function, booleanLocals),
            _ => false,
        };

    private static bool ContainsFunctionTypeParameter(
        TypeBase type,
        IReadOnlyCollection<string> genericTypeNames) =>
        type switch
        {
            ConstType constant => ContainsFunctionTypeParameter(
                constant.UnderlyingType, genericTypeNames),
            GenericType generic => genericTypeNames.Contains(generic.Name),
            ArrayType array => ContainsFunctionTypeParameter(array.ElementType, genericTypeNames),
            NullableType nullable => ContainsFunctionTypeParameter(
                nullable.UnderlyingType, genericTypeNames),
            NamedType named => named.TypeArguments.Any(argument =>
                ContainsFunctionTypeParameter(argument, genericTypeNames)),
            FunctionType function => ContainsFunctionTypeParameter(
                    function.ReturnType, genericTypeNames) ||
                function.ParameterTypes.Any(parameter =>
                    ContainsFunctionTypeParameter(parameter, genericTypeNames)),
            _ => false,
        };
}

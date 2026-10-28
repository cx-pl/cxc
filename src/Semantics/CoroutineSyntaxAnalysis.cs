namespace CxCompiler.Semantics;

using CxCompiler.Model.Expressions;
using CxCompiler.Model.Statements;

/// <summary>
/// Backend-independent syntax discovery used while analyzing coroutine bodies.
/// This deliberately describes source expressions and statements only; it does
/// not assign generated names, frame fields, or C layout.
/// </summary>
internal static class CoroutineSyntaxAnalysis
{
    public static IEnumerable<StatementBase> EnumerateStatements(
        IEnumerable<StatementBase> statements)
    {
        foreach (var statement in statements)
        {
            yield return statement;
            var children = statement switch
            {
                BlockStatement block => block.Statements,
                IfStatement conditional => conditional.ElseStatement is null
                    ? [conditional.ThenStatement]
                    : [conditional.ThenStatement, conditional.ElseStatement],
                WhileStatement loop => [loop.Body],
                DoWhileStatement loop => [loop.Body],
                ForStatement loop => loop.DeclarationInitializer is null
                    ? [loop.Body]
                    : [loop.Body],
                ForeachStatement loop => [loop.Body],
                SwitchStatement selection => selection.Sections
                    .SelectMany(section => section.Statements),
                TryStatement tryStatement => new[] { tryStatement.Body }
                    .Concat(tryStatement.CatchClauses.Select(clause => clause.Body))
                    .Concat(tryStatement.FinallyBody is null
                        ? [] : [tryStatement.FinallyBody]),
                LocalFunctionDeclarationStatement local when local.Function.Body is { } body =>
                    body,
                _ => [],
            };
            foreach (var child in EnumerateStatements(children))
            {
                yield return child;
            }
        }
    }

    public static IEnumerable<ExpressionBase> EnumerateAllExpressions(ExpressionBase expression)
    {
        yield return expression;
        foreach (var child in ExpressionChildren(expression))
        {
            foreach (var nested in EnumerateAllExpressions(child))
                yield return nested;
        }
    }

    public static IEnumerable<ExpressionBase> EnumerateBodyExpressions(
        IEnumerable<StatementBase> body) => EnumerateStatements(body)
            .SelectMany(DirectExpressions)
            .SelectMany(EnumerateAllExpressions);

    public static IEnumerable<StatementBase> EnumerateBodyStatements(
        IEnumerable<StatementBase> body) => EnumerateStatements(body);

    public static bool StatementContainsAwait(StatementBase statement) =>
        DirectExpressions(statement).Any(ContainsAwait);

    public static bool ContainsAwait(ExpressionBase expression) =>
        expression is AwaitExpression || ExpressionChildren(expression).Any(ContainsAwait);

    public static IEnumerable<AwaitExpression> EnumerateAwaits(ExpressionBase expression)
    {
        if (expression is AwaitExpression awaitExpression)
        {
            yield return awaitExpression;
        }

        foreach (var child in ExpressionChildren(expression))
        {
            foreach (var nested in EnumerateAwaits(child))
            {
                yield return nested;
            }
        }
    }

    public static IEnumerable<ExpressionBase> DirectExpressions(StatementBase statement)
    {
        switch (statement)
        {
            case ExpressionStatement expressionStatement:
                yield return expressionStatement.Expression;
                break;
            case LocalVariableDeclarationStatement declaration:
                foreach (var initializer in declaration.Declarators
                    .Select(declarator => declarator.Initializer)
                    .OfType<ExpressionBase>())
                {
                    yield return initializer;
                }
                break;
            case ReturnStatement { Expression: { } expression }:
                yield return expression;
                break;
            case ThrowStatement { Expression: { } expression }:
                yield return expression;
                break;
            case IfStatement conditional:
                yield return conditional.Condition;
                break;
            case WhileStatement loop:
                yield return loop.Condition;
                break;
            case DoWhileStatement loop:
                yield return loop.Condition;
                break;
            case ForStatement loop:
                if (loop.DeclarationInitializer is not null)
                {
                    foreach (var initializer in loop.DeclarationInitializer.Declarators
                        .Select(declarator => declarator.Initializer)
                        .OfType<ExpressionBase>())
                    {
                        yield return initializer;
                    }
                }
                foreach (var expression in loop.InitializerExpressions)
                    yield return expression;
                if (loop.Condition is not null)
                    yield return loop.Condition;
                foreach (var expression in loop.Iterators)
                    yield return expression;
                break;
            case ForeachStatement loop:
                yield return loop.Collection;
                if (loop.GetEnumeratorInvocation is not null)
                    yield return loop.GetEnumeratorInvocation;
                if (loop.MoveNextInvocation is not null)
                    yield return loop.MoveNextInvocation;
                if (loop.CurrentExpression is not null)
                    yield return loop.CurrentExpression;
                break;
            case SwitchStatement selection:
                yield return selection.Expression;
                foreach (var label in selection.Sections.SelectMany(section => section.Labels))
                {
                    if (label.Value is not null)
                        yield return label.Value;
                    if (label.Filter is not null)
                        yield return label.Filter;
                }
                break;
            case TryStatement tryStatement:
                foreach (var filter in tryStatement.CatchClauses
                    .Select(clause => clause.Filter)
                    .OfType<ExpressionBase>())
                {
                    yield return filter;
                }
                break;
        }
    }

    private static IEnumerable<ExpressionBase> ExpressionChildren(ExpressionBase expression) => expression switch
    {
        AwaitExpression awaited => [awaited.LoweredExpression ?? awaited.Operand],
        AssignmentExpression assignment => [assignment.Target, assignment.Value],
        BinaryExpression binary => [binary.Left, binary.Right],
        ConditionalExpression conditional =>
            [conditional.Condition, conditional.WhenTrue, conditional.WhenFalse],
        CastExpression cast => [cast.Operand],
        UnaryExpression unary => [unary.Operand],
        MemberAccessExpression member => [member.Target],
        InvocationExpression invocation => [
            .. invocation.Receiver is null ? [] : new[] { invocation.Receiver },
            invocation.Target,
            .. invocation.Arguments],
        ArrayAccessExpression access => [access.Target, .. access.Indices],
        ArrayCreationExpression creation => [creation.Length],
        ObjectCreationExpression creation => creation.Arguments,
        NativeStackAllocationExpression allocation => [allocation.Count],
        NullCoalescingExpression coalescing => [coalescing.Left, coalescing.Right],
        SwitchExpression selection => [selection.Selector,
            .. selection.Arms.SelectMany(arm => new[] { arm.Label, arm.Filter, arm.Value }
                .OfType<ExpressionBase>())],
        TypeTestExpression typeTest => [typeTest.Operand],
        // Lambda bodies execute independently of the containing expression's
        // suspension and are analyzed as their own functions.
        LambdaExpression => [],
        _ => [],
    };
}

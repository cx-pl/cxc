namespace CxCompiler.Semantics;

using CxCompiler.Model.Expressions;
using CxCompiler.Model.Common;
using CxCompiler.Model.Statements;

/// <summary>
/// Source-level coroutine control flow. State identity and edges are logical;
/// this model contains no generated C/CX names or frame layout.
/// </summary>
internal sealed record CoroutineSuspensionPlan(
    int EntryState,
    IReadOnlyList<CoroutinePlanBlock> Blocks,
    IReadOnlyList<CoroutineCleanupRegion> CleanupRegions);

internal sealed record CoroutinePlanBlock(
    int State,
    IReadOnlyList<StatementBase> Operations,
    CoroutinePlanTerminator Terminator,
    IReadOnlySet<LocalVariableSymbol> LiveValues,
    IReadOnlySet<LocalVariableSymbol> CapturedValues,
    IReadOnlyList<CoroutineCleanupEdge> CleanupEdges);

internal abstract record CoroutinePlanTerminator;
internal sealed record CoroutinePlanNext(int State) : CoroutinePlanTerminator;
internal sealed record CoroutinePlanBranch(
    ExpressionBase Condition,
    int WhenTrue,
    int WhenFalse) : CoroutinePlanTerminator;
internal sealed record CoroutinePlanYield(
    ExpressionBase Expression,
    int NextState,
    SourceSpan? SourceSpan) : CoroutinePlanTerminator;
internal sealed record CoroutinePlanAwait(
    ExpressionBase Awaited,
    LocalVariableSymbol? Result,
    int NextState) : CoroutinePlanTerminator;
internal sealed record CoroutinePlanComplete : CoroutinePlanTerminator;

internal sealed record CoroutineCleanupRegion(int Id, TryStatement Body);
internal sealed record CoroutineCleanupEdge(int RegionId, CoroutineCleanupTransfer Transfer);
internal enum CoroutineCleanupTransfer { Continue, Break, Return, Fault, Dispose }

/// <summary>
/// Builds the first source-only plan slice: a linear body with yields and
/// ordinary expression statements. More complex control flow remains on the
/// existing iterator builder until each construct has plan-level coverage.
/// </summary>
internal static class CoroutineSuspensionPlanBuilder
{
    public static bool TryBuildLinearYieldPlan(
        IReadOnlyList<StatementBase> statements,
        out CoroutineSuspensionPlan plan)
    {
        var blocks = new List<CoroutinePlanBlock>();
        int Add(IReadOnlyList<StatementBase> operations, CoroutinePlanTerminator terminator)
        {
            var state = blocks.Count;
            blocks.Add(new CoroutinePlanBlock(
                state, operations, terminator,
                new HashSet<LocalVariableSymbol>(),
                new HashSet<LocalVariableSymbol>(), []));
            return state;
        }

        var continuation = Add([], new CoroutinePlanComplete());
        var hasYield = false;
        if (!TryBuildSequence(statements, continuation, out var entryState))
        {
            plan = new CoroutineSuspensionPlan(0, [], []);
            return false;
        }

        if (!hasYield)
        {
            plan = new CoroutineSuspensionPlan(0, [], []);
            return false;
        }

        PopulateLiveValues(blocks);
        plan = new CoroutineSuspensionPlan(entryState, blocks, []);
        return true;

        bool TryBuildSequence(
            IReadOnlyList<StatementBase> sequence,
            int nextState,
            out int state)
        {
            state = nextState;
            for (var index = sequence.Count - 1; index >= 0; index--)
            {
                if (!TryBuildStatement(sequence[index], state, out state))
                    return false;
            }
            return true;
        }

        bool TryBuildStatement(StatementBase statement, int nextState, out int state)
        {
            switch (statement)
            {
                case BlockStatement block:
                    return TryBuildSequence(block.Statements, nextState, out state);
                case IfStatement conditional:
                    if (!TryBuildStatement(conditional.ThenStatement, nextState,
                            out var whenTrue))
                    {
                        state = 0;
                        return false;
                    }
                    var whenFalse = nextState;
                    if (conditional.ElseStatement is not null &&
                        !TryBuildStatement(conditional.ElseStatement, nextState,
                            out whenFalse))
                    {
                        state = 0;
                        return false;
                    }
                    state = Add([], new CoroutinePlanBranch(
                        conditional.Condition, whenTrue, whenFalse));
                    return true;
                case ExpressionStatement expression:
                    state = Add([expression], new CoroutinePlanNext(nextState));
                    return true;
                case YieldReturnStatement yielded:
                    hasYield = true;
                    state = Add([], new CoroutinePlanYield(
                        yielded.Expression, nextState, yielded.SourceSpan));
                    return true;
                case YieldBreakStatement:
                    hasYield = true;
                    state = Add([], new CoroutinePlanComplete());
                    return true;
                case ReturnStatement { Expression: null }:
                    state = Add([], new CoroutinePlanComplete());
                    return true;
                default:
                    state = 0;
                    return false;
            }
        }
    }

    private static void PopulateLiveValues(List<CoroutinePlanBlock> blocks)
    {
        var blocksByState = blocks.ToDictionary(block => block.State);
        for (var index = 0; index < blocks.Count; index++)
        {
            if (blocks[index].Terminator is not CoroutinePlanYield yielded)
                continue;

            var liveValues = new HashSet<LocalVariableSymbol>();
            var capturedValues = new HashSet<LocalVariableSymbol>();
            var pending = new Stack<int>();
            var visited = new HashSet<int>();
            pending.Push(yielded.NextState);
            while (pending.TryPop(out var state) && visited.Add(state))
            {
                var block = blocksByState[state];
                foreach (var operation in block.Operations)
                {
                    foreach (var expression in CoroutineSyntaxAnalysis.DirectExpressions(operation)
                        .SelectMany(CoroutineSyntaxAnalysis.EnumerateAllExpressions))
                    {
                        AddReferences(expression, liveValues, capturedValues);
                    }
                }

                switch (block.Terminator)
                {
                    case CoroutinePlanNext next:
                        pending.Push(next.State);
                        break;
                    case CoroutinePlanBranch branch:
                        foreach (var expression in CoroutineSyntaxAnalysis
                            .EnumerateAllExpressions(branch.Condition))
                        {
                            AddReferences(expression, liveValues, capturedValues);
                        }
                        pending.Push(branch.WhenTrue);
                        pending.Push(branch.WhenFalse);
                        break;
                    case CoroutinePlanYield nextYield:
                        foreach (var expression in CoroutineSyntaxAnalysis
                            .EnumerateAllExpressions(nextYield.Expression))
                        {
                            AddReferences(expression, liveValues, capturedValues);
                        }
                        pending.Push(nextYield.NextState);
                        break;
                    case CoroutinePlanAwait awaited:
                        foreach (var expression in CoroutineSyntaxAnalysis
                            .EnumerateAllExpressions(awaited.Awaited))
                        {
                            AddReferences(expression, liveValues, capturedValues);
                        }
                        if (awaited.Result is not null)
                            liveValues.Add(awaited.Result);
                        pending.Push(awaited.NextState);
                        break;
                }
            }

            blocks[index] = blocks[index] with
            {
                LiveValues = liveValues,
                CapturedValues = capturedValues,
            };
        }
    }

    private static void AddReferences(
        ExpressionBase expression,
        HashSet<LocalVariableSymbol> liveValues,
        HashSet<LocalVariableSymbol> capturedValues)
    {
        switch (expression)
        {
            case IdentifierExpression { TargetLocal: { } local }:
                liveValues.Add(local);
                break;
            case IdentifierExpression { TargetCapture: { } capture }:
                liveValues.Add(capture.Local);
                capturedValues.Add(capture.Local);
                break;
            case ThisExpression { TargetCapture: { } capture }:
                liveValues.Add(capture.Local);
                capturedValues.Add(capture.Local);
                break;
            case LambdaExpression lambda:
                foreach (var capture in lambda.Captures)
                {
                    liveValues.Add(capture.Local);
                    capturedValues.Add(capture.Local);
                }
                break;
        }
    }
}

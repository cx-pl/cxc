using CxCompiler.Model.Common;
using CxCompiler.Model.Expressions;
using CxCompiler.Model.Statements;
using CxCompiler.Semantics;
using CxCompiler.Model.Types.BuiltInTypes;

namespace CxCompiler.Tests;

public sealed class CoroutineSyntaxAnalysisTests
{
    [Fact]
    public void FindsAwaitExpressionsInSourceOrderWithoutCEmission()
    {
        var first = new AwaitExpression(new IdentifierExpression("first"));
        var second = new AwaitExpression(new IdentifierExpression("second"));
        var expression = new BinaryExpression(first, "+", second);

        var awaits = CoroutineSyntaxAnalysis.EnumerateAwaits(expression).ToArray();

        Assert.Equal([first, second], awaits);
        Assert.True(CoroutineSyntaxAnalysis.ContainsAwait(expression));
        Assert.False(CoroutineSyntaxAnalysis.ContainsAwait(
            new IdentifierExpression(new QualifiedIdentifier("plain"))));
    }

    [Fact]
    public void BuildsLinearYieldPlanWithoutGeneratedNamesOrCEmission()
    {
        var before = new ExpressionStatement(new IdentifierExpression("before"));
        var yielded = new YieldReturnStatement(new IdentifierExpression("value"));
        var liveSymbol = new LocalVariableSymbol("live", BuiltInSystemTypes.Int);
        var liveReference = new IdentifierExpression("live");
        liveReference.BindLocal(liveSymbol);
        var capturedSymbol = new LocalVariableSymbol("captured", BuiltInSystemTypes.Int);
        var lambda = new LambdaExpression([], null, []);
        lambda.Capture(capturedSymbol);
        var after = new ExpressionStatement(liveReference);
        var afterCapture = new ExpressionStatement(lambda);
        var statements = new StatementBase[] { before, yielded, after, afterCapture };

        Assert.True(CoroutineSuspensionPlanBuilder.TryBuildLinearYieldPlan(
            statements, out var plan));

        var entry = Assert.Single(plan.Blocks, block =>
            block.State == plan.EntryState);
        Assert.Same(before, Assert.Single(entry.Operations));
        var yieldBlock = Assert.Single(plan.Blocks, block =>
            block.Terminator is CoroutinePlanYield);
        var yieldTerminator = Assert.IsType<CoroutinePlanYield>(yieldBlock.Terminator);
        Assert.Same(yielded.Expression, yieldTerminator.Expression);
        var continuation = Assert.Single(plan.Blocks, block =>
            block.State == yieldTerminator.NextState);
        Assert.Same(after, Assert.Single(continuation.Operations));
        Assert.Contains(plan.Blocks, block =>
            block.Operations.Contains(afterCapture));
        Assert.Contains(liveSymbol, yieldBlock.LiveValues);
        Assert.Contains(capturedSymbol, yieldBlock.LiveValues);
        Assert.Contains(capturedSymbol, yieldBlock.CapturedValues);
        Assert.DoesNotContain(liveSymbol, yieldBlock.CapturedValues);
        Assert.Contains(plan.Blocks, block =>
            block.Terminator is CoroutinePlanComplete);
    }

    [Fact]
    public void RepresentsBranchesAndYieldEdgesInTheSourcePlan()
    {
        var condition = new IdentifierExpression("condition");
        var thenYield = new YieldReturnStatement(new IdentifierExpression("left"));
        var elseYield = new YieldReturnStatement(new IdentifierExpression("right"));
        var conditional = new IfStatement(condition,
            new BlockStatement([thenYield]), new BlockStatement([elseYield]));

        Assert.True(CoroutineSuspensionPlanBuilder.TryBuildLinearYieldPlan(
            [conditional], out var plan));

        var branch = Assert.Single(plan.Blocks, block =>
            block.Terminator is CoroutinePlanBranch);
        var branchTerminator = Assert.IsType<CoroutinePlanBranch>(branch.Terminator);
        Assert.Same(condition, branchTerminator.Condition);
        Assert.NotEqual(branchTerminator.WhenTrue, branchTerminator.WhenFalse);
        Assert.Contains(plan.Blocks, block =>
            block.State == branchTerminator.WhenTrue &&
            block.Terminator is CoroutinePlanYield);
        Assert.Contains(plan.Blocks, block =>
            block.State == branchTerminator.WhenFalse &&
            block.Terminator is CoroutinePlanYield);
    }

    [Fact]
    public void PlanModelCarriesLogicalCleanupEdgesWithoutGeneratedNames()
    {
        var protectedBody = new BlockStatement([]);
        var finallyBody = new BlockStatement([
            new ExpressionStatement(new IdentifierExpression("cleanup")),
        ]);
        var sourceRegion = new TryStatement(protectedBody, [], finallyBody);
        var region = new CoroutineCleanupRegion(3, sourceRegion);
        var edge = new CoroutineCleanupEdge(3, CoroutineCleanupTransfer.Dispose);
        var block = new CoroutinePlanBlock(
            4, [], new CoroutinePlanComplete(),
            new HashSet<LocalVariableSymbol>(),
            new HashSet<LocalVariableSymbol>(), [edge]);
        var plan = new CoroutineSuspensionPlan(4, [block], [region]);

        Assert.Same(sourceRegion, Assert.Single(plan.CleanupRegions).Body);
        Assert.Equal(CoroutineCleanupTransfer.Dispose,
            Assert.Single(Assert.Single(plan.Blocks).CleanupEdges).Transfer);
    }
}

namespace CxCompiler.Model.Expressions;

using CxCompiler.Semantics;

public sealed class InvocationExpression : ExpressionBase
{
    public ExpressionBase Target { get; }
    public IReadOnlyList<ExpressionBase> Arguments { get; }
    public FunctionSymbol? TargetSymbol { get; private set; }
    public ExpressionBase? Receiver { get; private set; }
    public int ReceiverBaseDepth { get; private set; }
    public int? DispatchSlotIndex { get; private set; }
    public string? ReceiverTemporaryName { get; private set; }

    public InvocationExpression(ExpressionBase target, IReadOnlyList<ExpressionBase> arguments)
    {
        Target = target;
        Arguments = arguments;
    }

    public void BindTarget(
        FunctionSymbol targetSymbol,
        ExpressionBase? receiver = null,
        int receiverBaseDepth = 0,
        int? dispatchSlotIndex = null,
        string? receiverTemporaryName = null)
    {
        TargetSymbol = targetSymbol;
        Receiver = receiver;
        ReceiverBaseDepth = receiverBaseDepth;
        DispatchSlotIndex = dispatchSlotIndex;
        ReceiverTemporaryName = receiverTemporaryName;
    }
}

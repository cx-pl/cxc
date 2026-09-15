namespace CxCompiler.Model.Expressions;

using CxCompiler.Semantics;

public sealed class InvocationExpression : ExpressionBase
{
    public ExpressionBase Target { get; }
    public IReadOnlyList<ExpressionBase> Arguments { get; }
    public FunctionSymbol? TargetSymbol { get; private set; }

    public InvocationExpression(ExpressionBase target, IReadOnlyList<ExpressionBase> arguments)
    {
        Target = target;
        Arguments = arguments;
    }

    public void BindTarget(FunctionSymbol targetSymbol)
    {
        TargetSymbol = targetSymbol;
    }
}

namespace CxCompiler.Model.Expressions;

public sealed class ArrayAccessExpression : ExpressionBase
{
    public ExpressionBase Target { get; }
    public IReadOnlyList<ExpressionBase> Indices { get; }

    public ArrayAccessExpression(
        ExpressionBase target,
        IReadOnlyList<ExpressionBase> indices)
    {
        Target = target;
        Indices = indices;
    }
}

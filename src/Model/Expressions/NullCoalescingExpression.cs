namespace CxCompiler.Model.Expressions;

public sealed class NullCoalescingExpression : ExpressionBase
{
    public ExpressionBase Left { get; }

    public ExpressionBase Right { get; }

    public NullCoalescingExpression(ExpressionBase left, ExpressionBase right)
    {
        Left = left;
        Right = right;
    }
}

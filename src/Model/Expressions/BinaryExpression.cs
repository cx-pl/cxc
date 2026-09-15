namespace CxCompiler.Model.Expressions;

public sealed class BinaryExpression : ExpressionBase
{
    public ExpressionBase Left { get; }
    public string Operator { get; }
    public ExpressionBase Right { get; }

    public BinaryExpression(ExpressionBase left, string @operator, ExpressionBase right)
    {
        Left = left;
        Operator = @operator;
        Right = right;
    }
}

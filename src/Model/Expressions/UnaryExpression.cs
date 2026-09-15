namespace CxCompiler.Model.Expressions;

public sealed class UnaryExpression : ExpressionBase
{
    public string Operator { get; }
    public ExpressionBase Operand { get; }
    public bool Postfix { get; }

    public UnaryExpression(string @operator, ExpressionBase operand, bool postfix = false)
    {
        Operator = @operator;
        Operand = operand;
        Postfix = postfix;
    }
}

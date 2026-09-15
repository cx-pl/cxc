namespace CxCompiler.Model.Expressions;

public sealed class AssignmentExpression : ExpressionBase
{
    public ExpressionBase Target { get; }
    public string Operator { get; }
    public ExpressionBase Value { get; }

    public AssignmentExpression(
        ExpressionBase target,
        string @operator,
        ExpressionBase value)
    {
        Target = target;
        Operator = @operator;
        Value = value;
    }
}

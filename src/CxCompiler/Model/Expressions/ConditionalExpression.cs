namespace CxCompiler.Model.Expressions;

public sealed class ConditionalExpression : ExpressionBase
{
    public ExpressionBase Condition { get; }

    public ExpressionBase WhenTrue { get; }

    public ExpressionBase WhenFalse { get; }

    public ConditionalExpression(
        ExpressionBase condition,
        ExpressionBase whenTrue,
        ExpressionBase whenFalse)
    {
        Condition = condition;
        WhenTrue = whenTrue;
        WhenFalse = whenFalse;
    }
}

namespace CxCompiler.Model.Expressions;

/// <summary>
/// An explicit suspension expression. Binding and lowering determine whether
/// the enclosing function and awaited value support asynchronous execution.
/// </summary>
public sealed class AwaitExpression(ExpressionBase operand) : ExpressionBase
{
    public ExpressionBase Operand { get; private set; } = operand;
    public ExpressionBase? LoweredExpression { get; private set; }

    public void ReplaceOperand(ExpressionBase expression) => Operand = expression;

    public void BindLoweredExpression(ExpressionBase expression) =>
        LoweredExpression = expression;
}

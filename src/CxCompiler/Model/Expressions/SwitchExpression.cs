namespace CxCompiler.Model.Expressions;

public sealed class SwitchExpression : ExpressionBase
{
    public ExpressionBase Selector { get; }
    public IReadOnlyList<SwitchExpressionArm> Arms { get; }
    public string? TemporaryName { get; private set; }

    public SwitchExpression(ExpressionBase selector, IReadOnlyList<SwitchExpressionArm> arms)
    {
        Selector = selector;
        Arms = arms;
    }

    public void BindTemporary(string temporaryName) => TemporaryName = temporaryName;
}

public sealed class SwitchExpressionArm(
    ExpressionBase? Label,
    ExpressionBase? Filter,
    ExpressionBase Value)
{
    public ExpressionBase? Label { get; } = Label;
    public ExpressionBase? Filter { get; } = Filter;
    public ExpressionBase Value { get; } = Value;
    public bool IsDefault => Label is null;
}

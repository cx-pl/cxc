namespace CxCompiler.Model.Expressions;

using CxCompiler.Semantics;

public sealed class ArrayAccessExpression : ExpressionBase
{
    public ExpressionBase Target { get; }
    public IReadOnlyList<ExpressionBase> Indices { get; }
    public PropertySymbol? TargetProperty { get; private set; }
    public PropertyAccessorSymbol? PropertyGetter { get; private set; }

    public ArrayAccessExpression(
        ExpressionBase target,
        IReadOnlyList<ExpressionBase> indices)
    {
        Target = target;
        Indices = indices;
    }

    public void BindProperty(PropertySymbol property, PropertyAccessorSymbol getter)
    {
        TargetProperty = property;
        PropertyGetter = getter;
    }
}

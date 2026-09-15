namespace CxCompiler.Model.Expressions;

using CxCompiler.Semantics;

public sealed class MemberAccessExpression : ExpressionBase
{
    public ExpressionBase Target { get; }
    public string MemberName { get; }

    public FieldSymbol? TargetField { get; private set; }
    public PropertySymbol? TargetProperty { get; private set; }
    public PropertyAccessorSymbol? PropertyGetter { get; private set; }

    public MemberAccessExpression(ExpressionBase target, string memberName)
    {
        Target = target;
        MemberName = memberName;
    }

    public void BindField(FieldSymbol field)
    {
        TargetField = field;
    }

    public void BindProperty(PropertySymbol property, PropertyAccessorSymbol getter)
    {
        TargetProperty = property;
        PropertyGetter = getter;
    }
}

namespace CxCompiler.Model.Expressions;

using CxCompiler.Semantics;

public sealed class AssignmentExpression : ExpressionBase
{
    public ExpressionBase Target { get; }
    public string Operator { get; }
    public ExpressionBase Value { get; }
    public PropertySymbol? TargetProperty { get; private set; }
    public PropertyAccessorSymbol? PropertySetter { get; private set; }
    public string? TemporaryName { get; private set; }

    public AssignmentExpression(
        ExpressionBase target,
        string @operator,
        ExpressionBase value)
    {
        Target = target;
        Operator = @operator;
        Value = value;
    }

    public void BindPropertySetter(
        PropertySymbol property,
        PropertyAccessorSymbol setter,
        string temporaryName)
    {
        TargetProperty = property;
        PropertySetter = setter;
        TemporaryName = temporaryName;
    }
}

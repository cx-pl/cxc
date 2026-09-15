using CxCompiler.Model.Common;

namespace CxCompiler.Model.Expressions;

using CxCompiler.Semantics;

public sealed class IdentifierExpression : ExpressionBase
{
    public QualifiedIdentifier Identifier { get; }

    public FieldSymbol? TargetField { get; private set; }
    public PropertySymbol? TargetProperty { get; private set; }
    public PropertyAccessorSymbol? PropertyGetter { get; private set; }

    public IdentifierExpression(QualifiedIdentifier identifier)
    {
        Identifier = identifier;
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

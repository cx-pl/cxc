using CxCompiler.Model.Common;

namespace CxCompiler.Model.Expressions;

public sealed class IdentifierExpression : ExpressionBase
{
    public QualifiedIdentifier Identifier { get; }

    public IdentifierExpression(QualifiedIdentifier identifier)
    {
        Identifier = identifier;
    }
}

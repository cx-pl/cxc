using CxCompiler.Model.Expressions;

namespace CxCompiler.Model.Statements;

public sealed class ThrowStatement : StatementBase
{
    public ExpressionBase? Expression { get; }

    public ThrowStatement(ExpressionBase? expression)
    {
        Expression = expression;
    }
}

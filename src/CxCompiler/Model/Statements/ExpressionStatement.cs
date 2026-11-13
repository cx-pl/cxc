using CxCompiler.Model.Expressions;

namespace CxCompiler.Model.Statements;

public sealed class ExpressionStatement : StatementBase
{
    public ExpressionBase Expression { get; }

    public ExpressionStatement(ExpressionBase expression)
    {
        Expression = expression;
    }
}

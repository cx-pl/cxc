using CxCompiler.Model.Expressions;

namespace CxCompiler.Model.Statements;

public sealed class ReturnStatement : StatementBase
{
    public ExpressionBase? Expression { get; }

    public ReturnStatement(ExpressionBase? expression)
    {
        Expression = expression;
    }
}

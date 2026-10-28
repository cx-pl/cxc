using CxCompiler.Model.Expressions;

namespace CxCompiler.Model.Statements;

public sealed class ReturnStatement : StatementBase
{
    public ExpressionBase? Expression { get; private set; }

    public ReturnStatement(ExpressionBase? expression)
    {
        Expression = expression;
    }

    public void SetExpression(ExpressionBase? expression) => Expression = expression;
}

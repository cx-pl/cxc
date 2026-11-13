using CxCompiler.Model.Expressions;

namespace CxCompiler.Model.Statements;

public sealed class YieldReturnStatement(ExpressionBase expression) : StatementBase
{
    public ExpressionBase Expression { get; } = expression;
}

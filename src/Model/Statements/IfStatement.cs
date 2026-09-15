using CxCompiler.Model.Expressions;

namespace CxCompiler.Model.Statements;

public sealed class IfStatement : StatementBase
{
    public ExpressionBase Condition { get; }
    public StatementBase ThenStatement { get; }
    public StatementBase? ElseStatement { get; }

    public IfStatement(
        ExpressionBase condition,
        StatementBase thenStatement,
        StatementBase? elseStatement)
    {
        Condition = condition;
        ThenStatement = thenStatement;
        ElseStatement = elseStatement;
    }
}

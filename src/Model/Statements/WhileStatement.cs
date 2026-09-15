using CxCompiler.Model.Expressions;

namespace CxCompiler.Model.Statements;

public sealed class WhileStatement : StatementBase
{
    public ExpressionBase Condition { get; }
    public StatementBase Body { get; }

    public WhileStatement(ExpressionBase condition, StatementBase body)
    {
        Condition = condition;
        Body = body;
    }
}

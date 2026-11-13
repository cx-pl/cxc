using CxCompiler.Model.Expressions;

namespace CxCompiler.Model.Statements;

public sealed class DoWhileStatement : StatementBase
{
    public StatementBase Body { get; }
    public ExpressionBase Condition { get; }

    public DoWhileStatement(StatementBase body, ExpressionBase condition)
    {
        Body = body;
        Condition = condition;
    }
}

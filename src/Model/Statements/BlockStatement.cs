namespace CxCompiler.Model.Statements;

public sealed class BlockStatement : StatementBase
{
    public IReadOnlyList<StatementBase> Statements { get; }

    public BlockStatement(IReadOnlyList<StatementBase> statements)
    {
        Statements = statements;
    }
}

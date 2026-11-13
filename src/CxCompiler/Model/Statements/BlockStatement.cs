namespace CxCompiler.Model.Statements;

public sealed class BlockStatement : StatementBase
{
    public IReadOnlyList<StatementBase> Statements { get; }
    public bool IsUnsafe { get; }

    public BlockStatement(IReadOnlyList<StatementBase> statements, bool isUnsafe = false)
    {
        Statements = statements;
        IsUnsafe = isUnsafe;
    }
}

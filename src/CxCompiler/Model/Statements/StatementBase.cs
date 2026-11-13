namespace CxCompiler.Model.Statements;

public abstract class StatementBase : CxCompiler.Model.Common.IHasSourceSpan
{
    public CxCompiler.Model.Common.SourceSpan? SourceSpan { get; set; }
}

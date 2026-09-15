namespace CxCompiler.Model.Expressions;

public sealed class LiteralExpression : ExpressionBase
{
    public string SourceText { get; }
    public bool IsString => SourceText.StartsWith('"');

    public LiteralExpression(string sourceText)
    {
        SourceText = sourceText;
    }
}

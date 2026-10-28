namespace CxCompiler.Model.Expressions;

public class LiteralExpression : ExpressionBase
{
    public string SourceText { get; }
    public bool IsString => SourceText.StartsWith('"');

    public LiteralExpression(string sourceText)
    {
        SourceText = sourceText;
    }
}

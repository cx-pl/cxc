using Antlr4.Runtime;

namespace CxCompiler;

public sealed class ParserErrorListener : BaseErrorListener, IAntlrErrorListener<int>
{
    private readonly string? _sourcePath;
    private readonly List<string> _diagnostics = [];
    private readonly HashSet<string> _seenDiagnostics = new(StringComparer.Ordinal);

    public IReadOnlyList<string> Diagnostics => _diagnostics;
    public bool HasErrors => _diagnostics.Count > 0;

    public ParserErrorListener(string? sourcePath = null)
    {
        _sourcePath = sourcePath;
    }

    public override void SyntaxError(
        TextWriter output,
        IRecognizer recognizer,
        IToken offendingSymbol,
        int line,
        int charPositionInLine,
        string msg,
        RecognitionException e)
    {
        AddDiagnostic(recognizer, line, charPositionInLine, msg);
    }

    public void SyntaxError(
        TextWriter output,
        IRecognizer recognizer,
        int offendingSymbol,
        int line,
        int charPositionInLine,
        string msg,
        RecognitionException e)
    {
        AddDiagnostic(recognizer, line, charPositionInLine, msg);
    }

    private void AddDiagnostic(IRecognizer recognizer, int line, int column, string message)
    {
        var sourcePath = _sourcePath ?? recognizer.InputStream.SourceName;
        var diagnostic = $"{sourcePath}({line},{column + 1}): error: {message}";
        if (_seenDiagnostics.Add(diagnostic))
        {
            _diagnostics.Add(diagnostic);
        }
    }
}

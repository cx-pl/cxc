using Antlr4.Runtime;

namespace CxCompiler;

class ParserErrorListener : BaseErrorListener, IAntlrErrorListener<int>
{
    public override void SyntaxError(
        TextWriter output,
        IRecognizer recognizer,
        IToken offendingSymbol,
        int line,
        int charPositionInLine,
        string msg,
        RecognitionException e)
    {
        string sourceName = recognizer.InputStream.SourceName;
        Console.WriteLine("line:{0} col:{1} src:{2} msg:{3}", line, charPositionInLine, sourceName, msg);
        Console.WriteLine("--------------------");
        Console.WriteLine(e);
        Console.WriteLine("--------------------");
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
        string sourceName = recognizer.InputStream.SourceName;
        Console.WriteLine("line:{0} col:{1} src:{2} msg:{3}", line, charPositionInLine, sourceName, msg);
        Console.WriteLine("--------------------");
        Console.WriteLine(e);
        Console.WriteLine("--------------------");
    }
}
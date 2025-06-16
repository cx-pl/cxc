namespace CxCompiler.OutputGenerators;

public sealed class IndentingWriter
{
    private StreamWriter _writer;
    private int _indent = 0;
    private string _identString;

    public IndentingWriter(StreamWriter writer, char indentChar = ' ', int indentSize = 4)
    {
        _writer = writer;
        _identString = new string(indentChar, indentSize);
    }

    public void Write(string value)
    {
        _writer.Write(value);
    }

    public void WriteNewLine()
    {
        _writer.WriteLine();
    }

    public void WriteLine()
    {
        WriteIndent();
        _writer.WriteLine();
    }

    public void WriteLine(string? value)
    {
        WriteIndent();
        _writer.WriteLine(value);
    }

    public void WriteIndent()
    {
        for (int i = 0; i < _indent; i++)
        {
            _writer.Write(_identString);
        }
    }

    public void IncreaseIndent()
    {
        _indent++;
    }

    public void DecreaseIndent()
    {
        _indent--;
    }
}
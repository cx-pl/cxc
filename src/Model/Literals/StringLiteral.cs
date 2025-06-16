namespace CxCompiler.Model.Literals;

public class StringLiteral : LiteralBase
{
    public readonly string Value;

    public StringLiteral(string value)
    {
        Value = value;
    }
}
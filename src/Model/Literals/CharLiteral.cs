namespace CxCompiler.Model.Literals;

public class CharLiteral : LiteralBase
{
    public readonly char Value;

    public CharLiteral(char value)
    {
        Value = value;
    }
}

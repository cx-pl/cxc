namespace CxCompiler.Model.Literals;

public class IntegerLiteral : LiteralBase
{
    public readonly IntegerType Type;
    public readonly long Value;
    public readonly IntegerRepresentation Representation;

    public IntegerLiteral(IntegerType type, long value, IntegerRepresentation representation)
    {
        Type = type;
        Value = value;
        Representation = representation;
    }
}

namespace CxCompiler.Model.Literals;

public class FloatingLiteral : LiteralBase
{
    public readonly FloatingType Type;
    public readonly int Integer;
    public readonly int Fractional;
    public readonly int Exponent;

    public FloatingLiteral(FloatingType type, int integer, int fractional, int exponent)
    {
        Type = type;
        Integer = integer;
        Fractional = fractional;
        Exponent = exponent;
    }
}

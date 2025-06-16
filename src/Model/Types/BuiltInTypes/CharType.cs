using CxCompiler.Model.Common;

namespace CxCompiler.Model.Types.BuiltInTypes;

public class CharType : ValueTypeBase
{
    public override int Size => 4;

    public CharType(QualifiedIdentifier @namespace)
        : base("Char", @namespace)
    {
    }
}

using CxCompiler.Model.Common;

namespace CxCompiler.Model.Types.BuiltInTypes;

public class UShortType : IntTypeBase
{
    public override int Size => 2;
    public override bool Signed => false;

    public UShortType(QualifiedIdentifier @namespace)
        : base("UShort", @namespace)
    {
    }
}

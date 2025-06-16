using CxCompiler.Model.Common;

namespace CxCompiler.Model.Types.BuiltInTypes;

public class ULongType : IntTypeBase
{
    public override int Size => 8;
    public override bool Signed => false;

    public ULongType(QualifiedIdentifier @namespace)
        : base("ULong", @namespace)
    {
    }
}

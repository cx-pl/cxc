using CxCompiler.Model.Common;

namespace CxCompiler.Model.Types.BuiltInTypes;

public class UIntType : IntTypeBase
{
    public override int Size => 4;
    public override bool Signed => false;

    public UIntType(QualifiedIdentifier @namespace)
        : base("UInt", @namespace)
    {
    }
}

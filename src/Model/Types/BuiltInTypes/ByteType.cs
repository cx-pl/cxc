using CxCompiler.Model.Common;

namespace CxCompiler.Model.Types.BuiltInTypes;

public class ByteType : IntTypeBase
{
    public override int Size => 1;
    public override bool Signed => false;

    public ByteType(QualifiedIdentifier @namespace)
        : base("Byte", @namespace)
    {
    }
}

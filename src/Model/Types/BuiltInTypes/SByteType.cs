using CxCompiler.Model.Common;

namespace CxCompiler.Model.Types.BuiltInTypes;

public class SByteType : IntTypeBase
{
    public override int Size => 1;
    public override bool Signed => true;

    public SByteType(QualifiedIdentifier @namespace)
        : base("SByte", @namespace)
    {
    }
}

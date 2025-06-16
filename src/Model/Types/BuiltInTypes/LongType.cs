using CxCompiler.Model.Common;

namespace CxCompiler.Model.Types.BuiltInTypes;

public class LongType : IntTypeBase
{
    public override int Size => 8;
    public override bool Signed => true;

    public LongType(QualifiedIdentifier @namespace)
        : base("Long", @namespace)
    {
    }
}

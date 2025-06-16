using CxCompiler.Model.Common;

namespace CxCompiler.Model.Types.BuiltInTypes;

public class ShortType : IntTypeBase
{
    public override int Size => 2;
    public override bool Signed => true;

    public ShortType(QualifiedIdentifier @namespace)
        : base("Short", @namespace)
    {
    }
}

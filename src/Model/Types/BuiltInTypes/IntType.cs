using CxCompiler.Model.Common;

namespace CxCompiler.Model.Types.BuiltInTypes;

public class IntType : IntTypeBase
{
    public override int Size => 4;
    public override bool Signed => true;

    public IntType(QualifiedIdentifier @namespace)
        : base("Int", @namespace)
    {
    }
}

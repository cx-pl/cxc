using CxCompiler.Model.Common;

namespace CxCompiler.Model.Types.BuiltInTypes;

public sealed class WCharType(QualifiedIdentifier @namespace) : IntTypeBase("WChar", @namespace)
{
    public override int Size => 2;
    public override bool Signed => false;
}

using CxCompiler.Model.Common;

namespace CxCompiler.Model.Types.BuiltInTypes;

public class DoubleType : FloatTypeBase
{
    public override int Size => 8;

    public DoubleType(QualifiedIdentifier @namespace)
        : base("Double", @namespace)
    {
    }
}

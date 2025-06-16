using CxCompiler.Model.Common;

namespace CxCompiler.Model.Types.BuiltInTypes;

public class FloatType : FloatTypeBase
{
    public override int Size => 4;

    public FloatType(QualifiedIdentifier @namespace)
        : base("Float", @namespace)
    {
    }
}

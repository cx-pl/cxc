using CxCompiler.Model.Common;

namespace CxCompiler.Model.Types.BuiltInTypes;

public class BoolType : ValueTypeBase
{
    public override int Size => 1;

    public BoolType(QualifiedIdentifier @namespace)
        : base("Bool", @namespace)
    {
    }
}

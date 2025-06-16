using CxCompiler.Model.Common;

namespace CxCompiler.Model.Types.BuiltInTypes;

public class PtrType : ValueTypeBase
{
    public override int Size => 8;

    public PtrType(QualifiedIdentifier @namespace)
        : base("Ptr", @namespace)
    {
    }
}
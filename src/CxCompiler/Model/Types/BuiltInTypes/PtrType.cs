using CxCompiler.Model.Common;

namespace CxCompiler.Model.Types.BuiltInTypes;

public class PtrType : ValueTypeBase
{
    public TypeBase? Pointee { get; }
    public override int Size => 8;

    public PtrType(QualifiedIdentifier @namespace, TypeBase? pointee = null)
        : base("Ptr", @namespace)
    {
        Pointee = pointee;
    }
}

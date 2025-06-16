using CxCompiler.Model.Common;

namespace CxCompiler.Model.Types.BuiltInTypes;

public class VoidType : TypeBase
{
    public VoidType(QualifiedIdentifier @namespace)
        : base("Void", @namespace)
    {
    }
}

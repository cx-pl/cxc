using CxCompiler.Model.Common;

namespace CxCompiler.Model.Types.BuiltInTypes;

public class FunctionType : TypeBase
{
    public FunctionType(QualifiedIdentifier @namespace)
        : base("Function", @namespace)
    {
    }
}

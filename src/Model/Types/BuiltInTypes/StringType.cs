using CxCompiler.Model.Common;

namespace CxCompiler.Model.Types.BuiltInTypes;

public class StringType : ReferenceTypeBase
{
    public StringType(QualifiedIdentifier @namespace)
        : base("String", @namespace, BuiltInSystemTypes.Object)
    {
    }
}

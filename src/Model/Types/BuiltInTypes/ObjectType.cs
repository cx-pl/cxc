using CxCompiler.Model.Common;

namespace CxCompiler.Model.Types.BuiltInTypes;

public class ObjectType : ReferenceTypeBase
{
    public ObjectType(QualifiedIdentifier @namespace)
        : base("Object", @namespace, null)
    {
    }
}

using CxCompiler.Model.Common;

namespace CxCompiler.Model.Types;

public abstract class ReferenceTypeBase : TypeBase
{
    public ReferenceTypeBase? BaseType { get; }

    protected ReferenceTypeBase(string name, QualifiedIdentifier @namespace, ReferenceTypeBase? baseType) 
        : base(name, @namespace)
    {
        BaseType = baseType;
    }
}

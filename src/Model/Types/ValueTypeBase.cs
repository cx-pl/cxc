using CxCompiler.Model.Common;

namespace CxCompiler.Model.Types;

public abstract class ValueTypeBase : TypeBase
{
    public abstract int Size { get; }

    protected ValueTypeBase(string name, QualifiedIdentifier @namespace)
        : base(name, @namespace)
    {
    }
}
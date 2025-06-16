using CxCompiler.Model.Common;

namespace CxCompiler.Model.Types.BuiltInTypes;

public abstract class IntTypeBase : ValueTypeBase
{
    public abstract bool Signed { get; }

    public IntTypeBase(string name, QualifiedIdentifier @namespace)
        : base(name, @namespace)
    {
    }
}

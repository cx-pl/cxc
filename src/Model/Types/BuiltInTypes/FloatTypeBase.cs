using CxCompiler.Model.Common;

namespace CxCompiler.Model.Types.BuiltInTypes;

public abstract class FloatTypeBase : ValueTypeBase
{
    public FloatTypeBase(string name, QualifiedIdentifier @namespace)
        : base(name, @namespace)
    {
    }
}

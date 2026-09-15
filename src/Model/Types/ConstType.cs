using CxCompiler.Model.Common;

namespace CxCompiler.Model.Types;

public sealed class ConstType : TypeBase
{
    public TypeBase UnderlyingType { get; }

    public ConstType(TypeBase underlyingType)
        : base(underlyingType.Name, underlyingType.Namespace)
    {
        UnderlyingType = underlyingType;
    }
}

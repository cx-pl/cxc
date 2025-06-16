using CxCompiler.Model.Common;

namespace CxCompiler.Model.Types;

public class NullableType : TypeBase
{
    public TypeBase UnderlyingType { get; }
    
    public NullableType(TypeBase underlyingType)
        : base($"{underlyingType.Name}?", QualifiedIdentifier.Empty)
    {
        UnderlyingType = underlyingType;
    }
}
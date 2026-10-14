using CxCompiler.Model.Common;

namespace CxCompiler.Model.Types;

public sealed class FixedArrayType(TypeBase elementType, int length)
    : TypeBase($"{elementType.Name}[{length}]", QualifiedIdentifier.Empty)
{
    public TypeBase ElementType { get; } = elementType;
    public int Length { get; } = length;
}

using CxCompiler.Model.Common;

namespace CxCompiler.Model.Types;

public class ArrayType : TypeBase
{
    public TypeBase ElementType { get; }
    
    public ArrayType(TypeBase elementType)
        : base($"{elementType.Name}[]", QualifiedIdentifier.Empty)
    {
        ElementType = elementType;
    }
}

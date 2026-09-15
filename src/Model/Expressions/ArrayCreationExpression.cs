using CxCompiler.Model.Types;

namespace CxCompiler.Model.Expressions;

public sealed class ArrayCreationExpression : ExpressionBase
{
    public TypeBase ElementType { get; }
    public ExpressionBase Length { get; }

    public ArrayCreationExpression(TypeBase elementType, ExpressionBase length)
    {
        ElementType = elementType;
        Length = length;
    }
}

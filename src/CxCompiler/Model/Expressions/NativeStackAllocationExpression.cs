using CxCompiler.Model.Types;

namespace CxCompiler.Model.Expressions;

public sealed class NativeStackAllocationExpression(TypeBase elementType, ExpressionBase count) : ExpressionBase
{
    public TypeBase ElementType { get; } = elementType;
    public ExpressionBase Count { get; } = count;
}

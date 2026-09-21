using CxCompiler.Model.Types;

namespace CxCompiler.Model.Expressions;

public sealed class CastExpression(TypeBase targetType, ExpressionBase operand) : ExpressionBase
{
    public TypeBase TargetType { get; } = targetType;
    public ExpressionBase Operand { get; } = operand;
}

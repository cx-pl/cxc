using CxCompiler.Model.Types;

namespace CxCompiler.Model.Expressions;

public sealed class TypeTestExpression(ExpressionBase operand, TypeBase targetType) : ExpressionBase
{
    public ExpressionBase Operand { get; } = operand;
    public TypeBase TargetType { get; } = targetType;
}

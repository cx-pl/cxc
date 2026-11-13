using CxCompiler.Model.Types;

namespace CxCompiler.Model.Expressions;

public sealed class TypeTestExpression(ExpressionBase operand, TypeBase targetType) : ExpressionBase
{
    public ExpressionBase Operand { get; } = operand;
    public TypeBase TargetType { get; private set; } = targetType;
    internal void BindGenericTypeParameters(IReadOnlyCollection<string> parameterNames) =>
        TargetType = GenericTypeSubstitution.BindParameters(TargetType, parameterNames);
}

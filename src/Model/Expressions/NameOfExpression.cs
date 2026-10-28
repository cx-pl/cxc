using CxCompiler.Model.Types;

namespace CxCompiler.Model.Expressions;

public sealed class NameOfExpression(string name, TypeBase? type, ExpressionBase? operand)
    : LiteralExpression($"\"{name}\"")
{
    public TypeBase? Type { get; } = type;
    public ExpressionBase? Operand { get; } = operand;
}

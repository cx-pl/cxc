using CxCompiler.Model.Types;

namespace CxCompiler.Model.Expressions;

public sealed class CastExpression(TypeBase targetType, ExpressionBase operand, bool isSafe = false) : ExpressionBase
{
    public bool IsSafe { get; } = isSafe;
    public TypeBase TargetType { get; private set; } = targetType;
    public ExpressionBase Operand { get; } = operand;
    public CxCompiler.Semantics.FunctionSymbol? NativeCallbackSymbol { get; internal set; }

    internal void SetClosedTargetType(TypeBase targetType) => TargetType = targetType;
}

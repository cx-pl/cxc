using CxCompiler.Model.Types;

namespace CxCompiler.Model.Expressions;

public sealed class TypeOfExpression(TypeBase type) : ExpressionBase
{
    public TypeBase Type { get; private set; } = type;
    internal void SetClosedType(TypeBase type) => Type = type;
}

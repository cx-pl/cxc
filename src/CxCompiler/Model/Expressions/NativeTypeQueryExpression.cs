using CxCompiler.Model.Types;

namespace CxCompiler.Model.Expressions;

public sealed class NativeTypeQueryExpression(string query, TypeBase type, string? field) : ExpressionBase
{
    public string Query { get; } = query;
    public TypeBase Type { get; } = type;
    public string? Field { get; } = field;
}

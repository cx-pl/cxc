using CxCompiler.Model.Types;
using CxCompiler.Semantics;

namespace CxCompiler.Model.Expressions;

public sealed class ObjectCreationExpression : ExpressionBase
{
    public TypeBase RequestedType { get; }

    public IReadOnlyList<ExpressionBase> Arguments { get; }

    public FunctionSymbol? Constructor { get; private set; }

    public ClassType? ClassType { get; private set; }

    public string? TemporaryName { get; private set; }

    public ObjectCreationExpression(
        TypeBase requestedType,
        IReadOnlyList<ExpressionBase> arguments)
    {
        RequestedType = requestedType;
        Arguments = arguments;
    }

    public void BindConstructor(
        FunctionSymbol constructor,
        ClassType classType,
        string temporaryName)
    {
        Constructor = constructor;
        ClassType = classType;
        TemporaryName = temporaryName;
    }
}

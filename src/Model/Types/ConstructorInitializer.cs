using CxCompiler.Model.Expressions;
using CxCompiler.Semantics;

namespace CxCompiler.Model.Types;

public enum ConstructorInitializerKind
{
    Base,
    This,
}

public sealed class ConstructorInitializer
{
    public ConstructorInitializerKind Kind { get; }
    public IReadOnlyList<ExpressionBase> Arguments { get; }
    public FunctionSymbol? Target { get; private set; }

    public ConstructorInitializer(
        ConstructorInitializerKind kind,
        IReadOnlyList<ExpressionBase> arguments)
    {
        Kind = kind;
        Arguments = arguments;
    }

    public void BindTarget(FunctionSymbol target)
    {
        Target = target;
    }
}

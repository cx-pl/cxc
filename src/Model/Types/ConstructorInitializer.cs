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
    private readonly List<ExpressionBase> _arguments;
    public IReadOnlyList<ExpressionBase> Arguments => _arguments;
    public FunctionSymbol? Target { get; private set; }

    public ConstructorInitializer(
        ConstructorInitializerKind kind,
        IReadOnlyList<ExpressionBase> arguments)
    {
        Kind = kind;
        _arguments = arguments.ToList();
    }

    public void BindTarget(FunctionSymbol target)
    {
        Target = target;
    }

    public void AddDefaultArguments(IEnumerable<ExpressionBase> arguments) =>
        _arguments.AddRange(arguments);
}

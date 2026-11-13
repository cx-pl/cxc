using CxCompiler.Model.Common;

namespace CxCompiler.Model.Types.BuiltInTypes;

public sealed class NativeFunctionType(TypeBase returnType, IReadOnlyList<TypeBase> parameters,
    string convention, string failureResult)
    : FunctionType(QualifiedIdentifier.Empty, returnType, parameters)
{
    public int ContextParameter { get; internal set; } = -1;
    public bool SymbolOnly { get; internal set; }
    public string CallingConvention { get; } = convention;
    public string FailureResult { get; } = failureResult;
}

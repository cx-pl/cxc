namespace CxCompiler.Model.Expressions;

using CxCompiler.Semantics;

public sealed class InvocationExpression : ExpressionBase
{
    public ExpressionBase Target { get; }
    private readonly List<ExpressionBase> _arguments;
    public IReadOnlyList<ExpressionBase> Arguments => _arguments;
    public IReadOnlyList<CxCompiler.Model.Types.TypeBase> ExplicitTypeArguments { get; }
    public FunctionSymbol? TargetSymbol { get; private set; }
    private readonly Dictionary<string, FunctionSymbol> _closedGenericTargets =
        new(StringComparer.Ordinal);
    public CxCompiler.Model.Types.BuiltInTypes.FunctionType? FunctionType { get; private set; }
    public ExpressionBase? Receiver { get; private set; }
    public int ReceiverBaseDepth { get; private set; }
    public int? DispatchSlotIndex { get; private set; }
    public CxCompiler.Model.Types.NamedType? DispatchInterfaceType { get; private set; }
    public CxCompiler.Model.Types.NamedType? OpenDispatchInterfaceType { get; private set; }
    public string? ReceiverTemporaryName { get; private set; }
    public string? FunctionValueTemporaryName { get; private set; }
    public bool IsDelegateInvocationList { get; private set; }
    public bool IsConceptMethodInvocation { get; private set; }
    public ExpressionBase? DelegateInvocationListReceiver { get; private set; }
    public IReadOnlyList<CxCompiler.Model.Types.TypeBase> ConceptMethodTypeArguments { get; private set; } = [];

    public InvocationExpression(
        ExpressionBase target,
        IReadOnlyList<ExpressionBase> arguments,
        IReadOnlyList<CxCompiler.Model.Types.TypeBase>? explicitTypeArguments = null)
    {
        Target = target;
        _arguments = arguments.ToList();
        ExplicitTypeArguments = explicitTypeArguments ?? [];
    }

    public void BindTarget(
        FunctionSymbol targetSymbol,
        ExpressionBase? receiver = null,
        int receiverBaseDepth = 0,
        int? dispatchSlotIndex = null,
        string? receiverTemporaryName = null,
        CxCompiler.Model.Types.NamedType? dispatchInterfaceType = null)
    {
        TargetSymbol = targetSymbol;
        Receiver = receiver;
        ReceiverBaseDepth = receiverBaseDepth;
        DispatchSlotIndex = dispatchSlotIndex;
        ReceiverTemporaryName = receiverTemporaryName;
        DispatchInterfaceType = dispatchInterfaceType;
        OpenDispatchInterfaceType = dispatchInterfaceType;
    }

    public void SetClosedDispatchInterfaceType(
        CxCompiler.Model.Types.NamedType? dispatchInterfaceType) =>
        DispatchInterfaceType = dispatchInterfaceType;

    public void BindFunctionType(
        CxCompiler.Model.Types.BuiltInTypes.FunctionType functionType,
        string temporaryName)
    {
        FunctionType = functionType;
        FunctionValueTemporaryName = temporaryName;
    }

    public void BindDelegateInvocationList(
        CxCompiler.Model.Types.BuiltInTypes.FunctionType functionType,
        ExpressionBase receiver,
        string temporaryName)
    {
        FunctionType = functionType;
        FunctionValueTemporaryName = temporaryName;
        IsDelegateInvocationList = true;
        DelegateInvocationListReceiver = receiver;
    }

    public void AddDefaultArguments(IEnumerable<ExpressionBase> arguments) =>
        _arguments.AddRange(arguments);

    public void BindConceptMethodTypeArguments(
        IReadOnlyList<CxCompiler.Model.Types.TypeBase> typeArguments) =>
        ConceptMethodTypeArguments = typeArguments;

    public void BindAsConceptMethod() => IsConceptMethodInvocation = true;

    public void BindClosedGenericTarget(string callerSpecializationName, FunctionSymbol target) =>
        _closedGenericTargets[callerSpecializationName] = target;

    public FunctionSymbol? GetClosedGenericTarget(string callerSpecializationName) =>
        _closedGenericTargets.GetValueOrDefault(callerSpecializationName);

    public void ApplyClosedGenericTarget(string callerSpecializationName)
    {
        if (_closedGenericTargets.TryGetValue(callerSpecializationName, out var target))
        {
            TargetSymbol = target;
        }
    }

    public void RestoreTargetSymbol(FunctionSymbol? targetSymbol) =>
        TargetSymbol = targetSymbol;
}

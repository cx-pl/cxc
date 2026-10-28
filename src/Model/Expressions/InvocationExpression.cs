namespace CxCompiler.Model.Expressions;

using CxCompiler.Semantics;
using CxCompiler.Model.Types;

public sealed class InvocationExpression : ExpressionBase
{
    public ExpressionBase Target { get; }
    private readonly List<ExpressionBase> _arguments;
    public IReadOnlyList<ExpressionBase> Arguments => _arguments;
    public IReadOnlyList<CxCompiler.Model.Types.TypeBase> ExplicitTypeArguments { get; }
    public FunctionSymbol? TargetSymbol { get; private set; }
    private readonly Dictionary<string, FunctionSymbol> _closedGenericTargets =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, GenericVirtualDispatchTarget>
        _genericVirtualDispatchTargets = new(StringComparer.Ordinal);
    public CxCompiler.Model.Types.BuiltInTypes.FunctionType? FunctionType { get; private set; }
    public ExpressionBase? Receiver { get; private set; }
    public int ReceiverBaseDepth { get; private set; }
    public int? DispatchSlotIndex { get; private set; }
    public CxCompiler.Model.Types.NamedType? DispatchInterfaceType { get; private set; }
    public CxCompiler.Model.Types.NamedType? OpenDispatchInterfaceType { get; private set; }
    public string? ReceiverTemporaryName { get; private set; }
    public string? FunctionValueTemporaryName { get; private set; }
    public bool IsDelegateInvocationList { get; private set; }
    public bool IsDelegateDynamicInvoke { get; private set; }
    public bool IsNativeResolve { get; private set; }
    public bool IsNativeRegistration { get; private set; }
    public bool IsNativeRegistrationFunction { get; private set; }
    public CxCompiler.Model.Types.BuiltInTypes.FunctionType? ManagedCallbackSignature { get; private set; }
    public bool IsConceptMethodInvocation { get; private set; }
    public ExpressionBase? DelegateInvocationListReceiver { get; private set; }
    public ExpressionBase? DelegateDynamicInvokeReceiver { get; private set; }
    public IReadOnlyList<CxCompiler.Model.Types.TypeBase> ConceptMethodTypeArguments { get; private set; } = [];
    public IReadOnlyCollection<GenericVirtualDispatchTarget> GenericVirtualDispatchTargets =>
        _genericVirtualDispatchTargets.Values;
    public FunctionDeclaration? DirectGeneratedIteratorTarget { get; private set; }
    public PropertyAccessorDeclaration? DirectGeneratedIteratorGetter { get; private set; }
    public ClassDeclaration? DirectGeneratedIteratorOwner { get; private set; }
    public ExpressionBase? DirectGeneratedIteratorReceiver { get; private set; }

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

    public void BindDirectGeneratedIteratorCall(
        FunctionDeclaration target,
        ClassDeclaration owner,
        ExpressionBase receiver)
    {
        DirectGeneratedIteratorTarget = target;
        DirectGeneratedIteratorOwner = owner;
        DirectGeneratedIteratorReceiver = receiver;
    }

    public void BindDirectGeneratedIteratorGetter(
        PropertyAccessorDeclaration getter,
        ClassDeclaration owner,
        ExpressionBase receiver)
    {
        DirectGeneratedIteratorGetter = getter;
        DirectGeneratedIteratorOwner = owner;
        DirectGeneratedIteratorReceiver = receiver;
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

    public void BindNativeResolve(CxCompiler.Model.Types.BuiltInTypes.NativeFunctionType signature,
        ExpressionBase receiver, string temporaryName)
    {
        FunctionType = signature;
        Receiver = receiver;
        FunctionValueTemporaryName = temporaryName;
        IsNativeResolve = true;
    }

    public void BindNativeRegistration(CxCompiler.Model.Types.BuiltInTypes.NativeFunctionType native,
        CxCompiler.Model.Types.BuiltInTypes.FunctionType managed, string temporaryName, ExpressionBase? receiver = null)
    {
        FunctionType = native; ManagedCallbackSignature = managed;
        FunctionValueTemporaryName = temporaryName; Receiver = receiver;
        IsNativeRegistration = receiver is null;
        IsNativeRegistrationFunction = receiver is not null;
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

    public void BindDelegateDynamicInvoke(
        CxCompiler.Model.Types.BuiltInTypes.FunctionType functionType,
        ExpressionBase receiver,
        string temporaryName)
    {
        FunctionType = functionType;
        FunctionValueTemporaryName = temporaryName;
        IsDelegateDynamicInvoke = true;
        DelegateDynamicInvokeReceiver = receiver;
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

    public void BindGenericVirtualDispatchTarget(
        CxCompiler.Model.Types.NamedType runtimeType,
        FunctionSymbol implementation)
    {
        var runtimeIdentity = runtimeType.ConstructedIdentity?.CanonicalName ??
            runtimeType.ResolvedTypeFullName.ToString();
        var declaration = implementation.Declaration;
        var methodArguments = declaration is null
            ? []
            : declaration.GenericTypeNames.Select(name =>
                implementation.GenericTypeArguments.GetValueOrDefault(name)?.FullName.ToString() ??
                throw new InternalCompilerException(
                    $"Generic virtual implementation '{implementation.FullName}' has no closed type argument for '{name}'."));
        var key = $"{runtimeIdentity}|{string.Join("|", methodArguments)}";
        _genericVirtualDispatchTargets[key] =
            new GenericVirtualDispatchTarget(runtimeType, implementation);
    }
}

public sealed record GenericVirtualDispatchTarget(
    CxCompiler.Model.Types.NamedType RuntimeType,
    FunctionSymbol Implementation);

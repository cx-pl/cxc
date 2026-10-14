namespace CxCompiler.Model.Expressions;

using CxCompiler.Semantics;

public sealed class MemberAccessExpression : ExpressionBase
{
    private readonly Dictionary<string, GenericVirtualDispatchTarget>
        _genericVirtualDispatchTargets = new(StringComparer.Ordinal);
    public ExpressionBase Target { get; }
    public string MemberName { get; }
    public IReadOnlyList<CxCompiler.Model.Types.TypeBase>? StaticContainingTypeArguments { get; private set; }

    public FieldSymbol? TargetField { get; private set; }
    public PropertySymbol? TargetProperty { get; private set; }
    public PropertyAccessorSymbol? PropertyGetter { get; private set; }
    public EnumMemberSymbol? TargetEnumMember { get; private set; }
    public FunctionSymbol? FunctionValueSymbol { get; private set; }
    public bool FunctionValueIsStatic { get; private set; }
    public string? FunctionValueAdapterName { get; private set; }
    public string? FunctionValueEnvironmentName { get; private set; }
    public string? FunctionValueReceiverTemporaryName { get; private set; }
    public bool IsFunctionValueTarget { get; private set; }
    public bool IsFunctionValueMethod { get; private set; }
    public string? FunctionValueMetadataTemporaryName { get; private set; }
    public int ReceiverBaseDepth { get; private set; }
    public int? InterfaceDispatchSlotIndex { get; private set; }
    public string? InterfaceReceiverTemporaryName { get; private set; }
    public CxCompiler.Model.Types.NamedType? InterfaceDispatchType { get; private set; }
    public IReadOnlyCollection<GenericVirtualDispatchTarget> GenericVirtualDispatchTargets =>
        _genericVirtualDispatchTargets.Values;

    public MemberAccessExpression(ExpressionBase target, string memberName)
    {
        Target = target;
        MemberName = memberName;
    }

    public void SetStaticContainingTypeArguments(
        IReadOnlyList<CxCompiler.Model.Types.TypeBase> typeArguments) =>
        StaticContainingTypeArguments = typeArguments;

    public void BindField(FieldSymbol field, int receiverBaseDepth = 0)
    {
        TargetField = field;
        ReceiverBaseDepth = receiverBaseDepth;
    }

    public void BindProperty(
        PropertySymbol property,
        PropertyAccessorSymbol getter,
        int receiverBaseDepth = 0,
        int? interfaceDispatchSlotIndex = null,
        string? interfaceReceiverTemporaryName = null,
        CxCompiler.Model.Types.NamedType? interfaceDispatchType = null)
    {
        TargetProperty = property;
        PropertyGetter = getter;
        ReceiverBaseDepth = receiverBaseDepth;
        InterfaceDispatchSlotIndex = interfaceDispatchSlotIndex;
        InterfaceReceiverTemporaryName = interfaceReceiverTemporaryName;
        InterfaceDispatchType = interfaceDispatchType;
    }

    public void BindPropertyTarget(PropertySymbol property)
    {
        TargetProperty = property;
    }

    public void BindEnumMember(EnumMemberSymbol member)
    {
        TargetEnumMember = member;
    }

    public void BindFunctionValue(
        FunctionSymbol symbol,
        string adapterName,
        string environmentName,
        string receiverTemporaryName,
        int receiverBaseDepth,
        int? interfaceDispatchSlotIndex)
    {
        FunctionValueSymbol = symbol;
        FunctionValueAdapterName = adapterName;
        FunctionValueEnvironmentName = environmentName;
        FunctionValueReceiverTemporaryName = receiverTemporaryName;
        ReceiverBaseDepth = receiverBaseDepth;
        InterfaceDispatchSlotIndex = interfaceDispatchSlotIndex;
    }

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
                throw new CxCompiler.Model.InternalCompilerException(
                    $"Generic virtual implementation '{implementation.FullName}' has no closed type argument for '{name}'."));
        var key = $"{runtimeIdentity}|{string.Join("|", methodArguments)}";
        _genericVirtualDispatchTargets[key] =
            new GenericVirtualDispatchTarget(runtimeType, implementation);
    }

    public void BindStaticFunctionValue(FunctionSymbol symbol)
    {
        FunctionValueSymbol = symbol;
        FunctionValueIsStatic = true;
    }

    public void BindFunctionValueTarget(string temporaryName)
    {
        IsFunctionValueTarget = true;
        FunctionValueMetadataTemporaryName = temporaryName;
    }

    public void BindFunctionValueMethod(string temporaryName)
    {
        IsFunctionValueMethod = true;
        FunctionValueMetadataTemporaryName = temporaryName;
    }
}

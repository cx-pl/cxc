namespace CxCompiler.Model.Expressions;

using CxCompiler.Semantics;

public sealed class AssignmentExpression : ExpressionBase
{
    public ExpressionBase Target { get; }
    public string Operator { get; }
    public ExpressionBase Value { get; }
    public PropertySymbol? TargetProperty { get; private set; }
    public PropertyAccessorSymbol? PropertySetter { get; private set; }
    public PropertyAccessorSymbol? PropertyGetter { get; private set; }
    public string? PropertyReceiverTemporaryName { get; private set; }
    public IReadOnlyList<string> PropertyIndexTemporaryNames { get; private set; } = [];
    public int? PropertyGetterInterfaceDispatchSlotIndex { get; private set; }
    public string? PropertyGetterInterfaceReceiverTemporaryName { get; private set; }
    public CxCompiler.Model.Types.NamedType? PropertyGetterInterfaceDispatchType { get; private set; }
    public string? TemporaryName { get; private set; }
    public int ReceiverBaseDepth { get; private set; }
    public int? InterfaceDispatchSlotIndex { get; private set; }
    public string? InterfaceReceiverTemporaryName { get; private set; }
    public CxCompiler.Model.Types.NamedType? InterfaceDispatchType { get; private set; }
    public string? DelegateTemporaryName { get; private set; }
    public FunctionSymbol? OperatorSymbol { get; private set; }
    public int OperatorBaseDepth { get; private set; }

    public AssignmentExpression(
        ExpressionBase target,
        string @operator,
        ExpressionBase value)
    {
        Target = target;
        Operator = @operator;
        Value = value;
    }

    public void BindPropertySetter(
        PropertySymbol property,
        PropertyAccessorSymbol setter,
        string temporaryName,
        int receiverBaseDepth = 0,
        int? interfaceDispatchSlotIndex = null,
        string? interfaceReceiverTemporaryName = null,
        CxCompiler.Model.Types.NamedType? interfaceDispatchType = null,
        string? receiverTemporaryName = null,
        IReadOnlyList<string>? indexTemporaryNames = null)
    {
        TargetProperty = property;
        PropertySetter = setter;
        TemporaryName = temporaryName;
        ReceiverBaseDepth = receiverBaseDepth;
        InterfaceDispatchSlotIndex = interfaceDispatchSlotIndex;
        InterfaceReceiverTemporaryName = interfaceReceiverTemporaryName;
        InterfaceDispatchType = interfaceDispatchType;
        PropertyReceiverTemporaryName = receiverTemporaryName;
        PropertyIndexTemporaryNames = indexTemporaryNames ?? [];
    }

    public void BindPropertyCompound(
        PropertySymbol property,
        PropertyAccessorSymbol getter,
        PropertyAccessorSymbol setter,
        string valueTemporaryName,
        int receiverBaseDepth = 0,
        string? receiverTemporaryName = null,
        int? getterInterfaceDispatchSlotIndex = null,
        string? getterInterfaceReceiverTemporaryName = null,
        CxCompiler.Model.Types.NamedType? getterInterfaceDispatchType = null,
        int? setterInterfaceDispatchSlotIndex = null,
        string? setterInterfaceReceiverTemporaryName = null,
        CxCompiler.Model.Types.NamedType? setterInterfaceDispatchType = null,
        IReadOnlyList<string>? indexTemporaryNames = null)
    {
        TargetProperty = property;
        PropertyGetter = getter;
        PropertySetter = setter;
        TemporaryName = valueTemporaryName;
        PropertyReceiverTemporaryName = receiverTemporaryName;
        PropertyIndexTemporaryNames = indexTemporaryNames ?? [];
        ReceiverBaseDepth = receiverBaseDepth;
        PropertyGetterInterfaceDispatchSlotIndex = getterInterfaceDispatchSlotIndex;
        PropertyGetterInterfaceReceiverTemporaryName = getterInterfaceReceiverTemporaryName;
        PropertyGetterInterfaceDispatchType = getterInterfaceDispatchType;
        InterfaceDispatchSlotIndex = setterInterfaceDispatchSlotIndex;
        InterfaceReceiverTemporaryName = setterInterfaceReceiverTemporaryName;
        InterfaceDispatchType = setterInterfaceDispatchType;
    }

    public void BindOperator(
        FunctionSymbol symbol,
        int baseDepth,
        string? temporaryName = null)
    {
        OperatorSymbol = symbol;
        OperatorBaseDepth = baseDepth;
        TemporaryName = temporaryName;
    }

    public void BindDelegateCompound(string temporaryName)
    {
        DelegateTemporaryName = temporaryName;
    }
}

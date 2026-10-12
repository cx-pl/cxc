namespace CxCompiler.Model.Expressions;

using CxCompiler.Semantics;

public sealed class UnaryExpression : ExpressionBase
{
    public string Operator { get; }
    public ExpressionBase Operand { get; }
    public bool Postfix { get; }
    public FunctionSymbol? OperatorSymbol { get; private set; }
    public int OperatorBaseDepth { get; private set; }
    public string? OperatorTemporaryName { get; private set; }
    public string? OperatorOldValueTemporaryName { get; private set; }
    public PropertySymbol? TargetProperty { get; private set; }
    public PropertyAccessorSymbol? PropertyGetter { get; private set; }
    public PropertyAccessorSymbol? PropertySetter { get; private set; }
    public string? PropertyReceiverTemporaryName { get; private set; }
    public IReadOnlyList<string> PropertyIndexTemporaryNames { get; private set; } = [];
    public int? PropertyGetterInterfaceDispatchSlotIndex { get; private set; }
    public string? PropertyGetterInterfaceReceiverTemporaryName { get; private set; }
    public CxCompiler.Model.Types.NamedType? PropertyGetterInterfaceDispatchType { get; private set; }
    public int? PropertySetterInterfaceDispatchSlotIndex { get; private set; }
    public string? PropertySetterInterfaceReceiverTemporaryName { get; private set; }
    public CxCompiler.Model.Types.NamedType? PropertySetterInterfaceDispatchType { get; private set; }
    public int PropertyReceiverBaseDepth { get; private set; }

    public UnaryExpression(string @operator, ExpressionBase operand, bool postfix = false)
    {
        Operator = @operator;
        Operand = operand;
        Postfix = postfix;
    }

    public void BindOperator(
        FunctionSymbol symbol,
        int baseDepth,
        string? temporaryName = null,
        string? oldValueTemporaryName = null)
    {
        OperatorSymbol = symbol;
        OperatorBaseDepth = baseDepth;
        OperatorTemporaryName = temporaryName;
        OperatorOldValueTemporaryName = oldValueTemporaryName;
    }

    public void BindPropertyMutation(
        PropertySymbol property,
        PropertyAccessorSymbol getter,
        PropertyAccessorSymbol setter,
        string valueTemporaryName,
        string? oldValueTemporaryName,
        int receiverBaseDepth,
        string? receiverTemporaryName,
        IReadOnlyList<string> indexTemporaryNames,
        int? getterInterfaceDispatchSlotIndex,
        string? getterInterfaceReceiverTemporaryName,
        CxCompiler.Model.Types.NamedType? getterInterfaceDispatchType,
        int? setterInterfaceDispatchSlotIndex,
        string? setterInterfaceReceiverTemporaryName,
        CxCompiler.Model.Types.NamedType? setterInterfaceDispatchType)
    {
        TargetProperty = property;
        PropertyGetter = getter;
        PropertySetter = setter;
        OperatorTemporaryName = valueTemporaryName;
        OperatorOldValueTemporaryName = oldValueTemporaryName;
        PropertyReceiverBaseDepth = receiverBaseDepth;
        PropertyReceiverTemporaryName = receiverTemporaryName;
        PropertyIndexTemporaryNames = indexTemporaryNames;
        PropertyGetterInterfaceDispatchSlotIndex = getterInterfaceDispatchSlotIndex;
        PropertyGetterInterfaceReceiverTemporaryName = getterInterfaceReceiverTemporaryName;
        PropertyGetterInterfaceDispatchType = getterInterfaceDispatchType;
        PropertySetterInterfaceDispatchSlotIndex = setterInterfaceDispatchSlotIndex;
        PropertySetterInterfaceReceiverTemporaryName = setterInterfaceReceiverTemporaryName;
        PropertySetterInterfaceDispatchType = setterInterfaceDispatchType;
    }
}

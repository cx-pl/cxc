namespace CxCompiler.Model.Expressions;

using CxCompiler.Semantics;

public sealed class MemberAccessExpression : ExpressionBase
{
    public ExpressionBase Target { get; }
    public string MemberName { get; }

    public FieldSymbol? TargetField { get; private set; }
    public PropertySymbol? TargetProperty { get; private set; }
    public PropertyAccessorSymbol? PropertyGetter { get; private set; }
    public EnumMemberSymbol? TargetEnumMember { get; private set; }
    public int ReceiverBaseDepth { get; private set; }
    public int? InterfaceDispatchSlotIndex { get; private set; }
    public string? InterfaceReceiverTemporaryName { get; private set; }

    public MemberAccessExpression(ExpressionBase target, string memberName)
    {
        Target = target;
        MemberName = memberName;
    }

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
        string? interfaceReceiverTemporaryName = null)
    {
        TargetProperty = property;
        PropertyGetter = getter;
        ReceiverBaseDepth = receiverBaseDepth;
        InterfaceDispatchSlotIndex = interfaceDispatchSlotIndex;
        InterfaceReceiverTemporaryName = interfaceReceiverTemporaryName;
    }

    public void BindEnumMember(EnumMemberSymbol member)
    {
        TargetEnumMember = member;
    }
}

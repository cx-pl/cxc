using CxCompiler.Model.Common;
using CxCompiler.Model.Types.BuiltInTypes;

namespace CxCompiler.Model.Types;

public enum InterfaceDispatchSlotKind
{
    Method,
    PropertyGetter,
    PropertySetter,
}

public sealed record InterfaceDispatchSlot(
    int Index,
    InterfaceDispatchSlotKind Kind,
    DeclarationBase Contract,
    DeclarationBase? Implementation)
{
    public FunctionDeclaration? ContractFunction => Contract as FunctionDeclaration;
    public PropertyAccessorDeclaration? ContractAccessor => Contract as PropertyAccessorDeclaration;
    public FunctionDeclaration? ImplementationFunction => Implementation as FunctionDeclaration;
    public PropertyAccessorDeclaration? ImplementationAccessor => Implementation as PropertyAccessorDeclaration;

    public TypeBase ReturnType => ContractFunction?.ReturnType ??
        (Kind == InterfaceDispatchSlotKind.PropertySetter
            ? BuiltInSystemTypes.Void
            : ContractAccessor!.ParentPropertyDeclaration.Type);

    public bool Const => ContractFunction?.Const ?? ContractAccessor!.Const;

    public IReadOnlyList<FunctionParameter> Parameters
    {
        get
        {
            if (ContractFunction is { } function)
            {
                return function.Parameters;
            }

            var accessor = ContractAccessor!;
            if (Kind != InterfaceDispatchSlotKind.PropertySetter)
            {
                return accessor.Parameters;
            }

            return [.. accessor.Parameters, new FunctionParameter(
                "value",
                accessor.ParentPropertyDeclaration.Type,
                null)];
        }
    }
}

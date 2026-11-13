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
    public IReadOnlyDictionary<string, TypeBase> ContractTypeArguments { get; init; } =
        new Dictionary<string, TypeBase>(StringComparer.Ordinal);

    public FunctionDeclaration? ContractFunction => Contract as FunctionDeclaration;
    public PropertyAccessorDeclaration? ContractAccessor => Contract as PropertyAccessorDeclaration;
    public FunctionDeclaration? ImplementationFunction => Implementation as FunctionDeclaration;
    public PropertyAccessorDeclaration? ImplementationAccessor => Implementation as PropertyAccessorDeclaration;

    public TypeBase ReturnType => ContractFunction is { } function
        ? GenericTypeSubstitution.Substitute(function.ReturnType, ContractTypeArguments)
        :
        (Kind == InterfaceDispatchSlotKind.PropertySetter
            ? BuiltInSystemTypes.Void
            : GenericTypeSubstitution.Substitute(
                ContractAccessor!.ParentPropertyDeclaration.Type, ContractTypeArguments));

    public bool Const => ContractFunction?.Const ?? ContractAccessor!.Const;

    public IReadOnlyList<FunctionParameter> Parameters
    {
        get
        {
            if (ContractFunction is { } function)
            {
                return function.Parameters.Select(parameter => Substitute(parameter)).ToArray();
            }

            var accessor = ContractAccessor!;
            if (Kind != InterfaceDispatchSlotKind.PropertySetter)
            {
                return accessor.Parameters.Select(parameter => Substitute(parameter)).ToArray();
            }

            return [.. accessor.Parameters, new FunctionParameter(
                "value",
                GenericTypeSubstitution.Substitute(
                    accessor.ParentPropertyDeclaration.Type, ContractTypeArguments),
                null)];
        }
    }

    private FunctionParameter Substitute(FunctionParameter parameter) => new(
        parameter.Name,
        GenericTypeSubstitution.Substitute(parameter.ParameterType, ContractTypeArguments),
        parameter.DefaultValueText,
        parameter.DefaultValueIsIdentifier)
    {
        DefaultNameOfExpression = parameter.DefaultNameOfExpression,
    };
}

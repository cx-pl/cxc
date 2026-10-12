namespace CxCompiler.Model.Types;

public sealed record InterfaceDispatchTable(
    ClassDeclaration Interface,
    IReadOnlyList<InterfaceDispatchSlot> Slots,
    NamedType? ConstructedInterfaceType = null);

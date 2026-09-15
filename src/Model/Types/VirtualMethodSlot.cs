namespace CxCompiler.Model.Types;

public sealed record VirtualMethodSlot(
    int Index,
    FunctionDeclaration Contract,
    FunctionDeclaration? Implementation);

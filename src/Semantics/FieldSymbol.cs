using CxCompiler.Model.Types;

namespace CxCompiler.Semantics;

public sealed class FieldSymbol
{
    public string ModuleName { get; }
    public TypeBase ContainingType { get; }
    public ClassType ContainingClassType { get; }
    public FieldDeclaration Declaration { get; }

    public FieldSymbol(
        string moduleName,
        TypeBase containingType,
        ClassType containingClassType,
        FieldDeclaration declaration)
    {
        ModuleName = moduleName;
        ContainingType = containingType;
        ContainingClassType = containingClassType;
        Declaration = declaration;
    }
}

using CxCompiler.Model.Types;
using CxCompiler.Model.Common;

namespace CxCompiler.Semantics;

public sealed class FieldSymbol
{
    public string ModuleName { get; }
    public TypeBase ContainingType { get; }
    public ClassType ContainingClassType { get; }
    public FieldDeclaration Declaration { get; }
    public IReadOnlyList<QualifiedIdentifier> Imports { get; }

    public FieldSymbol(
        string moduleName,
        TypeBase containingType,
        ClassType containingClassType,
        FieldDeclaration declaration,
        IReadOnlyList<QualifiedIdentifier>? imports = null)
    {
        ModuleName = moduleName;
        ContainingType = containingType;
        ContainingClassType = containingClassType;
        Declaration = declaration;
        Imports = imports ?? [];
    }
}

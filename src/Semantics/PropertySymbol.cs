using CxCompiler.Model.Common;
using CxCompiler.Model.Types;

namespace CxCompiler.Semantics;

public sealed class PropertySymbol
{
    public string ModuleName { get; }
    public QualifiedIdentifier FullName { get; }
    public TypeBase ContainingType { get; }
    public ClassType ContainingClassType { get; }
    public TypeBase Type { get; }
    public bool IsStatic { get; }
    public IReadOnlyList<PropertyAccessorSymbol> Accessors { get; }

    public PropertySymbol(
        string moduleName,
        QualifiedIdentifier fullName,
        TypeBase containingType,
        ClassType containingClassType,
        TypeBase type,
        bool isStatic,
        IReadOnlyList<PropertyAccessorSymbol> accessors)
    {
        ModuleName = moduleName;
        FullName = fullName;
        ContainingType = containingType;
        ContainingClassType = containingClassType;
        Type = type;
        IsStatic = isStatic;
        Accessors = accessors;
    }
}

public sealed record PropertyAccessorSymbol(
    string Name,
    bool Const,
    IReadOnlyList<TypeBase> ParameterTypes);

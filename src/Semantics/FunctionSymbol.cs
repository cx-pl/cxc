using CxCompiler.Model.Common;
using CxCompiler.Model.Types;

namespace CxCompiler.Semantics;

public sealed class FunctionSymbol
{
    public string ModuleName { get; }
    public QualifiedIdentifier FullName { get; }
    public IReadOnlyList<TypeBase> ParameterTypes { get; }
    public TypeBase ReturnType { get; }
    public int OverloadIndex { get; }
    public FunctionDeclaration? Declaration { get; }

    public FunctionSymbol(
        string moduleName,
        QualifiedIdentifier fullName,
        IReadOnlyList<TypeBase> parameterTypes,
        TypeBase returnType,
        int overloadIndex = 1,
        FunctionDeclaration? declaration = null)
    {
        ModuleName = moduleName;
        FullName = fullName;
        ParameterTypes = parameterTypes;
        ReturnType = returnType;
        OverloadIndex = overloadIndex;
        Declaration = declaration;
    }
}

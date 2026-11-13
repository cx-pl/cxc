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
    public NativeImportMetadata? NativeImport => Declaration?.NativeImport;
    public bool IsAsync => Declaration?.IsAsync ?? false;
    public FunctionDeclaration? ImplementationDeclaration { get; }
    public string? SpecializationName { get; }
    public NamedType? ClosedContainingType { get; }
    public IReadOnlyDictionary<string, FunctionSymbol> ConceptOperatorBindings { get; }
    public IReadOnlyDictionary<string, TypeBase> GenericTypeArguments { get; }

    public FunctionSymbol(
        string moduleName,
        QualifiedIdentifier fullName,
        IReadOnlyList<TypeBase> parameterTypes,
        TypeBase returnType,
        int overloadIndex = 1,
        FunctionDeclaration? declaration = null,
        string? specializationName = null,
        NamedType? closedContainingType = null,
        IReadOnlyDictionary<string, FunctionSymbol>? conceptOperatorBindings = null,
        IReadOnlyDictionary<string, TypeBase>? genericTypeArguments = null,
        FunctionDeclaration? implementationDeclaration = null)
    {
        ModuleName = moduleName;
        FullName = fullName;
        ParameterTypes = parameterTypes.Select(ArrayType.Canonicalize).ToArray();
        ReturnType = ArrayType.Canonicalize(returnType);
        OverloadIndex = overloadIndex;
        Declaration = declaration;
        ImplementationDeclaration = implementationDeclaration;
        SpecializationName = specializationName;
        ClosedContainingType = closedContainingType;
        ConceptOperatorBindings = conceptOperatorBindings ??
            new Dictionary<string, FunctionSymbol>(StringComparer.Ordinal);
        GenericTypeArguments = genericTypeArguments ??
            new Dictionary<string, TypeBase>(StringComparer.Ordinal);
    }
}

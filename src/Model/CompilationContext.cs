using CxCompiler.Model.Common;

namespace CxCompiler.Model;

public class CompilationContext
{
    public DeclarationScope DeclarationScope { get; } = new DeclarationScope();
    public QualifiedIdentifier Namespace => DeclarationScope.Namespace;

    private readonly List<QualifiedIdentifier> imports = [];
    public IReadOnlyList<QualifiedIdentifier> Imports => imports.AsReadOnly();

    public void SetNamespace(QualifiedIdentifier @namespace)
    {
        DeclarationScope.SetNamespace(@namespace);
    }

    public void AddImport(QualifiedIdentifier name)
    {
        imports.Add(name);
    }
}

using CxCompiler.Model.Common;

namespace CxCompiler.Model;

public class CompilationContext
{
    /// <summary>The CX project that owns these declarations; null means the active project.</summary>
    public string? ProjectName { get; internal set; }
    public bool IsProjectReference { get; internal set; }
    public DeclarationScope DeclarationScope { get; } = new DeclarationScope();
    public QualifiedIdentifier Namespace => DeclarationScope.FullNamespace;

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

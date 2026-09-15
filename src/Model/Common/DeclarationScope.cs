
namespace CxCompiler.Model.Common;

public class DeclarationScope
{
    public DeclarationScope? Parent { get; }
    public QualifiedIdentifier Namespace { get; private set; }
    public QualifiedIdentifier FullNamespace => Parent?.FullNamespace + Namespace;

    private readonly List<DeclarationBase> _declarations = [];
    public IReadOnlyList<DeclarationBase> Declarations => _declarations.AsReadOnly();

    public DeclarationScope(
        DeclarationScope? parent = null,
        QualifiedIdentifier? @namespace = null)
    {
        Parent = parent;

        Namespace = @namespace ?? QualifiedIdentifier.Empty;
    }

    public void SetNamespace(QualifiedIdentifier @namespace)
    {
        Namespace = @namespace;
    }

    public void AddDeclaration(DeclarationBase declaration)
    {
        _declarations.Add(declaration);
    }

    public void AddDeclarations(ReadOnlySpan<DeclarationBase> declarations)
    {
        _declarations.AddRange(declarations);
    }

    public IReadOnlyList<DeclarationBase> FindDeclarations(string name, bool searchInParent = true)
    {
        var declarations = _declarations
            .Where(x => x.Name == name)
            .ToArray();
        if (declarations.Length != 0)
        {
            return declarations;
        }

        if (searchInParent && Parent != null)
        {
            return Parent.FindDeclarations(name, searchInParent);
        }

        return [];
    }
}

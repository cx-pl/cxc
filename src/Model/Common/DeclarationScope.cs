
namespace CxCompiler.Model.Common;

public class DeclarationScope
{
    public DeclarationScope? Parent { get; }
    public QualifiedIdentifier Namespace { get; private set; } = QualifiedIdentifier.Empty;

    private readonly List<DeclarationBase> declarations = [];
    public IReadOnlyList<DeclarationBase> Declarations => declarations.AsReadOnly();

    public QualifiedIdentifier FullNamespace => _GetFullNamespace();

    public DeclarationScope(DeclarationScope? parent = null)
    {
        Parent = parent;
    }

    public void SetNamespace(QualifiedIdentifier @namespace)
    {
        Namespace = @namespace;
    }

    public void AddDeclaration(DeclarationBase declaration)
    {
        _ValidateDeclarationCanBeAdded(declaration);

        declarations.Add(declaration);
    }

    public void AddDeclarations(ReadOnlySpan<DeclarationBase> declarations)
    {
        foreach (var declaration in declarations)
        {
            AddDeclaration(declaration);
        }            
    }

    private void _ValidateDeclarationCanBeAdded(DeclarationBase declaration)
    {
        // TODO: add partial declaration support

        // TODO: implement
        //if (_FindDeclaration(declaration.Name, false) is not null)
        //{
        //    throw new CompilationErrorException($"'{declaration.Name}' is already declared");
        //}
    }

    private DeclarationBase? _FindDeclaration(string name, bool findInParent = true)
    {
        var declaration = declarations.FirstOrDefault(x => x.Name == name);
        if (declaration != null)
        {
            return declaration;
        }

        if (findInParent && Parent != null)
        {
            return Parent._FindDeclaration(name);
        }

        return null;
    }

    private QualifiedIdentifier _GetFullNamespace()
    {
        var parts = new List<string>(Namespace.Parts);
        var currentScope = Parent;
        while (currentScope != null)
        {
            if (!currentScope.Namespace.IsEmpty)
            {
                parts.InsertRange(0, currentScope.Namespace.Parts);
            }
            currentScope = currentScope.Parent;
        }
        return new QualifiedIdentifier(parts.ToArray());
    }
}
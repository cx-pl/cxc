namespace CxCompiler.Model.Common;

public abstract class DeclarationBase
{
    public string Name { get; }
    public QualifiedIdentifier Namespace { get; set; }
    public QualifiedIdentifier FullName => new QualifiedIdentifier(Namespace, Name);

    protected DeclarationBase(string name)
    {
        Name = name;
        Namespace = QualifiedIdentifier.Empty;
    }

    protected DeclarationBase(string name, QualifiedIdentifier @namespace)
    {
        Name = name;
        Namespace = @namespace;
    }
}

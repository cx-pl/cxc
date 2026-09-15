using System.Diagnostics;

namespace CxCompiler.Model.Common;

[DebuggerDisplay("{FullName}")]
public abstract class DeclarationBase
{
    public string DeclarationType { get; }
    public string Name { get; }
    public QualifiedIdentifier Namespace { get; }
    public QualifiedIdentifier FullName { get; }

    protected DeclarationBase(string type, string name)
        : this(type, QualifiedIdentifier.Empty, name)
    {
    }

    protected DeclarationBase(string type, QualifiedIdentifier? @namespace, string name)
    {
        Namespace = @namespace ?? QualifiedIdentifier.Empty;
        DeclarationType = type;
        Name = name;
        FullName = new(Namespace, Name);
    }
}

namespace CxCompiler.Model.Common;

// Source and bound constant metadata. Constructors are never executed by the compiler.
public sealed class AnnotationApplication : IHasSourceSpan
{
    public SourceSpan? SourceSpan { get; set; }
    public QualifiedIdentifier Name { get; }
    public string? Target { get; }
    public IReadOnlyList<AnnotationArgument> Arguments { get; }
    public string? ResolvedTypeName { get; internal set; }
    public IReadOnlyDictionary<string, string> BoundArguments { get; internal set; } =
        new Dictionary<string, string>();

    public AnnotationApplication(QualifiedIdentifier name, string? target,
        IReadOnlyList<AnnotationArgument> arguments)
    {
        Name = name;
        Target = target;
        Arguments = arguments;
    }
}

public sealed record AnnotationArgument(string? Name, string ValueText);

namespace CxCompiler.Model.Project;

public class CxProject
{
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Licence { get; set; } = string.Empty;
    public string Website { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public CxProjectType Type { get; set; }

    private List<CompilationContext> _compilationContexts = new List<CompilationContext>();
    public IReadOnlyList<CompilationContext> CompilationContexts => _compilationContexts.AsReadOnly();

    public static CxProject CreateDefaultApplicationProject(string name = "unnamed") => new()
    {
        Name = name,
        Version = "1.0",
        Type = CxProjectType.Executable,
    };

    public void AddCompilationContext(CompilationContext context)
    {
        if (context == null)
        {
            throw new InternalCompilerException("Compilation context cannot be null");
        }

        _compilationContexts.Add(context);
    }
}

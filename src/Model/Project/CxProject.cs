namespace CxCompiler.Model.Project;

public class CxProject
{
    public string Name { get; set; }
    public string Version { get; set; }
    public string Description { get; set; }
    public string Licence { get; set; }
    public string Website { get; set; }
    public string Author { get; set; }
    public CxProjectType Type { get; set; }

    private List<CompilationContext> _compilationContexts = new List<CompilationContext>();
    public IReadOnlyList<CompilationContext> CompilationContexts => _compilationContexts.AsReadOnly();

    public static CxProject CreateUnnamedApplicationProject() => new()
    {
        Name = "Unnamed",
        Version = "0.0",
        Type = CxProjectType.Application,
    };

    //public CxProject(string name, string version, string description, string author, CxProjectType type)
    //{
    //    Name = name;
    //    Version = version;
    //    Description = description;
    //    Author = author;
    //    Type = type;
    //}

    public void AddCompilationContext(CompilationContext context)
    {
        if (context == null)
        {
            throw new InternalCompilerException("Compilation context cannot be null");
        }

        _compilationContexts.Add(context);
    }
}

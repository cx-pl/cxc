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
    public List<string> Targets { get; set; } = [];
    public CxPackageSection Package { get; set; } = new();
    public List<string> Dependencies { get; set; } = [];

    private List<CompilationContext> _compilationContexts = new List<CompilationContext>();
    public IReadOnlyList<CompilationContext> CompilationContexts => _compilationContexts.AsReadOnly();
    private readonly Dictionary<string, CxCompiler.Semantics.FunctionSymbol> _genericFunctionInstances = [];
    public IReadOnlyCollection<CxCompiler.Semantics.FunctionSymbol> GenericFunctionInstances =>
        _genericFunctionInstances.Values;
    private readonly Dictionary<string, (CxCompiler.Model.Types.NamedType Type,
        CxCompiler.Model.Types.ClassDeclaration Declaration)> _genericTypeInstances = [];
    public IReadOnlyCollection<(CxCompiler.Model.Types.NamedType Type,
        CxCompiler.Model.Types.ClassDeclaration Declaration)> GenericTypeInstances =>
        _genericTypeInstances.Values;

    internal void ClearGenericTypeInstances() => _genericTypeInstances.Clear();

    internal void AddGenericTypeInstance(CxCompiler.Model.Types.NamedType type,
        CxCompiler.Model.Types.ClassDeclaration declaration)
    {
        if (type.ConstructedIdentity is { } identity)
        {
            _genericTypeInstances.TryAdd(identity.CanonicalName, (type, declaration));
        }
    }

    internal void ClearGenericFunctionInstances() => _genericFunctionInstances.Clear();

    internal void AddGenericFunctionInstance(CxCompiler.Semantics.FunctionSymbol symbol)
    {
        if (symbol.SpecializationName is { } name)
        {
            _genericFunctionInstances.TryAdd(name, symbol);
        }
    }

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

public class CxPackageSection
{
    public List<string> Sources { get; set; } = [];
    public List<string> Binary { get; set; } = [];
}

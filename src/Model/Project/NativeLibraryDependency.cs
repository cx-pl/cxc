namespace CxCompiler.Model.Project;

// Target selection is performed by generated CMake, including cross compilation.
public sealed class NativeLibraryDependency
{
    public NativeLibraryTarget? Windows { get; set; }
    public NativeLibraryTarget? Linux { get; set; }
    public string Visibility { get; set; } = "public";
    public string Provider { get; set; } = "sdk";
}

public sealed class NativeLibraryTarget
{
    public List<string> Architectures { get; set; } = [];
    public List<string> Toolchains { get; set; } = [];
    public List<string> Headers { get; set; } = [];
    public List<string> LinkLibraries { get; set; } = [];
    public List<string> IncludeDirectories { get; set; } = [];
    public List<string> Definitions { get; set; } = [];
    public List<string> RuntimeArtifacts { get; set; } = [];
    public List<string> RuntimeSearchPaths { get; set; } = [];
    public string? PkgConfig { get; set; }
    public string? CmakePackage { get; set; }
    public List<string> ImportedTargets { get; set; } = [];
    public string? SdkRoot { get; set; }
    public string? Version { get; set; }
    public string Linkage { get; set; } = "shared";
}

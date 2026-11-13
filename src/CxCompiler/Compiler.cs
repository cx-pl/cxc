using Antlr4.Runtime;
using CxCompiler.Grammar;
using CxCompiler.Model.Errors;
using CxCompiler.Model.Project;
using CxCompiler.OutputGenerators;
using CxCompiler.ParserVisitors;
using CxCompiler.Preprocessing;
using CxCompiler.Semantics;
using CxCompiler.Generators;
using System.Text.RegularExpressions;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using System.Diagnostics;

namespace CxCompiler;

public class Compiler
{
    public const string Usage = """
        Usage: cxc [options] <file.cx|project.cxproj>

        Options:
          -D, --define SYMBOL       Define a conditional-compilation symbol
          -o, --output-dir DIR      Write generated files to DIR (default: <project>/.obj)
          --module-name NAME        Override the module/target name (letters and digits only)
          --emit-only               Generate C and CMake files without building (default)
          --compile                 Build generated C with CMake
          --configuration NAME      Build configuration: Debug or Release (default: Release)
          --cxcore-dir DIR          Path to a cxcore source checkout for --compile
          -v, --verbosity LEVEL     quiet, normal, or verbose (default: normal)
          --diagnostics-format FMT  text or json (default: text)
          --version, -V             Show compiler version
          -h, --help                Show this help
        """;

    private sealed record Options(
        string? OutputDirectory,
        string? ModuleName,
        string? CxCoreDirectory,
        string Configuration,
        bool Build,
        string Verbosity,
        string DiagnosticsFormat,
        HashSet<string> Symbols,
        List<string> Inputs);

    private string? _projectPath = null;
    private CxProject? _project = null;
    private readonly Dictionary<string, string> _sourceTexts = new(StringComparer.Ordinal);
    private IReadOnlySet<string> _preprocessorSymbols = new HashSet<string>(StringComparer.Ordinal);

    public void Compile(ReadOnlySpan<string> args)
    {
        var options = ParseOptions(args);
        _verbosity = options.Verbosity;
        _diagnosticsFormat = options.DiagnosticsFormat;
        _outputDirectoryOverride = options.OutputDirectory;
        _moduleNameOverride = options.ModuleName;
        _cxCoreDirectory = options.CxCoreDirectory;
        _buildConfiguration = options.Configuration;
        _buildGeneratedCode = options.Build;
        var sourceArguments = options.Inputs;
        var symbols = options.Symbols;
        _preprocessorSymbols = symbols;

        if (sourceArguments.Count == 1 && sourceArguments[0].EndsWith(".cxproj", StringComparison.OrdinalIgnoreCase))
        {
            CompileProjectGraph(Path.GetFullPath(sourceArguments[0]), symbols, options);
            return;
        }
        foreach (var arg in sourceArguments)
        {
            if (arg.EndsWith(".cxproj", StringComparison.OrdinalIgnoreCase))
                CompileCxProjectFile(arg);
            else if (arg.EndsWith(".cx", StringComparison.OrdinalIgnoreCase))
                CompileCxSourceFile(arg);
            else
                throw new CompilationErrorException($"Unsupported file type: {arg}");
        }

        new SemanticBinder().Bind(_project!);
        var projectFilePath = Path.GetFullPath(_projectPath!);
        var projectDirectory = Path.GetDirectoryName(projectFilePath)!;
        var outputDirectory = ResolveOutputDirectory(projectDirectory);
        Directory.CreateDirectory(outputDirectory);
        IgnoreGeneratedDirectories(projectDirectory);
        CCodeOutputGenerator.GenerateOutput(
            _project!,
            Path.Combine(outputDirectory, $"{_project!.Name}.cxproj"),
            projectDirectory);
        if (_verbosity != "quiet") Console.WriteLine($"Generated {_project.Name} in {outputDirectory}");
        BuildIfRequested(outputDirectory, projectDirectory);
    }

    private static Options ParseOptions(ReadOnlySpan<string> args)
    {
        var sourceArguments = new List<string>();
        var symbols = new HashSet<string>(StringComparer.Ordinal);
        string? outputDirectory = null;
        string? moduleName = null;
        string? cxCoreDirectory = null;
        string configuration = "Release";
        string verbosity = "normal";
        string diagnosticsFormat = "text";
        var build = false;
        var emitOnly = false;
        for (var index = 0; index < args.Length; index++)
        {
            var arg = args[index];
            if (arg is "-h" or "--help" or "help")
                throw new CommandLineException("Help must be requested by itself.");
            if (arg is "--compile") { build = true; continue; }
            if (arg is "--emit-only") { emitOnly = true; continue; }
            if (arg is "-o" or "--output-dir" or "--module-name" or "--cxcore-dir" or
                "--configuration" or
                "-v" or "--verbosity" or "--diagnostics-format")
            {
                if (++index >= args.Length)
                    throw new CommandLineException($"Compiler option '{arg}' requires a value.");
                var value = args[index];
                switch (arg)
                {
                    case "-o": case "--output-dir": outputDirectory = value; break;
                    case "--module-name": moduleName = value; break;
                    case "--cxcore-dir": cxCoreDirectory = value; break;
                    case "--configuration": configuration = value; break;
                    case "-v": case "--verbosity": verbosity = value; break;
                    case "--diagnostics-format": diagnosticsFormat = value; break;
                }
                continue;
            }
            if (arg.StartsWith("--output-dir=", StringComparison.Ordinal)) { outputDirectory = arg[13..]; continue; }
            if (arg.StartsWith("--module-name=", StringComparison.Ordinal)) { moduleName = arg[14..]; continue; }
            if (arg.StartsWith("--cxcore-dir=", StringComparison.Ordinal)) { cxCoreDirectory = arg[13..]; continue; }
            if (arg.StartsWith("--configuration=", StringComparison.Ordinal)) { configuration = arg[16..]; continue; }
            if (arg.StartsWith("--verbosity=", StringComparison.Ordinal)) { verbosity = arg[12..]; continue; }
            if (arg.StartsWith("--diagnostics-format=", StringComparison.Ordinal)) { diagnosticsFormat = arg[21..]; continue; }
            if (arg.StartsWith("-", StringComparison.Ordinal) && arg is not ("-D" or "--define") &&
                !arg.StartsWith("--define=", StringComparison.Ordinal) &&
                !(arg.StartsWith("-D", StringComparison.Ordinal) && arg.Length > 2))
                throw new CommandLineException($"Unknown compiler option '{arg}'.");
            string? symbol = null;
            if (arg is "-D" or "--define")
            {
                if (++index >= args.Length)
                    throw new CommandLineException($"Compiler option '{arg}' requires a symbol name.");
                symbol = args[index];
            }
            else if (arg.StartsWith("--define=", StringComparison.Ordinal))
            {
                symbol = arg[9..];
            }
            else if (arg.StartsWith("-D", StringComparison.Ordinal) && arg.Length > 2)
            {
                symbol = arg[2..];
            }

            if (symbol is null)
            {
                sourceArguments.Add(arg);
                continue;
            }
            if (!System.Text.RegularExpressions.Regex.IsMatch(symbol, "^[A-Za-z_][A-Za-z0-9_]*$"))
                throw new CommandLineException($"Invalid preprocessor symbol '{symbol}'.");
            symbols.Add(symbol);
        }
        if (sourceArguments.Count == 0)
            throw new CommandLineException("A CX source file or project file is required.");
        if (sourceArguments.Count > 1 && sourceArguments.Any(input => input.EndsWith(".cxproj", StringComparison.OrdinalIgnoreCase)))
            throw new CommandLineException("A project file cannot be combined with other input files.");
        if (build && emitOnly)
            throw new CommandLineException("Options '--compile' and '--emit-only' cannot be combined.");
        if (verbosity is not ("quiet" or "normal" or "verbose"))
            throw new CommandLineException($"Unknown verbosity level '{verbosity}'. Expected quiet, normal, or verbose.");
        if (diagnosticsFormat is not ("text" or "json"))
            throw new CommandLineException($"Unknown diagnostics format '{diagnosticsFormat}'. Expected text or json.");
        if (configuration is not ("Debug" or "Release"))
            throw new CommandLineException($"Unknown build configuration '{configuration}'. Expected Debug or Release.");
        if (moduleName is not null && !Regex.IsMatch(moduleName, "^[A-Za-z][A-Za-z0-9]*$"))
            throw new CommandLineException("Module name must start with a letter and contain only letters and digits; underscores are not allowed.");
        if (outputDirectory is not null && string.IsNullOrWhiteSpace(outputDirectory))
            throw new CommandLineException("Output directory cannot be empty.");
        if (cxCoreDirectory is not null && string.IsNullOrWhiteSpace(cxCoreDirectory))
            throw new CommandLineException("cxcore directory cannot be empty.");
        return new Options(outputDirectory, moduleName, cxCoreDirectory, configuration, build,
            verbosity, diagnosticsFormat, symbols, sourceArguments);
    }

    private string _verbosity = "normal";
    private string _diagnosticsFormat = "text";
    private string? _outputDirectoryOverride;
    private string? _moduleNameOverride;
    private string? _cxCoreDirectory;
    private string _buildConfiguration = "Release";
    private bool _buildGeneratedCode;

    private string ResolveOutputDirectory(string projectDirectory) =>
        Path.GetFullPath(_outputDirectoryOverride is null
            ? Path.Combine(projectDirectory, ".obj")
            : Path.IsPathRooted(_outputDirectoryOverride)
                ? _outputDirectoryOverride
                : Path.Combine(Environment.CurrentDirectory, _outputDirectoryOverride));

    private void BuildIfRequested(string outputDirectory, string projectDirectory)
    {
        if (!_buildGeneratedCode) return;
        var cxCoreDirectory = _cxCoreDirectory ?? Environment.GetEnvironmentVariable("CXCORE_SOURCE_DIR")
            ?? FindSiblingCxCore(projectDirectory);
        if (!Directory.Exists(cxCoreDirectory) || !File.Exists(Path.Combine(cxCoreDirectory, "CMakeLists.txt")))
            throw new CompilationErrorException(
                "Native build requested, but cxcore was not found. Pass --cxcore-dir or set CXCORE_SOURCE_DIR.");

        var buildDirectory = Path.Combine(outputDirectory, "build");
        RunCMake(["-S", outputDirectory, "-B", buildDirectory,
            $"-DCXCORE_SOURCE_DIR={Path.GetFullPath(cxCoreDirectory)}",
            $"-DCMAKE_BUILD_TYPE={_buildConfiguration}"]);
        RunCMake(["--build", buildDirectory, "--config", _buildConfiguration]);

        void RunCMake(IEnumerable<string> arguments)
        {
            var startInfo = new ProcessStartInfo("cmake")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
            using var process = Process.Start(startInfo)
                ?? throw new CompilationErrorException("Could not start CMake.");
            var standardOutput = process.StandardOutput.ReadToEndAsync();
            var standardError = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            Task.WaitAll(standardOutput, standardError);
            if (_verbosity == "verbose")
            {
                Console.Write(standardOutput.Result);
                Console.Error.Write(standardError.Result);
            }
            if (process.ExitCode != 0)
                throw new CompilationErrorException(
                    $"CMake failed with exit code {process.ExitCode}.{Environment.NewLine}{standardError.Result.Trim()}");
        }
    }

    private static string FindSiblingCxCore(string startDirectory)
    {
        var current = new DirectoryInfo(Path.GetFullPath(startDirectory));
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "cxcore");
            if (File.Exists(Path.Combine(candidate, "CMakeLists.txt"))) return candidate;
            current = current.Parent;
        }
        return Path.Combine(startDirectory, "cxcore");
    }

    private void WriteDiagnostic(string severity, string message)
    {
        if (_diagnosticsFormat == "json")
            Console.Error.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { severity, message }));
        else
            Console.Error.WriteLine(message);
    }

    private void CompileCxProjectFile(string filePath)
    {
        if (_project is not null)
        {
            throw new CompilationErrorException("Project file has already been compiled. Only one project file can be processed at a time.");
        }

        using var reader = new StreamReader(filePath);
        _projectPath = filePath;
        _project = Path.GetExtension(filePath).ToLowerInvariant() switch
        {
            ".cxproj" or ".cxpm" or ".yaml" or ".yml" => ParseCxProjectFromYaml(reader),
            ".json" => ParseCxProjectFromJson(reader),
            _ => throw new CompilationErrorException($"Unsupported project file format: {Path.GetExtension(filePath)}"),
        };
        ValidateProjectName(_project.Name);
        if (_moduleNameOverride is not null)
            _project.Name = _moduleNameOverride;
        if (Path.GetFileName(filePath) == "package.cxpm")
        {
            // cxpm preserves the manifest and places selected payloads in source/ and bin/.
            // Translate manifest-relative assets into the retained source tree.
            string TranslateAssetPath(string value) => PackageAssetPath(value, _project.Package?.Root == "..");
            foreach (var library in _project.NativeLibraries.Values)
                foreach (var target in new[] { library.Windows, library.Linux }.OfType<NativeLibraryTarget>())
                {
                    target.IncludeDirectories = target.IncludeDirectories.Select(TranslateAssetPath).ToList();
                    target.RuntimeArtifacts = target.RuntimeArtifacts.Select(TranslateAssetPath).ToList();
                    target.RuntimeSearchPaths = target.RuntimeSearchPaths.Select(TranslateAssetPath).ToList();
                    target.LinkLibraries = target.LinkLibraries.Select(value => value.Contains('/') || value.Contains('\\') ? TranslateAssetPath(value) : value).ToList();
                    if (target.SdkRoot is { } sdkRoot) target.SdkRoot = TranslateAssetPath(sdkRoot);
                }
        }
        _project.Targets ??= [];
        if (_project.Targets.Any(target => string.IsNullOrWhiteSpace(target)
            || !Regex.IsMatch(target, "^[A-Za-z0-9][A-Za-z0-9._-]*$")))
            throw new CompilationErrorException("Project 'targets' entries must be valid Runtime Identifiers (RIDs).");

        var projectDirectoryPath = Path.GetDirectoryName(filePath)
            ?? throw new InvalidOperationException("Project file path is invalid.");
        var outputDirectory = ResolveOutputDirectory(projectDirectoryPath);
        var projectDirectory = new DirectoryInfo(projectDirectoryPath);
        if (!projectDirectory.Exists)
        {
            throw new InvalidOperationException($"Project directory does not exist: {projectDirectory.FullName}");
        }
        foreach (var sourceFile in projectDirectory.EnumerateFiles("*.cx", SearchOption.AllDirectories)
            .Where(file => !IsPathWithin(outputDirectory, file.FullName))
            .Where(file => !Path.GetRelativePath(projectDirectoryPath, file.FullName)
                .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(part => part is ".packages" or ".obj" or ".bin" or ".dist" or
                    "examples" or "samples" or "tests" or "benchmarks"))
            .OrderBy(file => file.FullName, StringComparer.Ordinal))
        {
            CompileCxSourceFile(sourceFile.FullName);
        }
    }

    private sealed record ProjectNode(string Path, CxProject Project, IReadOnlyList<ProjectNode> References,
        Compiler Compiler);

    private static string PackageAssetPath(string path, bool repositoryRoot)
    {
        if (Path.IsPathRooted(path)) return path;
        path = path.Replace('\\', '/');
        if (path.StartsWith("binary/", StringComparison.Ordinal)) return "bin/" + path[7..];
        if (path.StartsWith("bin/", StringComparison.Ordinal) || path.StartsWith("source/", StringComparison.Ordinal)) return path;
        if (!repositoryRoot) return "source/" + path;
        var parts = new List<string> { "src" };
        foreach (var part in path.Split('/'))
        {
            if (part is "" or ".") continue;
            if (part == "..")
            {
                if (parts.Count == 0)
                    throw new CompilationErrorException("Native package asset escapes package.root.");
                parts.RemoveAt(parts.Count - 1);
            }
            else parts.Add(part);
        }
        return "source/" + string.Join("/", parts);
    }

    private void CompileProjectGraph(string rootPath, IReadOnlySet<string> symbols, Options options)
    {
        var nodes = new Dictionary<string, ProjectNode>(StringComparer.OrdinalIgnoreCase);
        var active = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var root = LoadProject(rootPath);
        var duplicateNames = nodes.Values.GroupBy(node => node.Project.Name, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Select(node => node.Path)
                .Distinct(StringComparer.OrdinalIgnoreCase).Skip(1).Any());
        if (duplicateNames is not null)
            throw new CompilationErrorException(
                $"Multiple CX project files use target name '{duplicateNames.Key}'.");
        var compiled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CompileNode(root);

        ProjectNode LoadProject(string path)
        {
            path = Path.GetFullPath(path);
            if (active.Contains(path))
            {
                throw new CompilationErrorException($"Project reference cycle detected at '{path}'.");
            }
            if (nodes.TryGetValue(path, out var existing)) return existing;
            if (!File.Exists(path))
            {
                throw new CompilationErrorException($"Referenced CX project does not exist: '{path}'.");
            }

            active.Add(path);
            var compiler = new Compiler { _preprocessorSymbols = new HashSet<string>(symbols, StringComparer.Ordinal) };
            if (string.Equals(path, rootPath, StringComparison.OrdinalIgnoreCase))
            {
                compiler._moduleNameOverride = options.ModuleName;
                compiler._outputDirectoryOverride = options.OutputDirectory;
                compiler._cxCoreDirectory = options.CxCoreDirectory;
                compiler._buildConfiguration = options.Configuration;
                compiler._buildGeneratedCode = options.Build;
                compiler._verbosity = options.Verbosity;
                compiler._diagnosticsFormat = options.DiagnosticsFormat;
            }
            compiler.CompileCxProjectFile(path);
            var project = compiler._project!;
            var projectDirectory = Path.GetDirectoryName(path)!;
            var refs = new List<ProjectNode>();
            var seenReferences = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var reference in project.ProjectReferences.Order(StringComparer.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(reference))
                    throw new CompilationErrorException($"Project '{project.Name}' has an empty project reference.");
                var referencePath = Path.GetFullPath(Path.Combine(projectDirectory, reference));
                if (!string.Equals(Path.GetExtension(referencePath), ".cxproj",
                    StringComparison.OrdinalIgnoreCase))
                    throw new CompilationErrorException(
                        $"Project reference '{reference}' in '{project.Name}' must name a .cxproj file.");
                if (!seenReferences.Add(referencePath))
                    throw new CompilationErrorException($"Project '{project.Name}' references '{reference}' more than once.");
                refs.Add(LoadProject(referencePath));
            }
            if (project.Dependencies.Count > 0)
            {
                var lockPath = Path.Combine(projectDirectory, "cxpm.lock");
                var packageRoot = projectDirectory;
                if (Path.GetFileName(path) == "package.cxpm")
                {
                    // Restored transitive dependencies share the consuming app's lock.
                    var parent = Directory.GetParent(projectDirectory);
                    while (parent is not null && parent.Name != ".packages") parent = parent.Parent;
                    packageRoot = parent?.Parent?.FullName ?? projectDirectory;
                    lockPath = Path.Combine(packageRoot, "cxpm.lock");
                }
                if (!File.Exists(lockPath))
                    throw new CompilationErrorException($"Project '{project.Name}' has package dependencies; run cxpm restore before compilation.");
                using var lockDocument = System.Text.Json.JsonDocument.Parse(File.ReadAllText(lockPath));
                if (lockDocument.RootElement.GetProperty("LockVersion").GetInt32() != 1)
                    throw new CompilationErrorException("Unsupported cxpm.lock version; run cxpm restore with a compatible cxpm.");
                if (Path.GetFileName(path) != "package.cxpm")
                {
                    var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
                    if (lockDocument.RootElement.GetProperty("ProjectHash").GetString() != hash)
                        throw new CompilationErrorException("cxpm.lock does not match the project manifest; run cxpm restore before compilation.");
                }
                var packages = lockDocument.RootElement.GetProperty("Packages");
                foreach (var dependency in project.Dependencies.Order(StringComparer.Ordinal))
                {
                    var separator = dependency.LastIndexOf('@');
                    if (separator <= 0) throw new CompilationErrorException($"Invalid package dependency '{dependency}'.");
                    var id = dependency[..separator];
                    var locked = packages.EnumerateObject().SingleOrDefault(package =>
                        string.Equals(package.Name, id, StringComparison.OrdinalIgnoreCase));
                    if (locked.Value.ValueKind != System.Text.Json.JsonValueKind.Object)
                        throw new CompilationErrorException($"Package '{id}' is not present in cxpm.lock; run cxpm restore.");
                    var version = locked.Value.GetProperty("Version").GetString()!;
                    if (!Regex.IsMatch(id, @"^[A-Za-z0-9][A-Za-z0-9._-]*$") ||
                        !Regex.IsMatch(version, @"^[A-Za-z0-9][A-Za-z0-9.+-]*$"))
                        throw new CompilationErrorException("Invalid restored package identity.");
                    var manifest = Path.Combine(packageRoot, ".packages", id.ToLowerInvariant(), version, "package.cxpm");
                    var packageNode = LoadProject(manifest);
                    if (!string.Equals(packageNode.Project.Name, id, StringComparison.OrdinalIgnoreCase) || packageNode.Project.Version != version)
                        throw new CompilationErrorException($"Restored package '{id}' manifest identity does not match cxpm.lock.");
                    refs.Add(packageNode);
                }
            }
            refs = refs.OrderBy(reference => reference.Project.Name, StringComparer.Ordinal).ToList();
            if (refs.Any(reference => reference.Project.Name == project.Name) ||
                refs.Select(reference => reference.Project.Name).Distinct(StringComparer.Ordinal).Count() != refs.Count)
                throw new CompilationErrorException(
                    $"Project '{project.Name}' has referenced projects with conflicting target names.");
            active.Remove(path);
            var node = new ProjectNode(path, project, refs, compiler);
            nodes.Add(path, node);
            return node;
        }

        void CompileNode(ProjectNode node)
        {
            if (!compiled.Add(node.Path)) return;
            foreach (var reference in node.References) CompileNode(reference);
            foreach (var reference in node.References)
            {
                node.Project.ResolvedProjectDirectories.Add(Path.GetDirectoryName(reference.Path)!);
                node.Project.ResolvedProjectNames.Add(reference.Project.Name);
            }
            var externalNodes = EnumerateDependencies(node).DistinctBy(reference => reference.Path,
                StringComparer.OrdinalIgnoreCase).ToArray();
            foreach (var external in externalNodes)
            {
                node.Project.ReferencedNativeLibraries[external.Project.Name] =
                    external.Project.NativeLibraries;
                var exportedGenericTypeInstances =
                    CCodeOutputGenerator.GetPublicApiGenericTypeInstances(external.Project);
                var exportedGenericFunctionInstances =
                    CCodeOutputGenerator.GetPublicApiGenericFunctionInstances(external.Project);
                foreach (var context in external.Project.CompilationContexts
                    .Where(context => !context.IsProjectReference))
                    node.Project.AddReferencedCompilationContext(context, external.Project.Name);
                foreach (var instance in exportedGenericTypeInstances)
                {
                    node.Project.AddReferencedGenericTypeIdentity(
                        instance.Type.ConstructedIdentity!.CanonicalName);
                }
                foreach (var instance in exportedGenericFunctionInstances)
                    if (instance.SpecializationName is { } name)
                        node.Project.AddReferencedGenericFunctionSpecialization(name);
            }
            foreach (var apiReference in node.Project.ModuleApiReferences ?? [])
            {
                if (string.IsNullOrWhiteSpace(apiReference.ModuleName) ||
                    string.IsNullOrWhiteSpace(apiReference.Source))
                {
                    throw new CompilationErrorException(
                        $"Project '{node.Project.Name}' has a module API reference with a missing module_name or source.");
                }

                var projectDirectory = Path.GetDirectoryName(node.Path)!;
                var sourcePath = Path.GetFullPath(Path.Combine(projectDirectory, apiReference.Source));
                if (!File.Exists(sourcePath))
                {
                    throw new CompilationErrorException(
                        $"Project '{node.Project.Name}' module API source '{apiReference.Source}' does not exist.");
                }
                if (sourcePath.EndsWith(".cxapi.json", StringComparison.OrdinalIgnoreCase))
                {
                    var apiDocument = CxModuleApiMetadataLoader.Read(sourcePath);
                    if (apiReference.ModuleName != apiDocument.ModuleName)
                    {
                        throw new CompilationErrorException(
                            $"Project '{node.Project.Name}' module API reference names " +
                            $"'{apiReference.ModuleName}', but '{apiReference.Source}' declares " +
                            $"module '{apiDocument.ModuleName}'.");
                    }
                    if (string.IsNullOrWhiteSpace(apiReference.Version) ||
                        apiReference.Version != apiDocument.ModuleVersion)
                    {
                        throw new CompilationErrorException(
                            $"Project '{node.Project.Name}' module API reference for " +
                            $"'{apiReference.ModuleName}' must pin version " +
                            $"'{apiDocument.ModuleVersion}' in '{apiReference.Source}'.");
                    }
                    node.Compiler.CompileModuleApiSources(apiDocument, sourcePath,
                        apiReference.SyntheticCore);
                }
                else
                {
                    node.Compiler.CompileCxSourceFile(sourcePath, apiReference.ModuleName,
                        syntheticCore: apiReference.SyntheticCore);
                }
            }
            var packageApiMetadataPaths = new List<string>();
            var packageApiDocuments = new Dictionary<string,
                CxModuleApiMetadataDocument>(StringComparer.Ordinal);
            foreach (var packageApiPath in FindPackageApiMetadataPaths(externalNodes))
            {
                var apiDocument = CxModuleApiMetadataLoader.Read(packageApiPath);
                if (packageApiDocuments.TryGetValue(apiDocument.ModuleName, out var previous))
                {
                    if (previous.ModuleVersion != apiDocument.ModuleVersion ||
                        !previous.Sources.SequenceEqual(apiDocument.Sources))
                    {
                        throw new CompilationErrorException(
                            $"Restored binary packages provide conflicting API metadata for CX " +
                            $"module '{apiDocument.ModuleName}'.");
                    }
                    packageApiMetadataPaths.Add(packageApiPath);
                    continue;
                }
                packageApiDocuments.Add(apiDocument.ModuleName, apiDocument);
                packageApiMetadataPaths.Add(packageApiPath);

                var hasSourceProject = externalNodes.Any(external =>
                    external.Project.Name == apiDocument.ModuleName &&
                    external.Project.CompilationContexts.Count != 0);
                if (apiDocument.ModuleName == node.Project.Name || hasSourceProject ||
                    node.Project.ModuleApiReferences?.Any(reference =>
                        reference.ModuleName == apiDocument.ModuleName) == true)
                {
                    continue;
                }

                node.Project.ModuleApiReferences ??= [];
                node.Project.ModuleApiReferences.Add(new CxModuleApiReference
                {
                    ModuleName = apiDocument.ModuleName,
                    Source = packageApiPath,
                    Version = apiDocument.ModuleVersion,
                });
                node.Compiler.CompileModuleApiSources(apiDocument, packageApiPath);
            }

            var looseApiDocuments = new Dictionary<string,
                CxModuleApiMetadataDocument>(StringComparer.Ordinal);
            foreach (var looseApiPath in FindLooseApiMetadataPaths(node.Project, node.Path))
            {
                var apiDocument = CxModuleApiMetadataLoader.Read(looseApiPath);
                if (looseApiDocuments.TryGetValue(apiDocument.ModuleName, out var previous))
                {
                    if (previous.ModuleVersion != apiDocument.ModuleVersion ||
                        !previous.Sources.SequenceEqual(apiDocument.Sources))
                    {
                        throw new CompilationErrorException(
                            $"Configured binary search paths provide conflicting API metadata for CX " +
                            $"module '{apiDocument.ModuleName}'.");
                    }
                    continue;
                }
                looseApiDocuments.Add(apiDocument.ModuleName, apiDocument);

                var hasSourceProject = externalNodes.Any(external =>
                    external.Project.Name == apiDocument.ModuleName &&
                    external.Project.CompilationContexts.Count != 0);
                if (apiDocument.ModuleName == node.Project.Name || hasSourceProject ||
                    node.Project.ModuleApiReferences?.Any(reference =>
                        reference.ModuleName == apiDocument.ModuleName) == true)
                {
                    continue;
                }

                node.Project.ModuleApiReferences ??= [];
                node.Project.ModuleApiReferences.Add(new CxModuleApiReference
                {
                    ModuleName = apiDocument.ModuleName,
                    Source = looseApiPath,
                    Version = apiDocument.ModuleVersion,
                });
                node.Compiler.CompileModuleApiSources(apiDocument, looseApiPath);
            }

            var consumerProjectDirectory = Path.GetDirectoryName(node.Path)!;
            var referencedApiEffectPaths = (node.Project.ModuleApiReferences ?? [])
                .Select(reference => Path.GetFullPath(Path.Combine(
                    consumerProjectDirectory, reference.Source)))
                .Select(CxModuleApiMetadataLoader.FindSiblingEffectsMetadata)
                .OfType<string>();
            var effectMetadataPaths = node.Project.EffectMetadataReferences
                .Select(reference => Path.GetFullPath(Path.Combine(consumerProjectDirectory, reference)))
                .Concat(packageApiMetadataPaths.Select(CxModuleApiMetadataLoader
                    .FindSiblingEffectsMetadata).OfType<string>())
                .Concat(referencedApiEffectPaths)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (effectMetadataPaths.Length != 0)
            {
                node.Project.ModuleApiReferences ??= [];
                var autoLoadedModules = new HashSet<string>(StringComparer.Ordinal);
                foreach (var effectMetadataPath in effectMetadataPaths)
                {
                    var apiMetadataPath =
                        CxModuleApiMetadataLoader.FindSiblingApiMetadata(effectMetadataPath);
                    if (apiMetadataPath is null) continue;

                    var apiDocument = CxModuleApiMetadataLoader.Read(apiMetadataPath);
                    if (node.Project.ModuleApiReferences.Any(reference =>
                            reference.ModuleName == apiDocument.ModuleName) ||
                        !autoLoadedModules.Add(apiDocument.ModuleName))
                    {
                        continue;
                    }

                    node.Project.ModuleApiReferences.Add(new CxModuleApiReference
                    {
                        ModuleName = apiDocument.ModuleName,
                        Source = apiMetadataPath,
                        Version = apiDocument.ModuleVersion,
                    });
                    node.Compiler.CompileModuleApiSources(apiDocument, apiMetadataPath);
                }
                CxEffectMetadataLoader.Apply(node.Project, effectMetadataPaths);
            }
            var stageTimer = Stopwatch.StartNew();
            if (node.Compiler._verbosity == "verbose")
                Console.WriteLine($"Running generators for {node.Project.Name}...");
            node.Compiler.RunConfiguredGenerators(node.Project, node.Path, externalNodes);
            if (node.Compiler._verbosity == "verbose")
                Console.WriteLine($"Generators for {node.Project.Name} completed in {stageTimer.Elapsed}.");
            stageTimer.Restart();
            if (node.Compiler._verbosity == "verbose")
                Console.WriteLine($"Binding {node.Project.Name}...");
            var binder = new SemanticBinder();
            if (node.Compiler._verbosity == "verbose")
            {
                binder.ProgressReporter = progress =>
                    Console.WriteLine($"[{node.Project.Name} binder] {progress}");
            }
            binder.Bind(node.Project);
            if (node.Compiler._verbosity == "verbose")
                Console.WriteLine($"Binding {node.Project.Name} completed in {stageTimer.Elapsed}.");
            var directory = Path.GetDirectoryName(node.Path)!;
            var objectDirectory = node.Compiler.ResolveOutputDirectory(directory);
            Directory.CreateDirectory(objectDirectory);
            IgnoreGeneratedDirectories(directory);
            stageTimer.Restart();
            if (node.Compiler._verbosity == "verbose")
                Console.WriteLine($"Generating C for {node.Project.Name}...");
            CCodeOutputGenerator.GenerateOutput(node.Project,
                Path.Combine(objectDirectory, Path.GetFileName(node.Path)), directory);
            if (node.Compiler._verbosity != "quiet")
                Console.WriteLine($"Generated {node.Project.Name} in {objectDirectory}");
            if (node.Compiler._verbosity == "verbose")
                Console.WriteLine($"C generation for {node.Project.Name} completed in {stageTimer.Elapsed}.");
            node.Compiler.BuildIfRequested(objectDirectory, directory);
        }

        static IEnumerable<ProjectNode> EnumerateDependencies(ProjectNode node)
        {
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var ordered = new List<ProjectNode>();
            Visit(node);
            return ordered;
            void Visit(ProjectNode current)
            {
                foreach (var reference in current.References.OrderBy(item => item.Project.Name,
                    StringComparer.OrdinalIgnoreCase))
                {
                    Visit(reference);
                    if (visited.Add(reference.Path)) ordered.Add(reference);
                }
            }
        }
    }

    private static IEnumerable<string> FindPackageApiMetadataPaths(
        IReadOnlyList<ProjectNode> externalNodes)
    {
        foreach (var external in externalNodes
            .Where(node => string.Equals(Path.GetFileName(node.Path), "package.cxpm",
                StringComparison.OrdinalIgnoreCase))
            .OrderBy(node => node.Path, StringComparer.OrdinalIgnoreCase))
        {
            var binaryDirectory = Path.Combine(Path.GetDirectoryName(external.Path)!, "bin");
            if (!Directory.Exists(binaryDirectory)) continue;
            foreach (var path in Directory.EnumerateFiles(binaryDirectory,
                "*.cxapi.json", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase))
                yield return Path.GetFullPath(path);
        }
    }

    private static IEnumerable<string> FindLooseApiMetadataPaths(
        CxProject project,
        string projectPath)
    {
        var projectDirectory = Path.GetDirectoryName(projectPath)!;
        var searchDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var conventionalBinaryDirectory = Path.Combine(projectDirectory, "bin");
        if (Directory.Exists(conventionalBinaryDirectory))
            searchDirectories.Add(Path.GetFullPath(conventionalBinaryDirectory));

        foreach (var searchPath in project.ModuleApiSearchPaths
            .Order(StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(searchPath))
                throw new CompilationErrorException(
                    $"Project '{project.Name}' has an empty module API search path.");
            var directory = Path.GetFullPath(Path.Combine(projectDirectory, searchPath));
            if (!Directory.Exists(directory))
                throw new CompilationErrorException(
                    $"Project '{project.Name}' module API search path '{searchPath}' does not exist.");
            searchDirectories.Add(directory);
        }

        foreach (var directory in searchDirectories.Order(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var path in Directory.EnumerateFiles(directory,
                "*.cxapi.json", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase))
            {
                yield return Path.GetFullPath(path);
            }
        }
    }

    private void CompileModuleApiSources(
        CxModuleApiMetadataDocument apiDocument,
        string apiMetadataPath,
        bool syntheticCore = false)
    {
        foreach (var source in apiDocument.Sources)
        {
            var sourcePath = Path.GetFullPath(Path.Combine(
                Path.GetDirectoryName(apiMetadataPath)!, source.Name));
            CompileCxSourceText(source.Source, sourcePath, apiDocument.ModuleName,
                syntheticCore);
        }
    }

    private void CompileCxSourceFile(
        string filePath,
        string? referenceModuleName = null,
        bool syntheticCore = false)
    {
        if (_verbosity == "verbose") Console.WriteLine($"Compiling {filePath}");

        if (_project is null)
        {
            _projectPath = filePath;
            _project = CxProject.CreateDefaultApplicationProject(
                name: _moduleNameOverride ?? Path.GetFileNameWithoutExtension(filePath));
            ValidateProjectName(_project.Name);
        }

        var fullPath = Path.GetFullPath(filePath);
        CompileCxSourceText(File.ReadAllText(fullPath), fullPath,
            referenceModuleName, syntheticCore);
    }

    private void CompileCxSourceText(
        string sourceText,
        string filePath,
        string? referenceModuleName = null,
        bool syntheticCore = false)
    {
        if (_verbosity == "verbose") Console.WriteLine($"Compiling {filePath}");
        var fullPath = Path.GetFullPath(filePath);
        _sourceTexts[fullPath] = sourceText;
        var preprocessed = CxPreprocessor.Process(sourceText, fullPath, _preprocessorSymbols);
        foreach (var diagnostic in preprocessed.Diagnostics.Where(diagnostic => diagnostic.Severity == "warning"))
            WriteDiagnostic("warning", diagnostic.ToString());
        var preprocessingErrors = preprocessed.Diagnostics
            .Where(diagnostic => diagnostic.Severity == "error")
            .Select(diagnostic => diagnostic.ToString())
            .ToArray();
        if (preprocessingErrors.Length > 0)
            throw new CompilationErrorException(string.Join(Environment.NewLine, preprocessingErrors));

        if (referenceModuleName is null)
        {
            var projectDirectory = Path.GetDirectoryName(Path.GetFullPath(_projectPath!))!;
            var sourceName = Path.GetRelativePath(projectDirectory, fullPath)
                .Replace('\\', '/');
            _project!.AddModuleApiSource(sourceName, preprocessed.Source);
        }

        var inputStream = new AntlrInputStream(preprocessed.Source) { name = fullPath };
        var errorListener = new ParserErrorListener(fullPath);

        var lexer = new CxLexer(inputStream);
        lexer.RemoveErrorListeners();
        lexer.AddErrorListener(errorListener);
        var tokenStream = new CommonTokenStream(lexer);

        var parser = new CxParser(tokenStream);
        parser.RemoveErrorListeners();
        parser.AddErrorListener(errorListener);

        var compilationUnit = parser.compilationUnit();
        if (errorListener.HasErrors)
        {
            throw new CxCompiler.Model.Errors.CompilationErrorException(
                string.Join(Environment.NewLine, errorListener.Diagnostics));
        }

        var visitor = new CompilationUnitParserVisitor();
        var compilationContext = visitor.Visit(compilationUnit);

        if (compilationContext != null)
        {
            if (referenceModuleName is null)
                _project!.AddCompilationContext(compilationContext);
            else
                _project!.AddReferencedCompilationContext(compilationContext, referenceModuleName,
                    syntheticCore);
        }
    }

    private void RunConfiguredGenerators(
        CxProject project,
        string projectPath,
        IReadOnlyList<ProjectNode> externalNodes)
    {
        if (project.Generators is null || project.Generators.Count == 0) return;

        var projectDirectory = Path.GetDirectoryName(Path.GetFullPath(projectPath))!;
        var generatorIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var generator in project.Generators)
        {
            var id = generator?.Id;
            if (generator is null || string.IsNullOrWhiteSpace(id) ||
                !Regex.IsMatch(id, "^[A-Za-z0-9][A-Za-z0-9._-]*$"))
                throw new CompilationErrorException(
                    $"Project '{project.Name}' has a generator with an invalid ID.");
            if (!generatorIds.Add(id!))
                throw new CompilationErrorException(
                    $"Project '{project.Name}' configures generator ID '{generator.Id}' more than once.");
            if (generator.TimeoutSeconds is < 1 or > 3600)
                throw new CompilationErrorException(
                    $"Generator '{generator.Id}' timeout_seconds must be between 1 and 3600.");
            if (generator.Arguments is null || generator.AdditionalFiles is null)
                throw new CompilationErrorException(
                    $"Generator '{generator.Id}' arguments and additional_files must be lists.");
        }

        var snapshot = CreateGeneratorSnapshot(projectPath, externalNodes);
        var snapshotPayload = GeneratorSnapshotCodec.Encode(snapshot);
        var outputDirectory = Path.Combine(ResolveOutputDirectory(projectDirectory), "generators");
        foreach (var generator in project.Generators.OrderBy(item => item.Id, StringComparer.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(generator.Executable))
                throw new CompilationErrorException(
                    $"Generator '{generator.Id}' requires an executable path.");

            var startInfo = new ProcessStartInfo(ResolveGeneratorExecutable(
                generator.Executable, projectDirectory))
            {
                WorkingDirectory = projectDirectory,
            };
            foreach (var argument in generator.Arguments)
            {
                if (argument is null)
                    throw new CompilationErrorException(
                        $"Generator '{generator.Id}' has a null argument.");
                startInfo.ArgumentList.Add(argument);
            }

            var requestId = 1UL;
            var frames = new List<GeneratorProtocolFrame>
            {
                new(GeneratorProtocol.CurrentVersion, GeneratorMessageKind.Handshake, requestId, []),
                new(GeneratorProtocol.CurrentVersion, GeneratorMessageKind.Snapshot, requestId, snapshotPayload),
            };
            foreach (var additionalPath in generator.AdditionalFiles
                .Order(StringComparer.Ordinal))
            {
                var file = ReadGeneratorAdditionalFile(additionalPath, projectDirectory, generator.Id);
                frames.Add(new GeneratorProtocolFrame(GeneratorProtocol.CurrentVersion,
                    GeneratorMessageKind.AdditionalFile, requestId,
                    GeneratorWorkerMessages.EncodeAdditionalFile(file)));
            }
            frames.Add(new GeneratorProtocolFrame(GeneratorProtocol.CurrentVersion,
                GeneratorMessageKind.Generate, requestId, []));

            GeneratorWorkerResult result;
            try
            {
                result = GeneratorWorkerClient.RunAsync(startInfo, frames,
                    TimeSpan.FromSeconds(generator.TimeoutSeconds)).GetAwaiter().GetResult();
            }
            catch (GeneratorWorkerException exception)
            {
                throw new CompilationErrorException(
                    $"Generator '{generator.Id}' failed: {exception.Message}");
            }

            var sources = new List<GeneratorSourceOutput>();
            var hints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var hasErrors = false;
            foreach (var message in result.Messages)
            {
                if (message.Kind == GeneratorMessageKind.SourceOutput)
                {
                    var source = GeneratorWorkerMessages.DecodeSourceOutput(message.Payload);
                    var hintName = NormalizeGeneratorHintName(source.HintName, generator.Id);
                    if (!hints.Add(hintName))
                        throw new CompilationErrorException(
                            $"Generator '{generator.Id}' emitted duplicate hint name '{hintName}'.");
                    sources.Add(source with { HintName = hintName });
                }
                else if (message.Kind == GeneratorMessageKind.Diagnostic)
                {
                    var diagnostic = GeneratorWorkerMessages.DecodeDiagnostic(message.Payload);
                    var location = diagnostic.Location;
                    var severity = diagnostic.Severity;
                    var text = $"{location.Path}({location.StartLine},{location.StartColumn}): " +
                        $"{diagnostic.Id}: {diagnostic.Message}";
                    WriteDiagnostic(severity, text);
                    hasErrors |= severity == "error";
                }
            }
            if (hasErrors)
                throw new CompilationErrorException(
                    $"Generator '{generator.Id}' reported errors; generated output was not accepted.");

            var ownedDirectory = Path.GetFullPath(Path.Combine(outputDirectory, generator.Id));
            Directory.CreateDirectory(ownedDirectory);
            foreach (var source in sources.OrderBy(item => item.HintName, StringComparer.Ordinal))
            {
                if (System.Text.Encoding.UTF8.GetByteCount(source.Source) > GeneratorProtocol.MaximumPayloadLength)
                    throw new CompilationErrorException(
                        $"Generator '{generator.Id}' source '{source.HintName}' exceeds the 64 MiB limit.");
                var generatedPath = Path.GetFullPath(Path.Combine(ownedDirectory,
                    source.HintName.Replace('/', Path.DirectorySeparatorChar)));
                if (!IsPathWithin(ownedDirectory, generatedPath))
                    throw new CompilationErrorException(
                        $"Generator '{generator.Id}' emitted a path outside its owned output directory.");
                Directory.CreateDirectory(Path.GetDirectoryName(generatedPath)!);
                File.WriteAllText(generatedPath, source.Source, new System.Text.UTF8Encoding(false, true));
                CompileCxSourceFile(generatedPath);
            }
        }
    }

    private GeneratorInputSnapshot CreateGeneratorSnapshot(
        string projectPath,
        IReadOnlyList<ProjectNode> externalNodes)
    {
        var snapshotCompiler = new Compiler
        {
            _preprocessorSymbols = new HashSet<string>(_preprocessorSymbols, StringComparer.Ordinal),
            _moduleNameOverride = _moduleNameOverride,
        };
        snapshotCompiler.CompileCxProjectFile(projectPath);
        var project = snapshotCompiler._project!;
        foreach (var external in externalNodes)
        {
            var externalCompiler = new Compiler
            {
                _preprocessorSymbols = new HashSet<string>(_preprocessorSymbols, StringComparer.Ordinal),
            };
            externalCompiler.CompileCxProjectFile(external.Path);
            var externalDirectory = Path.GetDirectoryName(Path.GetFullPath(external.Path))!;
            var externalGeneratorOutput = Path.Combine(
                external.Compiler.ResolveOutputDirectory(externalDirectory), "generators");
            foreach (var generatedSource in external.Compiler._sourceTexts.Keys
                .Where(sourcePath => IsPathWithin(externalGeneratorOutput, sourcePath))
                .Order(StringComparer.Ordinal))
                externalCompiler.CompileCxSourceFile(generatedSource);
            foreach (var context in externalCompiler._project!.CompilationContexts
                .Where(context => !context.IsProjectReference))
                project.AddReferencedCompilationContext(context, external.Project.Name);
            project.ReferencedNativeLibraries[external.Project.Name] = external.Project.NativeLibraries;
        }

        new SemanticBinder().BindForGeneratorSnapshot(project);
        return GeneratorSnapshotBuilder.Create(project,
            Path.GetDirectoryName(Path.GetFullPath(projectPath))!, snapshotCompiler._sourceTexts);
    }

    private static string ResolveGeneratorExecutable(string executable, string projectDirectory)
    {
        var normalized = executable.Trim();
        if (Path.IsPathRooted(normalized)) return Path.GetFullPath(normalized);
        if (normalized.Contains(Path.DirectorySeparatorChar) ||
            normalized.Contains(Path.AltDirectorySeparatorChar))
            return Path.GetFullPath(Path.Combine(projectDirectory, normalized));
        var projectLocal = Path.GetFullPath(Path.Combine(projectDirectory, normalized));
        return File.Exists(projectLocal) ? projectLocal : normalized;
    }

    private static GeneratorAdditionalFile ReadGeneratorAdditionalFile(
        string relativePath,
        string projectDirectory,
        string generatorId)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
            throw new CompilationErrorException(
                $"Generator '{generatorId}' additional_files entries must be project-relative paths.");
        var fullPath = Path.GetFullPath(Path.Combine(projectDirectory, relativePath));
        if (!IsPathWithin(projectDirectory, fullPath))
            throw new CompilationErrorException(
                $"Generator '{generatorId}' additional file '{relativePath}' escapes the project directory.");
        if (!File.Exists(fullPath))
            throw new CompilationErrorException(
                $"Generator '{generatorId}' additional file '{relativePath}' does not exist.");
        var bytes = File.ReadAllBytes(fullPath);
        string content;
        try { content = new System.Text.UTF8Encoding(false, true).GetString(bytes); }
        catch (System.Text.DecoderFallbackException exception)
        {
            throw new CompilationErrorException(
                $"Generator '{generatorId}' additional file '{relativePath}' is not valid UTF-8: {exception.Message}");
        }
        var normalized = Path.GetRelativePath(projectDirectory, fullPath)
            .Replace(Path.DirectorySeparatorChar, '/');
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
        return new GeneratorAdditionalFile(normalized, hash, content);
    }

    private static string NormalizeGeneratorHintName(string hintName, string generatorId)
    {
        if (string.IsNullOrWhiteSpace(hintName))
            throw new CompilationErrorException($"Generator '{generatorId}' emitted an empty hint name.");
        var normalized = hintName.Replace('\\', '/');
        if (normalized.Contains(':') || normalized.StartsWith('/') ||
            !normalized.EndsWith(".g.cx", StringComparison.OrdinalIgnoreCase) ||
            normalized.Split('/').Any(part => part is "" or "." or ".."))
            throw new CompilationErrorException(
                $"Generator '{generatorId}' emitted invalid hint name '{hintName}'; use a relative .g.cx path.");
        return normalized;
    }

    private static bool IsPathWithin(string directory, string path)
    {
        var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(path);
        return candidate.StartsWith(root, OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal);
    }

    private static void ValidateProjectName(string name)
    {
        if (name?.Contains('_') == true)
            throw new CompilationErrorException(
                $"Project name '{name}' must not contain underscores. Choose a name such as 'MyProject'; for a source file, use --module-name to specify its project name.");
    }

    private static CxProject ParseCxProjectFromYaml(StreamReader reader)
    {
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .Build();
        return deserializer.Deserialize<CxProject>(reader);
    }

    private static CxProject ParseCxProjectFromJson(StreamReader reader)
    {
        return System.Text.Json.JsonSerializer.Deserialize<CxProject>(
            reader.ReadToEnd(),
            new System.Text.Json.JsonSerializerOptions()
            {
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
            })
            ?? throw new InvalidOperationException("Failed to deserialize CxProject from JSON file.");
    }

    private static void IgnoreGeneratedDirectories(string projectDirectory)
    {
        var ignoreFilePath = Path.Combine(projectDirectory, ".gitignore");
        var contents = File.Exists(ignoreFilePath)
            ? File.ReadAllText(ignoreFilePath)
            : string.Empty;
        var lines = contents.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var additions = new[] { "/.obj/", "/.bin/" }
            .Where(entry => !lines.Any(line => string.Equals(
                line.Trim(), entry, StringComparison.Ordinal)));
        var missingEntries = additions.ToArray();
        if (missingEntries.Length == 0)
        {
            return;
        }

        var prefix = contents.Length == 0 || contents.EndsWith('\n')
            ? contents
            : contents + Environment.NewLine;
        File.WriteAllText(ignoreFilePath,
            prefix + string.Join(Environment.NewLine, missingEntries) + Environment.NewLine);
    }
}

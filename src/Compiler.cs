using Antlr4.Runtime;
using CxCompiler.Grammar;
using CxCompiler.Model.Errors;
using CxCompiler.Model.Project;
using CxCompiler.OutputGenerators;
using CxCompiler.ParserVisitors;
using CxCompiler.Preprocessing;
using CxCompiler.Semantics;
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
          --module-name NAME        Override the generated module/target name
          --emit-only               Generate C and CMake files without building (default)
          --compile                 Build generated C with CMake
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
        bool Build,
        string Verbosity,
        string DiagnosticsFormat,
        HashSet<string> Symbols,
        List<string> Inputs);

    private string? _projectPath = null;
    private CxProject? _project = null;
    private IReadOnlySet<string> _preprocessorSymbols = new HashSet<string>(StringComparer.Ordinal);

    public void Compile(ReadOnlySpan<string> args)
    {
        var options = ParseOptions(args);
        _verbosity = options.Verbosity;
        _diagnosticsFormat = options.DiagnosticsFormat;
        _outputDirectoryOverride = options.OutputDirectory;
        _moduleNameOverride = options.ModuleName;
        _cxCoreDirectory = options.CxCoreDirectory;
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
                    case "-v": case "--verbosity": verbosity = value; break;
                    case "--diagnostics-format": diagnosticsFormat = value; break;
                }
                continue;
            }
            if (arg.StartsWith("--output-dir=", StringComparison.Ordinal)) { outputDirectory = arg[13..]; continue; }
            if (arg.StartsWith("--module-name=", StringComparison.Ordinal)) { moduleName = arg[14..]; continue; }
            if (arg.StartsWith("--cxcore-dir=", StringComparison.Ordinal)) { cxCoreDirectory = arg[13..]; continue; }
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
        if (moduleName is not null && !Regex.IsMatch(moduleName, "^[A-Za-z_][A-Za-z0-9_]*$"))
            throw new CommandLineException("Module name must be a valid C identifier.");
        if (outputDirectory is not null && string.IsNullOrWhiteSpace(outputDirectory))
            throw new CommandLineException("Output directory cannot be empty.");
        if (cxCoreDirectory is not null && string.IsNullOrWhiteSpace(cxCoreDirectory))
            throw new CommandLineException("cxcore directory cannot be empty.");
        return new Options(outputDirectory, moduleName, cxCoreDirectory, build,
            verbosity, diagnosticsFormat, symbols, sourceArguments);
    }

    private string _verbosity = "normal";
    private string _diagnosticsFormat = "text";
    private string? _outputDirectoryOverride;
    private string? _moduleNameOverride;
    private string? _cxCoreDirectory;
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
            $"-DCXCORE_SOURCE_DIR={Path.GetFullPath(cxCoreDirectory)}"]);
        RunCMake(["--build", buildDirectory, "--config", "Release"]);

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
            ".cxproj" or ".yaml" or ".yml" => ParseCxProjectFromYaml(reader),
            ".json" => ParseCxProjectFromJson(reader),
            _ => throw new CompilationErrorException($"Unsupported project file format: {Path.GetExtension(filePath)}"),
        };
        if (_moduleNameOverride is not null)
            _project.Name = _moduleNameOverride;
        _project.Targets ??= [];
        if (_project.Targets.Any(target => string.IsNullOrWhiteSpace(target)
            || !Regex.IsMatch(target, "^[A-Za-z0-9][A-Za-z0-9._-]*$")))
            throw new CompilationErrorException("Project 'targets' entries must be valid Runtime Identifiers (RIDs).");

        var projectDirectoryPath = Path.GetDirectoryName(filePath)
            ?? throw new InvalidOperationException("Project file path is invalid.");
        var projectDirectory = new DirectoryInfo(projectDirectoryPath);
        if (!projectDirectory.Exists)
        {
            throw new InvalidOperationException($"Project directory does not exist: {projectDirectory.FullName}");
        }
        foreach (var sourceFile in projectDirectory.EnumerateFiles("*.cx", SearchOption.AllDirectories)
            .OrderBy(file => file.FullName, StringComparer.Ordinal))
        {
            CompileCxSourceFile(sourceFile.FullName);
        }
    }

    private sealed record ProjectNode(string Path, CxProject Project, IReadOnlyList<ProjectNode> References,
        Compiler Compiler);

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
                var exportedGenericTypeInstances =
                    CCodeOutputGenerator.GetPublicApiGenericTypeInstances(external.Project);
                var exportedGenericFunctionInstances =
                    CCodeOutputGenerator.GetPublicApiGenericFunctionInstances(external.Project);
                foreach (var context in external.Project.CompilationContexts)
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
            new SemanticBinder().Bind(node.Project);
            var directory = Path.GetDirectoryName(node.Path)!;
            var objectDirectory = node.Compiler.ResolveOutputDirectory(directory);
            Directory.CreateDirectory(objectDirectory);
            IgnoreGeneratedDirectories(directory);
            CCodeOutputGenerator.GenerateOutput(node.Project,
                Path.Combine(objectDirectory, Path.GetFileName(node.Path)), directory);
            if (node.Compiler._verbosity != "quiet")
                Console.WriteLine($"Generated {node.Project.Name} in {objectDirectory}");
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

    private void CompileCxSourceFile(string filePath)
    {
        if (_verbosity == "verbose") Console.WriteLine($"Compiling {filePath}");

        if (_project is null)
        {
            _projectPath = filePath;
            _project = CxProject.CreateDefaultApplicationProject(
                name: _moduleNameOverride ?? Path.GetFileNameWithoutExtension(filePath));
        }

        var preprocessed = CxPreprocessor.Process(File.ReadAllText(filePath), filePath, _preprocessorSymbols);
        foreach (var diagnostic in preprocessed.Diagnostics.Where(diagnostic => diagnostic.Severity == "warning"))
            WriteDiagnostic("warning", diagnostic.ToString());
        var preprocessingErrors = preprocessed.Diagnostics
            .Where(diagnostic => diagnostic.Severity == "error")
            .Select(diagnostic => diagnostic.ToString())
            .ToArray();
        if (preprocessingErrors.Length > 0)
            throw new CompilationErrorException(string.Join(Environment.NewLine, preprocessingErrors));

        var inputStream = new AntlrInputStream(preprocessed.Source) { name = filePath };
        var errorListener = new ParserErrorListener(filePath);

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
            _project.AddCompilationContext(compilationContext);
        }
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

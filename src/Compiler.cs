using Antlr4.Runtime;
using CxCompiler.Grammar;
using CxCompiler.Model.Errors;
using CxCompiler.Model.Project;
using CxCompiler.OutputGenerators;
using CxCompiler.ParserVisitors;
using CxCompiler.Semantics;
using System.Text.RegularExpressions;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace CxCompiler;

public class Compiler
{
    private string? _projectPath = null;
    private CxProject? _project = null;

    public void Compile(ReadOnlySpan<string> args)
    {
        if (args.Length == 1 && args[0].EndsWith(".cxproj", StringComparison.OrdinalIgnoreCase))
        {
            CompileProjectGraph(Path.GetFullPath(args[0]));
            return;
        }
        foreach (var arg in args)
        {
            if (arg.EndsWith(".cxproj", StringComparison.OrdinalIgnoreCase))
            {
                CompileCxProjectFile(arg);
            }
            else if (arg.EndsWith(".cx", StringComparison.OrdinalIgnoreCase))
            {
                CompileCxSourceFile(arg);
            }
            else
            {
                throw new CompilationErrorException($"Unsupported file type: {arg}");
            }
        }

        new SemanticBinder().Bind(_project!);
        var projectFilePath = Path.GetFullPath(_projectPath!);
        var projectDirectory = Path.GetDirectoryName(projectFilePath)!;
        var objectDirectory = Path.Combine(projectDirectory, ".obj");
        Directory.CreateDirectory(objectDirectory);
        IgnoreGeneratedDirectories(projectDirectory);
        CCodeOutputGenerator.GenerateOutput(
            _project!,
            Path.Combine(objectDirectory, Path.GetFileName(projectFilePath)),
            projectDirectory);
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

    private sealed record ProjectNode(string Path, CxProject Project, IReadOnlyList<ProjectNode> References);

    private static void CompileProjectGraph(string rootPath)
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
            var compiler = new Compiler();
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
            var node = new ProjectNode(path, project, refs);
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
            var objectDirectory = Path.Combine(directory, ".obj");
            Directory.CreateDirectory(objectDirectory);
            IgnoreGeneratedDirectories(directory);
            CCodeOutputGenerator.GenerateOutput(node.Project,
                Path.Combine(objectDirectory, Path.GetFileName(node.Path)), directory);
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
        Console.WriteLine($"Compiling {filePath}");

        if (_project is null)
        {
            _projectPath = filePath;
            _project = CxProject.CreateDefaultApplicationProject(
                name: Path.GetFileNameWithoutExtension(filePath));
        }

        var inputStream = new AntlrInputStream(File.ReadAllText(filePath)) { name = filePath };
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

using Antlr4.Runtime;
using CxCompiler.Grammar;
using CxCompiler.Model.Errors;
using CxCompiler.Model.Project;
using CxCompiler.OutputGenerators;
using CxCompiler.ParserVisitors;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace CxCompiler;

public class Compiler
{
    private string? _projectPath = null;
    private CxProject? _project = null;

    public void Compile(ReadOnlySpan<string> args)
    {
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

        CCodeOutputGenerator.GenerateOutput(_project!, _projectPath!);
    }

    private void CompileCxProjectFile(string filePath)
    {
        if (_project is not null)
        {
            throw new CompilationErrorException("Project file has already been compiled. Only one project file can be processed at a time.");
        }

        using var reader = new StreamReader(filePath);
        _projectPath = filePath;
        _project = Path.GetExtension(filePath) switch
        {
            ".cxproj" or ".yaml" or ".yml" => ParseCxProjectFromYaml(reader),
            ".json" => ParseCxProjectFromJson(reader),
            _ => throw new CompilationErrorException($"Unsupported project file format: {Path.GetExtension(filePath)}"),
        };

        var projectDirectoryPath = Path.GetDirectoryName(filePath)
            ?? throw new InvalidOperationException("Project file path is invalid.");
        var projectDirectory = new DirectoryInfo(projectDirectoryPath);
        if (!projectDirectory.Exists)
        {
            throw new InvalidOperationException($"Project directory does not exist: {projectDirectory.FullName}");
        }
        foreach (var sourceFile in projectDirectory.EnumerateFiles("*.cx", SearchOption.AllDirectories))
        {
            CompileCxSourceFile(sourceFile.FullName);
        }
    }

    private void CompileCxSourceFile(string filePath)
    {
        Console.WriteLine($"Compiling {filePath}");

        if (_project is null)
        {
            _projectPath = filePath;
            _project = CxProject.CreateUnnamedApplicationProject();
        }

        using var reader = new StreamReader(filePath);
        var inputStream = new AntlrInputStream(reader);
        var errorListener = new ParserErrorListener();

        var lexer = new CxLexer(inputStream);
        lexer.RemoveErrorListeners();
        lexer.AddErrorListener(errorListener);
        var tokenStream = new CommonTokenStream(lexer);

        var parser = new CxParser(tokenStream);
        parser.RemoveErrorListeners();
        parser.AddErrorListener(errorListener);

        var compilationUnit = parser.compilationUnit();
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
}

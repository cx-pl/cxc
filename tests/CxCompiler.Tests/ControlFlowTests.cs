using CxCompiler.Model.Errors;
using CxCompiler.Model.Project;
using CxCompiler.Model.Statements;
using CxCompiler.Model.Types;
using CxCompiler.OutputGenerators;
using CxCompiler.Semantics;

namespace CxCompiler.Tests;

public sealed class ControlFlowTests
{
    [Fact]
    public void ParserPreservesStructuredControlFlow()
    {
        var project = CreateProject("""
            void Run(bool enabled) {
                if (enabled) {
                    while (enabled) {
                        enabled = false;
                    }
                } else {
                    do {
                        enabled = true;
                    } while (!enabled);
                }
                for (int index = 0; index < 3; index++) {
                    enabled = false;
                }
            }
            """);

        var function = Assert.IsType<FunctionDeclaration>(
            project.CompilationContexts.Single().DeclarationScope.Declarations.Single());
        var conditional = Assert.IsType<IfStatement>(function.Body![0]);
        var thenBlock = Assert.IsType<BlockStatement>(conditional.ThenStatement);
        var elseBlock = Assert.IsType<BlockStatement>(conditional.ElseStatement);
        var loop = Assert.IsType<ForStatement>(function.Body[1]);

        Assert.IsType<WhileStatement>(thenBlock.Statements.Single());
        Assert.IsType<DoWhileStatement>(elseBlock.Statements.Single());
        Assert.NotNull(loop.DeclarationInitializer);
        Assert.Single(loop.Iterators);
    }

    [Fact]
    public void BinderRequiresBooleanConditions()
    {
        var project = CreateProject("""
            void Main() {
                if (1) {
                }
            }
            """);

        var exception = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(project));

        Assert.Contains("Condition must have type 'System.Bool'", exception.Message);
    }

    [Fact]
    public void ForInitializerIsScopedToTheLoop()
    {
        var project = CreateProject("""
            void Main() {
                for (int index = 0; index < 3; index++) {
                    int value = index;
                }
                index = 0;
            }
            """);

        var exception = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(project));

        Assert.Contains("Cannot resolve value 'index'", exception.Message);
    }

    [Fact]
    public void BinderAcceptsOnlyCompleteConditionalReturnPaths()
    {
        var completeProject = CreateProject("""
            int Select(bool condition) {
                if (condition) {
                    return 1;
                } else {
                    return 2;
                }
            }
            """);
        var incompleteProject = CreateProject("""
            int Select(bool condition) {
                if (condition) {
                    return 1;
                }
            }
            """);

        new SemanticBinder().Bind(completeProject);
        var exception = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(incompleteProject));

        Assert.Contains("must return a value of type 'System.Int'", exception.Message);
    }

    [Fact]
    public void EmitsIfWhileDoAndForStatements()
    {
        var project = CreateProject("""
            void Main() {
                bool running = true;
                if (running) {
                    while (running) {
                        running = false;
                    }
                } else {
                    do {
                        running = true;
                    } while (!running);
                }
                for (int index = 0; index < 3; index++) {
                    running = false;
                }
            }
            """);
        new SemanticBinder().Bind(project);
        var outputDirectory = Path.Combine(
            Path.GetTempPath(),
            $"cxc-tests-{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(outputDirectory);
            CCodeOutputGenerator.GenerateOutput(
                project,
                Path.Combine(outputDirectory, "ControlFlow.cx"));
            var generatedSource = File.ReadAllText(
                Path.Combine(outputDirectory, "unnamed.c"));

            Assert.Contains("if (running)", generatedSource);
            Assert.Contains("while (running)", generatedSource);
            Assert.Contains("do", generatedSource);
            Assert.Contains("while ((!running));", generatedSource);
            Assert.Contains("for (cx_int index = 0; (index < 3); (index++))", generatedSource);
        }
        finally
        {
            if (Directory.Exists(outputDirectory))
            {
                Directory.Delete(outputDirectory, recursive: true);
            }
        }
    }

    private static CxProject CreateProject(string source)
    {
        var project = CxProject.CreateDefaultApplicationProject();
        project.AddCompilationContext(CompilerTestHelper.Parse(source));
        return project;
    }
}

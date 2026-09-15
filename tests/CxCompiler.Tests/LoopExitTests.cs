using CxCompiler.Model.Errors;
using CxCompiler.Model.Project;
using CxCompiler.Model.Statements;
using CxCompiler.Model.Types;
using CxCompiler.OutputGenerators;
using CxCompiler.Semantics;

namespace CxCompiler.Tests;

public sealed class LoopExitTests
{
    [Fact]
    public void ParserPreservesBreakAndContinueStatements()
    {
        var project = CreateProject("""
            void Run(bool running) {
                while (running) {
                    if (running) {
                        continue;
                    }
                    break;
                }
            }
            """);

        var function = Assert.IsType<FunctionDeclaration>(
            project.CompilationContexts.Single().DeclarationScope.Declarations.Single());
        var loop = Assert.IsType<WhileStatement>(function.Body!.Single());
        var body = Assert.IsType<BlockStatement>(loop.Body);
        var conditional = Assert.IsType<IfStatement>(body.Statements[0]);
        var conditionalBody = Assert.IsType<BlockStatement>(conditional.ThenStatement);

        Assert.IsType<ContinueStatement>(conditionalBody.Statements.Single());
        Assert.IsType<BreakStatement>(body.Statements[1]);
    }

    [Theory]
    [InlineData("break", "The 'break' statement can only be used inside a loop")]
    [InlineData("continue", "The 'continue' statement can only be used inside a loop")]
    public void BinderRejectsLoopExitOutsideLoop(string keyword, string expectedMessage)
    {
        var project = CreateProject($$"""
            void Main() {
                {{keyword}};
            }
            """);

        var exception = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(project));

        Assert.Contains(expectedMessage, exception.Message);
    }

    [Fact]
    public void BindsAndEmitsLoopExits()
    {
        var project = CreateProject("""
            void Run(bool running) {
                while (running) {
                    if (running) {
                        continue;
                    }
                    break;
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
                Path.Combine(outputDirectory, "LoopExits.cx"));
            var generatedSource = File.ReadAllText(
                Path.Combine(outputDirectory, "Unnamed.c"));

            Assert.Contains("continue;", generatedSource);
            Assert.Contains("break;", generatedSource);
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
        var project = CxProject.CreateUnnamedApplicationProject();
        project.AddCompilationContext(CompilerTestHelper.Parse(source));
        return project;
    }
}

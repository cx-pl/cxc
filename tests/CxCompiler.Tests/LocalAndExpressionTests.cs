using CxCompiler.Model.Errors;
using CxCompiler.Model.Expressions;
using CxCompiler.Model.Project;
using CxCompiler.Model.Statements;
using CxCompiler.Model.Types;
using CxCompiler.Model.Types.BuiltInTypes;
using CxCompiler.OutputGenerators;
using CxCompiler.Semantics;

namespace CxCompiler.Tests;

public sealed class LocalAndExpressionTests
{
    [Fact]
    public void ParserPreservesOperatorPrecedence()
    {
        var project = CreateProject("""
            int Calculate() {
                int result = 1 + 2 * 3;
                return result;
            }
            """);

        var function = Assert.IsType<FunctionDeclaration>(
            project.CompilationContexts.Single().DeclarationScope.Declarations.Single());
        var declaration = Assert.IsType<LocalVariableDeclarationStatement>(function.Body![0]);
        var addition = Assert.IsType<BinaryExpression>(declaration.Declarators.Single().Initializer);
        var multiplication = Assert.IsType<BinaryExpression>(addition.Right);

        Assert.Equal("+", addition.Operator);
        Assert.Equal("*", multiplication.Operator);
    }

    [Fact]
    public void BindsInferredLocalAssignmentsAndNestedShadowing()
    {
        var project = CreateProject("""
            int Calculate(int value) {
                var result = value + 1;
                {
                    int result = 2;
                    result++;
                }
                result += 3;
                return result;
            }
            """);

        new SemanticBinder().Bind(project);

        var function = Assert.IsType<FunctionDeclaration>(
            project.CompilationContexts.Single().DeclarationScope.Declarations.Single());
        var declaration = Assert.IsType<LocalVariableDeclarationStatement>(function.Body![0]);
        var assignment = Assert.IsType<ExpressionStatement>(function.Body[2]);

        Assert.Same(BuiltInSystemTypes.Int, declaration.Declarators.Single().Type);
        Assert.Same(BuiltInSystemTypes.Int, assignment.Expression.InferredType);
    }

    [Fact]
    public void ReportsDuplicateLocalInSameScope()
    {
        var project = CreateProject("""
            void Main() {
                int value;
                int value;
            }
            """);

        var exception = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(project));

        Assert.Contains("Local 'value' is already declared in this scope", exception.Message);
    }

    [Fact]
    public void ReportsInitializerTypeMismatch()
    {
        var project = CreateProject("""
            void Main() {
                int value = "text";
            }
            """);

        var exception = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(project));

        Assert.Contains("Cannot initialize local 'value'", exception.Message);
    }

    [Fact]
    public void EmitsLocalsOperatorsAssignmentsAndBlocks()
    {
        const string source = """
            int Calculate(int value) {
                int result = value + 1;
                {
                    int nested = result * 2;
                }
                result += 3;
                return result;
            }
            """;
        var project = CreateProject(source);
        new SemanticBinder().Bind(project);
        var outputDirectory = Path.Combine(
            Path.GetTempPath(),
            $"cxc-tests-{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(outputDirectory);
            CCodeOutputGenerator.GenerateOutput(
                project,
                Path.Combine(outputDirectory, "Expressions.cx"));
            var generatedSource = File.ReadAllText(
                Path.Combine(outputDirectory, "unnamed.c"));

            Assert.Contains("cx_int result = (value + 1);", generatedSource);
            Assert.Contains("cx_int nested = (result * 2);", generatedSource);
            Assert.Contains("result += 3;", generatedSource);
            Assert.Contains("return result;", generatedSource);
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

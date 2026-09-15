using CxCompiler.Model.Errors;
using CxCompiler.Model.Expressions;
using CxCompiler.Model.Project;
using CxCompiler.Model.Statements;
using CxCompiler.Model.Types;
using CxCompiler.Model.Types.BuiltInTypes;
using CxCompiler.OutputGenerators;
using CxCompiler.Semantics;

namespace CxCompiler.Tests;

public sealed class ConditionalExpressionTests
{
    [Fact]
    public void ParsesAndBindsConditionalExpression()
    {
        var project = CreateProject("""
            int Select(bool condition, int first, int second) {
                return condition ? first : second;
            }
            """);

        new SemanticBinder().Bind(project);

        var function = Assert.IsType<FunctionDeclaration>(
            project.CompilationContexts.Single().DeclarationScope.Declarations.Single());
        var statement = Assert.IsType<ReturnStatement>(Assert.Single(function.Body!));
        var conditional = Assert.IsType<ConditionalExpression>(statement.Expression);

        Assert.Same(BuiltInSystemTypes.Bool, conditional.Condition.InferredType);
        Assert.Same(BuiltInSystemTypes.Int, conditional.InferredType);
    }

    [Fact]
    public void RejectsNonBooleanCondition()
    {
        var project = CreateProject("""
            int Select(int condition) {
                return condition ? 1 : 2;
            }
            """);

        var exception = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(project));

        Assert.Contains("requires a bool condition", exception.Message);
    }

    [Fact]
    public void RejectsBranchesWithDifferentTypes()
    {
        var project = CreateProject("""
            int Select(bool condition) {
                return condition ? 1 : false;
            }
            """);

        var exception = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(project));

        Assert.Contains("branches must have the same type", exception.Message);
    }

    [Fact]
    public void EmitsConditionalExpressionAndItsStringLiterals()
    {
        var project = CreateProject("""
            string Select(bool condition) {
                return condition ? "yes" : "no";
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
                Path.Combine(outputDirectory, "Conditional.cx"));
            var generatedSource = File.ReadAllText(
                Path.Combine(outputDirectory, "Unnamed.c"));

            Assert.Contains("CX_STRING_DEF", generatedSource);
            Assert.Contains("\"yes\"", generatedSource);
            Assert.Contains("\"no\"", generatedSource);
            Assert.Contains("return (condition ? &CX_ID_2(Unnamed, __string_", generatedSource);
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

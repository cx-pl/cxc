using CxCompiler.Model.Errors;
using CxCompiler.Model.Expressions;
using CxCompiler.Model.Project;
using CxCompiler.Model.Statements;
using CxCompiler.Model.Types;
using CxCompiler.Model.Types.BuiltInTypes;
using CxCompiler.OutputGenerators;
using CxCompiler.Semantics;

namespace CxCompiler.Tests;

public sealed class UnaryExpressionTests
{
    [Fact]
    public void ParserBuildsNestedPrefixAndPostfixExpressions()
    {
        var project = CreateProject("""
            int Update(int value) {
                value++;
                return -(~(+value));
            }

            int Grouped(int value) {
                return -(value + 1);
            }
            """);

        var function = GetFunction(project, "Update");
        var postfix = Assert.IsType<UnaryExpression>(
            Assert.IsType<ExpressionStatement>(function.Body![0]).Expression);
        var minus = Assert.IsType<UnaryExpression>(
            Assert.IsType<ReturnStatement>(function.Body[1]).Expression);
        var complement = Assert.IsType<UnaryExpression>(minus.Operand);
        var plus = Assert.IsType<UnaryExpression>(complement.Operand);

        Assert.Equal("++", postfix.Operator);
        Assert.True(postfix.Postfix);
        Assert.Equal("-", minus.Operator);
        Assert.False(minus.Postfix);
        Assert.Equal("~", complement.Operator);
        Assert.Equal("+", plus.Operator);

        var groupedMinus = Assert.IsType<UnaryExpression>(
            Assert.IsType<ReturnStatement>(GetFunction(project, "Grouped").Body!.Single()).Expression);
        var groupedAddition = Assert.IsType<BinaryExpression>(groupedMinus.Operand);
        Assert.Equal("+", groupedAddition.Operator);
    }

    [Fact]
    public void BinderInfersUnaryResultTypes()
    {
        var project = CreateProject("""
            int Transform(int value) {
                int first = +value;
                int second = -first;
                int third = ~second;
                ++third;
                third--;
                return third;
            }

            bool Negate(bool value) {
                return !value;
            }

            int ReadOnly(const int value) {
                return -value;
            }
            """);

        new SemanticBinder().Bind(project);

        var transform = GetFunction(project, "Transform");
        var unaryExpressions = transform.Body!
            .OfType<LocalVariableDeclarationStatement>()
            .Select(statement => Assert.IsType<UnaryExpression>(
                statement.Declarators.Single().Initializer))
            .Concat(transform.Body!.OfType<ExpressionStatement>()
                .Select(statement => Assert.IsType<UnaryExpression>(statement.Expression)))
            .ToArray();
        var negate = Assert.IsType<UnaryExpression>(
            Assert.IsType<ReturnStatement>(GetFunction(project, "Negate").Body!.Single()).Expression);
        var readOnly = Assert.IsType<UnaryExpression>(
            Assert.IsType<ReturnStatement>(GetFunction(project, "ReadOnly").Body!.Single()).Expression);

        Assert.All(unaryExpressions, expression =>
            Assert.Same(BuiltInSystemTypes.Int, expression.InferredType));
        Assert.Same(BuiltInSystemTypes.Bool, negate.InferredType);
        Assert.Same(BuiltInSystemTypes.Int, readOnly.InferredType);
    }

    [Fact]
    public void BinderRejectsInvalidUnaryOperands()
    {
        var logicalProject = CreateProject("""
            bool Invalid(string value) {
                return !value;
            }
            """);
        var incrementProject = CreateProject("""
            void Invalid() {
                1++;
            }
            """);
        var constProject = CreateProject("""
            void Invalid(const int value) {
                value++;
            }
            """);

        var logicalException = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(logicalProject));
        var incrementException = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(incrementProject));
        var constException = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(constProject));

        Assert.Contains("Operator '!' cannot be applied to 'System.String'", logicalException.Message);
        Assert.Contains("Operator '++' requires an assignable value", incrementException.Message);
        Assert.Contains("Operator '++' requires a writable value", constException.Message);
    }

    [Fact]
    public void EmitsPrefixAndPostfixUnaryOperators()
    {
        var project = CreateProject("""
            int Update(int value, bool enabled) {
                int negative = -value;
                int complement = ~negative;
                bool disabled = !enabled;
                ++value;
                value--;
                return +complement;
            }
            """);
        new SemanticBinder().Bind(project);

        var source = GenerateSource(project);

        Assert.Contains("cx_int negative = (-value);", source);
        Assert.Contains("cx_int complement = (~negative);", source);
        Assert.Contains("cx_bool disabled = (!enabled);", source);
        Assert.Contains("(++value);", source);
        Assert.Contains("(value--);", source);
        Assert.Contains("return (+complement);", source);
    }

    private static CxProject CreateProject(string source)
    {
        var project = CxProject.CreateDefaultApplicationProject();
        project.AddCompilationContext(CompilerTestHelper.Parse(source));
        return project;
    }

    private static FunctionDeclaration GetFunction(CxProject project, string name)
    {
        return Assert.IsType<FunctionDeclaration>(
            project.CompilationContexts.Single().DeclarationScope.Declarations
                .Single(declaration => declaration.Name == name));
    }

    private static string GenerateSource(CxProject project)
    {
        var outputDirectory = Path.Combine(
            Path.GetTempPath(),
            $"cxc-tests-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(outputDirectory);
            CCodeOutputGenerator.GenerateOutput(
                project,
                Path.Combine(outputDirectory, "Unary.cx"));
            return File.ReadAllText(Path.Combine(outputDirectory, "unnamed.c"));
        }
        finally
        {
            if (Directory.Exists(outputDirectory))
            {
                Directory.Delete(outputDirectory, recursive: true);
            }
        }
    }
}

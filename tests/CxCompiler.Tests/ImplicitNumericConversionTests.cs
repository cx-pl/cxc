using CxCompiler.Model.Errors;
using CxCompiler.Model.Expressions;
using CxCompiler.Model.Project;
using CxCompiler.Model.Statements;
using CxCompiler.Model.Types;
using CxCompiler.Model.Types.BuiltInTypes;
using CxCompiler.Semantics;

namespace CxCompiler.Tests;

public sealed class ImplicitNumericConversionTests
{
    [Fact]
    public void AllowsWideningConversionsInValueContexts()
    {
        var project = CreateProject("""
            void Consume(long value) {}

            long Convert(int value) {
                long local = value;
                local = value;
                Consume(value);
                return value;
            }
            """);

        new SemanticBinder().Bind(project);
    }

    [Fact]
    public void RejectsNarrowingConversion()
    {
        var project = CreateProject("""
            void Convert(long value) {
                int local = value;
            }
            """);

        var exception = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(project));

        Assert.Contains("Cannot initialize local 'local'", exception.Message);
    }

    [Fact]
    public void UsesWiderTypeForMixedNumericExpression()
    {
        var project = CreateProject("""
            long Add(int left, long right) {
                return left + right;
            }
            """);

        new SemanticBinder().Bind(project);

        var function = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<FunctionDeclaration>()
            .Single();
        var statement = Assert.IsType<ReturnStatement>(Assert.Single(function.Body!));
        var expression = Assert.IsType<BinaryExpression>(statement.Expression);
        Assert.Same(BuiltInSystemTypes.Long, expression.InferredType);
    }

    [Fact]
    public void UsesWiderTypeForConditionalExpression()
    {
        var project = CreateProject("""
            long Select(bool condition, int first, long second) {
                return condition ? first : second;
            }
            """);

        new SemanticBinder().Bind(project);

        var function = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<FunctionDeclaration>()
            .Single();
        var statement = Assert.IsType<ReturnStatement>(Assert.Single(function.Body!));
        var expression = Assert.IsType<ConditionalExpression>(statement.Expression);
        Assert.Same(BuiltInSystemTypes.Long, expression.InferredType);
    }

    [Fact]
    public void PrefersExactAndClosestWideningOverloads()
    {
        var project = CreateProject("""
            void Consume(int value) {}
            void Consume(long value) {}
            void Consume(double value) {}

            void Run(int value, short small) {
                Consume(value);
                Consume(small);
            }
            """);

        new SemanticBinder().Bind(project);

        var run = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<FunctionDeclaration>()
            .Single(function => function.Name == "Run");
        var invocations = run.Body!
            .Cast<ExpressionStatement>()
            .Select(statement => Assert.IsType<InvocationExpression>(statement.Expression))
            .ToArray();
        Assert.Same(BuiltInSystemTypes.Int, invocations[0].TargetSymbol!.ParameterTypes.Single());
        Assert.Same(BuiltInSystemTypes.Int, invocations[1].TargetSymbol!.ParameterTypes.Single());
    }

    private static CxProject CreateProject(string source)
    {
        var project = CxProject.CreateDefaultApplicationProject();
        project.AddCompilationContext(CompilerTestHelper.Parse(source));
        return project;
    }
}

using CxCompiler.Model.Errors;
using CxCompiler.Model.Expressions;
using CxCompiler.Model.Project;
using CxCompiler.Model.Statements;
using CxCompiler.Model.Types;
using CxCompiler.Model.Types.BuiltInTypes;
using CxCompiler.Semantics;

namespace CxCompiler.Tests;

public sealed class SemanticBinderTests
{
    [Fact]
    public void ResolvesCoreFunctionThroughImport()
    {
        var project = CreateProject("""
            import System;

            void Main() {
                Console.WriteLine("Hello world!");
            }
            """);

        new SemanticBinder().Bind(project);

        var invocation = GetOnlyInvocation(project);
        Assert.NotNull(invocation.TargetSymbol);
        Assert.Equal("cxcore", invocation.TargetSymbol.ModuleName);
        Assert.Equal("System.Console.WriteLine", invocation.TargetSymbol.FullName.ToString());
    }

    [Fact]
    public void ResolvesFunctionInCurrentNamespace()
    {
        var project = CreateProject("""
            namespace Example;

            void Helper() {}
            void Main() {
                Helper();
            }
            """);

        new SemanticBinder().Bind(project);

        var main = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<FunctionDeclaration>()
            .Single(function => function.Name == "Main");
        var statement = Assert.IsType<ExpressionStatement>(Assert.Single(main.Body!));
        var invocation = Assert.IsType<InvocationExpression>(statement.Expression);
        Assert.NotNull(invocation.TargetSymbol);
        Assert.Equal("unnamed", invocation.TargetSymbol.ModuleName);
        Assert.Equal("Example.Helper", invocation.TargetSymbol.FullName.ToString());
    }

    [Fact]
    public void ReportsUnresolvedFunctionWithoutImport()
    {
        var project = CreateProject("""
            void Main() {
                Console.WriteLine("Hello world!");
            }
            """);

        var exception = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(project));

        Assert.Contains("Cannot resolve function 'Console.WriteLine'", exception.Message);
    }

    [Fact]
    public void SelectsOverloadByArgumentType()
    {
        var project = CreateProject("""
            void Print(int value) {}
            void Print(string value) {}

            void Main() {
                Print("text");
            }
            """);

        new SemanticBinder().Bind(project);

        var main = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<FunctionDeclaration>()
            .Single(function => function.Name == "Main");
        var statement = Assert.IsType<ExpressionStatement>(Assert.Single(main.Body!));
        var invocation = Assert.IsType<InvocationExpression>(statement.Expression);
        Assert.NotNull(invocation.TargetSymbol);
        Assert.Equal(2, invocation.TargetSymbol.OverloadIndex);
        Assert.Same(BuiltInSystemTypes.String, invocation.TargetSymbol.ParameterTypes.Single());
        Assert.Same(BuiltInSystemTypes.Void, invocation.InferredType);
        Assert.Same(
            BuiltInSystemTypes.String,
            Assert.Single(invocation.Arguments).InferredType);
    }

    [Fact]
    public void ReportsWhenNoOverloadAcceptsArgumentTypes()
    {
        var project = CreateProject("""
            void Print(int value) {}

            void Main() {
                Print("text");
            }
            """);

        var exception = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(project));

        Assert.Contains("No overload of 'Print' accepts (System.String)", exception.Message);
    }

    [Fact]
    public void InfersParameterExpressionAndValidatesReturnType()
    {
        var project = CreateProject("""
            string Echo(string value) {
                return value;
            }
            """);

        new SemanticBinder().Bind(project);

        var function = Assert.IsType<FunctionDeclaration>(
            project.CompilationContexts.Single().DeclarationScope.Declarations.Single());
        var statement = Assert.IsType<ReturnStatement>(Assert.Single(function.Body!));
        Assert.Same(BuiltInSystemTypes.String, statement.Expression!.InferredType);
    }

    [Fact]
    public void ReportsWrongReturnType()
    {
        var project = CreateProject("""
            int GetValue() {
                return "wrong";
            }
            """);

        var exception = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(project));

        Assert.Contains("returns 'System.String', but 'System.Int' is required", exception.Message);
    }

    [Fact]
    public void ReportsMissingReturnValue()
    {
        var project = CreateProject("""
            int GetValue() {}
            """);

        var exception = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(project));

        Assert.Contains("must return a value of type 'System.Int'", exception.Message);
    }

    private static CxProject CreateProject(string source)
    {
        var project = CxProject.CreateDefaultApplicationProject();
        project.AddCompilationContext(CompilerTestHelper.Parse(source));
        return project;
    }

    private static InvocationExpression GetOnlyInvocation(CxProject project)
    {
        var function = Assert.IsType<FunctionDeclaration>(
            project.CompilationContexts.Single().DeclarationScope.Declarations.Single());
        var statement = Assert.IsType<ExpressionStatement>(Assert.Single(function.Body!));
        return Assert.IsType<InvocationExpression>(statement.Expression);
    }
}

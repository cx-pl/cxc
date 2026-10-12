using CxCompiler.Model.Project;
using CxCompiler.Model.Types;
using CxCompiler.OutputGenerators;
using CxCompiler.Semantics;

namespace CxCompiler.Tests;

public sealed class ImplicitConstructorTests
{
    [Fact]
    public void ClassWithoutDeclaredConstructorsGetsAParameterlessConstructor()
    {
        var project = CreateProject("""
            public class Empty { }
            int Run() { Empty value = new Empty(); return 1; }
            """);

        new SemanticBinder().Bind(project);

        var empty = Assert.IsType<ClassDeclaration>(project.CompilationContexts.Single()
            .DeclarationScope.Declarations.Single(declaration => declaration.Name == "Empty"));
        var constructor = Assert.IsType<ConstructorDeclaration>(Assert.Single(
            empty.MemberDeclarations.Declarations.OfType<ConstructorDeclaration>()));
        Assert.Empty(constructor.Parameters);
        Assert.Empty(constructor.Body!);
    }

    [Fact]
    public void ImplicitDerivedConstructorChainsToBaseParameterlessConstructor()
    {
        var project = CreateProject("""
            public class Base { }
            public class Derived : Base { }
            int Run() { Derived value = new Derived(); return 1; }
            """);

        new SemanticBinder().Bind(project);
        var outputDirectory = Path.Combine(Path.GetTempPath(), $"implicit-ctor-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(outputDirectory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(outputDirectory, "input.cx"));
            var generated = File.ReadAllText(Path.Combine(outputDirectory, "implicit_constructor_test.c"));
            Assert.Contains("__base)", generated);
        }
        finally
        {
            if (Directory.Exists(outputDirectory)) Directory.Delete(outputDirectory, recursive: true);
        }
    }

    [Fact]
    public void ClosedGenericClassWithoutDeclaredConstructorsGetsAParameterlessConstructor()
    {
        var project = CreateProject("""
            public class Box<T> { }
            Box<int> Create() { return new Box<int>(); }
            """);

        new SemanticBinder().Bind(project);

        var box = Assert.IsType<ClassDeclaration>(project.CompilationContexts.Single()
            .DeclarationScope.Declarations.Single(declaration => declaration.Name == "Box"));
        var constructor = Assert.IsType<ConstructorDeclaration>(Assert.Single(
            box.MemberDeclarations.Declarations.OfType<ConstructorDeclaration>()));
        Assert.Empty(constructor.Parameters);

        var creation = Assert.IsType<CxCompiler.Model.Expressions.ObjectCreationExpression>(
            Assert.IsType<CxCompiler.Model.Statements.ReturnStatement>(
                Assert.Single(project.CompilationContexts.Single().DeclarationScope.Declarations
                    .OfType<FunctionDeclaration>().Single().Body!)).Expression);
        Assert.NotNull(creation.Constructor);
        Assert.Empty(creation.Constructor!.ParameterTypes);
    }

    [Fact]
    public void ClassWithExplicitParameterizedConstructorDoesNotGetAnImplicitOne()
    {
        var project = CreateProject("""
            public class RequiresValue { public constructor(int value) { } }
            int Run() { RequiresValue value = new RequiresValue(); return 1; }
            """);

        var error = Assert.Throws<CxCompiler.Model.Errors.CompilationErrorException>(
            () => new SemanticBinder().Bind(project));

        Assert.Contains("No constructor for 'implicit_constructor_test.RequiresValue'", error.Message);
    }

    [Fact]
    public void StaticClassDoesNotGetAnInstanceConstructor()
    {
        var project = CreateProject("public static class Utilities { }");

        new SemanticBinder().Bind(project);

        var utilities = Assert.IsType<ClassDeclaration>(Assert.Single(
            project.CompilationContexts.Single().DeclarationScope.Declarations));
        Assert.Empty(utilities.MemberDeclarations.Declarations.OfType<ConstructorDeclaration>());
    }

    private static CxProject CreateProject(string source)
    {
        var project = CxProject.CreateDefaultApplicationProject("implicit_constructor_test");
        project.AddCompilationContext(CompilerTestHelper.Parse(source));
        return project;
    }
}

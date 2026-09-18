using CxCompiler.Model.Errors;
using CxCompiler.Model.Expressions;
using CxCompiler.Model.Project;
using CxCompiler.Model.Statements;
using CxCompiler.Model.Types;
using CxCompiler.OutputGenerators;
using CxCompiler.Semantics;

namespace CxCompiler.Tests;

public sealed class ObjectCreationTests
{
    [Fact]
    public void ParsesConstructorBodiesAndBindsClassConstruction()
    {
        var project = CreateProject("""
            public class Widget {
                public constructor(int value) {}
            }

            Widget Create() {
                return new Widget(42);
            }
            """);

        new SemanticBinder().Bind(project);

        var declarations = project.CompilationContexts.Single().DeclarationScope.Declarations;
        var type = Assert.IsType<ClassDeclaration>(declarations[0]);
        var constructor = Assert.IsType<ConstructorDeclaration>(
            type.MemberDeclarations.Declarations.Single());
        Assert.NotNull(constructor.Body);

        var function = Assert.IsType<FunctionDeclaration>(declarations[1]);
        var statement = Assert.IsType<ReturnStatement>(Assert.Single(function.Body!));
        var creation = Assert.IsType<ObjectCreationExpression>(statement.Expression);
        Assert.NotNull(creation.Constructor);
        Assert.Equal(ClassType.Class, creation.ClassType);
        Assert.Equal("__cx_new_0", creation.TemporaryName);
    }

    [Fact]
    public void SelectsConstructorOverload()
    {
        var project = CreateProject("""
            public struct Value {
                public constructor(int value) {}
                public constructor(bool value) {}
            }

            Value Create() {
                return new Value(true);
            }
            """);

        new SemanticBinder().Bind(project);

        var function = Assert.IsType<FunctionDeclaration>(
            project.CompilationContexts.Single().DeclarationScope.Declarations[1]);
        var statement = Assert.IsType<ReturnStatement>(Assert.Single(function.Body!));
        var creation = Assert.IsType<ObjectCreationExpression>(statement.Expression);

        Assert.Equal(2, creation.Constructor!.OverloadIndex);
        Assert.Equal(ClassType.Struct, creation.ClassType);
    }

    [Fact]
    public void RejectsMissingConstructorOverload()
    {
        var project = CreateProject("""
            public class Widget {
                public constructor(int value) {}
            }

            Widget Create() {
                return new Widget(false);
            }
            """);

        var exception = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(project));

        Assert.Contains("No constructor", exception.Message);
    }

    [Fact]
    public void RejectsStaticTypeConstruction()
    {
        var project = CreateProject("""
            public static class Utility {}

            void Main() {
                new Utility();
            }
            """);

        var exception = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(project));

        Assert.Contains("Static type", exception.Message);
    }

    [Fact]
    public void BindsCoreStringConstructor()
    {
        var project = CreateProject("""
            string Create() {
                return new string(3u, 'x');
            }
            """);

        new SemanticBinder().Bind(project);

        var function = Assert.IsType<FunctionDeclaration>(
            project.CompilationContexts.Single().DeclarationScope.Declarations.Single());
        var statement = Assert.IsType<ReturnStatement>(Assert.Single(function.Body!));
        var creation = Assert.IsType<ObjectCreationExpression>(statement.Expression);

        Assert.Equal("cxcore", creation.Constructor!.ModuleName);
        Assert.Equal(3, creation.Constructor.OverloadIndex);
    }

    [Fact]
    public void EmitsHeapAndValueTypeConstruction()
    {
        var project = CreateProject("""
            public class Widget {
                public constructor(int value) {}
            }

            public struct Number {
                public constructor(int value) {}
            }

            Widget CreateWidget() {
                return new Widget(42);
            }

            Number CreateNumber() {
                return new Number(7);
            }
            """);
        new SemanticBinder().Bind(project);
        var generatedSource = GenerateSource(project);

        Assert.Contains("CX_INIT_VTABLE(__this, CX_ID_2(unnamed, Widget));", generatedSource);
        Assert.Contains("cx_object_new((cx_uint)sizeof( struct CX_ID_2(unnamed, Widget)))", generatedSource);
        Assert.Contains("CX_ID_3(unnamed, Widget, __constructor)(__cx_new_0 =", generatedSource);
        Assert.Contains("CX_ID_3(unnamed, Number, __constructor)(&__cx_new_0, 7)", generatedSource);
    }

    private static CxProject CreateProject(string source)
    {
        var project = CxProject.CreateDefaultApplicationProject();
        project.AddCompilationContext(CompilerTestHelper.Parse(source));
        return project;
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
                Path.Combine(outputDirectory, "Construction.cx"));
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

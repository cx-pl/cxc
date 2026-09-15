using CxCompiler.Model.Errors;
using CxCompiler.Model.Expressions;
using CxCompiler.Model.Project;
using CxCompiler.Model.Statements;
using CxCompiler.Model.Types;
using CxCompiler.OutputGenerators;
using CxCompiler.Semantics;

namespace CxCompiler.Tests;

public sealed class FieldAccessTests
{
    [Fact]
    public void BindsThisAndImplicitInstanceFields()
    {
        var project = CreateProject("""
            public class Counter {
                private int value = 1;
                public constructor() {}

                public int Read() {
                    return this.value;
                }

                public int Increment() {
                    value += 1;
                    return value;
                }
            }
            """);

        new SemanticBinder().Bind(project);

        var type = Assert.IsType<ClassDeclaration>(
            project.CompilationContexts.Single().DeclarationScope.Declarations.Single());
        var read = Assert.IsType<FunctionDeclaration>(
            type.MemberDeclarations.Declarations.Single(member => member.Name == "Read"));
        var returnStatement = Assert.IsType<ReturnStatement>(Assert.Single(read.Body!));
        var access = Assert.IsType<MemberAccessExpression>(returnStatement.Expression);
        Assert.IsType<ThisExpression>(access.Target);
        Assert.Equal("value", access.TargetField!.Declaration.Name);

        var increment = Assert.IsType<FunctionDeclaration>(
            type.MemberDeclarations.Declarations.Single(member => member.Name == "Increment"));
        var assignmentStatement = Assert.IsType<ExpressionStatement>(increment.Body![0]);
        var assignment = Assert.IsType<AssignmentExpression>(assignmentStatement.Expression);
        var identifier = Assert.IsType<IdentifierExpression>(assignment.Target);
        Assert.Equal("value", identifier.TargetField!.Declaration.Name);
    }

    [Fact]
    public void LocalShadowsImplicitField()
    {
        var project = CreateProject("""
            public struct Value {
                private int number;

                public int Read() {
                    int number = 7;
                    return number;
                }
            }
            """);

        new SemanticBinder().Bind(project);

        var type = Assert.IsType<ClassDeclaration>(
            project.CompilationContexts.Single().DeclarationScope.Declarations.Single());
        var function = Assert.IsType<FunctionDeclaration>(
            type.MemberDeclarations.Declarations.Single(member => member.Name == "Read"));
        var returnStatement = Assert.IsType<ReturnStatement>(function.Body![1]);
        var identifier = Assert.IsType<IdentifierExpression>(returnStatement.Expression);
        Assert.Null(identifier.TargetField);
    }

    [Fact]
    public void BindsExplicitClassAndStructReceivers()
    {
        var project = CreateProject("""
            public class Box {
                public int value;

                public int Read(Box other) {
                    return other.value;
                }
            }

            public struct Pair {
                public int value;

                public int Read(Pair other) {
                    return other.value;
                }
            }
            """);

        new SemanticBinder().Bind(project);

        foreach (var type in project.CompilationContexts.Single().DeclarationScope.Declarations
            .Cast<ClassDeclaration>())
        {
            var function = Assert.IsType<FunctionDeclaration>(
                type.MemberDeclarations.Declarations.Single(member => member.Name == "Read"));
            var statement = Assert.IsType<ReturnStatement>(Assert.Single(function.Body!));
            var access = Assert.IsType<MemberAccessExpression>(statement.Expression);
            Assert.Equal("value", access.TargetField!.Declaration.Name);
        }
    }

    [Fact]
    public void BindsStaticFieldAccessAndAssignment()
    {
        var project = CreateProject("""
            public static class Settings {
                public static int Count = 2;

                public static int Update() {
                    Settings.Count = 3;
                    return Settings.Count;
                }
            }
            """);

        new SemanticBinder().Bind(project);

        var type = Assert.IsType<ClassDeclaration>(
            project.CompilationContexts.Single().DeclarationScope.Declarations.Single());
        var function = Assert.IsType<FunctionDeclaration>(
            type.MemberDeclarations.Declarations.Single(member => member.Name == "Update"));
        var assignment = Assert.IsType<AssignmentExpression>(
            Assert.IsType<ExpressionStatement>(function.Body![0]).Expression);
        var access = Assert.IsType<MemberAccessExpression>(assignment.Target);

        Assert.True(access.TargetField!.Declaration.IsStatic);
    }

    [Fact]
    public void RejectsInstanceFieldFromStaticFunction()
    {
        var project = CreateProject("""
            public class Counter {
                private int value;

                public static int Read() {
                    return value;
                }
            }
            """);

        var exception = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(project));

        Assert.Contains("cannot be used from a static function", exception.Message);
    }

    [Fact]
    public void RejectsConstFieldAssignment()
    {
        var project = CreateProject("""
            public static class Settings {
                public static const int Limit = 10;

                public static void Change() {
                    Settings.Limit = 20;
                }
            }
            """);

        var exception = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(project));

        Assert.Contains("Const field", exception.Message);
    }

    [Fact]
    public void EmitsInstanceStaticAndInitializedFields()
    {
        var project = CreateProject("""
            public class Counter {
                private int value = 1;
                public static string Name = "counter";
                public constructor() {}

                public int Read(Counter other) {
                    value = other.value;
                    return this.value;
                }
            }
            """);
        new SemanticBinder().Bind(project);
        var (header, source) = Generate(project);

        Assert.Contains("cx_int value;", header);
        Assert.Contains("extern  struct CX_ID_3(cxcore, System, String)* CX_ID_3(Unnamed, Counter, Name);", header);
        Assert.Contains("CX_ID_3(Unnamed, Counter, Name) = &CX_ID_2(Unnamed, __string_", source);
        Assert.Contains("__this->value = 1;", source);
        Assert.Contains("__this->value = (other)->value;", source);
        Assert.Contains("return (__this)->value;", source);
    }

    private static CxProject CreateProject(string source)
    {
        var project = CxProject.CreateUnnamedApplicationProject();
        project.AddCompilationContext(CompilerTestHelper.Parse(source));
        return project;
    }

    private static (string Header, string Source) Generate(CxProject project)
    {
        var outputDirectory = Path.Combine(
            Path.GetTempPath(),
            $"cxc-tests-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(outputDirectory);
            CCodeOutputGenerator.GenerateOutput(
                project,
                Path.Combine(outputDirectory, "Fields.cx"));
            return (
                File.ReadAllText(Path.Combine(outputDirectory, "Unnamed.h")),
                File.ReadAllText(Path.Combine(outputDirectory, "Unnamed.c")));
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

using CxCompiler.Model.Errors;
using CxCompiler.Model.Expressions;
using CxCompiler.Model.Project;
using CxCompiler.Model.Statements;
using CxCompiler.Model.Types;
using CxCompiler.OutputGenerators;
using CxCompiler.Semantics;

namespace CxCompiler.Tests;

public sealed class InheritanceTests
{
    [Fact]
    public void ParsesAndResolvesBaseClassAndInterfaces()
    {
        var project = CreateProject("""
            public interface IFirst {}
            public interface ISecond {}
            public class Parent {}
            public class Child : Parent, IFirst, ISecond {}
            """);

        new SemanticBinder().Bind(project);

        var types = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<ClassDeclaration>()
            .ToDictionary(type => type.Name);
        var child = types["Child"];
        Assert.Same(types["Parent"], child.BaseClassDeclaration);
        Assert.Equal(
            ["IFirst", "ISecond"],
            child.BaseInterfaces.Select(type => type.Name));
    }

    [Fact]
    public void BindsInheritedFieldsPropertiesAndMethods()
    {
        var project = CreateProject("""
            public class Parent {
                protected int value;

                public int Number {
                    const get { return value; }
                    set(value) { this.value = value; }
                }

                public int Add(int amount) const {
                    return value + amount;
                }

                public constructor(int initial) {
                    value = initial;
                }
            }

            public class Child : Parent {
                public constructor(int initial) : base(initial) {}

                public int Read(Child other) const {
                    return value + Number + other.Add(1);
                }

                public void Write(int next) {
                    Number = next;
                }
            }
            """);

        new SemanticBinder().Bind(project);

        var child = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<ClassDeclaration>()
            .Single(type => type.Name == "Child");
        var constructor = child.MemberDeclarations.Declarations
            .OfType<ConstructorDeclaration>()
            .Single();
        Assert.Equal(ConstructorInitializerKind.Base, constructor.Initializer!.Kind);
        Assert.Equal("Parent", constructor.Initializer.Target!.Declaration!.ParentClassDeclaration!.Name);

        var read = child.MemberDeclarations.Declarations
            .OfType<FunctionDeclaration>()
            .Single(function => function.Name == "Read");
        var returned = Assert.IsType<ReturnStatement>(Assert.Single(read.Body!));
        var outerAdd = Assert.IsType<BinaryExpression>(returned.Expression);
        var invocation = Assert.IsType<InvocationExpression>(outerAdd.Right);
        Assert.Equal("Parent", invocation.TargetSymbol!.Declaration!.ParentClassDeclaration!.Name);
        Assert.Equal(1, invocation.ReceiverBaseDepth);
    }

    [Fact]
    public void EmitsBaseLayoutConstructorChainingAndInheritedAccessPaths()
    {
        var project = CreateProject("""
            public class Parent {
                protected int value;
                public int Number {
                    const get { return value; }
                    set(value) { this.value = value; }
                }
                public int Add(int amount) const { return value + amount; }
                public constructor(int initial) { value = initial; }
            }

            public class Child : Parent {
                public constructor(int initial) : base(initial) {}
                public int Read(Child other) const {
                    return value + Number + other.Add(1);
                }

                public void Write(int next) {
                    Number = next;
                }
            }
            """);
        new SemanticBinder().Bind(project);

        var (header, source) = GenerateOutput(project);

        Assert.True(
            header.IndexOf("CX_TYPE_DEF(CX_ID_2(Unnamed, Parent))", StringComparison.Ordinal) <
            header.IndexOf("CX_TYPE_DEF(CX_ID_2(Unnamed, Child))", StringComparison.Ordinal));
        Assert.Contains("struct CX_ID_2(Unnamed, Parent) __base;", header);
        Assert.Contains(
            "CX_ID_3(Unnamed, Parent, __constructor)(&__this->__base, initial);",
            source);
        Assert.Contains("(__this)->__base.value", source);
        Assert.Contains(
            "CX_ID_4(Unnamed, Parent, Number, __const_get)(&((__this)->__base))",
            source);
        Assert.Contains(
            "CX_ID_3(Unnamed, Parent, Add)(&((other)->__base), 1)",
            source);
        Assert.Contains(
            "CX_ID_4(Unnamed, Parent, Number, __set)(&((__this)->__base)",
            source);
        Assert.Contains(
            "CX_ID_3(Unnamed, Parent, __typeinfo)",
            source);
    }

    [Fact]
    public void BindsThisConstructorChains()
    {
        var project = CreateProject("""
            public class Value {
                private int number;
                public constructor() : this(7) {}
                public constructor(int initial) { number = initial; }
            }
            """);

        new SemanticBinder().Bind(project);
        var type = Assert.IsType<ClassDeclaration>(
            project.CompilationContexts.Single().DeclarationScope.Declarations.Single());
        var constructor = type.MemberDeclarations.Declarations
            .OfType<ConstructorDeclaration>()
            .First();

        Assert.Equal(ConstructorInitializerKind.This, constructor.Initializer!.Kind);
        Assert.Equal(2, constructor.Initializer.Target!.OverloadIndex);
    }

    [Theory]
    [InlineData("public final class Parent {} public class Child : Parent {}", "final type")]
    [InlineData("public class One {} public class Two {} public class Child : One, Two {}", "more than one base class")]
    [InlineData("public class One : Two {} public class Two : One {}", "Inheritance cycle")]
    [InlineData("public struct Value {} public class Child : Value {}", "cannot derive")]
    public void RejectsInvalidBaseRelationships(string source, string expectedMessage)
    {
        var exception = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(CreateProject(source)));

        Assert.Contains(expectedMessage, exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RejectsMissingBaseConstructorAndThisCycles()
    {
        var missingBase = CreateProject("""
            public class Parent { public constructor(int value) {} }
            public class Child : Parent { public constructor() {} }
            """);
        var missingException = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(missingBase));
        Assert.Contains("No constructor for 'Unnamed.Parent' accepts", missingException.Message);

        var cycle = CreateProject("""
            public class Value {
                public constructor() : this(1) {}
                public constructor(int value) : this() {}
            }
            """);
        var cycleException = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(cycle));
        Assert.Contains("Constructor initializer cycle", cycleException.Message);
    }

    private static CxProject CreateProject(string source)
    {
        var project = CxProject.CreateUnnamedApplicationProject();
        project.AddCompilationContext(CompilerTestHelper.Parse(source));
        return project;
    }

    private static (string Header, string Source) GenerateOutput(CxProject project)
    {
        var outputDirectory = Path.Combine(
            Path.GetTempPath(),
            $"cxc-tests-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(outputDirectory);
            CCodeOutputGenerator.GenerateOutput(
                project,
                Path.Combine(outputDirectory, "Inheritance.cx"));
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

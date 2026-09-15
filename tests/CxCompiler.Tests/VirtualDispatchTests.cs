using CxCompiler.Model.Errors;
using CxCompiler.Model.Project;
using CxCompiler.Model.Types;
using CxCompiler.OutputGenerators;
using CxCompiler.Semantics;

namespace CxCompiler.Tests;

public sealed class VirtualDispatchTests
{
    [Fact]
    public void AssignsStableSlotsAndReplacesOverrides()
    {
        var project = CreateProject("""
            public class Parent {
                public virtual int First() { return 1; }
                public virtual int Second() { return 2; }
            }
            public class Child : Parent {
                public override int First() { return 3; }
            }
            """);

        new SemanticBinder().Bind(project);

        var types = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<ClassDeclaration>()
            .ToDictionary(type => type.Name);
        Assert.Equal([1, 2], types["Parent"].VirtualMethodSlots.Select(slot => slot.Index));
        Assert.Equal("Child", types["Child"].VirtualMethodSlots[0]
            .Implementation!.ParentClassDeclaration!.Name);
        Assert.Equal("Parent", types["Child"].VirtualMethodSlots[1]
            .Implementation!.ParentClassDeclaration!.Name);
    }

    [Fact]
    public void EmitsVTablesVirtualCallsAndBaseUpcasts()
    {
        var project = CreateProject("""
            public class Parent {
                public virtual int Value() { return 1; }
                public constructor() {}
            }
            public class Child : Parent {
                public override int Value() { return 7; }
                public constructor() {}
            }
            public static class Program {
                public static int Read(Parent value) { return value.Value(); }
                public static int Main() {
                    Parent value = new Child();
                    return Read(value);
                }
            }
            """);
        new SemanticBinder().Bind(project);

        var (_, source) = GenerateOutput(project);

        Assert.Contains("CX_VTABLE_ENTRY(CX_ID_3(Unnamed, Parent, Value))", source);
        Assert.Contains("CX_VTABLE_ENTRY(CX_ID_3(Unnamed, Child, Value))", source);
        Assert.Contains("CX_GET_VTABLE(value))[1]", source);
        Assert.Contains("CX_ID_2(Unnamed, Parent)* value = (", source);
        Assert.Contains("CX_ID_2(Unnamed, Parent)*)(", source);
    }

    [Fact]
    public void AllowsImplicitImplementationOfAbstractFunction()
    {
        var project = CreateProject("""
            public abstract class Parent {
                public abstract int Value();
            }
            public class Child : Parent {
                public int Value() { return 5; }
            }
            """);

        new SemanticBinder().Bind(project);

        var child = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<ClassDeclaration>()
            .Single(type => type.Name == "Child");
        Assert.Equal("Child", Assert.Single(child.VirtualMethodSlots)
            .Implementation!.ParentClassDeclaration!.Name);
    }

    [Theory]
    [InlineData(
        "public class Parent { public int Value() { return 1; } } public class Child : Parent { public override int Value() { return 2; } }",
        "no matching virtual function")]
    [InlineData(
        "public class Parent { public virtual int Value() { return 1; } } public class Child : Parent { public override final int Value() { return 2; } } public class Grandchild : Child { public override int Value() { return 3; } }",
        "final function")]
    [InlineData(
        "public abstract class Parent { public abstract int Value(); } public class Child : Parent {}",
        "does not implement abstract function")]
    [InlineData(
        "public abstract class Parent { public abstract int Value() { return 1; } }",
        "cannot have a body")]
    public void RejectsInvalidVirtualContracts(string source, string expectedMessage)
    {
        var exception = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(CreateProject(source)));

        Assert.Contains(expectedMessage, exception.Message, StringComparison.OrdinalIgnoreCase);
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
                Path.Combine(outputDirectory, "Virtual.cx"));
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

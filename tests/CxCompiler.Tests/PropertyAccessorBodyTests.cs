using CxCompiler.Model.Errors;
using CxCompiler.Model.Project;
using CxCompiler.Model.Types;
using CxCompiler.OutputGenerators;
using CxCompiler.Semantics;

namespace CxCompiler.Tests;

public sealed class PropertyAccessorBodyTests
{
    [Fact]
    public void ParsesBindsAndEmitsInstanceAccessorBodies()
    {
        var project = CreateProject("""
            public class Counter {
                private int _value;

                public int Value {
                    const get { return _value; }
                    set(value) { _value = value; }
                }
            }
            """);

        new SemanticBinder().Bind(project);

        var property = GetProperty(project, "Value");
        var getter = property.PropertyAccessorDeclarations.Single(accessor => accessor.Name == "get");
        var setter = property.PropertyAccessorDeclarations.Single(accessor => accessor.Name == "set");
        Assert.Single(getter.Body!);
        Assert.Equal(["value"], setter.BodyFunction!.Parameters.Select(parameter => parameter.Name));

        var source = GenerateSource(project);
        Assert.Contains(
            "CX_ID_4(unnamed, Counter, Value, __const_get)(const struct CX_ID_2(unnamed, Counter)* __this)",
            source);
        Assert.Contains("return __this->_value;", source);
        Assert.Contains(
            "CX_ID_4(unnamed, Counter, Value, __set)(struct CX_ID_2(unnamed, Counter)* __this, cx_int value)",
            source);
        Assert.Contains("__this->_value = value;", source);
    }

    [Fact]
    public void BindsIndexedAccessorParametersAndSetterValue()
    {
        var project = CreateProject("""
            public struct Values {
                private uint _last;

                public uint Item {
                    const get(uint index) { return index; }
                    set(uint index, uint value) { _last = index + value; }
                }
            }
            """);

        new SemanticBinder().Bind(project);

        var property = GetProperty(project, "Item");
        var setter = property.PropertyAccessorDeclarations.Single(accessor => accessor.Name == "set");
        Assert.Equal(
            ["index", "value"],
            setter.BodyFunction!.Parameters.Select(parameter => parameter.Name));

        var source = GenerateSource(project);
        Assert.Contains(
            "CX_ID_4(unnamed, Values, Item, __set)(struct CX_ID_2(unnamed, Values)* __this, cx_uint index, cx_uint value)",
            source);
        Assert.Contains("__this->_last = (index + value);", source);
    }

    [Fact]
    public void EmitsStaticAndArrowAccessorBodies()
    {
        var project = CreateProject("""
            public static class Settings {
                private static int _current;

                public static int Current {
                    get => return _current;
                    set(value) { _current = value; }
                }
            }
            """);

        new SemanticBinder().Bind(project);
        var source = GenerateSource(project);

        Assert.Contains("CX_ID_4(unnamed, Settings, Current, __get)()", source);
        Assert.Contains("return CX_ID_3(unnamed, Settings, _current);", source);
        Assert.Contains("CX_ID_4(unnamed, Settings, Current, __set)(cx_int value)", source);
    }

    [Fact]
    public void RejectsGetterBodyWithoutReturn()
    {
        var project = CreateProject("""
            public class Value {
                public int Number { get { int result = 1; } }
            }
            """);

        var exception = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(project));

        Assert.Contains("must return a value", exception.Message);
    }

    [Fact]
    public void RejectsExternAccessorWithBody()
    {
        var project = CreateProject("""
            public class Value {
                public int Number { extern get { return 1; } }
            }
            """);

        var exception = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(project));

        Assert.Contains("Extern property accessor", exception.Message);
    }

    private static CxProject CreateProject(string source)
    {
        var project = CxProject.CreateDefaultApplicationProject();
        project.AddCompilationContext(CompilerTestHelper.Parse(source));
        return project;
    }

    private static PropertyDeclaration GetProperty(CxProject project, string name)
    {
        var type = Assert.IsType<ClassDeclaration>(
            project.CompilationContexts.Single().DeclarationScope.Declarations.Single());
        return Assert.IsType<PropertyDeclaration>(
            type.MemberDeclarations.Declarations.Single(declaration => declaration.Name == name));
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
                Path.Combine(outputDirectory, "PropertyBodies.cx"));
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

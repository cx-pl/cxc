using CxCompiler.Model.Errors;
using CxCompiler.Model.Project;
using CxCompiler.Model.Types;
using CxCompiler.OutputGenerators;
using CxCompiler.Semantics;

namespace CxCompiler.Tests;

public sealed class DefaultParameterTests
{
    [Fact]
    public void ParsesLiteralDefaultValues()
    {
        var project = CreateProject("""
            int Add(int left, int right = 5) { return left + right; }
            """);

        var function = Assert.IsType<FunctionDeclaration>(
            project.CompilationContexts.Single().DeclarationScope.Declarations.Single());

        Assert.Equal("5", function.Parameters[1].DefaultValueText);
    }

    [Fact]
    public void OmittingDefaultArgumentsEmitsTheirValuesAtTheCallSite()
    {
        var generated = GenerateSource("""
            int Add(int left, int right = 5) { return left + right; }
            int Run() { return Add(7); }
            """);

        Assert.Contains("Add)(7, 5)", generated);
    }

    [Fact]
    public void IntegralLiteralDefaultsMayNarrowWhenTheValueFits()
    {
        var generated = GenerateSource("""
            short Read(short value = 0) { return value; }
            short Run() { return Read(); }
            """);

        Assert.Contains("Read)(0)", generated);
    }

    [Fact]
    public void OptionalParametersParticipateInOverloadResolution()
    {
        var generated = GenerateSource("""
            int Pick(int value, int extra = 1) { return value + extra; }
            int Pick(string value) { return 2; }
            int Run() { return Pick(7); }
            """);

        Assert.Contains("Pick)(7, 1)", generated);
    }

    [Fact]
    public void OmittingConstructorDefaultUsesTheDefaultAtTheCreationSite()
    {
        var generated = GenerateSource("""
            public class Box {
                public int Value;
                public constructor(int value = 9) { Value = value; }
            }
            int Run() { Box box = new Box(); return box.Value; }
            """);

        Assert.Contains(", 9)", generated);
    }

    [Fact]
    public void OmittingMethodDefaultUsesTheValueAtTheCallSite()
    {
        var generated = GenerateSource("""
            public class Calculator {
                public int Add(int value = 6) { return value + 1; }
            }
            int Run() { Calculator calculator = new Calculator(); return calculator.Add(); }
            """);

        Assert.Contains(", 6)", generated);
    }

    [Fact]
    public void NullDefaultIsAcceptedForReferenceParameters()
    {
        var generated = GenerateSource("""
            public class Item { public constructor() {} }
            Item Read(Item value = null) { return value; }
            Item Run() { return Read(); }
            """);

        Assert.Contains("Read)(CX_NULL)", generated);
    }

    [Fact]
    public void DefaultLiteralUsesTheParameterTypeAndIsInsertedAtTheCallSite()
    {
        var generated = GenerateSource("""
            int Read(int value = default) { return value; }
            int Run() { return Read(); }
            """);

        Assert.Contains("Read)(((cx_int){ 0 })", generated);
    }

    [Fact]
    public void DefaultLiteralZeroInitializesStructParameters()
    {
        var generated = GenerateSource("""
            public struct Point { public int X; }
            Point Read(Point value = default) { return value; }
            Point Run() { return Read(); }
            """);

        Assert.Contains("{ 0 }", generated);
    }

    [Fact]
    public void DefaultLiteralUsesTheDeclaredLocalType()
    {
        var generated = GenerateSource("""
            int Run() { int value = default; return value; }
            """);

        Assert.Contains("((cx_int){ 0 })", generated);
    }

    [Fact]
    public void DefaultLiteralRequiresAContextualType()
    {
        var project = CreateProject("""
            int Run() { var value = default; return 0; }
            """);

        var error = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(project));

        Assert.Contains("The default literal requires a target type", error.Message);
    }

    [Fact]
    public void EnumMemberDefaultsAreResolvedAndInsertedAtCallSites()
    {
        var generated = GenerateSource("""
            public enum Shade { Dark, Light }
            Shade Read(Shade value = Shade.Light) { return value; }
            Shade Run() { return Read(); }
            """);

        Assert.Contains("Read)(CX_ID_3(default_parameter_test, Shade, Light)", generated);
    }

    [Fact]
    public void EnumMemberDefaultsApplyToConstructors()
    {
        var generated = GenerateSource("""
            public enum Shade { Dark, Light }
            public class Lamp {
                public Shade Value;
                public constructor(Shade value = Shade.Light) { Value = value; }
            }
            Lamp Run() { return new Lamp(); }
            """);

        Assert.Contains("CX_ID_3(default_parameter_test, Shade, Light)", generated);
    }

    [Fact]
    public void ConstructorInitializerCanOmitBaseConstructorDefaults()
    {
        var generated = GenerateSource("""
            public class Base {
                public int Value;
                public constructor(int value = 4) { Value = value; }
            }
            public class Derived : Base {
                public constructor() : base() { }
            }
            int Run() { Derived value = new Derived(); return value.Value; }
            """);

        Assert.Contains("__base, 4)", generated);
    }

    [Fact]
    public void RejectsOmissionOfRequiredTrailingParameter()
    {
        var project = CreateProject("""
            int Add(int left = 5, int right) { return left + right; }
            """);

        var error = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(project));

        Assert.Contains("Required parameter 'right' cannot follow a parameter with a default value", error.Message);
    }

    [Fact]
    public void RejectsDefaultValuesThatDoNotMatchTheParameterType()
    {
        var project = CreateProject("""
            int Read(string value = 3) { return 0; }
            """);

        var error = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(project));

        Assert.Contains("Default value '3' cannot be assigned to parameter 'value'", error.Message);
    }

    [Fact]
    public void RejectsIntegralLiteralDefaultsOutsideTheParameterRange()
    {
        var project = CreateProject("""
            short Read(short value = 32768) { return value; }
            """);

        var error = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(project));

        Assert.Contains("Default value '32768' cannot be assigned to parameter 'value'", error.Message);
    }

    private static CxProject CreateProject(string source)
    {
        var project = CxProject.CreateDefaultApplicationProject("default_parameter_test");
        project.AddCompilationContext(CompilerTestHelper.Parse(source));
        return project;
    }

    private static string GenerateSource(string source)
    {
        var project = CreateProject(source);
        new SemanticBinder().Bind(project);
        var directory = Path.Combine(Path.GetTempPath(), $"default-parameter-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            return File.ReadAllText(Path.Combine(directory, "default_parameter_test.c"));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}

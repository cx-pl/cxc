using CxCompiler.Model.Project;
using CxCompiler.OutputGenerators;
using CxCompiler.Semantics;

namespace CxCompiler.Tests;

public sealed class ReflectionMetadataTests
{
    [Fact]
    public void EmitsFieldsFunctionsConstructorsPropertiesAndParameters()
    {
        var source = Generate("""
            public enum Mode { Off, On }
            public interface IValue { int Read(int scale); }
            public class Sample : IValue {
                private int _value;
                public static int Total;
                public Mode Mode;
                public constructor(int value) { _value = value; }
                public virtual int Read(int scale) { return _value * scale; }
                public int Value { const get { return _value; } set(value) { _value = value; } }
            }
            """);

        Assert.Contains("static const struct cx_reflection_field CX_ID_3(unnamed, Sample, __reflection_fields)[]", source);
        Assert.Contains("offsetof(struct CX_ID_2(unnamed, Sample), _value)", source);
        Assert.Contains("CX_REFLECTION_NO_OFFSET", source);
        Assert.Contains("static const struct cx_reflection_parameter CX_ID_3(unnamed, Sample, __reflection_params_", source);
        Assert.Contains("CX_REFLECTION_FLAG_FUNCTION_CONSTRUCTOR", source);
        Assert.Contains("CX_REFLECTION_FLAG_FUNCTION_PROPERTY_GET", source);
        Assert.Contains("CX_REFLECTION_FLAG_FUNCTION_PROPERTY_SET", source);
        Assert.Contains("CX_REFLECTION_FLAG_VIRTUAL", source);
        Assert.Contains("\"scale\"", source);
        Assert.Contains("&CX_ID_3(unnamed, Mode, __typeinfo)", source);
        Assert.Contains("CX_REFLECTION_FLAG_TYPE_ENUM", source);
        Assert.Contains(".RuntimeFieldCount = 3", source);
        Assert.Contains(".RuntimeFunctionCount = 4", source);
    }

    [Fact]
    public void EmitsInheritedInterfacesNestedTypesAndGenericArity()
    {
        var source = Generate("""
            public interface IBase {}
            public interface IDerived : IBase {}
            public class Outer<T> {
                public class Nested : IDerived {}
            }
            """);

        Assert.Contains("CX_ID_3(unnamed, Outer, __typeinfo)", source);
        Assert.Contains(".GenericArity = 1", source);
        Assert.Contains("CX_REFLECTION_FLAG_TYPE_GENERIC", source);
        Assert.Contains("CX_ID_4(unnamed, Outer, Nested, __typeinfo)", source);
        Assert.Contains("CX_ID_4(unnamed, Outer, Nested, __interfaces)", source);
        Assert.Contains("&CX_ID_3(unnamed, IBase, __typeinfo)", source);
        Assert.Contains("&CX_ID_3(unnamed, IDerived, __typeinfo)", source);
    }

    private static string Generate(string input)
    {
        var project = CxProject.CreateDefaultApplicationProject();
        project.AddCompilationContext(CompilerTestHelper.Parse(input));
        new SemanticBinder().Bind(project);
        var outputDirectory = Path.Combine(Path.GetTempPath(), $"cxc-tests-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(outputDirectory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(outputDirectory, "Reflection.cx"));
            return File.ReadAllText(Path.Combine(outputDirectory, "unnamed.c"));
        }
        finally
        {
            if (Directory.Exists(outputDirectory)) Directory.Delete(outputDirectory, recursive: true);
        }
    }
}

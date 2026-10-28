using CxCompiler.Model.Errors;
using CxCompiler.Model.Expressions;
using CxCompiler.Model.Project;
using CxCompiler.Model.Statements;
using CxCompiler.OutputGenerators;
using CxCompiler.Semantics;

namespace CxCompiler.Tests;

public sealed class RuntimeTypeTests
{
    [Fact]
    public void BindsCheckedCastsAndTypeTests()
    {
        var project = CreateProject(Source);

        new SemanticBinder().Bind(project);

        var functions = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.ClassDeclaration>()
            .Single(type => type.Name == "Program")
            .MemberDeclarations.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .ToDictionary(function => function.Name);
        Assert.IsType<CastExpression>(GetReturnExpression(functions["Down"]));
        Assert.IsType<CastExpression>(GetReturnExpression(functions["FromInterface"]));
        Assert.IsType<CastExpression>(GetReturnExpression(functions["ToInterface"]));
        Assert.IsType<ReturnStatement>(functions["StructToInterface"].Body!.Single());
        Assert.IsType<CastExpression>(GetReturnExpression(functions["ToDerivedInterface"]));
        Assert.IsType<TypeTestExpression>(GetReturnExpression(functions["IsDerived"]));
        Assert.IsType<TypeTestExpression>(GetReturnExpression(functions["IsDerivedInterface"]));
    }

    [Fact]
    public void EmitsRuntimeMetadataCheckedCastsAndTypeTests()
    {
        var project = CreateProject(Source);
        new SemanticBinder().Bind(project);

        var source = GenerateSource(project);

        Assert.Contains("static const struct cx_interface_impl unnamed_Derived___interfaces[]", source);
        Assert.Contains("static const struct cx_runtime_type_info unnamed_Derived___runtime_type_info", source);
        Assert.Equal(1, source.Split("static const struct cx_runtime_type_info unnamed_Derived___runtime_type_info").Length - 1);
        Assert.Contains(".interfaces = (const struct cx_interface_impl*)unnamed_Derived___interfaces", source);
        Assert.Contains(".fields = CX_NULL", source);
        Assert.Contains("struct cxcore_System_Reflection_TypeInfo unnamed_Derived___typeinfo", source);
        Assert.Contains(".RuntimeTypeInfo = (cx_ptr)&unnamed_Derived___runtime_type_info", source);
        Assert.DoesNotContain(".RuntimeFields =", source);
        Assert.DoesNotContain(".RuntimeFieldCount =", source);
        Assert.DoesNotContain(".RuntimeFunctions =", source);
        Assert.DoesNotContain(".RuntimeFunctionCount =", source);
        Assert.DoesNotContain(".RuntimeInterfaces =", source);
        Assert.DoesNotContain(".RuntimeInterfaceCount =", source);
        Assert.Contains("CX_BEGIN_INTERFACE_VTABLE_DEF(unnamed_Derived___iface_IDerived___vtable, unnamed_Derived)", source);
        Assert.Contains(
            "{ &unnamed_IBase___typeinfo, unnamed_Value___iface_IBase___vtable },",
            source);
        Assert.Contains("cx_checked_cast_object((cx_ptr)(value), &unnamed_Derived___typeinfo)", source);
        Assert.Contains("cx_checked_cast_interface(value, &unnamed_Derived___typeinfo)", source);
        Assert.Contains("cx_checked_cast_object_to_interface((cx_ptr)(value), &unnamed_IBase___typeinfo)", source);
        Assert.Contains("cx_box_value((const void*)&((struct unnamed_Value[])", source);
        Assert.Contains("cx_checked_cast_interface_to_interface(value, &unnamed_IDerived___typeinfo)", source);
        Assert.Contains("cx_is_object((cx_ptr)(value), &unnamed_Derived___typeinfo)", source);
        Assert.Contains("cx_is_interface(value, &unnamed_IDerived___typeinfo)", source);
        Assert.Contains("(struct unnamed_Derived*)CX_NULL", source);
    }

    [Theory]
    [InlineData(
        "public final class Left {} public final class Right {} public static class Program { public static Right Convert(Left value) { return (Right)value; } }",
        "Cannot explicitly convert")]
    [InlineData(
        "public final class Value {} public interface IValue {} public static class Program { public static bool Test(Value value) { return value is IValue; } }",
        "can never succeed")]
    public void RejectsImpossibleRuntimeConversions(string source, string expectedMessage)
    {
        var exception = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(CreateProject(source)));

        Assert.Contains(expectedMessage, exception.Message);
    }

    private const string Source = """
        public interface IBase { int BaseValue(); }
        public interface IDerived : IBase { int DerivedValue(); }
        public class Base { public constructor() {} }
        public class Derived : Base, IDerived {
            public int BaseValue() { return 1; }
            public int DerivedValue() { return 2; }
            public constructor() {}
        }
        public struct Value : IBase { public int BaseValue() { return 7; } }
        public static class Program {
            public static Derived Down(Base value) { return (Derived)value; }
            public static Derived FromInterface(IBase value) { return (Derived)value; }
            public static IBase ToInterface(Base value) { return (IBase)value; }
            public static IBase StructToInterface(Value value) { return value; }
            public static IDerived ToDerivedInterface(IBase value) { return (IDerived)value; }
            public static bool IsDerived(Base value) { return value is Derived; }
            public static bool IsDerivedInterface(IBase value) { return value is IDerived; }
            public static Derived NullCast() { return (Derived)null; }
        }
        """;

    private static ExpressionBase GetReturnExpression(
        CxCompiler.Model.Types.FunctionDeclaration function)
    {
        return ((ReturnStatement)function.Body!.Single()).Expression!;
    }

    private static CxProject CreateProject(string source)
    {
        var project = CxProject.CreateDefaultApplicationProject();
        project.AddCompilationContext(CompilerTestHelper.Parse(source));
        return project;
    }

    private static string GenerateSource(CxProject project)
    {
        var outputDirectory = Path.Combine(Path.GetTempPath(), $"cxc-tests-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(outputDirectory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(outputDirectory, "RuntimeTypes.cx"));
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

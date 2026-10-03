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

        Assert.Contains("static const struct cx_interface_impl CX_ID_3(unnamed, Derived, __interfaces)[]", source);
        Assert.Contains("static const struct cx_runtime_type_info CX_ID_3(unnamed, Derived, __runtime_type_info)", source);
        Assert.Equal(1, source.Split("static const struct cx_runtime_type_info CX_ID_3(unnamed, Derived, __runtime_type_info)").Length - 1);
        Assert.Contains(".interfaces = (const struct cx_interface_impl*)CX_ID_3(unnamed, Derived, __interfaces)", source);
        Assert.Contains(".fields = CX_NULL", source);
        Assert.Contains("struct CX_ID_4(cxcore, System, Reflection, TypeInfo) CX_ID_3(unnamed, Derived, __typeinfo)", source);
        Assert.Contains(".RuntimeTypeInfo = (cx_ptr)&CX_ID_3(unnamed, Derived, __runtime_type_info)", source);
        Assert.DoesNotContain(".RuntimeFields =", source);
        Assert.DoesNotContain(".RuntimeFieldCount =", source);
        Assert.DoesNotContain(".RuntimeFunctions =", source);
        Assert.DoesNotContain(".RuntimeFunctionCount =", source);
        Assert.DoesNotContain(".RuntimeInterfaces =", source);
        Assert.DoesNotContain(".RuntimeInterfaceCount =", source);
        Assert.Contains("CX_BEGIN_INTERFACE_VTABLE_DEF(CX_ID_5(unnamed, Derived, __iface, IDerived, __vtable), CX_ID_2(unnamed, Derived))", source);
        Assert.Contains("cx_checked_cast_object((cx_ptr)(value), &CX_ID_3(unnamed, Derived, __typeinfo))", source);
        Assert.Contains("cx_checked_cast_interface(value, &CX_ID_3(unnamed, Derived, __typeinfo))", source);
        Assert.Contains("cx_checked_cast_object_to_interface((cx_ptr)(value), &CX_ID_3(unnamed, IBase, __typeinfo))", source);
        Assert.Contains("cx_checked_cast_interface_to_interface(value, &CX_ID_3(unnamed, IDerived, __typeinfo))", source);
        Assert.Contains("cx_is_object((cx_ptr)(value), &CX_ID_3(unnamed, Derived, __typeinfo))", source);
        Assert.Contains("cx_is_interface(value, &CX_ID_3(unnamed, IDerived, __typeinfo))", source);
        Assert.Contains("(struct CX_ID_2(unnamed, Derived)*)CX_NULL", source);
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
        public static class Program {
            public static Derived Down(Base value) { return (Derived)value; }
            public static Derived FromInterface(IBase value) { return (Derived)value; }
            public static IBase ToInterface(Base value) { return (IBase)value; }
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

using Antlr4.Runtime;
using CxCompiler.Grammar;
using CxCompiler.Model.Errors;
using CxCompiler.Model.Expressions;
using CxCompiler.Model.Project;
using CxCompiler.Model.Statements;
using CxCompiler.Model.Types;
using CxCompiler.Model.Types.BuiltInTypes;
using CxCompiler.OutputGenerators;
using CxCompiler.Semantics;

namespace CxCompiler.Tests;

public sealed class KeywordExpressionTests
{
    [Fact]
    public void AsUsesRelationalPrecedenceAndProducesSafeCast()
    {
        var project = Bind("""
            public class Value { }
            bool Matches(object value) { return value as Value != null; }
            int? Unbox(object value) { return value as int?; }
            """);
        var matches = Functions(project).Single(f => f.Name == "Matches");
        var comparison = Assert.IsType<BinaryExpression>(Assert.IsType<ReturnStatement>(matches.Body![0]).Expression);
        Assert.True(Assert.IsType<CastExpression>(comparison.Left).IsSafe);
        var output = Emit(project);
        Assert.Contains("cx_safe_cast_object(", output);
        Assert.Contains("cx_safe_unbox_nullable(", output);
    }

    [Fact]
    public void AsSupportsBothInterfaceDirections()
    {
        var output = Emit(Bind("""
            public interface IValue { }
            public interface IOther { }
            public class Value : IValue { }
            IValue FromObject(object value) { return value as IValue; }
            Value FromInterface(IValue value) { return value as Value; }
            IOther FromOther(IValue value) { return value as IOther; }
            """));
        Assert.Contains("cx_safe_cast_object_to_interface(", output);
        Assert.Contains("cx_safe_cast_interface(", output);
        Assert.Contains("cx_safe_cast_interface_to_interface(", output);
    }

    [Theory]
    [InlineData("int Bad(object value) { return value as int; }")]
    [InlineData("decimal Bad(object value) { return value as decimal; }")]
    [InlineData("int Bad(int value) { return value as long; }")]
    [InlineData("int Bad(int value) { return value as int; }")]
    [InlineData("decimal Bad(decimal value) { return value as decimal; }")]
    public void AsRejectsNonNullableValueTargets(string source) =>
        Assert.Contains("Cannot use 'as'", Assert.Throws<CompilationErrorException>(() => Bind(source)).Message);

    [Fact]
    public void NameofResolvesNamesWithoutEmittingOperandEvaluation()
    {
        var output = Emit(Bind("""
            public class Example {
                public int Field;
                public static int Method() { return 1; }
                public static int Method(int x) { return x; }
            }
            public enum Color { Red }
            Example Create() { return new Example(); }
            string Parameter(int value) { return nameof(value); }
            string Field() { return nameof(Create().Field); }
            string Method() { return nameof(Example.Method); }
            string Type() { return nameof(Example); }
            string InstanceMember() { return nameof(Example.Field); }
            string EnumMember() { return nameof(Color.Red); }
            string Namespace() { return nameof(System.Reflection); }
            """));
        Assert.Contains("\"value\"", output);
        Assert.Contains("\"Field\"", output);
        Assert.Contains("\"Method\"", output);
        Assert.Contains("\"Example\"", output);
        Assert.Contains("\"Red\"", output);
        // The only occurrence is Create's definition, never a nameof call site.
        Assert.Equal(1, output.Split("unnamed_Create(").Length - 1);
    }

    [Theory]
    [InlineData("string Bad() { return nameof(missing); }")]
    [InlineData("string Bad(int value) { return nameof(value.Missing); }")]
    public void NameofRejectsUnresolvedNames(string source) =>
        Assert.Throws<CompilationErrorException>(() => Bind(source));

    [Fact]
    public void TypeofReturnsExistingRuntimeTypeInfo()
    {
        var output = Emit(Bind("""
            public class Example { }
            System.Reflection.TypeInfo Number() { return typeof(int); }
            System.Reflection.TypeInfo UserType() { return typeof(Example); }
            System.Reflection.TypeInfo Nothing() { return typeof(void); }
            System.Reflection.TypeInfo DecimalType() { return typeof(decimal); }
            """));
        Assert.Contains("(*&cxcore_System_Int___typeinfo)", output);
        Assert.Contains("(*&unnamed_Example___typeinfo)", output);
        Assert.Contains("(*&cxcore_System_Void___typeinfo)", output);
        Assert.Contains("(*&cxcore_System_Decimal___typeinfo)", output);
    }

    [Fact]
    public void DecimalLiteralsArithmeticAndConversionsUseDecimalRuntime()
    {
        var project = Bind("""
            decimal Calculate(decimal value) {
                decimal sum = 0.1m + 0.2M;
                sum += value;
                sum++;
                return sum / 3m;
            }
            decimal FromInt(long value) { return value; }
            decimal FromDouble(double value) { return (decimal)value; }
            int ToInt(decimal value) { return (int)value; }
            double ToDouble(decimal value) { return (double)value; }
            bool Compare(decimal left, decimal right) { return left == right; }
            decimal Maximum() { return 79228162514264337593543950335m; }
            decimal Tiny() { return 1e-28m; }
            """);
        Assert.IsType<DecimalType>(Functions(project).First().ReturnType);
        var output = Emit(project);
        Assert.Contains("cx_decimal_add(", output);
        Assert.Contains("cx_decimal_add_assign(", output);
        Assert.Contains("cx_decimal_increment(", output);
        Assert.Contains("cx_decimal_divide(", output);
        Assert.Contains("cx_decimal_compare(", output);
        Assert.Contains("cx_decimal_from_i64(", output);
        Assert.Contains("cx_decimal_from_double(", output);
        Assert.Contains("cx_decimal_to_i64(", output);
        Assert.Contains("cx_decimal_to_double(", output);
        Assert.Contains("4294967295u, 4294967295u, 4294967295u, 0u", output);
    }

    [Fact]
    public void TypeofClosesGenericTypeParameters()
    {
        var output = Emit(Bind("""
            System.Reflection.TypeInfo Describe<T>() { return typeof(T); }
            System.Reflection.TypeInfo IntType() { return Describe<int>(); }
            System.Reflection.TypeInfo DecimalType() { return Describe<decimal>(); }
            """));
        Assert.Contains("(*&cxcore_System_Int___typeinfo)", output);
        Assert.Contains("(*&cxcore_System_Decimal___typeinfo)", output);
    }

    [Fact]
    public void NameofCanBeUsedAsAConstantDefaultArgument()
    {
        var output = Emit(Bind("""
            string Label(int value, string label = nameof(value)) { return label; }
            string Use() { return Label(1); }
            """));
        Assert.Contains("\"value\"", output);
        Assert.DoesNotContain("nameof(", output);
    }

    [Fact]
    public void AsSupportsClassConstrainedGenericTargets()
    {
        var output = Emit(Bind("""
            public class Example { }
            T Try<T>(object value) where T : class { return value as T; }
            Example Use(object value) { return Try<Example>(value); }
            """));
        Assert.Contains("cx_safe_cast_object(", output);
        Assert.Contains("&unnamed_Example___typeinfo", output);
    }

    [Fact]
    public void AsSupportsNullableStructConstrainedGenericTargets()
    {
        var output = Emit(Bind("""
            T? Try<T>(object value) where T : struct { return value as T?; }
            decimal? Use(object value) { return Try<decimal>(value); }
            """));
        Assert.Contains("cx_safe_unbox_nullable(", output);
    }

    [Fact]
    public void TypeofArrayNullableTypesAndEqualityUseRuntimeMetadata()
    {
        var output = Emit(Bind("""
            bool Same() { return typeof(int) == typeof(int); }
            System.Reflection.TypeInfo ArrayType() { return typeof(int[][]); }
            System.Reflection.TypeInfo NullableType() { return typeof(decimal?); }
            """));
        Assert.Contains(".Hash ==", output);
        Assert.Contains("cx_typeof_array(cx_typeof_array(", output);
        Assert.Contains("cx_typeof_nullable(", output);
    }

    [Theory]
    [InlineData("decimal Bad() { return 79228162514264337593543950336m; }")]
    [InlineData("decimal Bad(decimal value) { return value + 0.5; }")]
    [InlineData("decimal Bad(double value) { return value; }")]
    [InlineData("decimal Bad(decimal value) { return value & 1m; }")]
    public void DecimalRejectsOverflowAndInvalidImplicitConversions(string source) =>
        Assert.Throws<CompilationErrorException>(() => Bind(source));

    [Theory]
    [InlineData("out")]
    [InlineData("ref")]
    public void RemovedKeywordsBecomeIdentifiers(string text)
    {
        var lexer = new CxLexer(new AntlrInputStream(text));
        Assert.Equal(CxLexer.Identifier, lexer.NextToken().Type);
        Bind($"int {text}(int value) {{ return value; }}");
    }

    private static CxProject Bind(string source)
    {
        var project = CxProject.CreateDefaultApplicationProject();
        project.AddCompilationContext(CompilerTestHelper.Parse(source));
        new SemanticBinder().Bind(project);
        return project;
    }

    private static IEnumerable<FunctionDeclaration> Functions(CxProject project) =>
        project.CompilationContexts.Single().DeclarationScope.Declarations.OfType<FunctionDeclaration>();

    private static string Emit(CxProject project)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"cxc-keywords-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            return File.ReadAllText(Path.Combine(directory, "unnamed.c"));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}

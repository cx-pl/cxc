using CxCompiler.Model.Project;
using CxCompiler.OutputGenerators;
using CxCompiler.Semantics;

namespace CxCompiler.Tests;

public sealed class ReflectionValuesTests
{
    [Fact]
    public void BoxesAndUnboxesValuesAndGeneratesValueReflectionInvokers()
    {
        var project = CxProject.CreateDefaultApplicationProject("reflection_value_boxing");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public object Box(int value) { return value; }
            public int Unbox(object value) { return (int)value; }
            public object[] BoxArray(int value) {
                object[] values = new object[1];
                values[0] = value;
                return values;
            }
            public class ReflectionValueTarget {
                public long Add(int left, long right) { return left + right; }
                public long Echo(long value) { return value; }
            }
            public struct ReflectionValueRecord {
                public int Value;
                public constructor(int value) { Value = value; }
                public int Read(int offset) const { return Value + offset; }
                public void Add(int amount) { Value += amount; }
            }
            """));
        new SemanticBinder().Bind(project);

        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-reflection-value-boxing-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(
                Path.Combine(directory, "reflection_value_boxing.c"));
            Assert.Contains("cx_box_value(", source);
            Assert.Contains("cx_unbox_value(", source);
            Assert.Equal(7, source.Split("cx_box_value(").Length - 1);
            Assert.Contains("cx_unbox_value(__arguments[0]", source);
            Assert.Contains("cx_unbox_value(__arguments[1]", source);
            Assert.Contains("cx_unbox_value(__target", source);
            Assert.Contains("cx_unbox_value(__target", source);
            Assert.Contains("sizeof(cx_int)", source);
            Assert.DoesNotContain("_unknowntype_", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void EmitsInvokersForConcreteExpressionMethodsOnClosedGenericStructs()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_reflection_expression");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public struct GenericRecord<T> {
                public T Value;
                public T OtherValue;
                public int Offset;
                public int Other;
                public constructor(T value, int offset) {
                    Value = value;
                    Offset = offset;
                }
                public int Adjust(int amount) { return Offset + amount; }
                public void Update(int amount) {
                    Offset = amount;
                    Other = Offset + 1;
                }
                public void ResetValue() { Value = default; }
                public void SetValueFromSecond(T ignored, T value) { Value = value; }
                public void SetPair(T first, T second) {
                    Value = second;
                    OtherValue = first;
                }
            }
            public object Create() { return new GenericRecord<int>(3, 17); }
            """));
        new SemanticBinder().Bind(project);

        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-generic-reflection-expression-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(
                Path.Combine(directory, "generic_reflection_expression.c"));

            Assert.Contains("__instance->Offset + amount", source, StringComparison.Ordinal);
            Assert.Contains("cx_box_value(&__cx_value", source, StringComparison.Ordinal);
            Assert.Contains("__instance->Other", source, StringComparison.Ordinal);
            Assert.Contains(
                "__instance->Value = ((cx_int){ 0 });",
                source,
                StringComparison.Ordinal);
            Assert.Contains(
                "cx_unbox_value(__arguments[1]",
                source,
                StringComparison.Ordinal);
            Assert.Contains(
                "cx_unbox_value(__arguments[0]",
                source,
                StringComparison.Ordinal);
            Assert.Contains(
                "__instance->OtherValue = __cx_value_1;",
                source,
                StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}

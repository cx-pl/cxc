using CxCompiler.Model.Project;
using CxCompiler.OutputGenerators;
using CxCompiler.Semantics;

namespace CxCompiler.Tests;

public sealed class ClosedGenericStructPropertyExpressionTests
{
    [Fact]
    public void ClosedGenericStructSupportsConcreteExpressionPropertySetter()
    {
        var project = CxProject.CreateDefaultApplicationProject(
            "closed_generic_struct_property_setter_expression");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public struct Counter<T> {
                public int Count;
                public constructor(int count) { Count = count; }
                public int Adjusted { set { Count = value + 1; } }
            }
            public int Run() {
                Counter<int> counter = new Counter<int>(0);
                counter.Adjusted = 41;
                return counter.Count;
            }
            """));

        new SemanticBinder().Bind(project);

        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-closed-generic-struct-setter-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(
                Path.Combine(directory, "closed_generic_struct_property_setter_expression.c"));

            Assert.Contains("Count", source);
            Assert.Contains("+ 1", source);
            Assert.DoesNotContain("_unknowntype_", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ClosedGenericStructSupportsConcreteExpressionSetterThroughConstructedInterface()
    {
        var project = CxProject.CreateDefaultApplicationProject(
            "closed_generic_struct_generic_interface_setter_expression");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public interface IAdjustedCounter<T> {
                int Adjusted { set; }
                int Read() const;
            }
            public struct Counter<T> : IAdjustedCounter<T> {
                public int Count;
                public constructor(int count) { Count = count; }
                public int Adjusted { set { Count = value + 1; } }
                public int Read() const { return Count; }
            }
            public int Run() {
                Counter<int> source = new Counter<int>(0);
                IAdjustedCounter<int> counter = (IAdjustedCounter<int>)source;
                counter.Adjusted = 41;
                return counter.Read();
            }
            """));

        new SemanticBinder().Bind(project);

        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-closed-generic-struct-interface-setter-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(Path.Combine(directory,
                "closed_generic_struct_generic_interface_setter_expression.c"));

            Assert.Contains("Count", source);
            Assert.Contains("+ 1", source);
            Assert.DoesNotContain("_unknowntype_", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ClosedGenericStructSupportsGenericResultConditionalPropertyGetter()
    {
        var project = CxProject.CreateDefaultApplicationProject(
            "closed_generic_struct_generic_property_expression");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public struct OptionalValue<T> {
                public bool HasValue;
                public T Payload;
                public T Fallback;
                public constructor(bool hasValue, T payload, T fallback) {
                    HasValue = hasValue;
                    Payload = payload;
                    Fallback = fallback;
                }
                public T Value { const get { return HasValue ? Payload : default; } }
                public T ValueOrFallback { const get { return HasValue ? Payload : Fallback; } }
            }
            public int Run() {
                OptionalValue<int> present = new OptionalValue<int>(true, 23, 5);
                OptionalValue<int> absent = new OptionalValue<int>(false, 99, 7);
                return present.Value + absent.Value + present.ValueOrFallback +
                    absent.ValueOrFallback;
            }
            """));

        new SemanticBinder().Bind(project);

        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-closed-generic-struct-generic-property-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(
                Path.Combine(directory, "closed_generic_struct_generic_property_expression.c"));

            Assert.Contains("Payload", source);
            Assert.DoesNotContain("_unknowntype_", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ClosedGenericStructSupportsGenericResultConditionalGetterThroughConstructedInterface()
    {
        var project = CxProject.CreateDefaultApplicationProject(
            "closed_generic_struct_generic_interface_property_expression");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public interface IOptionalValue<T> {
                T Value { const get; }
                T Copied { const get; }
                T ConditionalCopy { const get; }
                T FieldSelectionCopy { const get; }
                T BooleanExpressionCopy { const get; }
            }
            public struct OptionalValue<T> : IOptionalValue<T> {
                public bool HasValue;
                public bool Ready;
                public T Payload;
                public T Fallback;
                public constructor(bool hasValue, T payload, T fallback) {
                    HasValue = hasValue;
                    Ready = true;
                    Payload = payload;
                    Fallback = fallback;
                }
                public T Value { const get { return HasValue ? Payload : Fallback; } }
                public T Copied { const get { T copy = Payload; return copy; } }
                public T ConditionalCopy {
                    const get { T copy = HasValue && Ready ? Payload : default; return copy; }
                }
                public T FieldSelectionCopy {
                    const get { var copy = HasValue ? Payload : Fallback; return copy; }
                }
                public T BooleanExpressionCopy {
                    const get { T copy = !HasValue || !Ready ? Payload : default; return copy; }
                }
            }
            public int Run() {
                OptionalValue<int> present = new OptionalValue<int>(true, 23, 5);
                IOptionalValue<int> boxed = (IOptionalValue<int>)present;
                return boxed.Value + boxed.Copied + present.Copied +
                    boxed.ConditionalCopy + present.ConditionalCopy +
                    boxed.FieldSelectionCopy + present.FieldSelectionCopy +
                    boxed.BooleanExpressionCopy + present.BooleanExpressionCopy;
            }
            """));

        new SemanticBinder().Bind(project);

        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-closed-generic-struct-generic-interface-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(Path.Combine(directory,
                "closed_generic_struct_generic_interface_property_expression.c"));

            Assert.Contains("Payload", source);
            Assert.Contains("Fallback", source);
            Assert.DoesNotContain("_unknowntype_", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ClosedGenericStructSupportsConditionalGenericResultMethodThroughConstructedInterface()
    {
        var project = CxProject.CreateDefaultApplicationProject(
            "closed_generic_struct_generic_interface_method_expression");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public interface IOptionalValue<T> { T Read(bool usePayload) const; }
            public struct OptionalValue<T> : IOptionalValue<T> {
                public bool HasValue;
                public T Payload;
                public T Fallback;
                public constructor(bool hasValue, T payload, T fallback) {
                    HasValue = hasValue;
                    Payload = payload;
                    Fallback = fallback;
                }
                public T Read(bool usePayload) const {
                    return usePayload ? Payload : Fallback;
                }
            }
            public int ReadOptionalThroughConstraint<U>(U value, bool usePayload)
                where U : IOptionalValue<int> {
                return value.Read(usePayload);
            }
            public int Run() {
                OptionalValue<int> present = new OptionalValue<int>(true, 23, 5);
                IOptionalValue<int> value = (IOptionalValue<int>)present;
                return value.Read(true) + present.Read(false) +
                    ReadOptionalThroughConstraint(present, true);
            }
            """));

        new SemanticBinder().Bind(project);

        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-closed-generic-struct-generic-interface-method-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(Path.Combine(directory,
                "closed_generic_struct_generic_interface_method_expression.c"));

            Assert.Contains("Payload", source);
            Assert.Contains("Fallback", source);
            Assert.DoesNotContain("_unknowntype_", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ClosedGenericStructSupportsConcreteExpressionPropertyGetter()
    {
        var project = CxProject.CreateDefaultApplicationProject(
            "closed_generic_struct_property_expression");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public struct Counter<T> {
                public int Count;
                public constructor(int count) { Count = count; }
                public int Score { const get { return Count + 1; } }
            }
            public int Run() {
                Counter<int> counter = new Counter<int>(0);
                return counter.Score;
            }
            """));

        new SemanticBinder().Bind(project);

        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-closed-generic-struct-property-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(
                Path.Combine(directory, "closed_generic_struct_property_expression.c"));

            Assert.Contains("Count + 1", source);
            Assert.DoesNotContain("_unknowntype_", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}

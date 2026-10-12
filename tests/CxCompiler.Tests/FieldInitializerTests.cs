using CxCompiler.Model.Errors;
using CxCompiler.Model.Project;
using CxCompiler.OutputGenerators;
using CxCompiler.Semantics;

namespace CxCompiler.Tests;

public sealed class FieldInitializerTests
{
    [Fact]
    public void EmitsAnInstanceFieldInitializerExpressionInTheConstructor()
    {
        var project = CreateProject("""
            public int CreateInitialValue() { return 40 + 2; }
            public class Counter {
                public int Value = CreateInitialValue();
                public constructor() {}
                public int Read() { return Value; }
            }
            """);
        var directory = Path.Combine(Path.GetTempPath(), $"cxc-field-init-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(Path.Combine(directory, "field_initializer.c"));
            Assert.Contains(
                "__this->Value = CX_ID_2(field_initializer, CreateInitialValue)();",
                source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void AllowsDefaultInitializerOnClosedGenericValueField()
    {
        var project = CreateProject("""
            public struct GenericDefault<T> {
                public T Value = default;
                public constructor() {}
            }
            public GenericDefault<int> Create() {
                return new GenericDefault<int>();
            }
            """);
        var directory = Path.Combine(Path.GetTempPath(), $"cxc-generic-field-default-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(Path.Combine(directory, "field_initializer.c"));

            Assert.Contains("__constructor", source);
            Assert.DoesNotContain("__this->Value = CX_NULL", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void AllowsDefaultInitializerOnConstructedGenericReferenceField()
    {
        var project = CreateProject("""
            public class GenericChild<T> { public T Value; }
            public class GenericHolder<T> {
                public GenericChild<T> Child = default;
                public constructor() {}
            }
            public GenericHolder<int> Create() {
                return new GenericHolder<int>();
            }
            """);
        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-generic-constructed-field-default-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(Path.Combine(directory, "field_initializer.c"));

            Assert.Contains("GenericHolder", source);
            Assert.DoesNotContain("__this->Child =", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void AllowsDefaultInitializerOnConstructedGenericValueField()
    {
        var project = CreateProject("""
            public struct GenericChildValue<T> {
                public T Value;
                public constructor() {}
            }
            public struct GenericHolderValue<T> {
                public GenericChildValue<T> Child = default;
                public constructor() {}
            }
            public GenericHolderValue<int> Create() {
                return new GenericHolderValue<int>();
            }
            """);
        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-generic-constructed-value-field-default-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(Path.Combine(directory, "field_initializer.c"));

            Assert.Contains("GenericHolderValue", source);
            Assert.DoesNotContain("__this->Child =", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void EmitsDynamicStaticFieldInitializersInTheModuleInitializer()
    {
        var project = CreateProject("""
            public int CreateInitialValue() { return 42; }
            public static class Values {
                public static int Value = CreateInitialValue();
                public static int Next = Values.Value + 1;
            }
            public int Main() { return Values.Next; }
            """);
        var directory = Path.Combine(Path.GetTempPath(), $"cxc-static-field-init-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(Path.Combine(directory, "field_initializer.c"));

            Assert.Contains("static _Thread_local cx_bool __cx_module_initializing_", source);
            Assert.Contains("__cx_module_init_", source);
            Assert.Contains(" = CX_ID_2(field_initializer, CreateInitialValue)();", source);
            Assert.True(source.IndexOf("CreateInitialValue)();", StringComparison.Ordinal) <
                source.LastIndexOf("__cx_module_initialized_", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void RunsStaticConstructorAfterStaticFieldsAndRetainsInstanceConstructorGeneration()
    {
        var project = CreateProject("""
            public int CreateInitialValue() { return 40 + 2; }
            public class StaticState {
                public static int Value = CreateInitialValue();
                public static int Runs;
                static constructor() {
                    Value += 1;
                    Runs += 1;
                }
            }
            public int Main() {
                StaticState value = new StaticState();
                if (StaticState.Value == 43 && StaticState.Runs == 1 && value != null) {
                    return 42;
                }
                return 0;
            }
            """);
        var directory = Path.Combine(Path.GetTempPath(), $"cxc-static-ctor-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(Path.Combine(directory, "field_initializer.c"));
            var initializer = source.IndexOf(
                "CreateInitialValue)();", StringComparison.Ordinal);
            var staticConstructorCall = source.IndexOf(
                "StaticState, __constructor", Math.Max(initializer, 0), StringComparison.Ordinal);
            var initialized = source.IndexOf(" = CX_TRUE;", staticConstructorCall,
                StringComparison.Ordinal);

            Assert.True(initializer >= 0);
            Assert.True(staticConstructorCall > initializer);
            Assert.True(initialized > staticConstructorCall);
            Assert.Contains("StaticState, __constructor, _2", source);
            Assert.DoesNotContain(
                "CX_REFLECTION_FLAG_STATIC | CX_REFLECTION_FLAG_FUNCTION_CONSTRUCTOR", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void RejectsStaticConstructorsWithParameters()
    {
        var error = Assert.Throws<CompilationErrorException>(() => CreateProject("""
            public class StaticState {
                static constructor(int value) {}
            }
            """));

        Assert.Contains("must be the only static constructor", error.Message);
    }

    [Fact]
    public void RejectsMultipleStaticConstructors()
    {
        var error = Assert.Throws<CompilationErrorException>(() => CreateProject("""
            public class StaticState {
                static constructor() {}
                static constructor() {}
            }
            """));

        Assert.Contains("only one static constructor", error.Message);
    }

    [Fact]
    public void RejectsAccessModifiersAndConstructorInitializersOnStaticConstructors()
    {
        var accessError = Assert.Throws<CompilationErrorException>(() => CreateProject("""
            public class StaticState {
                public static constructor() {}
            }
            """));
        var initializerError = Assert.Throws<CompilationErrorException>(() => CreateProject("""
            public class StaticState : BaseState {
                static constructor() : base() {}
            }
            public class BaseState { public constructor() {} }
            """));

        Assert.Contains("no access modifier", accessError.Message);
        Assert.Contains("initializer", initializerError.Message);
    }

    private static CxProject CreateProject(string source)
    {
        var project = CxProject.CreateDefaultApplicationProject("field_initializer");
        project.AddCompilationContext(CompilerTestHelper.Parse(source));
        new SemanticBinder().Bind(project);
        return project;
    }
}

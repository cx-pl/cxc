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
                "__this->Value = field_initializer_CreateInitialValue();",
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
            public GenericHolder<string> Create() {
                return new GenericHolder<string>();
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

            Assert.Contains("static struct cx_runtime_once __cx_module_once_", source);
            Assert.Contains("__cx_module_init_", source);
            Assert.Contains(" = field_initializer_CreateInitialValue();", source);
            Assert.True(source.IndexOf("CreateInitialValue();", StringComparison.Ordinal) <
                source.LastIndexOf("cx_runtime_once_complete", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void EmitsDynamicStaticArrayFieldInitializersInTheModuleInitializer()
    {
        var project = CreateProject("""
            public static class StaticArrays {
                public static int[] Values = new int[3];
                public static string[] Names = new string[0];
            }
            public int Main() {
                return StaticArrays.Values.Length == 3u &&
                    StaticArrays.Names.Length == 0u ? 42 : 0;
            }
            """);
        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-static-array-field-init-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project,
                Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(Path.Combine(directory,
                "field_initializer.c"));
            var header = File.ReadAllText(Path.Combine(directory,
                "field_initializer.h"));

            Assert.Contains("cx_array_new", source);
            Assert.Contains("CX_FIELD_INITIALIZER_DATA_API ", source);
            Assert.Contains("CX_FIELD_INITIALIZER_DATA_API extern ", header);
            var lastArrayAllocation = source.LastIndexOf(
                "cx_array_new", StringComparison.Ordinal);
            var moduleInitialized = source.LastIndexOf(
                "cx_runtime_once_complete", StringComparison.Ordinal);
            Assert.True(lastArrayAllocation >= 0 && lastArrayAllocation < moduleInitialized);
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
                "CreateInitialValue();", StringComparison.Ordinal);
            var staticConstructorCall = source.IndexOf(
                "StaticState___constructor", Math.Max(initializer, 0), StringComparison.Ordinal);
            var initialized = source.IndexOf("cx_runtime_once_complete", staticConstructorCall,
                StringComparison.Ordinal);

            Assert.True(initializer >= 0);
            Assert.True(staticConstructorCall > initializer);
            Assert.True(initialized > staticConstructorCall);
            Assert.Contains("StaticState___constructor__2", source);
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

    [Fact]
    public void AllowsParameterlessObjectCreationInClosedGenericReferenceFieldInitializer()
    {
        var project = CreateProject("""
            public class Seed { public int Value; public constructor() { Value = 17; } }
            public class GenericHolder<T> {
                public Seed SeedValue = new Seed();
                public constructor(Seed seed) { SeedValue = seed; }
                public Seed Read() { return SeedValue; }
            }
            public GenericHolder<Seed> Create(Seed seed) {
                return new GenericHolder<Seed>(seed);
            }
            """);
        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-generic-field-object-init-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            var owner = project.GenericTypeInstances.Single(instance =>
                instance.Declaration.Name == "GenericHolder");
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(Path.Combine(directory, "field_initializer.c"));

            var fieldInitializer = source.IndexOf(
                "__this->SeedValue =", StringComparison.Ordinal);
            var constructorAssignment = source.LastIndexOf(
                "__this->SeedValue =", StringComparison.Ordinal);
            Assert.True(fieldInitializer >= 0);
            Assert.True(constructorAssignment > fieldInitializer);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void AllowsClosedGenericObjectCreationInClosedGenericReferenceFieldInitializer()
    {
        var project = CreateProject("""
            public class Seed {
                public static int Created;
                public constructor() { Created += 1; }
            }
            public class GenericSeed<T> {
                public Seed Marker = new Seed();
                public T Value;
                public constructor(T value) { Value = value; }
            }
            public class GenericHolder<T> {
                public GenericSeed<int> SeedValue = new GenericSeed<int>(17);
                public constructor(GenericSeed<int> seed) { SeedValue = seed; }
            }
            public GenericHolder<int> Create(GenericSeed<int> seed) {
                return new GenericHolder<int>(seed);
            }
            """);
        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-generic-field-constructed-object-init-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(Path.Combine(directory, "field_initializer.c"));

            var fieldInitializer = source.IndexOf(
                "__this->SeedValue =", StringComparison.Ordinal);
            var constructorAssignment = source.LastIndexOf(
                "__this->SeedValue =", StringComparison.Ordinal);
            Assert.True(fieldInitializer >= 0);
            Assert.True(constructorAssignment > fieldInitializer);
            Assert.Contains("GenericSeed", source);
            Assert.Contains("17", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ClosesGenericObjectCreationFromClosedGenericOwnerFieldInitializer()
    {
        var project = CreateProject("""
            public class Marker {
                public static int Created;
                public constructor() { Created += 1; }
            }
            public class GenericSeed<T> {
                public Marker MarkerValue = new Marker();
                public T Value;
            }
            public class GenericHolder<T> {
                public GenericSeed<T> SeedValue = new GenericSeed<T>();
                public constructor(GenericSeed<T> seed) { SeedValue = seed; }
            }
            public GenericHolder<int> CreateInt(GenericSeed<int> seed) {
                return new GenericHolder<int>(seed);
            }
            public GenericHolder<long> CreateLong(GenericSeed<long> seed) {
                return new GenericHolder<long>(seed);
            }
            """);
        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-generic-field-open-object-init-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(Path.Combine(directory, "field_initializer.c"));

            Assert.Contains("GenericSeed", source);
            Assert.Contains("CreateInt", source);
            Assert.Contains("CreateLong", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ClosedGenericFieldInitializersCanReadEarlierConcreteFields()
    {
        var project = CreateProject("""
            public class ValueReader {
                public int Value = 17;
                public int Add(int amount) { return Value + amount; }
            }
            public class GenericHolder<T> {
                public ValueReader Reader = new ValueReader();
                public int MethodResult = Reader.Add(3);
                public int Seed = 17;
                public int Copy = Seed + 1;
                public constructor() {}
            }
            public GenericHolder<string> Create() {
                return new GenericHolder<string>();
            }
            """);
        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-generic-field-reference-init-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(Path.Combine(directory, "field_initializer.c"));

            var seedInitializer = source.IndexOf("__this->Seed =", StringComparison.Ordinal);
            var copyInitializer = source.IndexOf("__this->Copy =", StringComparison.Ordinal);
            var readerInitializer = source.IndexOf("__this->Reader =", StringComparison.Ordinal);
            var methodInitializer = source.IndexOf("__this->MethodResult =", StringComparison.Ordinal);
            Assert.True(seedInitializer >= 0);
            Assert.True(copyInitializer > seedInitializer);
            Assert.True(readerInitializer >= 0);
            Assert.True(methodInitializer > readerInitializer);
            Assert.Contains("__this->Seed + 1", source);
            Assert.Contains("Add", source);

            var invalidProject = CreateProject("""
                public class GenericHolder<T> {
                    public int Copy = Seed + 1;
                    public int Seed = 17;
                    public constructor() {}
                }
                public GenericHolder<int> Create() {
                    return new GenericHolder<int>();
                }
                """);
            var error = Assert.Throws<CompilationErrorException>(() =>
                CCodeOutputGenerator.GenerateOutput(
                    invalidProject, Path.Combine(directory, "invalid.cx")));
            Assert.Contains("Closed value layout", error.Message);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ConstructedGenericFieldInitializerArgumentsCanReadEarlierConcreteFields()
    {
        var project = CreateProject("""
            public class GenericSeed<T> {
                public T Value;
                public constructor(T value) { Value = value; }
            }
            public class GenericHolder<T> {
                public int Seed = 40;
                public GenericSeed<int> SeedValue = new GenericSeed<int>(Seed + 2);
                public constructor() {}
            }
            public GenericHolder<int> Create() {
                return new GenericHolder<int>();
            }
            """);
        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-generic-field-constructor-expression-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(Path.Combine(directory, "field_initializer.c"));

            var seedInitializer = source.IndexOf("__this->Seed = 40", StringComparison.Ordinal);
            var valueInitializer = source.IndexOf("__this->SeedValue =", StringComparison.Ordinal);
            Assert.True(seedInitializer >= 0);
            Assert.True(valueInitializer > seedInitializer);
            Assert.Contains("__this->Seed + 2", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ClosedGenericFieldCanInitializeConstructedGenericStruct()
    {
        var project = CreateProject("""
            public struct GenericPair<T> {
                public T Value;
                public constructor(T value) { Value = value; }
            }
            public class GenericHolder<T> {
                public int Seed = 40;
                public GenericPair<int> Pair = new GenericPair<int>(Seed + 2);
                public constructor() {}
            }
            public GenericHolder<int> CreateInt() {
                return new GenericHolder<int>();
            }
            public GenericHolder<long> CreateLong() {
                return new GenericHolder<long>();
            }
            """);
        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-generic-field-constructed-struct-init-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(Path.Combine(directory, "field_initializer.c"));

            Assert.Contains("GenericPair", source);
            Assert.Contains("__this->Seed + 2", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ClosedGenericFieldCanAllocateConcreteArrayInitializer()
    {
        var project = CreateProject("""
            public class GenericHolder<T> {
                public int[] Values = new int[3u];
                public constructor() {}
            }
            public GenericHolder<int> Create() {
                return new GenericHolder<int>();
            }
            """);
        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-generic-field-array-init-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(Path.Combine(directory, "field_initializer.c"));

            Assert.Contains("cx_array_new", source);
            Assert.Contains("3u", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ClosedGenericFieldCanAllocateConcreteArrayFromEarlierFieldExpression()
    {
        var project = CreateProject("""
            public class GenericHolder<T> {
                public uint Capacity = 3u;
                public int[] Values = new int[Capacity + 2u];
                public constructor() {}
            }
            public GenericHolder<int> Create() {
                return new GenericHolder<int>();
            }
            """);
        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-generic-field-array-expression-init-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(Path.Combine(directory, "field_initializer.c"));

            Assert.Contains("cx_array_new", source);
            Assert.Contains("__this->Capacity + 2u", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ClosedGenericConstructorCanAllocateArrayFromLengthExpression()
    {
        var project = CreateProject("""
            public class GenericBuffer<T> {
                public T[] Values;
                public constructor(uint capacity) {
                    Values = new T[capacity + 2u];
                }
            }
            public GenericBuffer<int> Create(uint capacity) {
                return new GenericBuffer<int>(capacity);
            }
            """);
        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-generic-constructor-array-expression-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(Path.Combine(directory, "field_initializer.c"));

            Assert.Contains("cx_array_new", source);
            Assert.Contains("capacity + 2u", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ClosedGenericFieldCanInitializeArrayOfItsTypeParameter()
    {
        var project = CreateProject("""
            public class GenericHolder<T> {
                public uint Capacity = 2u;
                public T[] Values = new T[Capacity + 1u];
                public constructor() {}
            }
            public GenericHolder<int> CreateInt() {
                return new GenericHolder<int>();
            }
            public GenericHolder<long> CreateLong() {
                return new GenericHolder<long>();
            }
            """);
        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-generic-field-typed-array-init-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(Path.Combine(directory, "field_initializer.c"));
            Assert.Contains("__this->Capacity + 1u", source);
            Assert.Contains("sizeof(cx_int)", source);
            Assert.Contains("sizeof(cx_long)", source);
            Assert.DoesNotContain("sizeof(struct void*)", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ClosedGenericStructFieldCanInitializeArrayOfItsTypeParameter()
    {
        var project = CreateProject("""
            public class GenericArrayBox<T> { public T Value; }
            public struct GenericArrayPair<T> {
                public T Value;
                public constructor(T value) { Value = value; }
            }
            public struct GenericBuffer<T> {
                public uint Capacity = 2u;
                public T[] Values = new T[Capacity + 1u];
                public GenericArrayBox<T>[] Boxes = new GenericArrayBox<T>[2u];
                public GenericArrayPair<T>[] Pairs = new GenericArrayPair<T>[2u];
                public constructor() {}
            }
            public GenericBuffer<int> CreateInt() {
                return new GenericBuffer<int>();
            }
            public GenericBuffer<long> CreateLong() {
                return new GenericBuffer<long>();
            }
            """);
        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-generic-struct-field-array-init-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(Path.Combine(directory, "field_initializer.c"));

            Assert.Contains("__this->Capacity + 1u", source);
            Assert.Contains("cx_array_new((cx_uint)((__this->Capacity + 1u)), " +
                "(cx_uint)sizeof(cx_int))", source);
            Assert.Contains("cx_array_new((cx_uint)((__this->Capacity + 1u)), " +
                "(cx_uint)sizeof(cx_long))", source);
            Assert.Equal(6, source.Split("cx_array_new(", StringSplitOptions.None).Length - 1);
            Assert.DoesNotContain("_unknowntype_", source);
            Assert.DoesNotContain("sizeof(struct void*)", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static CxProject CreateProject(string source)
    {
        var project = CxProject.CreateDefaultApplicationProject("field_initializer");
        project.AddCompilationContext(CompilerTestHelper.Parse(source));
        new SemanticBinder().Bind(project);
        return project;
    }
}

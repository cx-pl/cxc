using CxCompiler.Model.Project;
using CxCompiler.OutputGenerators;
using CxCompiler.Semantics;

namespace CxCompiler.Tests;

public sealed class ReflectionMetadataTests
{
    [Fact]
    public void ModuleAnnotationLocationsPreserveCxFileLineAndColumn()
    {
        var source = Generate("""
            @System.AnnotationUsage(targets = "function")
            public class Note : System.Annotation { public constructor() {} }
            public class Target {
                @Note
                public void Run() {}
            }
            """);
        Assert.Contains("UNNAMED___annotation_sources[]", source);
        Assert.Contains("{ \"test.cx\", 4u, 5u }", source);
        Assert.Contains(".annotationSources = UNNAMED___annotation_sources", source);
    }

    [Fact]
    public void CustomExceptionTypesInheritCoreExceptionProperties()
    {
        var source = Generate("""
            public class CustomError : System.Exception {
                public constructor() : base("detail") {}
            }
            public class Reader {
                public string Message(CustomError error) { return error.Message; }
            }
            """);
        Assert.Contains("cxcore_System_Exception_Message___const_get", source);
    }

    [Fact]
    public void ModuleArrayReadersPreserveValueReferenceAndInterfaceElementLayouts()
    {
        var source = Generate("""
            public interface IRow {}
            public class Row : IRow {}
            public class Data {
                public static int[] Numbers() { return new int[0u]; }
                public static Row[] Rows() { return new Row[0u]; }
                public static IRow[] Contracts() { return new IRow[0u]; }
            }
            """);
        Assert.Contains(".arrayReaderCount = 3u", source);
        Assert.Contains("sizeof(cx_int), &cxcore_System_Int___typeinfo, 1u, maximumLength", source);
        Assert.Contains("sizeof(struct unnamed_Row*), &unnamed_Row___typeinfo, 0u, maximumLength", source);
        Assert.Contains("sizeof(struct cx_iface_ref), &unnamed_IRow___typeinfo, 2u, maximumLength", source);
    }

    [Fact]
    public void ClosedReferenceTypesInitializeNonGenericInterfaceMetadataInSharedModules()
    {
        var source = Generate("""
            public interface IRow { object Read(); }
            public class Row<T> : IRow {
                private T _value;
                public constructor(T value) { _value = value; }
                public object Read() { return _value as object; }
            }
            public class Data { public object Create() { return new Row<int>(7); } }
            """);
        Assert.Matches(@"___interfaces\[0\] = \(struct cx_interface_impl\)\{ &unnamed_IRow___typeinfo, cx_generic_", source);
    }

    [Fact]
    public void VariadicAnnotationPoliciesWorkBeforeTheirTypeDeclaration()
    {
        var source = Generate("""
            public class Target { @Bag(1, true, null) @Bag() public void Run() {} }
            @System.AnnotationUsage(targets = "function", repeatable = true, variadic = true)
            public class Bag : System.Annotation { public constructor(object[] values) {} }
            """);
        Assert.Contains("\"unnamed.Bag\"", source);
        Assert.Contains("\"argument0\", \"1\"", source);
        Assert.Contains("cx_bool value", source);
        Assert.Contains(".annotationCount = 3u", source);
    }

    [Fact]
    public void VariadicAnnotationsRejectNamedArguments()
    {
        var error = Assert.Throws<CxCompiler.Model.Errors.CompilationErrorException>(() => Generate("""
            @System.AnnotationUsage(targets = "function", variadic = true)
            public class Bag : System.Annotation { public constructor(object[] values) {} }
            public class Target { @Bag(values = 1) public void Run() {} }
            """));
        Assert.Contains("positional constant arguments only", error.Message);
    }

    [Fact]
    public void VariadicAnnotationPolicyValidatesItsConstructorContract()
    {
        var error = Assert.Throws<CxCompiler.Model.Errors.CompilationErrorException>(() => Generate("""
            @System.AnnotationUsage(variadic = true)
            public class BadBag : System.Annotation { public constructor(int value) {} }
            """));
        Assert.Contains("public constructor taking object[]", error.Message);
    }

    [Fact]
    public void TypedAnnotationValuesPreserveDeclaredWidthsAndEmbeddedNullStrings()
    {
        var source = Generate("""
            @System.AnnotationUsage(targets = "function")
            public class Values : System.Annotation {
                public constructor(sbyte small, ulong big, string text, char control) {}
            }
            public class Target {
                @Values(-8, 18446744073709551615ul, "a\0b", '\b')
                public void Run() {}
            }
            """);
        Assert.Contains("cx_sbyte value =", source);
        Assert.Contains("cx_ulong value =", source);
        Assert.Contains("18446744073709551615ULL", source);
        Assert.Contains("0x61, 0x00, 0x62", source);
        Assert.Contains("&cxcore_System_SByte___typeinfo", source);
        Assert.Contains("&cxcore_System_ULong___typeinfo", source);
        Assert.Contains("cx_char value = (cx_char)(8ULL)", source);
    }

    [Fact]
    public void AnnotationFloatConstantsRejectOverflowAfterConversion()
    {
        var error = Assert.Throws<CxCompiler.Model.Errors.CompilationErrorException>(() => Generate("""
            @System.AnnotationUsage(targets = "function")
            public class Note : System.Annotation { public constructor(float value) {} }
            public class Target { @Note(3.5e38) public void Run() {} }
            """));
        Assert.Contains("outside float range", error.Message);
    }

    [Fact]
    public void ModuleAnnotationsRetainResolvedIdentityAndBoundDefaults()
    {
        var source = Generate("""
            @System.AnnotationUsage(targets = "function")
            public class Note : System.Annotation {
                public constructor(string text, int priority = 7) {}
            }
            public class Target {
                @Note("example")
                public void Run() {}
            }
            """);
        Assert.Contains(".formatVersion = 1u", source);
        Assert.Contains("\"unnamed.Note\"", source);
        Assert.Contains("\"function\", \"Run\"", source);
        Assert.Contains("{ \"priority\", \"7\",", source);
        Assert.Contains("{ \"text\", \"\\\"example\\\"\",", source);
        Assert.Contains("&unnamed_Target___typeinfo, 0, -1", source);
        Assert.Contains(".annotationCount = 2u", source);
    }

    [Fact]
    public void ModuleMetadataPreservesNoEscapeCallContract()
    {
        var source = Generate("""
            import System.Interop;
            public class Inspector {
                @NoEscape public extern void Inspect(object value);
            }
            """);

        Assert.Contains("cxcore.System.Interop.NoEscape", source);
        Assert.Contains("\"function\", \"Inspect\"", source);
        Assert.Contains("CX_REFLECTION_FLAG_FUNCTION_NO_ESCAPE", source);
    }

    [Fact]
    public void ReflectionFunctionEffectFlagsIncludeInheritedNoEscapeContract()
    {
        var source = Generate("""
            import System.Interop;
            public class Reader {
                @NoEscape public virtual void Read(object value) {}
            }
            public class SpecializedReader : Reader {
                public override void Read(object value) {}
            }
            """);

        // Two synthetic core comparer members plus the declared slot and override.
        Assert.Equal(4, source.Split("CX_REFLECTION_FLAG_FUNCTION_NO_ESCAPE").Length - 1);
    }

    [Fact]
    public void CurrentModuleCallsAndMethodValuesUseTheCallingModuleDescriptor()
    {
        var source = Generate("""
            import System.Reflection;
            public class Modules {
                public ModuleInfo Read() { return ModuleInfo.Current(); }
                public delegate() => ModuleInfo Factory() { return ModuleInfo.Current; }
            }
            """);
        Assert.Contains("cx_module_info_wrap(__cx_module_info_UNNAMED())", source);
        Assert.DoesNotContain("cxcore_System_Reflection_ModuleInfo_Current(", source);
    }

    [Fact]
    public void ModuleDescriptorListsOnlyPublicTypesInStableOrder()
    {
        var source = Generate("""
            public class Visible {
                private Hidden _value;
                public class Nested {}
            }
            class Hidden { public class Inner {} }
            public interface IVisible {}
            public enum Mode { Off, On }
            public delegate void Handler(int value);
            """);

        var start = source.IndexOf("UNNAMED___module_types[]", StringComparison.Ordinal);
        Assert.True(start >= 0);
        var end = source.IndexOf("};", start, StringComparison.Ordinal);
        var entries = source[start..end];
        Assert.Contains("&unnamed_Visible___typeinfo", entries);
        Assert.Contains("&unnamed_Visible_Nested___typeinfo", entries);
        Assert.Contains("&unnamed_IVisible___typeinfo", entries);
        Assert.Contains("&unnamed_Mode___typeinfo", entries);
        Assert.Contains("&unnamed_Handler___typeinfo", entries);
        Assert.DoesNotContain("Hidden", entries);
        Assert.Contains(".typeCount = 5u", source);
        Assert.Contains(".referenceCount = 0u", source);
        Assert.True(entries.IndexOf("IVisible", StringComparison.Ordinal) <
            entries.IndexOf("Mode", StringComparison.Ordinal));
        Assert.Contains("return &UNNAMED___module_info;", source);
    }

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

        Assert.Contains("static const struct cx_reflection_field unnamed_Sample___reflection_fields[]", source);
        Assert.Contains("offsetof(struct unnamed_Sample, _value)", source);
        Assert.Contains("CX_REFLECTION_NO_OFFSET", source);
        Assert.Contains("static const struct cx_reflection_parameter unnamed_Sample___reflection_params_", source);
        Assert.Contains("CX_REFLECTION_FLAG_FUNCTION_CONSTRUCTOR", source);
        Assert.Contains("CX_REFLECTION_FLAG_FUNCTION_PROPERTY_GET", source);
        Assert.Contains("CX_REFLECTION_FLAG_FUNCTION_PROPERTY_SET", source);
        Assert.Contains("CX_REFLECTION_FLAG_VIRTUAL", source);
        Assert.Contains("\"scale\"", source);
        Assert.Contains("&unnamed_Mode___typeinfo", source);
        Assert.Contains("CX_REFLECTION_FLAG_TYPE_ENUM", source);
        Assert.Contains(".fieldCount = 3", source);
        Assert.Contains(".functionCount = 4", source);
    }

    [Fact]
    public void ReflectionFieldMetadataRecordsRawPointerAndFunctionValueLayouts()
    {
        var source = Generate("""
            public delegate int Callback(int value);
            public struct Container {
                private ptr _state;
                public Callback _callback;
            }
            """);

        Assert.Contains("CX_REFLECTION_FLAG_FIELD_POINTER", source);
        Assert.Contains("CX_REFLECTION_FLAG_FIELD_FUNCTION_VALUE", source);
        Assert.Contains("sizeof(((struct unnamed_Container*)0)->_state)", source);
        Assert.Contains("sizeof(((struct unnamed_Container*)0)->_callback)", source);
    }

    [Fact]
    public void EmitsInheritedInterfacesNestedTypesAndGenericArity()
    {
        var project = CxProject.CreateDefaultApplicationProject("nested_generic_reflection");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public interface IBase {}
            public interface IDerived : IBase {}
            public class Outer<T> {
                public class Nested : IDerived {}
            }
            public Outer<int>.Nested CreateNested() {
                return new Outer<int>.Nested();
            }
            """));
        new SemanticBinder().Bind(project);
        var nested = Assert.Single(project.GenericTypeInstances);
        var nestedIdentity = nested.Type.ConstructedIdentity!.CIdentifier;
        var outputDirectory = Path.Combine(
            Path.GetTempPath(), $"cxc-nested-generic-reflection-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(outputDirectory);
            CCodeOutputGenerator.GenerateOutput(
                project, Path.Combine(outputDirectory, "input.cx"));
            var source = File.ReadAllText(
                Path.Combine(outputDirectory, "nested_generic_reflection.c"));

            Assert.Contains("nested_generic_reflection_Outer___typeinfo", source);
            Assert.Contains(".GenericArity = 1", source);
            Assert.Contains("CX_REFLECTION_FLAG_TYPE_GENERIC", source);
            Assert.Contains("nested_generic_reflection_Outer_Nested___typeinfo", source);
            Assert.Contains($"{nestedIdentity}___interfaces", source);
            Assert.Contains("&nested_generic_reflection_IBase___typeinfo", source);
            Assert.Contains("&nested_generic_reflection_IDerived___typeinfo", source);
        }
        finally
        {
            if (Directory.Exists(outputDirectory))
            {
                Directory.Delete(outputDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public void AsyncIteratorReflectionCarriesVersionedMoveAndDisposeMethods()
    {
        var project = CxProject.CreateDefaultApplicationProject();
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            namespace System;
            public interface IAsyncDisposable {
                System.Threading.Tasks.ValueTaskHandle DisposeAsync();
            }
            """));
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            namespace System.Collections;
            public interface IAsyncEnumerable<T> {
                IAsyncEnumerator<T> GetAsyncEnumerator(
                    System.Threading.Tasks.CancellationToken cancellationToken);
            }
            public interface IAsyncEnumerator<T> : System.IAsyncDisposable {
                T Current { const get; }
                System.Threading.Tasks.ValueTask<bool> MoveNextAsync();
            }
            """));
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            import System.Collections;
            import System.Threading.Tasks;
            public class Producer {
                public async IAsyncEnumerable<int> Values() { yield return 1; }
            }
            """));
        new SemanticBinder().Bind(project);
        var outputDirectory = Path.Combine(Path.GetTempPath(),
            $"cxc-async-iterator-reflection-{Guid.NewGuid():N}");
        string source;
        string[] generatedFileNames;
        try
        {
            Directory.CreateDirectory(outputDirectory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(outputDirectory, "Async.cx"));
            var generatedFiles = Directory.GetFiles(outputDirectory, "*.c", SearchOption.AllDirectories);
            generatedFileNames = generatedFiles.Select(Path.GetFileName)
                .Where(name => name is not null).Select(name => name!).ToArray();
            source = string.Join(Environment.NewLine, generatedFiles.Select(File.ReadAllText));
        }
        finally
        {
            if (Directory.Exists(outputDirectory)) Directory.Delete(outputDirectory, recursive: true);
        }

        Assert.True(source.Contains("\"Values\"", StringComparison.Ordinal),
            "Generated C files: " + string.Join(", ", generatedFileNames));
        Assert.Contains("\"GetAsyncEnumerator\"", source);
        Assert.Contains("\"MoveNextAsync\"", source);
        Assert.Contains("\"DisposeAsync\"", source);
        Assert.Contains("CX_REFLECTION_FLAG_VISIBILITY_PUBLIC", source);
    }

    [Fact]
    public void EmitsDynamicConstructionFactoryForPublicParameterlessClass()
    {
        var source = Generate("""
            public class PublicCtor {
                public int Value;
                public constructor() { Value = 42; }
            }
            public class PrivateCtor { private constructor() {} }
            public abstract class AbstractType {}
            """);

        Assert.Contains(
            "static cx_ptr unnamed_PublicCtor___reflection_create_instance(void)",
            source);
        Assert.Contains("unnamed_PublicCtor___constructor(__instance);", source);
        Assert.Contains("CX_INIT_VTABLE(__instance, unnamed_PublicCtor);", source);
        Assert.Contains(
            ".createInstance = unnamed_PublicCtor___reflection_create_instance,",
            source);
        Assert.Contains(".createInstance = CX_NULL,", source);
        Assert.DoesNotContain(
            "static cx_ptr unnamed_PrivateCtor___reflection_create_instance",
            source);
        Assert.DoesNotContain(
            "static cx_ptr unnamed_AbstractType___reflection_create_instance",
            source);
    }

    [Fact]
    public void EmitsDynamicConstructionFactoryForNonGenericStruct()
    {
        var source = Generate("""
            public struct Point {
                public int X;
                public int Y;
            }
            """);

        Assert.Contains(
            "static cx_ptr unnamed_Point___reflection_create_instance(void)",
            source);
        Assert.Contains("struct unnamed_Point __instance = { 0 };", source);
        Assert.Contains(
            "return cx_box_value(&__instance, (cx_uint)sizeof(__instance), " +
            "&unnamed_Point___typeinfo);",
            source);
        Assert.Contains(
            ".createInstance = unnamed_Point___reflection_create_instance,",
            source);
    }

    [Fact]
    public void EmitsDynamicConstructionFactoryForClosedGenericStruct()
    {
        var source = Generate("""
            public struct Box<T> {
                public T Value;
                public constructor(T value) { Value = value; }
            }
            public class Client {
                public object Create() {
                    Box<int> value = new Box<int>(1);
                    object boxed = value;
                    object[] arguments = new object[1];
                    arguments[0] = 2;
                    return boxed.Type.CreateInstance(arguments);
                }
            }
            """);

        Assert.Matches(
            @"static cx_ptr cx_generic_[0-9a-f]+___reflection_create_instance\(void\)",
            source);
        Assert.Contains("__instance = { 0 };", source);
        Assert.Contains("cx_box_value(&__instance", source);
        Assert.Matches(
            @"static cx_ptr cx_generic_[0-9a-f]+___reflection_invoke_0",
            source);
        Assert.Contains("cx_unbox_value(__arguments[0]", source);
        Assert.Matches(
            @"\.invokers = cx_generic_[0-9a-f]+___reflection_invokers,",
            source);
        Assert.Matches(
            @"\.createInstance = cx_generic_[0-9a-f]+___reflection_create_instance,",
            source);
    }

    [Fact]
    public void EmitsParameterizedReflectionConstructorInvoker()
    {
        var source = Generate("""
            public class Other {}
            public interface IThing {}
            public class Constructed {
                public Other Value;
                public constructor(Other value) { Value = value; }
            }
            public class InterfaceConstructed {
                public constructor(IThing value) {}
            }
            public struct ValueConstructed {
                public int Value;
                public constructor(int value) { Value = value; }
            }
            public class ReflectionClient {
                public object Create(TypeInfo type, object[] arguments) {
                    return type.CreateInstance(arguments);
                }
            }
            """);

        Assert.Contains(
            "static cx_ptr unnamed_Constructed___reflection_invoke_0", source);
        Assert.Contains("if (__target != CX_NULL) return CX_NULL;", source);
        Assert.Contains("unnamed_Constructed___constructor(__instance", source);
        Assert.Contains("unnamed_InterfaceConstructed___reflection_invoke_0", source);
        Assert.Contains("cx_checked_cast_object_to_interface(__arguments[0]", source);
        Assert.Contains("unnamed_ValueConstructed___reflection_invoke_0", source);
        Assert.Contains("cx_unbox_value(__arguments[0]", source);
        Assert.Contains("return cx_box_value(&__instance", source);
        Assert.Contains("return (cx_ptr)__instance;", source);
        Assert.Contains(
            "cxcore_System_Reflection_TypeInfo_CreateInstance__2", source);
    }

    [Fact]
    public void EmitsReferenceAndVirtualReflectionInvokersAndSkipsUnsupportedMethods()
    {
        var source = Generate("""
            public class Other {}
            public interface IThing {}
            public class InvokeTarget {
                public object Echo(object value) { return value; }
                public void Touch(Other other) { }
                public void UseInterface(IThing value) { }
                public static Other StaticEcho(Other value) { return value; }
                public int ReadNumber() { return 42; }
                public virtual object VirtualEcho(object value) { return value; }
                public constructor() {}
                public IThing ReturnInterface() { return new Thing(); }
            }
            public class Thing : IThing { }
            """);
        Assert.Contains("static cx_ptr unnamed_InvokeTarget___reflection_invoke_0", source);
        Assert.Contains("return (cx_ptr)unnamed_InvokeTarget_Echo(", source);
        Assert.Contains("unnamed_InvokeTarget_Touch(", source);
        Assert.Contains("unnamed_InvokeTarget_StaticEcho(", source);
        Assert.Contains("CX_REFLECTION_FLAG_STATIC", source);
        Assert.Contains(
            "static cx_ptr unnamed_InvokeTarget___reflection_invoke_5", source);
        Assert.Contains("CX_GET_VTABLE(", source);
        Assert.Contains(
            "return (cx_ptr)(unnamed_InvokeTarget_ReturnInterface(",
            source);
        Assert.Contains(
            "static cx_ptr unnamed_InvokeTarget___reflection_invoke_2",
            source);
        Assert.Contains("cx_checked_cast_object_to_interface(__arguments[0]", source);
        Assert.Contains(".invokers = (const cx_reflection_invoker*)unnamed_InvokeTarget___reflection_invokers", source);
        Assert.Contains("CX_NULL,", source);
    }

    [Fact]
    public void BindsObjectTypeAndDynamicConstructionMethod()
    {
        var source = Generate("""
            public class ReflectionClient {
                public object Create() {
                    ReflectionClient value = new ReflectionClient();
                    TypeInfo metadata = value.Type;
                    return metadata.CreateInstance();
                }
            }
            """);

        Assert.Contains(
            "cxcore_System_Object_Type___const_get",
            source);
        Assert.Contains(
            "cxcore_System_Reflection_TypeInfo_CreateInstance",
            source);
    }

    [Fact]
    public void EmitsDynamicConstructionFactoryForClosedReferenceGenericClass()
    {
        var source = Generate("""
            public class Payload {}
            public class Marker<T> { public constructor() {} }
            public Marker<Payload> Create() { return new Marker<Payload>(); }
            """);

        Assert.Matches(
            @"static cx_ptr cx_generic_[0-9a-f]+___reflection_create_instance\(void\)",
            source);
        Assert.Matches(
            @"\.createInstance = cx_generic_[0-9a-f]+___reflection_create_instance,",
            source);
        Assert.Contains(
            "return cx_generic_",
            source);
        Assert.Matches(
            @"return cx_generic_[0-9a-f]+___reflection_invoke_\d+\(CX_NULL, CX_NULL, 0u\);",
            source);
    }

    [Fact]
    public void ClosedGenericClassReflectionInvokerUsesConstructedStructReturnLayout()
    {
        var source = Generate("""
            public struct Pair<T, U> {
                public T Key;
                public U Value;
                public constructor(T key, U value) { Key = key; Value = value; }
            }
            public class Enumerator<T, U> {
                public Pair<T, U> Current() { return default; }
            }
            public Pair<string, int> ReadPair() {
                Enumerator<string, int> enumerator = new Enumerator<string, int>();
                return enumerator.Current();
            }
            """);

        Assert.Matches(
            @"struct cx_generic_[0-9a-f]+ __cx_value_return = cx_generic_[0-9a-f]+\(\(struct cx_generic_[0-9a-f]+\*\)__target\);",
            source);
        Assert.DoesNotContain(
            "struct unnamed_Pair __cx_value_return =",
            source);
    }

    [Fact]
    public void ForwardDeclaresOpenGenericTypeInfoBeforeEarlierGenericReferences()
    {
        var source = Generate("""
            public class Holder<T> { public Provider<T> Value; public constructor() {} }
            public class Provider<T> { public constructor() {} }
            public class Client {
                public object Create() {
                    Holder<int> holder = default;
                    Provider<int> provider = new Provider<int>();
                    return provider;
                }
            }
            """);

        var declaration = "extern struct cxcore_System_Reflection_TypeInfo " +
            "unnamed_Provider___typeinfo;";
        var vtable = "CX_BEGIN_VTABLE_DEF(unnamed_Holder)";
        Assert.True(source.IndexOf(declaration, StringComparison.Ordinal) >= 0);
        Assert.True(source.IndexOf(declaration, StringComparison.Ordinal) <
            source.IndexOf(vtable, StringComparison.Ordinal));
    }

    [Fact]
    public void ClosedGenericReflectionSubstitutionUsesConstructedTypeInfo()
    {
        var project = CxProject.CreateDefaultApplicationProject("closed_generic_reflection_typeinfo");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public class Provider<T> { public constructor() {} }
            public class Holder<T> { public Provider<T> Value; public constructor() {} }
            public class Client {
                public object Create() {
                    Holder<int> holder = default;
                    Provider<int> provider = new Provider<int>();
                    return provider;
                }
            }
            """));
        new SemanticBinder().Bind(project);
        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-closed-generic-reflection-typeinfo-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(Path.Combine(directory,
                "closed_generic_reflection_typeinfo.c"));
            var provider = project.GenericTypeInstances.Single(instance =>
                instance.Type.Name == "Provider" &&
                instance.Type.TypeArguments.Single() is CxCompiler.Model.Types.BuiltInTypes.IntType);
            var providerInfo = $"&{provider.Type.ConstructedIdentity!.CIdentifier}___typeinfo";

            Assert.Contains(providerInfo, source);
            Assert.DoesNotContain("&unnamed_Provider___typeinfo", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
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

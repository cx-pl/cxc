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
        Assert.Contains(".fieldCount = 3", source);
        Assert.Contains(".functionCount = 4", source);
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
            "static cx_ptr CX_ID_3(unnamed, PublicCtor, __reflection_create_instance)(void)",
            source);
        Assert.Contains("CX_ID_3(unnamed, PublicCtor, __constructor)(__instance);", source);
        Assert.Contains("CX_INIT_VTABLE(__instance, CX_ID_2(unnamed, PublicCtor));", source);
        Assert.Contains(
            ".createInstance = CX_ID_3(unnamed, PublicCtor, __reflection_create_instance),",
            source);
        Assert.Contains(".createInstance = CX_NULL,", source);
        Assert.DoesNotContain(
            "static cx_ptr CX_ID_3(unnamed, PrivateCtor, __reflection_create_instance)",
            source);
        Assert.DoesNotContain(
            "static cx_ptr CX_ID_3(unnamed, AbstractType, __reflection_create_instance)",
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
            "static cx_ptr CX_ID_3(unnamed, Point, __reflection_create_instance)(void)",
            source);
        Assert.Contains("struct CX_ID_2(unnamed, Point) __instance = { 0 };", source);
        Assert.Contains(
            "return cx_box_value(&__instance, (cx_uint)sizeof(__instance), " +
            "&CX_ID_3(unnamed, Point, __typeinfo));",
            source);
        Assert.Contains(
            ".createInstance = CX_ID_3(unnamed, Point, __reflection_create_instance),",
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
            @"static cx_ptr CX_ID_2\(cx_generic_[0-9a-f]+, __reflection_create_instance\)\(void\)",
            source);
        Assert.Contains("__instance = { 0 };", source);
        Assert.Contains("cx_box_value(&__instance", source);
        Assert.Matches(
            @"static cx_ptr CX_ID_2\(cx_generic_[0-9a-f]+, __reflection_invoke_0\)",
            source);
        Assert.Contains("cx_unbox_value(__arguments[0]", source);
        Assert.Matches(
            @"\.invokers = CX_ID_2\(cx_generic_[0-9a-f]+, __reflection_invokers\),",
            source);
        Assert.Matches(
            @"\.createInstance = CX_ID_2\(cx_generic_[0-9a-f]+, __reflection_create_instance\),",
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
            "static cx_ptr CX_ID_3(unnamed, Constructed, __reflection_invoke_0)", source);
        Assert.Contains("if (__target != CX_NULL) return CX_NULL;", source);
        Assert.Contains("CX_ID_3(unnamed, Constructed, __constructor)(__instance", source);
        Assert.Contains("CX_ID_3(unnamed, InterfaceConstructed, __reflection_invoke_0)", source);
        Assert.Contains("cx_checked_cast_object_to_interface(__arguments[0]", source);
        Assert.Contains("CX_ID_3(unnamed, ValueConstructed, __reflection_invoke_0)", source);
        Assert.Contains("cx_unbox_value(__arguments[0]", source);
        Assert.Contains("return cx_box_value(&__instance", source);
        Assert.Contains("return (cx_ptr)__instance;", source);
        Assert.Contains(
            "CX_ID_6(cxcore, System, Reflection, TypeInfo, CreateInstance, _2)", source);
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
        Assert.Contains("static cx_ptr CX_ID_3(unnamed, InvokeTarget, __reflection_invoke_0)", source);
        Assert.Contains("return (cx_ptr)CX_ID_3(unnamed, InvokeTarget, Echo)(", source);
        Assert.Contains("CX_ID_3(unnamed, InvokeTarget, Touch)(", source);
        Assert.Contains("CX_ID_3(unnamed, InvokeTarget, StaticEcho)(", source);
        Assert.Contains("CX_REFLECTION_FLAG_STATIC", source);
        Assert.Contains(
            "static cx_ptr CX_ID_3(unnamed, InvokeTarget, __reflection_invoke_5)", source);
        Assert.Contains("CX_GET_VTABLE(", source);
        Assert.Contains(
            "return (cx_ptr)(CX_ID_3(unnamed, InvokeTarget, ReturnInterface)(",
            source);
        Assert.Contains(
            "static cx_ptr CX_ID_3(unnamed, InvokeTarget, __reflection_invoke_2)",
            source);
        Assert.Contains("cx_checked_cast_object_to_interface(__arguments[0]", source);
        Assert.Contains(".invokers = (const cx_reflection_invoker*)CX_ID_3(unnamed, InvokeTarget, __reflection_invokers)", source);
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
            "CX_ID_5(cxcore, System, Object, Type, __const_get)",
            source);
        Assert.Contains(
            "CX_ID_5(cxcore, System, Reflection, TypeInfo, CreateInstance)",
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
            @"static cx_ptr CX_ID_2\(cx_generic_[0-9a-f]+, __reflection_create_instance\)\(void\)",
            source);
        Assert.Matches(
            @"\.createInstance = CX_ID_2\(cx_generic_[0-9a-f]+, __reflection_create_instance\),",
            source);
        Assert.Contains(
            "CX_ID_3(unnamed, Marker, __constructor)(__instance);",
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
            "struct CX_ID_2(unnamed, Pair) __cx_value_return =",
            source);
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

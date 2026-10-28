using CxCompiler.Model.Project;
using CxCompiler.Model.Errors;
using CxCompiler.OutputGenerators;
using CxCompiler.Semantics;
using CxCompiler.Model.Types;

namespace CxCompiler.Tests;

public sealed class CCodeOutputGeneratorTests
{
    [Fact]
    public void EmitsQualifiedNamesDirectlyAndPreservesUnderscores()
    {
        var generatedSource = GenerateSource("""
            namespace Company.Product;
            public class _Service {
                public static int _Value = 42;
                public constructor() { }
                public int Execute() { return _Value; }
                public int Execute(int value) { return value; }
            }
            """);

        Assert.Contains("unnamed_Company_Product__Service__Value = 42", generatedSource);
        Assert.Contains("unnamed_Company_Product__Service___constructor(", generatedSource);
        Assert.Contains("unnamed_Company_Product__Service_Execute(", generatedSource);
        Assert.Contains("unnamed_Company_Product__Service_Execute__2(", generatedSource);
    }

    [Fact]
    public void EmitsQualifiedNamesWithMoreThan32Parts()
    {
        var parts = Enumerable.Range(0, 40).Select(index => $"Namespace{index}").ToArray();
        var generatedSource = GenerateSource($$"""
            namespace {{string.Join('.', parts)}};
            public struct Payload { public int Value; }
            public int Read(Payload value) { return value.Value; }
            """);

        var prefix = "unnamed_" + string.Join('_', parts);
        Assert.Contains($"cx_int {prefix}_Read(", generatedSource);
        Assert.Contains($"struct {prefix}_Payload value", generatedSource);
    }

    [Fact]
    public void EmitsClosedGenericStructFieldAccessWithoutStructCast()
    {
        var generatedSource = GenerateSource("""
            public struct ZInner<T> {
                public T Value;
                public constructor(T initial) { Value = initial; }
            }
            public struct AOuter<T> {
                public ZInner<T> Inner;
                public constructor(ZInner<T> initial) { Inner = initial; }
            }
            public int ReadNested(AOuter<int> value) {
                return value.Inner.Value;
            }
            """);

        var functionStart = generatedSource.IndexOf(
            "unnamed_ReadNested(", StringComparison.Ordinal);
        Assert.True(functionStart >= 0);
        var functionEnd = generatedSource.IndexOf("\n}", functionStart,
            StringComparison.Ordinal);
        Assert.True(functionEnd > functionStart);
        var function = generatedSource[functionStart..functionEnd];
        Assert.True(!function.Contains("((struct cx_generic_", StringComparison.Ordinal),
            function);
    }

    [Fact]
    public void EmitsGenericVirtualSlotsWithoutTypedFunctionPointers()
    {
        var generatedSource = GenerateSource("""
            public class Base {
                public virtual T Echo<T>(T value) { return value; }
            }
            public class Derived : Base {
                public override U Echo<U>(U value) { return value; }
            }
            public int Run() {
                Base value = new Derived();
                return value.Echo(42);
            }
            """);

        Assert.Contains("cx_register_generic_virtual_method", generatedSource);
        Assert.Contains("cx_require_generic_virtual_method", generatedSource);
        Assert.Contains("__cx_generic_virtual_thunk_", generatedSource);
        Assert.Contains("CX_BEGIN_VTABLE_DEF(", generatedSource);
        Assert.True(generatedSource.Split(
            "{ .function = (cx_vtable_function)0 },",
            StringSplitOptions.None).Length >= 3);
    }

    [Fact]
    public void EmitsHelloWorldFunctionBody()
    {
        const string source = """
            import System;

            public void Main() {
                Console.WriteLine("Hello world!");
            }
            """;
        var generatedSource = GenerateSource(source);

        Assert.Contains(
            "CX_STRING_DEF(unnamed___string_",
            generatedSource);
        Assert.Contains("\"Hello world!\");", generatedSource);
        Assert.Contains("void unnamed_Main()", generatedSource);
        Assert.Contains(
            "cxcore_System_Console_WriteLine(&unnamed___string_",
            generatedSource);
    }

    [Fact]
    public void EmitsAllInterfaceReferenceFields()
    {
        var generatedSource = GenerateSource("""
            public interface IValue {}
            public class Value : IValue {}

            public IValue Create() { return new Value(); }
            """);

        Assert.Contains("(struct cx_iface_ref){", generatedSource);
        Assert.Contains(", CX_NULL }", generatedSource);
    }

    [Fact]
    public void EmitsCxCoreObjectBeforeClosedGenericValueLayouts()
    {
        var project = CxProject.CreateDefaultApplicationProject("cxcore");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            namespace System;
            public class Object {}
            public struct InlineValue<T> {
                private T _value;
                public constructor(T value) { _value = value; }
            }
            public int Main() {
                InlineValue<bool> value = new InlineValue<bool>(true);
                return 0;
            }
            """));
        new SemanticBinder().Bind(project);

        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-cxcore-closed-layout-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));

            var header = File.ReadAllText(Path.Combine(directory, "cxcore.internal.h"));
            var objectDefinition = header.IndexOf(
                "CX_TYPE_DEF(cxcore_System_Object) {", StringComparison.Ordinal);
            var closedValueDefinition = header.IndexOf(
                "struct cx_generic_", objectDefinition + 1, StringComparison.Ordinal);

            Assert.True(objectDefinition >= 0);
            Assert.True(closedValueDefinition > objectDefinition);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void EmitsObjectPrefixInOpenGenericStructPlaceholders()
    {
        var project = CxProject.CreateDefaultApplicationProject("cxcore");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            namespace System;
            public class Object {}
            public struct InlineValue<T> {
                private T _value;
                public constructor(T value) { _value = value; }
                public T GetValue() { return _value; }
            }
            public int Main() {
                InlineValue<bool> value = new InlineValue<bool>(true);
                return value.GetValue() ? 1 : 0;
            }
            """));
        new SemanticBinder().Bind(project);

        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-cxcore-open-generic-layout-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));

            var header = File.ReadAllText(Path.Combine(directory, "cxcore.internal.h"));
            var valueStart = header.IndexOf(
                "CX_TYPE_DEF(cxcore_System_InlineValue) {", StringComparison.Ordinal);
            var valueEnd = header.IndexOf("};", valueStart, StringComparison.Ordinal);
            var objectStart = header.IndexOf(
                "CX_TYPE_DEF(cxcore_System_Object) {", StringComparison.Ordinal);

            Assert.True(objectStart >= 0 && objectStart < valueStart);
            Assert.True(valueStart >= 0);
            Assert.True(valueEnd > valueStart);
            Assert.Contains("struct cxcore_System_Object __base;",
                header[valueStart..valueEnd]);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void EmitsClosedGenericStructReturnsThroughInterfaceDispatchByValue()
    {
        var generatedSource = GenerateSource("""
            public struct ValueTaskLike<T> {
                public T Value;
                public constructor(T value) { Value = value; }
            }
            public interface ISequence<T> {
                ValueTaskLike<bool> MoveNextAsync();
            }
            public class Sequence : ISequence<int> {
                public ValueTaskLike<bool> MoveNextAsync() {
                    return new ValueTaskLike<bool>(true);
                }
            }
            public bool Read(ISequence<int> sequence) {
                return sequence.MoveNextAsync().Value;
            }
            """);

        var dispatchLines = string.Join(Environment.NewLine,
            generatedSource.Split('\n').Where(line =>
                line.Contains("slot_1", StringComparison.Ordinal) ||
                line.Contains("MoveNextAsync", StringComparison.Ordinal)));
        var thunkDeclaration = generatedSource.Split('\n').First(line =>
            line.Contains("static ", StringComparison.Ordinal) &&
            line.Contains("slot_1", StringComparison.Ordinal));
        Assert.StartsWith("static struct cx_generic_", thunkDeclaration.Trim());
        Assert.False(thunkDeclaration.TrimStart().StartsWith(
            "static void", StringComparison.Ordinal));
        Assert.Matches(
            "struct cx_generic_[0-9a-f]+ \\(\\*\\)\\(cx_ptr\\)", generatedSource);
    }

    [Fact]
    public void DoesNotEmitUnusedLambdaEnvironmentAlias()
    {
        var generatedSource = GenerateSource("""
            delegate int Transform(int value);

            int Main() {
                int offset = 1;
                Transform transform = value => value + offset;
                return transform(2);
            }
            """);

        Assert.Contains("__cx_lambda_", generatedSource);
        Assert.DoesNotContain("__cx_closure =", generatedSource);
    }

    [Fact]
    public void EmitsDirectReturnAwaitCallbackWithCapturedLocals()
    {
        var generatedSource = GenerateSource("""
            import System.Threading.Tasks;
            async Task<int> Relay(Task<int> source) {
                return await source;
            }
            """);

        Assert.Contains("Relay", generatedSource);
        Assert.Contains("__cx_lambda_", generatedSource);
        Assert.Contains("__cx_environment", generatedSource);
    }

    [Fact]
    public void CastsClosedTaskCompletionSourceToOpenPropertyAbi()
    {
        var generatedSource = GenerateSource("""
            import System.Threading.Tasks;
            Task<int> Read() {
                TaskCompletionSource<int> completion = new TaskCompletionSource<int>();
                return completion.Task;
            }
            """);
        Assert.Contains(
            "TaskCompletionSource_Task___const_get(((struct cxcore_System_Threading_Tasks_TaskCompletionSource*)(&(completion)))",
            generatedSource);
    }

    [Fact]
    public void EmitsSwitchSectionTerminatorForStrictCCompilers()
    {
        var generatedSource = GenerateSource("""
            int Choose(int value) {
                switch (value) {
                    case 0: return 1;
                    default: return 2;
                }
            }
            """);

        Assert.Contains("switch (value)", generatedSource);
        Assert.Contains("break;", generatedSource);
    }

    [Fact]
    [Trait("Area", "Lowering")]
    public void EmitsNativeMainWrapperForCxStringArrayArguments()
    {
        var generatedSource = GenerateSource("""
            public uint Count(string[] args) {
                return args.Length;
            }

            public int Main(string[] args) {
                return 0;
            }
            """);

        Assert.Contains("int wmain(int argc, wchar_t **argv)", generatedSource);
        Assert.Contains("int main(int argc, char **argv)", generatedSource);
        Assert.Contains("cx_array_new(__cx_argument_count", generatedSource);
        Assert.Contains("cxcore_System_String___constructor__2", generatedSource);
        Assert.Contains("cxcore_System_Collections_Array_Length___const_get(args)", generatedSource);
        Assert.Contains("unnamed_Main(__cx_arguments)", generatedSource);
        Assert.Contains("int __cx_exit_code = (int)unnamed_Main(__cx_arguments);", generatedSource);
        Assert.Contains("cx_thread_wait_all();", generatedSource);
        Assert.Contains("return __cx_exit_code;", generatedSource);
    }

    [Fact]
    [Trait("Area", "NegativeDiagnostics")]
    public void RejectsUnsupportedNativeMainSignature()
    {
        var exception = Assert.Throws<CompilationErrorException>(
            () => GenerateSource("public int Main(string argument) { return 0; }"));

        Assert.Contains("must be a non-generic top-level function", exception.Message);
    }

    [Fact]
    [Trait("Area", "NegativeDiagnostics")]
    public void RejectsAsyncNativeMainSignature()
    {
        var exception = Assert.Throws<CompilationErrorException>(() => GenerateSource("""
            import System.Threading.Tasks;
            async Task Main() {}
            """));

        Assert.Contains("must be a non-generic top-level function", exception.Message);
    }

    [Fact]
    public void EmitsSelectedOverloadAndReturnStatement()
    {
        const string source = """
            void Print(int value) {}
            void Print(string value) {}

            string Echo(string value) {
                return value;
            }

            void Main() {
                Print("text");
            }
            """;

        var generatedSource = GenerateSource(source);

        Assert.Contains("unnamed_Print__2(", generatedSource);
        Assert.Contains("return value;", generatedSource);
    }

    [Fact]
    public void EmitsMulticastDelegateCombinationRemovalAndInvocation()
    {
        var generatedSource = GenerateSource("""
            int Main() {
                delegate(int) => int first = value => value + 1;
                delegate(int) => int second = value => value + 2;
                first += second;
                first -= second;
                return first(10);
            }
            """);

        Assert.Contains("__invocation_list", generatedSource);
        Assert.Contains("__cx_delegate_combine_CX_FN_", generatedSource);
        Assert.Contains("__cx_delegate_remove_CX_FN_", generatedSource);
        Assert.Contains("__cx_delegate_invoke_CX_FN_", generatedSource);
        Assert.Contains("__cx_delegate_equal_CX_FN_", generatedSource);
    }

    [Fact]
    public void EmitsAndInvokesNamedDelegateTypes()
    {
        const string source = """
            public delegate int Transform(int value);

            int Main() {
                Transform first = value => value + 1;
                Transform second = value => value + 2;
                first += second;
                bool same = first == second;
                bool isNull = first == null;
                return first(10);
            }
            """;
        var generatedHeader = GenerateOutput(source, "unnamed.h");
        var generatedSource = GenerateSource(source);

        Assert.Contains("typedef CX_FN_", generatedHeader);
        Assert.Contains("unnamed_Transform", generatedHeader);
        Assert.Contains("__cx_delegate_combine_CX_FN_", generatedSource);
        Assert.Contains("__cx_delegate_invoke_CX_FN_", generatedSource);
        Assert.Contains("__cx_delegate_equal_CX_FN_", generatedSource);
        Assert.Contains("__cx_delegate_is_null_CX_FN_", generatedSource);
    }

    [Fact]
    public void KeepsNamedDelegateTypesNominal()
    {
        var exception = Assert.Throws<CompilationErrorException>(() => GenerateSource("""
            delegate int First(int value);
            delegate int Second(int value);

            void Main() {
                First first = value => value;
                Second second = first;
            }
            """));

        Assert.Contains("Cannot initialize local", exception.Message);
    }

    [Fact]
    public void EmitsTypedDelegateInvocationList()
    {
        var generatedSource = GenerateSource("""
            delegate int Transform(int value);

            int Main() {
                Transform first = value => value + 1;
                Transform second = value => value + 2;
                first += second;
                Transform[] handlers = first.GetInvocationList();
                Transform firstHandler = handlers[0];
                Transform secondHandler = handlers[1];
                Transform empty = null;
                Transform[] emptyHandlers = empty.GetInvocationList();
                return handlers.Length == 2 &&
                    firstHandler(10) == 11 && secondHandler(10) == 12 &&
                    handlers[0](10) == 11 && handlers[1](10) == 12 &&
                    emptyHandlers.Length == 0u ? 0 : 1;
            }
            """);

        Assert.Contains("__cx_delegate_get_invocation_list_CX_FN_", generatedSource);
        Assert.Contains("cx_array_at", generatedSource);
    }

    [Fact]
    public void EmitsTypedInvocationListForAnonymousFunctionValues()
    {
        var generatedSource = GenerateSource("""
            int Main() {
                delegate(int) => int first = value => 11;
                delegate(int) => int second = value => 12;
                first += second;
                var handlers = first.GetInvocationList();
                delegate(int) => int empty = default;
                var emptyHandlers = empty.GetInvocationList();
                return handlers.Length == 2u &&
                    handlers[0](10) == 11 && handlers[1](10) == 12 &&
                    emptyHandlers.Length == 0u ? 0 : 1;
            }
            """);

        Assert.Contains("__cx_delegate_get_invocation_list_CX_FN_", generatedSource);
        Assert.Contains("cx_array_at", generatedSource);
    }

    [Fact]
    public void EmitsClosedElementLayoutForGenericArrayGetReturn()
    {
        var project = CxProject.CreateDefaultApplicationProject("cxcore");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            namespace System.Collections;
            public class Array<T> {
                public T Get(uint index) const { return default; }
            }
            """));
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            import System.Collections;
            public T First<T>(Array<T> values) { return values.Get(0u); }

            int Main() {
                long[] values = new long[1];
                return First(values) == 9000000000L ? 0 : 1;
            }
            """));
        new SemanticBinder().Bind(project);
        var outputDirectory = Path.Combine(Path.GetTempPath(), $"cxc-tests-{Guid.NewGuid():N}");
        string generatedSource;
        try
        {
            Directory.CreateDirectory(outputDirectory);
            CCodeOutputGenerator.GenerateOutput(
                project, Path.Combine(outputDirectory, "GenericArray.cx"));
            var sourceFile = Directory.GetFiles(outputDirectory, "*.c", SearchOption.AllDirectories)
                .Single(path => !Path.GetFileName(path).EndsWith(".metadata.c", StringComparison.Ordinal));
            generatedSource = File.ReadAllText(sourceFile);
        }
        finally
        {
            if (Directory.Exists(outputDirectory)) Directory.Delete(outputDirectory, recursive: true);
        }

        Assert.Contains("cx_long*)cx_array_at(values, (cx_uint)(0u), (cx_uint)sizeof(cx_long)", generatedSource);
    }

    [Fact]
    public void EmitsTypedInvocationListsForStaticAndInstanceMethodValues()
    {
        var generatedSource = GenerateSource("""
            public delegate int Transform(int value);
            public int AddOne(int value) { return value + 1; }
            public class Counter {
                public int AddTwo(int value) { return value + 2; }
            }

            int Main() {
                Counter counter = new Counter();
                Transform staticMethod = AddOne;
                Transform instanceMethod = counter.AddTwo;
                FunctionInfo topLevelInfo = staticMethod.Method;
                FunctionInfo methodInfo = instanceMethod.Method;
                int offset = 3;
                Transform closure = value => value + offset;
                FunctionInfo closureInfo = closure.Method;
                Transform[] staticList = staticMethod.GetInvocationList();
                Transform[] instanceList = instanceMethod.GetInvocationList();
                return staticList.Length == 1u && instanceList.Length == 1u &&
                    staticList[0](40) == 41 && instanceList[0](40) == 42 &&
                    staticMethod.Target == null && instanceMethod.Target == (object)counter &&
                    closure.Target != null ? 0 : 1;
            }
            """);

        Assert.Equal(3, generatedSource.Split(
            "__cx_delegate_get_invocation_list_CX_FN_", StringSplitOptions.None).Length - 1);
        Assert.Contains("__cx_delegate_invoke_CX_FN_", generatedSource);
        Assert.Contains(".__target = (cx_ptr)__cx_method_value_", generatedSource);
        Assert.Contains(".__target = CX_NULL", generatedSource);
        Assert.Contains("__cx_delegate_get_target_CX_FN_", generatedSource);
        Assert.Contains(".__method = __cx_function_reflection_", generatedSource);
        Assert.Contains("static struct cx_reflection_function __cx_function_reflection_", generatedSource);
        Assert.Contains(".__method = __cx_function_reflection_", generatedSource);
        Assert.Contains("_get()", generatedSource);
        Assert.Contains("cx_reflection_get_function_info", generatedSource);
    }

    [Fact]
    public void EmitsDynamicInvokeForBoxedArgumentsAndVoidDelegates()
    {
        var generatedSource = GenerateSource("""
            public delegate int Transform(int value);
            public delegate void Setter(int value);
            public class Target {
                public int Value;
                public void SetValue(int value) { Value = value; }
            }

            int Main() {
                Transform transform = value => value + 2;
                object[] arguments = new object[1];
                arguments[0] = 40;
                object result = transform.DynamicInvoke(arguments);
                Target target = new Target();
                Setter setter = target.SetValue;
                object[] setterArguments = new object[1];
                setterArguments[0] = 41;
                object voidResult = setter.DynamicInvoke(setterArguments);
                object[] wrongArguments = new object[1];
                wrongArguments[0] = "wrong type";
                return (int)result == 42 && voidResult == null &&
                    setter.DynamicInvoke(wrongArguments) == null && target.Value == 41 ? 0 : 1;
            }
            """);

        Assert.Contains("__cx_delegate_dynamic_invoke_CX_FN_", generatedSource);
        Assert.Contains("cx_unbox_value", generatedSource);
        Assert.Contains("cx_box_value", generatedSource);
        Assert.Contains("cx_boxed_value_type", generatedSource);
        Assert.Contains("#include \"cxruntime.h\"", generatedSource);
        Assert.Contains("Array_Length___const_get(arguments)", generatedSource);
        Assert.DoesNotContain("arguments->_length", generatedSource);
    }

    [Fact]
    public void EmitsTypedNullForAnonymousFunctionValueStorage()
    {
        var generatedSource = GenerateSource("""
            delegate int Transform(int value);

            int Main() {
                delegate(int) => int empty = null;
                empty = null;
                Transform namedEmpty = null;
                return empty.GetInvocationList().Length == 0u &&
                    namedEmpty.GetInvocationList().Length == 0u ? 0 : 1;
            }
            """);

        Assert.Equal(2, generatedSource.Split(" = (CX_FN_", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void RejectsInvokingAnIndexedNonFunctionValue()
    {
        var exception = Assert.Throws<CompilationErrorException>(() => GenerateSource("""
            int Main() {
                int[] values = new int[1];
                return values[0](1);
            }
            """));

        Assert.Contains("ArrayAccessExpression' cannot be invoked", exception.Message);
    }

    [Fact]
    public void EmitsGenericFunctionParametersAndReturnStorageAsPointers()
    {
        const string source = """
            public struct Optional<T> {
                public static extern Optional<T> Box(T value);
            }
            """;

        var generatedHeader = GenerateOutput(source, "unnamed.h");

        Assert.Contains(
            "extern CX_UNNAMED_API void unnamed_Optional_Box(",
            generatedHeader);
        Assert.Contains("void* value,", generatedHeader);
        Assert.Contains("void* __returnValue", generatedHeader);
        Assert.DoesNotContain("struct void*", generatedHeader);
    }

    [Fact]
    public void SeparatesGeneratedModuleImportsFromItsExports()
    {
        var project = CxProject.CreateDefaultApplicationProject("api_split");
        project.AddCompilationContext(CompilerTestHelper.Parse("public int Run() { return 1; }"));
        new SemanticBinder().Bind(project);
        var directory = Path.Combine(Path.GetTempPath(), $"cxc-api-split-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var header = File.ReadAllText(Path.Combine(directory, "api_split.h"));
            var cmake = File.ReadAllText(Path.Combine(directory, "CMakeLists.txt"));
            var source = File.ReadAllText(Path.Combine(directory, "api_split.c"));

            Assert.Contains("#if defined(CX_API_SPLIT_BUILD)", header);
            Assert.Contains("#define CX_API_SPLIT_API CX_EXPORT", header);
            Assert.Contains("#define CX_API_SPLIT_API CX_IMPORT", header);
            Assert.Contains("extern CX_API_SPLIT_API cx_int api_split_Run()", header);
            Assert.Contains("CXCORE_ABI_VERSION EQUAL 1", cmake);
            Assert.Contains(
                "if(CX_STATIC_LINK AND CMAKE_SYSTEM_NAME STREQUAL \"Linux\")",
                cmake);
            Assert.Contains("set(CMAKE_POSITION_INDEPENDENT_CODE ON)", cmake);
            Assert.Contains("CX_RUNTIME_ABI_VERSION != 1", source);
            Assert.Contains("target_compile_definitions(api_split PRIVATE CX_API_SPLIT_BUILD)", cmake);
            Assert.True(cmake.IndexOf("add_executable(api_split", StringComparison.Ordinal) <
                cmake.IndexOf("target_compile_definitions(api_split", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void EmitsBaseClassLayoutsBeforeDerivedClassLayouts()
    {
        var project = CxProject.CreateDefaultApplicationProject("class_layout_order");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public class Derived : Base {}
            public class Base {}
            """));
        new SemanticBinder().Bind(project);

        var directory = Path.Combine(
            Path.GetTempPath(), $"cxc-class-layout-order-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var header = File.ReadAllText(Path.Combine(directory, "class_layout_order.h"));

            Assert.True(header.IndexOf(
                    "CX_TYPE_DEF(class_layout_order_Base)",
                    StringComparison.Ordinal) <
                header.IndexOf(
                    "CX_TYPE_DEF(class_layout_order_Derived)",
                    StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void IncludesNativeProjectHeadersAndSelectsPlatformSources()
    {
        var project = CxProject.CreateDefaultApplicationProject("native_platforms");
        project.AddCompilationContext(CompilerTestHelper.Parse("public void Run() {}"));
        var directory = Path.Combine(
            Path.GetTempPath(), $"cxc-native-platforms-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.Combine(directory, "include"));
            Directory.CreateDirectory(Path.Combine(directory, "platform", "common"));
            Directory.CreateDirectory(Path.Combine(directory, "platform", "linux"));
            Directory.CreateDirectory(Path.Combine(directory, "platform", "win"));
            Directory.CreateDirectory(Path.Combine(directory, "tests"));
            File.WriteAllText(Path.Combine(directory, "platform", "common", "Shared.c"), "");
            File.WriteAllText(Path.Combine(directory, "platform", "linux", "Linux.c"), "");
            File.WriteAllText(Path.Combine(directory, "platform", "win", "Windows.c"), "");
            File.WriteAllText(Path.Combine(directory, "tests", "NativeTests.c"), "");
            var outputDirectory = Path.Combine(directory, ".obj");
            Directory.CreateDirectory(outputDirectory);
            CCodeOutputGenerator.GenerateOutput(
                project, Path.Combine(outputDirectory, "input.cx"), directory);

            var cmake = File.ReadAllText(Path.Combine(outputDirectory, "CMakeLists.txt"));
            Assert.Contains("target_include_directories(native_platforms PRIVATE", cmake);
            Assert.Contains("platform/common", cmake);
            Assert.Contains("include", cmake);
            Assert.Contains("if(WIN32)", cmake);
            Assert.Contains("if(CMAKE_SYSTEM_NAME STREQUAL \"Linux\")", cmake);
            Assert.Contains("platform/linux/Linux.c", cmake);
            Assert.Contains("platform/win/Windows.c", cmake);
            Assert.DoesNotContain("tests/NativeTests.c", cmake);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void BuildsCxcoreThroughItsExistingRuntimeTarget()
    {
        var project = CxProject.CreateDefaultApplicationProject("cxcore");
        project.AddCompilationContext(CompilerTestHelper.Parse("public void Run() {}"));
        new SemanticBinder().Bind(project);
        var directory = Path.Combine(Path.GetTempPath(), $"cxc-cxcore-cmake-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));

            var cmake = File.ReadAllText(Path.Combine(directory, "CMakeLists.txt"));

            Assert.Contains("if(NOT TARGET cxcore)", cmake);
            Assert.DoesNotContain("add_library(cxcore", cmake);
            Assert.DoesNotContain("target_sources(cxcore", cmake);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void EmitsCurrentSocketSchedulerRuntimeAbiInCxcoreHeader()
    {
        var project = CxProject.CreateDefaultApplicationProject("cxcore");
        project.AddCompilationContext(CompilerTestHelper.Parse("public void Run() {}"));
        new SemanticBinder().Bind(project);
        var directory = Path.Combine(Path.GetTempPath(), $"cxc-cxcore-socket-abi-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));

            var header = File.ReadAllText(Path.Combine(directory, "cxcore.h"));
            Assert.Contains("extern CX_CXCORE_API uint64_t cx_task_scheduler_post_on_socket(", header);
            Assert.Contains(
                "uintptr_t socket_handle, int writable, cx_action continuation,",
                header);
            Assert.Contains("cx_action shutdown_continuation);", header);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void SeparatesPublicApiHeaderFromInternalCompilationHeader()
    {
        var project = CxProject.CreateDefaultApplicationProject("public_api_filter");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            class ApiDependency {}
            class PrivateType {}
            void HiddenFunction() {}
            public int Exported(ApiDependency dependency) { return 1; }
            public class PublicType {
                private int HiddenMethod() { return 2; }
                public int VisibleMethod() { return 3; }
            }
            """));
        new SemanticBinder().Bind(project);

        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-public-api-filter-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));

            var publicHeader = File.ReadAllText(Path.Combine(directory, "public_api_filter.h"));
            var internalHeader = File.ReadAllText(
                Path.Combine(directory, "public_api_filter.internal.h"));
            var source = File.ReadAllText(Path.Combine(directory, "public_api_filter.c"));

            Assert.Contains("public_api_filter_Exported", publicHeader);
            Assert.Contains("public_api_filter_ApiDependency", publicHeader);
            Assert.Contains("public_api_filter_PublicType_VisibleMethod", publicHeader);
            Assert.Contains("#include \"public_api_filter.internal.h\"", publicHeader);
            Assert.DoesNotContain("public_api_filter_HiddenFunction", publicHeader);
            Assert.DoesNotContain("public_api_filter_PrivateType", publicHeader);
            Assert.DoesNotContain("public_api_filter_PublicType_HiddenMethod", publicHeader);

            Assert.Contains("public_api_filter_HiddenFunction", internalHeader);
            Assert.Contains("public_api_filter_PrivateType", internalHeader);
            Assert.Contains("public_api_filter_PublicType_HiddenMethod", internalHeader);
            Assert.Contains("#include \"public_api_filter.internal.h\"", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static string GenerateSource(string source)
    {
        return GenerateOutput(source, "unnamed.c");
    }

    private static string GenerateOutput(string source, string outputFileName)
    {
        var project = CxProject.CreateDefaultApplicationProject();
        project.AddCompilationContext(CompilerTestHelper.Parse(source));
        new SemanticBinder().Bind(project);
        var outputDirectory = Path.Combine(
            Path.GetTempPath(),
            $"cxc-tests-{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(outputDirectory);
            CCodeOutputGenerator.GenerateOutput(
                project,
                Path.Combine(outputDirectory, "HelloWorld.cx"));

            return File.ReadAllText(
                Path.Combine(outputDirectory, outputFileName));
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

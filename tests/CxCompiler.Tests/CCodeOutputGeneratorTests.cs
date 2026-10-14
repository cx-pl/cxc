using CxCompiler.Model.Project;
using CxCompiler.Model.Errors;
using CxCompiler.OutputGenerators;
using CxCompiler.Semantics;

namespace CxCompiler.Tests;

public sealed class CCodeOutputGeneratorTests
{
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
            "CX_STRING_DEF(CX_ID_2(unnamed, __string_",
            generatedSource);
        Assert.Contains("\"Hello world!\");", generatedSource);
        Assert.Contains("void CX_ID_2(unnamed, Main)()", generatedSource);
        Assert.Contains(
            "CX_ID_4(cxcore, System, Console, WriteLine)(&CX_ID_2(unnamed, __string_",
            generatedSource);
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
        Assert.Contains("CX_ID_5(cxcore, System, String, __constructor, _2)", generatedSource);
        Assert.Contains("CX_ID_6(cxcore, System, Collections, Array, Length, __const_get)(args)", generatedSource);
        Assert.Contains("CX_ID_2(unnamed, Main)(__cx_arguments)", generatedSource);
        Assert.Contains("int __cx_exit_code = (int)CX_ID_2(unnamed, Main)(__cx_arguments);", generatedSource);
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

        Assert.Contains("CX_ID_3(unnamed, Print, _2)(", generatedSource);
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
        Assert.Contains("CX_ID_2(unnamed, Transform)", generatedHeader);
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
            "extern CX_UNNAMED_API void CX_ID_3(unnamed, Optional, Box)(",
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
            Assert.Contains("extern CX_API_SPLIT_API cx_int CX_ID_2(api_split, Run)()", header);
            Assert.Contains("CXCORE_ABI_VERSION EQUAL 1", cmake);
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

            Assert.Contains("CX_ID_2(public_api_filter, Exported)", publicHeader);
            Assert.Contains("CX_ID_2(public_api_filter, ApiDependency)", publicHeader);
            Assert.Contains("CX_ID_3(public_api_filter, PublicType, VisibleMethod)", publicHeader);
            Assert.Contains("#include \"public_api_filter.internal.h\"", publicHeader);
            Assert.DoesNotContain("CX_ID_2(public_api_filter, HiddenFunction)", publicHeader);
            Assert.DoesNotContain("CX_ID_2(public_api_filter, PrivateType)", publicHeader);
            Assert.DoesNotContain("CX_ID_3(public_api_filter, PublicType, HiddenMethod)", publicHeader);

            Assert.Contains("CX_ID_2(public_api_filter, HiddenFunction)", internalHeader);
            Assert.Contains("CX_ID_2(public_api_filter, PrivateType)", internalHeader);
            Assert.Contains("CX_ID_3(public_api_filter, PublicType, HiddenMethod)", internalHeader);
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

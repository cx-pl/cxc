using CxCompiler.Model.Project;
using CxCompiler.Model.Errors;
using CxCompiler.OutputGenerators;
using CxCompiler.Semantics;

namespace CxCompiler.Tests;

public sealed class CCodeOutputGeneratorTests
{
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
        Assert.Contains("CX_ID_5(cxcore, System, Array, Length, __const_get)(args)", generatedSource);
        Assert.Contains("CX_ID_2(unnamed, Main)(__cx_arguments)", generatedSource);
        Assert.Contains("return (int)CX_ID_2(unnamed, Main)(__cx_arguments);", generatedSource);
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

            Assert.Contains("#if defined(CX_API_SPLIT_BUILD)", header);
            Assert.Contains("#define CX_API_SPLIT_API CX_EXPORT", header);
            Assert.Contains("#define CX_API_SPLIT_API CX_IMPORT", header);
            Assert.Contains("extern CX_API_SPLIT_API cx_int CX_ID_2(api_split, Run)()", header);
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

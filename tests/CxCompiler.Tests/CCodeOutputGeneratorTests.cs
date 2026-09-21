using CxCompiler.Model.Project;
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
    public void EmitsGenericFunctionParametersAndReturnStorageAsPointers()
    {
        const string source = """
            public struct Optional<T> {
                public static extern Optional<T> Box(T value);
            }
            """;

        var generatedHeader = GenerateOutput(source, "unnamed.h");

        Assert.Contains(
            "extern CX_EXPORT void CX_ID_3(unnamed, Optional, Box)(",
            generatedHeader);
        Assert.Contains("void* value,", generatedHeader);
        Assert.Contains("void* __returnValue", generatedHeader);
        Assert.DoesNotContain("struct void*", generatedHeader);
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

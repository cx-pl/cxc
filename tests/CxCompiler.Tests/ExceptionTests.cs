using CxCompiler.Model.Errors;
using CxCompiler.Model.Project;
using CxCompiler.OutputGenerators;
using CxCompiler.Semantics;

namespace CxCompiler.Tests;

public sealed class ExceptionTests
{
    [Fact]
    public void EmitsThrowCatchFilterRethrowAndFinally()
    {
        const string source = """
            import System;

            void Main() {
                try {
                    throw new Exception();
                }
                catch (Exception exception) when (exception != null) {
                    throw;
                }
                finally {
                    int completed = 1;
                }
            }
            """;

        var generatedSource = GenerateSource(source);

        Assert.Contains("struct cx_exception_frame __cx_finally_frame_", generatedSource);
        Assert.Contains("setjmp(__cx_exception_frame_", generatedSource);
        Assert.Contains("CX_THROW(", generatedSource);
        Assert.Contains(
            "CX_ID_4(cxcore, System, Exception, Matches)(cx_exception_current(), CX_ID_4(cxcore, System, Exception, __typeinfo))",
            generatedSource);
        Assert.Contains("CX_RETHROW();", generatedSource);
        Assert.Contains("cx_int completed = 1;", generatedSource);
        Assert.True(
            generatedSource.IndexOf("CX_RETHROW();", StringComparison.Ordinal) <
            generatedSource.IndexOf("cx_exception_clear();", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("void Main() { throw 1; }", "must derive from 'System.Exception'")]
    [InlineData("void Main() { throw; }", "only be used inside a catch clause")]
    [InlineData("void Main() { try {} catch (int value) {} }", "Catch type")]
    public void RejectsInvalidExceptionUsage(string source, string expectedMessage)
    {
        var project = CxProject.CreateDefaultApplicationProject();
        project.AddCompilationContext(CompilerTestHelper.Parse(source));

        var exception = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(project));

        Assert.Contains(expectedMessage, exception.Message);
    }

    [Fact]
    public void LowersControlTransfersThroughFinally()
    {
        const string source = """
            int ReturnValue() {
                try {
                    return 7;
                }
                finally {
                    int returnCleanup = 1;
                }
            }

            void ExitLoops() {
                while (true) {
                    try {
                        break;
                    }
                    finally {
                        int breakCleanup = 2;
                    }
                }
                for (int index = 0; index < 1; index++) {
                    try {
                        continue;
                    }
                    finally {
                        int continueCleanup = 3;
                    }
                }
            }
            """;

        var generatedSource = GenerateSource(source);

        Assert.Contains("__cx_return_value = 7;", generatedSource);
        Assert.Contains("goto __cx_finally_", generatedSource);
        Assert.Contains("return __cx_return_value;", generatedSource);
        Assert.Contains("cx_int returnCleanup = 1;", generatedSource);
        Assert.Contains("cx_int breakCleanup = 2;", generatedSource);
        Assert.Contains("cx_int continueCleanup = 3;", generatedSource);
        Assert.Contains("goto __cx_break_", generatedSource);
        Assert.Contains("goto __cx_continue_", generatedSource);
    }

    [Fact]
    public void NestedFinallyPropagatesReturnToOuterCleanup()
    {
        const string source = """
            int Main() {
                try {
                    try {
                        return 42;
                    }
                    finally {
                        int innerCleanup = 1;
                    }
                }
                finally {
                    int outerCleanup = 2;
                }
            }
            """;

        var generatedSource = GenerateSource(source);

        Assert.Equal(2, CountOccurrences(generatedSource, "goto __cx_finally_"));
        Assert.Contains("cx_int innerCleanup = 1;", generatedSource);
        Assert.Contains("cx_int outerCleanup = 2;", generatedSource);
    }

    [Fact]
    public void ReturnUnwindsTryAndCatchFramesWithoutFinally()
    {
        const string source = """
            import System;

            int Main() {
                try {
                    return 1;
                }
                catch (Exception exception) {
                    return 2;
                }
            }
            """;

        var generatedSource = GenerateSource(source);

        Assert.Contains("cx_exception_pop(&__cx_exception_frame_", generatedSource);
        Assert.Contains("cx_exception_clear();", generatedSource);
        Assert.Contains("return __cx_return_value;", generatedSource);
    }

    [Fact]
    public void ReturnFromFinallyOverridesPendingReturn()
    {
        const string source = """
            int Main() {
                try {
                    return 1;
                }
                finally {
                    return 2;
                }
            }
            """;

        var generatedSource = GenerateSource(source);

        Assert.Contains("__cx_return_value = 1;", generatedSource);
        Assert.Contains("__cx_return_value = 2;", generatedSource);
        Assert.Contains("if (cx_exception_pending()) cx_exception_clear();", generatedSource);
    }

    [Fact]
    public void SupportsUserDefinedExceptionTypes()
    {
        const string source = """
            import System;

            class CustomException : Exception {
                public constructor() {}
            }

            void Main() {
                try {
                    throw new CustomException();
                }
                catch (CustomException exception) {}
                catch (Exception exception) {}
            }
            """;

        var generatedSource = GenerateSource(source);

        Assert.Contains(
            "CX_ID_4(cxcore, System, Exception, Matches)(cx_exception_current(), CX_ID_3(unnamed, CustomException, __typeinfo))",
            generatedSource);
        Assert.Contains(
            "CX_ID_4(cxcore, System, Exception, Matches)(cx_exception_current(), CX_ID_4(cxcore, System, Exception, __typeinfo))",
            generatedSource);
    }

    private static string GenerateSource(string source)
    {
        var project = CxProject.CreateDefaultApplicationProject();
        project.AddCompilationContext(CompilerTestHelper.Parse(source));
        new SemanticBinder().Bind(project);
        var outputDirectory = Path.Combine(
            Path.GetTempPath(),
            $"cxc-exception-tests-{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(outputDirectory);
            CCodeOutputGenerator.GenerateOutput(
                project,
                Path.Combine(outputDirectory, "Exceptions.cx"));
            return File.ReadAllText(Path.Combine(outputDirectory, "unnamed.c"));
        }
        finally
        {
            if (Directory.Exists(outputDirectory))
            {
                Directory.Delete(outputDirectory, recursive: true);
            }
        }
    }

    private static int CountOccurrences(string value, string needle)
    {
        var count = 0;
        var offset = 0;
        while ((offset = value.IndexOf(needle, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += needle.Length;
        }
        return count;
    }
}

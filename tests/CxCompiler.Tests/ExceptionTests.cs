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

        Assert.Contains("struct cx_exception_frame __cx_finally_frame;", generatedSource);
        Assert.Contains("setjmp(__cx_exception_frame.environment)", generatedSource);
        Assert.Contains("CX_THROW(", generatedSource);
        Assert.Contains("cx_exception_matches(&CX_ID_4(cxcore, System, Exception, __typeinfo))", generatedSource);
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
    [InlineData("int Main() { try { return 1; } finally {} }", "'return' statement")]
    public void RejectsInvalidExceptionUsage(string source, string expectedMessage)
    {
        var project = CxProject.CreateDefaultApplicationProject();
        project.AddCompilationContext(CompilerTestHelper.Parse(source));

        var exception = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(project));

        Assert.Contains(expectedMessage, exception.Message);
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

        Assert.Contains("cx_exception_matches(&CX_ID_3(unnamed, CustomException, __typeinfo))", generatedSource);
        Assert.Contains("cx_exception_matches(&CX_ID_4(cxcore, System, Exception, __typeinfo))", generatedSource);
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
}

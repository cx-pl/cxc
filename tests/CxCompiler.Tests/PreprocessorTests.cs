using CxCompiler;
using CxCompiler.Model.Errors;

namespace CxCompiler.Tests;

public sealed class PreprocessorTests
{
    [Fact]
    public void CommandLineSymbolsSelectBranchesAndIgnoreInactiveInvalidCode()
    {
        WithSource("#if CX_CPU_X64 && !CX_OS_WINDOWS\npublic int Selected() { return 64; }\n#elif CX_OS_WINDOWS\nthis is not CX code\n#else\npublic int Selected() { return 32; }\n#endif\n",
            path =>
            {
                new Compiler().Compile(["-DCX_CPU_X64", "--define=CX_OS_LINUX", path]);
                var generated = File.ReadAllText(Path.Combine(Path.GetDirectoryName(path)!, ".obj", "test.c"));
                Assert.Contains("return 64;", generated);
                Assert.DoesNotContain("return 32;", generated);
            });
    }

    [Fact]
    public void DefineUndefAndNestedConditionalsChooseExpectedBranch()
    {
        WithSource("#define FEATURE\n#if FEATURE\n#undef FEATURE\n#if !FEATURE\npublic int Selected() { return 1; }\n#else\nthis is invalid\n#endif\n#endif\n",
            path =>
            {
                new Compiler().Compile([path]);
                var generated = File.ReadAllText(Path.Combine(Path.GetDirectoryName(path)!, ".obj", "test.c"));
                Assert.Contains("return 1;", generated);
            });
    }

    [Fact]
    public void ErrorDirectiveReportsOriginalSourceLocation()
    {
        WithSource("public int Before() { return 0; }\n#error platform is unsupported\n", path =>
        {
            var error = Assert.Throws<CompilationErrorException>(() => new Compiler().Compile([path]));
            Assert.Contains($"{path}(2,1): error: platform is unsupported", error.Message);
        });
    }

    [Fact]
    public void WarningDirectivePrintsLocationAndContinuesCompilation()
    {
        WithSource("#warning verify this platform\npublic int Available() { return 1; }\n", path =>
        {
            var originalError = Console.Error;
            using var captured = new StringWriter();
            Console.SetError(captured);
            try
            {
                new Compiler().Compile([path]);
            }
            finally
            {
                Console.SetError(originalError);
            }
            Assert.Contains($"{path}(1,1): warning: verify this platform", captured.ToString());
            Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(path)!, ".obj", "test.c")));
        });
    }

    [Fact]
    public void DirectiveTextInsideBlockCommentsIsIgnored()
    {
        WithSource("/*\n#if UNKNOWN\n#error ignored\n#endif\n*/\npublic int Available() { return 1; }\n",
            path => new Compiler().Compile([path]));
    }

    [Fact]
    public void CommandLineSymbolsFlowThroughProjectCompilation()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"cxc-preprocessor-project-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var projectPath = Path.Combine(directory, "app.cxproj");
            File.WriteAllText(projectPath, "name: app\ntype: Executable\n");
            File.WriteAllText(Path.Combine(directory, "main.cx"),
                "#if CX_OS_WINDOWS\npublic int Platform() { return 1; }\n#else\npublic int Platform() { return 2; }\n#endif\n");

            new Compiler().Compile(["--define", "CX_OS_WINDOWS", projectPath]);

            var generated = File.ReadAllText(Path.Combine(directory, ".obj", "app.c"));
            Assert.Contains("return 1;", generated);
            Assert.DoesNotContain("return 2;", generated);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("#else\n", "no matching #if")]
    [InlineData("#if true\npublic int A() { return 1; }\n", "missing #endif")]
    [InlineData("#if true\n#else\n#else\n#endif\n", "Only one #else")]
    [InlineData("#if A + B\n#endif\n", "Unexpected token")]
    [InlineData("#region platform\n", "Unknown preprocessor directive")]
    public void InvalidDirectiveStructureIsReported(string directives, string expectedMessage)
    {
        WithSource(directives, path =>
        {
            var error = Assert.Throws<CompilationErrorException>(() => new Compiler().Compile([path]));
            Assert.Contains(expectedMessage, error.Message, StringComparison.OrdinalIgnoreCase);
        });
    }

    private static void WithSource(string source, Action<string> action)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"cxc-preprocessor-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "test.cx");
        File.WriteAllText(path, source);
        try
        {
            action(path);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}

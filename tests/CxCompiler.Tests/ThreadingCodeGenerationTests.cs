using System.Text.RegularExpressions;
using CxCompiler.Model.Project;
using CxCompiler.OutputGenerators;
using CxCompiler.Semantics;

namespace CxCompiler.Tests;

public sealed class ThreadingCodeGenerationTests
{
    [Theory]
    [InlineData("while (enabled) { continue; }", "while ")]
    [InlineData("do { continue; } while (enabled);", "do")]
    [InlineData("for (;;) { continue; }", "for ")]
    [InlineData("foreach (int value in items) { continue; }", "for ")]
    public void AllocationFreeLoopsPollBeforeTheBodyIncludingContinue(string loop, string keyword)
    {
        var source = Generate($"public void Run(bool enabled, int[] items) {{ {loop} }}");
        Assert.Matches(Regex.Escape(keyword) + @"[^\n]*\n\s*\{\r?\n\s*cx_gc_safepoint\(\);", source);
    }

    [Fact]
    public void BindingAWorkerLambdaPreservesOuterPropertyTemporaryNumbers()
    {
        var source = Generate("""
            import System.Threading;
            public void Run() {
                AtomicInt value = new AtomicInt();
                value.Value = 1;
                Thread worker = Thread.Start(() => {});
                value.Value = 2;
                worker.Join();
            }
            """);
        Assert.Contains("cx_int __cx_property_value_0;", source);
        Assert.Contains("cx_int __cx_property_value_1;", source);
    }

    [Fact]
    public void ValueConstructionInitializesStorageBeforeConstructorEntryCanPoll()
    {
        var source = Generate("""
            public struct Defaults<T> {
                public T Value = default;
                public constructor() {}
            }
            public int Run() {
                Defaults<int> value = new Defaults<int>();
                return value.Value;
            }
            """);
        Assert.Contains("(memset(&__cx_new_0, 0, sizeof(__cx_new_0)), ", source);
    }

    [Fact]
    public void StaticAndDynamicModulesUseOneGcAwareInitializationGate()
    {
        var source = Generate("public class State { public static int Value = 42; } public void Run() {}");
        Assert.Contains("static struct cx_runtime_once __cx_module_once_", source);
        Assert.Contains("if (!cx_runtime_once_begin(&__cx_module_once_", source);
        Assert.Contains("cx_runtime_once_complete(&__cx_module_once_", source);
        Assert.Contains("cx_runtime_once_fail(&__cx_module_once_", source);
        Assert.Contains("setjmp(__cx_module_exception_frame.environment)", source);
        Assert.DoesNotContain("atomic_flag_test_and_set", source);
        Assert.DoesNotContain("static cx_bool registered", source);
        // Each generated function must initialize its module in static builds too.
        Assert.DoesNotContain("#if !defined(CX_STATIC_LINK) || defined(CX_MODULE_HAS_GENERIC_VIRTUAL_REGISTRATIONS)", source);
    }

    private static string Generate(string source)
    {
        var project = CxProject.CreateDefaultApplicationProject("threading");
        project.AddCompilationContext(CompilerTestHelper.Parse(source));
        new SemanticBinder().Bind(project);
        var directory = Path.Combine(Path.GetTempPath(), $"cxc-threading-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            return File.ReadAllText(Path.Combine(directory, "threading.c"));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}

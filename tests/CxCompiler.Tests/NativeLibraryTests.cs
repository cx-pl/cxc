using CxCompiler.Semantics;

namespace CxCompiler.Tests;

public sealed class NativeLibraryTests
{
    [Fact]
    public void LibrarySymbolsResolveIntoTypedPointersWithoutDataPointerCasts()
    {
        var project = NativeInteropTests.Project("""
            import System.Interop;
            @NativeFunction public delegate int ProcessId();
            public unsafe int Run() {
                NativeLibrary library = NativeLibrary.Load("libc.so.6");
                ProcessId function = library.Resolve<ProcessId>("getpid");
                return function();
            }
            """);
        new SemanticBinder().Bind(project);
        var source = NativeInteropTests.Generate(project).Source;
        Assert.Contains("cx_interop_library_resolve(library,", source);
        Assert.Contains("sizeof(__cx_native_symbol_", source);
        Assert.Contains("cx_interop_require_function", source);
        Assert.DoesNotContain("->__function", source);
    }
}

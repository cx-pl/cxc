using CxCompiler.Model.Errors;
using CxCompiler.Semantics;

namespace CxCompiler.Tests;

public sealed class NativeFunctionTests
{
    [Fact]
    public void NativeFunctionsAreSinglePointersAndStaticConversionsUseGuardedEntryPoints()
    {
        var project = NativeInteropTests.Project("""
            import System.Interop;
            @NativeFunction(callingConvention = NativeCallingConvention.Cdecl, failureResult = "-1")
            public delegate int Callback(int number, uint other);
            public int Add(int number, uint other) { return number + (int)other; }
            public unsafe int Run() { Callback callback = (Callback)Add; return callback(20, 22u); }
            """);
        new SemanticBinder().Bind(project);
        var generated = NativeInteropTests.Generate(project);
        Assert.Contains("(CX_NATIVE_CDECL *CX_FN_", generated.Header);
        Assert.Contains("cx_interop_callback_enter(&scope)", generated.Source);
        Assert.Contains("cx_interop_callback_failed(cx_exception_current())", generated.Source);
        Assert.Contains("cx_interop_callback_leave(&scope)", generated.Source);
        Assert.Contains("cx_interop_require_function", generated.Source);
    }

    [Fact]
    public void ManagedClosuresCannotBeReinterpretedAsNativePointers()
    {
        var project = NativeInteropTests.Project("""
            import System.Interop;
            @NativeFunction public delegate int Callback(int number);
            public unsafe void Run() { int captured = 1; delegate(int) => int closure = (int number) => number + captured; Callback callback = (Callback)closure; }
            """);
        Assert.Contains("non-capturing", Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project)).Message);
    }
}

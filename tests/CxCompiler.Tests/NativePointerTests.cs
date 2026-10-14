using CxCompiler.Model.Errors;
using CxCompiler.Semantics;

namespace CxCompiler.Tests;

public sealed class NativePointerTests
{
    [Fact]
    public void TypedPointersRetainPointeesConstnessAndNativeIndexing()
    {
        var project = NativeInteropTests.Project("""
            import System.Interop;
            @NativeImport("fixture", entryPoint = "write_value")
            public extern void Write(ptr<int> result);
            public unsafe int Run() {
                int value = 0;
                ptr<int> p = &value;
                Write(p);
                p[0] = 42;
                ptr<const int> read = p;
                ptr<ptr<int>> pp = &p;
                return read[0] + (*pp)[0];
            }
            """);
        new SemanticBinder().Bind(project);
        var generated = NativeInteropTests.Generate(project);
        Assert.Contains("cx_int*", generated.Source);
        Assert.Contains("const cx_int*", generated.Source);
        Assert.Contains("write_value(p)", generated.Source);
        Assert.DoesNotContain("cx_array_at", generated.Source);
    }

    [Theory]
    [InlineData("public int Run(ptr<int> p) { return p[0]; }", "unsafe")]
    [InlineData("public unsafe int Run(ptr<const int> p) { p[0] = 2; return 0; }", "const")]
    [InlineData("public unsafe int Run(ptr p) { return *p; }", "typed pointer")]
    [InlineData("public unsafe void Run(ptr<int> p) { ptr<byte> other = p; }", "Cannot initialize")]
    [InlineData("public unsafe void Run(string text) { ptr<string> p = &text; }", "native scalar")]
    public void InvalidPointerOperationsAreDiagnosed(string source, string diagnostic)
    {
        var project = NativeInteropTests.Project(source);
        Assert.Contains(diagnostic, Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project)).Message);
    }

    [Fact]
    public void PointerConversionsAreExplicitAndAliasesSupportPrimitiveAndPointerTargets()
    {
        var project = NativeInteropTests.Project("""
            alias IntPointer = ptr<int>;
            alias Count = uint;
            public unsafe Count Run(IntPointer p) { ptr raw = (ptr)p; IntPointer back = (IntPointer)raw; return (Count)back[0]; }
            """);
        new SemanticBinder().Bind(project);
        var generated = NativeInteropTests.Generate(project).Source;
        Assert.Contains("cx_int*", generated);
        Assert.Contains("((cx_ptr)", generated);
    }

    [Fact]
    public void WcharAndNativeScalarsUseTargetCTypes()
    {
        var project = NativeInteropTests.Project("""
            import System.Interop;
            @NativeImport("fixture") public extern CLong Native(SizeT count, ptr<wchar> text);
            public unsafe wchar Unit() { return (wchar)0xD83D; }
            """);
        new SemanticBinder().Bind(project);
        var generated = NativeInteropTests.Generate(project);
        Assert.Contains("cx_wchar", generated.Source);
        // The SDK supplies the import declaration; the model retains native aliases.
        Assert.Contains("cx_wchar", generated.Header);
    }

    [Fact]
    public void WcharRejectsOutOfRangeConstant()
    {
        var project = NativeInteropTests.Project("public wchar Unit() { return (wchar)65536; }");
        Assert.Contains("0xFFFF", Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project)).Message);
    }

    [Theory]
    [InlineData("(wchar)(-1)")]
    [InlineData("(wchar)0x10000")]
    [InlineData("(wchar)(65535 + 1)")]
    [InlineData("(wchar)(char)0x1F600")]
    public void WcharRejectsSignedAndComputedConstants(string expression)
    {
        var project = NativeInteropTests.Project($"public wchar Unit() {{ return {expression}; }}");
        Assert.Contains("0xFFFF", Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project)).Message);
    }

    [Fact]
    public void ConstVoidPointersAreNativeBorrowsWithoutDereference()
    {
        var project = NativeInteropTests.Project("public unsafe ptr<const void> Borrow(ptr p) { return (ptr<const void>)p; }");
        new SemanticBinder().Bind(project);
        Assert.Contains("const void*", NativeInteropTests.Generate(project).Source);
        project = NativeInteropTests.Project("public unsafe void Bad(ptr<const void> p) { var value = *p; }");
        Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
    }

    [Fact]
    public void StackBorrowsCannotEscapeOrEnterBlockingNativeCalls()
    {
        var project = NativeInteropTests.Project("public unsafe ptr<int> Bad() { int value = 0; return &value; }");
        Assert.Contains("stack storage", Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project)).Message);
        project = NativeInteropTests.Project("""
            import System.Interop;
            @NativeImport("fixture", blocking = true) public extern void Wait(ptr<int> value);
            public unsafe void Bad() { int value = 0; ptr<int> p = &value; Wait(p); }
            """);
        Assert.Contains("native buffers", Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project)).Message);
    }
}

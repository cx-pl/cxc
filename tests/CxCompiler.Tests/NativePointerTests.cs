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
            @NativeImport("fixture", entryPoint = "write_value", noEscape = true)
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

    [Theory]
    [InlineData("Memory.Realloc(block, 8u);")]
    [InlineData("Memory.Free(block);")]
    [InlineData("Memory.Copy(destination, source, 8u);")]
    public void RawMemoryOperationsRequireUnsafeContext(string operation)
    {
        var project = NativeInteropTests.Project($$"""
            import System;
            public void Run(ptr block, ptr destination, ptr source) {
                {{operation}}
            }
            """);
        AddMemoryContext(project);

        var error = Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
        Assert.Contains("unsafe function or context", error.Message);

        project = NativeInteropTests.Project($$"""
            import System;
            public unsafe void Run(ptr block, ptr destination, ptr source) {
                {{operation}}
            }
            """);
        AddMemoryContext(project);
        new SemanticBinder().Bind(project);
    }

    private static void AddMemoryContext(CxCompiler.Model.Project.CxProject project)
    {
        project.AddReferencedCompilationContext(CompilerTestHelper.Parse("""
            import System.Interop;
            namespace System;
            public static class Memory {
                public static extern void WithArena(delegate() => void action);
                public static extern void WithDurable(delegate() => void action);
                public static unsafe extern ptr Realloc(ptr block, uint size);
                public static unsafe extern void Free(ptr block);
                @NoEscape public static unsafe extern void Copy(ptr destination, ptr source, uint size);
            }
            """), "cxcore", syntheticCore: true);
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

    [Fact]
    public void StackBorrowsRequireTrustedNonRetainingNativeContract()
    {
        var project = NativeInteropTests.Project("""
            import System.Interop;
            @NativeImport("fixture") public extern void Consume(ptr<int> value);
            public unsafe void Bad() { int value = 42; Consume(&value); }
            """);

        Assert.Contains(
            "trusted noEscape contract",
            Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project)).Message);

        project = NativeInteropTests.Project("""
            import System.Interop;
            @NativeImport("fixture", noEscape = true) public extern void Consume(ptr<int> value);
            public unsafe void Good() { int value = 42; Consume(&value); }
            """);
        new SemanticBinder().Bind(project);
        var import = project.CompilationContexts[0].DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Consume").NativeImport;
        Assert.True(import?.NoEscape);
    }

    [Fact]
    public void NativeBufferCopyAcceptsBorrowedStackStorage()
    {
        var project = NativeInteropTests.Project("""
            import System.Interop;
            public unsafe void CopyLocal() {
                int value = 42;
                NativeBuffer.Copy((ptr)&value, (ptr)&value, 4ul);
            }
            """);

        new SemanticBinder().Bind(project);
    }

    [Fact]
    public void MemoryCopyAcceptsBorrowedStackStorage()
    {
        var project = NativeInteropTests.Project("""
            import System;
            public unsafe void CopyLocal() {
                int value = 42;
                Memory.Copy((ptr)&value, (ptr)&value, 4u);
            }
            """);
        AddMemoryContext(project);
        new SemanticBinder().Bind(project);
    }

    [Fact]
    public void NoEscapeAnnotationDoesNotOverrideRetainingFunctionBody()
    {
        var project = NativeInteropTests.Project("""
            import System.Interop;
            public class Holder { public ptr<int> Value; }
            @NoEscape public unsafe void Retain(Holder holder, ptr<int> value) {
                holder.Value = value;
            }
            public unsafe void Bad(Holder holder) {
                int value = 42;
                Retain(holder, &value);
            }
            """);

        Assert.Contains(
            "Cannot pass borrowed stack storage to a function that may retain it",
            Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project)).Message);
    }

    [Fact]
    public void DurablePointerReturnContractAllowsNativeResultToOutliveArena()
    {
        var project = NativeInteropTests.Project("""
            import System;
            import System.Interop;
            public static class Cache { public static ptr<int> Value; }
            @NativeImport("fixture")
            @DurablePointerReturn
            public extern ptr<int> Allocate();
            public unsafe void Run() {
                Memory.WithArena(() => { Cache.Value = Allocate(); });
            }
            """);
        AddMemoryContext(project);
        new SemanticBinder().Bind(project);
    }

    [Fact]
    public void DurablePointerReturnContractRequiresPointerResult()
    {
        var project = NativeInteropTests.Project("""
            import System.Interop;
            @NativeImport("fixture")
            @DurablePointerReturn
            public extern int Invalid();
            """);
        Assert.Contains("requires a ptr return type",
            Assert.Throws<CompilationErrorException>(
                () => new SemanticBinder().Bind(project)).Message);
    }

    [Theory]
    [InlineData("holder.Value = &value;")]
    [InlineData("values[0] = &value;")]
    [InlineData("output[0] = &value;")]
    [InlineData("holder.Value = offset;")]
    public void StackBorrowsCannotBeStoredOutsideLocalVariables(string assignment)
    {
        var project = NativeInteropTests.Project($$"""
            public class Holder { public ptr<int> Value; }
            public unsafe void Bad(Holder holder, ptr<ptr<int>> output) {
                ptr<int>[] values = new ptr<int>[1];
                int value = 42;
                ptr<int> borrowed = &value;
                ptr<int> offset = true ? borrowed : borrowed;
                {{assignment}}
            }
            """);

        Assert.Contains(
            "Cannot store a pointer borrowing local stack storage",
            Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project)).Message);
    }

    [Fact]
    public void StackAllocatedPointerCannotBeStoredInAManagedField()
    {
        var project = NativeInteropTests.Project("""
            public class Holder { public ptr<int> Value; }
            public unsafe void Bad(Holder holder) {
                ptr<int> storage = stackalloc<int>(4);
                holder.Value = storage;
            }
            """);

        Assert.Contains(
            "Cannot store a pointer borrowing local stack storage",
            Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project)).Message);
    }

    [Fact]
    public void SwitchExpressionPreservesStackBorrowProvenance()
    {
        var project = NativeInteropTests.Project("""
            public class Holder { public ptr<int> Value; }
            public unsafe void Bad(Holder holder, int selector) {
                int value = 42;
                ptr<int> borrowed = &value;
                ptr<int> selected = selector switch { 0 => &value, _ => borrowed };
                holder.Value = selected;
            }
            """);

        Assert.Contains(
            "Cannot store a pointer borrowing local stack storage",
            Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project)).Message);
    }

    [Fact]
    public void NullCoalescingPreservesStackBorrowProvenance()
    {
        var project = NativeInteropTests.Project("""
            public class Holder { public ptr<int> Value; }
            public unsafe void Bad(Holder holder) {
                int value = 42;
                ptr<int> borrowed = &value;
                ptr<int> selected = null ?? borrowed;
                holder.Value = selected;
            }
            """);

        Assert.Contains(
            "Cannot store a pointer borrowing local stack storage",
            Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project)).Message);
    }

    [Fact]
    public void ManagedFunctionReturnPreservesBorrowedParameterProvenance()
    {
        var project = NativeInteropTests.Project("""
            public class Holder { public ptr<int> Value; }
            public unsafe ptr<int> Identity(ptr<int> value) {
                ptr<int> forwarded = value;
                return forwarded;
            }
            public unsafe void Bad(Holder holder) {
                int local = 42;
                holder.Value = Identity(&local);
            }
            """);

        Assert.Contains(
            "Cannot store a pointer borrowing local stack storage",
            Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project)).Message);
    }

    [Fact]
    public void ManagedFunctionRetentionRejectsBorrowedArguments()
    {
        var project = NativeInteropTests.Project("""
            public class Holder { public ptr<int> Value; }
            public unsafe void Retain(Holder holder, ptr<int> value) { holder.Value = value; }
            public unsafe void Forward(Holder holder, ptr<int> value) { Retain(holder, value); }
            public unsafe void Bad(Holder holder) {
                int local = 42;
                Forward(holder, &local);
            }
            """);

        Assert.Contains(
            "function that may retain it",
            Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project)).Message);
    }

    [Fact]
    public void ProvenNonRetainingManagedFunctionAcceptsStackBorrow()
    {
        var project = NativeInteropTests.Project("""
            public unsafe int Read(ptr<int> value) { return value[0]; }
            public unsafe int Good() {
                int local = 42;
                return Read(&local);
            }
            """);

        new SemanticBinder().Bind(project);
    }

    [Fact]
    public void DirectRecursiveReadAcceptsStackBorrow()
    {
        var project = NativeInteropTests.Project("""
            public unsafe int RecursiveRead(ptr<int> value, int depth) {
                if (depth == 0) return value[0];
                return RecursiveRead(value, depth - 1);
            }
            public unsafe int Good() {
                int local = 42;
                return RecursiveRead(&local, 1);
            }
            """);

        new SemanticBinder().Bind(project);
    }

    [Fact]
    public void DirectRecursiveRetentionStillRejectsStackBorrow()
    {
        var project = NativeInteropTests.Project("""
            public class Holder { public ptr<int> Value; }
            public unsafe void RecurseThenRetain(Holder holder, ptr<int> value, int depth) {
                if (depth > 0) RecurseThenRetain(holder, value, depth - 1);
                holder.Value = value;
            }
            public unsafe void Bad(Holder holder) {
                int local = 42;
                RecurseThenRetain(holder, &local, 1);
            }
            """);

        Assert.Contains(
            "function that may retain it",
            Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project)).Message);
    }

    [Fact]
    public void LaterBoundManagedRetentionIsConservative()
    {
        var project = NativeInteropTests.Project("""
            public class Holder { public ptr<int> Value; }
            public unsafe void Forward(Holder holder, ptr<int> value) {
                Retain(holder, value);
            }
            public unsafe void Bad(Holder holder) {
                int local = 42;
                Forward(holder, &local);
            }
            public unsafe void Retain(Holder holder, ptr<int> value) {
                holder.Value = value;
            }
            """);

        Assert.Contains(
            "function that may retain it",
            Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project)).Message);
    }

    [Fact]
    public void LaterBoundManagedReturnIsConservative()
    {
        var project = NativeInteropTests.Project("""
            public class Holder { public ptr<int> Value; }
            public unsafe void Bad(Holder holder) {
                int local = 42;
                holder.Value = Identity(&local);
            }
            public unsafe ptr<int> Identity(ptr<int> value) { return value; }
            """);

        Assert.Contains(
            "Cannot pass borrowed stack storage to a function that may retain it",
            Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project)).Message);
    }
}

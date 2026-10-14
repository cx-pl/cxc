using CxCompiler.Model.Errors;
using CxCompiler.Semantics;

namespace CxCompiler.Tests;

public sealed class NativeTypeTests
{
    [Fact]
    public void OwnLayoutsAreOrderedByValueDependenciesAndRejectCycles()
    {
        var project = NativeInteropTests.Project("""
            import System.Interop;
            @NativeLayout public struct Outer { public Inner value; }
            @NativeLayout public struct Inner { public int value; }
            """);
        new SemanticBinder().Bind(project);
        var header = NativeInteropTests.Generate(project).Header;
        Assert.True(header.IndexOf("struct __cx_native_interop_Inner {") < header.IndexOf("struct __cx_native_interop_Outer {"));
        project = NativeInteropTests.Project("""
            import System.Interop;
            @NativeLayout public struct Outer { public Inner value; }
            @NativeLayout public struct Inner { public Outer value; }
            """);
        new SemanticBinder().Bind(project);
        Assert.Contains("cycles", Assert.Throws<CompilationErrorException>(() => NativeInteropTests.Generate(project)).Message);
    }

    [Fact]
    public void NativeEnumImplicitValuesFollowHexadecimalValues()
    {
        var project = NativeInteropTests.Project("public enum Value : ushort { First = 0x20, Second }");
        new SemanticBinder().Bind(project);
        Assert.Contains("(33)", NativeInteropTests.Generate(project).Header);
    }
    [Fact]
    public void NativeEnumUnderlyingTypesAreFixed()
    {
        var project = NativeInteropTests.Project("""
            import System.Interop;
            public enum Flags : ushort { None = 0, Enabled = 1 }
            @NativeImport("fixture") public extern void Native(Flags flags);
            """);
        new SemanticBinder().Bind(project);
        Assert.Contains("typedef cx_ushort", NativeInteropTests.Generate(project).Header);
        project = NativeInteropTests.Project("public enum TooLarge : byte { Value = 256 }");
        Assert.Contains("underlying type", Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project)).Message);
    }
    [Fact]
    public void ExplicitUnionLayoutsSupportPackingOffsetsAndInlineArrays()
    {
        var project = NativeInteropTests.Project("""
            import System.Interop;
            @NativeLayout(layout = "union", size = 8, alignment = 4)
            public struct Bits { @NativeField(offset = 0) public int value; public byte[8] bytes; }
            public unsafe int Run(ptr<Bits> p) { (*p).bytes[0] = (byte)42; return (*p).value; }
            """);
        new SemanticBinder().Bind(project);
        var generated = NativeInteropTests.Generate(project);
        Assert.Contains("union __cx_native_interop_Bits {", generated.Header);
        Assert.Contains("cx_byte bytes[8]", generated.Header);
        Assert.Contains("offsetof(union __cx_native_interop_Bits, value) == 0", generated.Header);
        Assert.DoesNotContain("cx_array_at", generated.Source);
    }

    [Fact]
    public void SdkRecordsUseUpstreamTypesAndCheckedProjections()
    {
        var project = NativeInteropTests.Project("""
            import System.Interop;
            @NativeType("fixture_point", header = "fixture.h", size = 8, alignment = 4)
            public struct Point { public int x; public int y; }
            @NativeType("fixture_owner", header = "fixture.h", opaque = true)
            public struct Owner;
            @NativeImport("fixture") public extern Point Make(int x, int y);
            @NativeImport("fixture") public extern ptr<Owner> Open();
            public unsafe int Run() { Point point = Make(20, 22); return point.x + point.y; }
            """);
        new SemanticBinder().Bind(project);
        var generated = NativeInteropTests.Generate(project);
        Assert.Contains("fixture_point", generated.Source);
        Assert.Contains("sizeof(fixture_point) == 8", generated.Header);
        Assert.Contains("_Alignof(fixture_point) == 4", generated.Header);
        Assert.Contains("_Generic(&((fixture_point*)0)->x, cx_int*: 1", generated.Header);
        Assert.DoesNotContain("CX_TYPE_DEF(CX_ID_2(interop, Point))", generated.Header);
    }

    [Theory]
    [InlineData("public unsafe int Run(ptr<Owner> p) { Owner value = *p; return 0; }", "Opaque")]
    [InlineData("public unsafe int Run(ptr<Owner> p) { Owner value = p[0]; return 0; }", "Opaque")]
    [InlineData("public void Run() { Owner value = new Owner(); }", "Opaque")]
    [InlineData("public void Run() { Owner value = default; }", "pointers only")]
    [InlineData("public class Holder { public Owner value; }", "managed fields")]
    [InlineData("public void Run(Owner[] values) {}", "managed arrays")]
    [InlineData("public void Run(Owner? value) {}", "nullable storage")]
    public void OpaqueTypesCannotBeMaterialized(string source, string diagnostic)
    {
        var project = NativeInteropTests.Project("""
            import System.Interop;
            @NativeType("fixture_owner", header = "fixture.h", opaque = true) public struct Owner;
            """ + source);
        Assert.Contains(diagnostic, Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project)).Message);
    }
}

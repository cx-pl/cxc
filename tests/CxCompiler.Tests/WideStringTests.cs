using CxCompiler.Model.Errors;
using CxCompiler.Model.Project;
using CxCompiler.OutputGenerators;
using CxCompiler.Semantics;

namespace CxCompiler.Tests;

public sealed class WideStringTests
{
    [Fact]
    public void ExplicitConversionsUseCoreSymbolsAndObjectVirtualDispatch()
    {
        var project = NativeInteropTests.Project("""
            import System;
            public string Run(string text) {
                WideString wide = text.ToWideString();
                object reference = wide;
                return reference.ToString();
            }
            """);
        new SemanticBinder().Bind(project);
        Assert.Single(project.CompilationContexts);
        var source = NativeInteropTests.Generate(project).Source;
        Assert.Contains("CX_ID_4(cxcore, System, String, ToWideString)", source);
        Assert.Contains("cx_vtable_entry", source);
    }

    [Theory]
    [InlineData("WideString wide = text;")]
    [InlineData("string copy = new WideString();")]
    public void TranscodingNeverHappensImplicitly(string statement)
    {
        var project = NativeInteropTests.Project($"import System; public void Run(string text) {{ {statement} }}");
        Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
    }

    [Fact]
    public void RawWideImportRejectsManagedWideString()
    {
        var project = NativeInteropTests.Project("""
            import System; import System.Interop;
            @NativeImport("fixture") public extern void Native(WideString text);
            """);
        Assert.Contains("managed types", Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project)).Message);
    }
}

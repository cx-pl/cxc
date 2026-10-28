using CxCompiler.Model.Errors;
using CxCompiler.Model.Project;
using CxCompiler.Model.Types;
using CxCompiler.OutputGenerators;
using CxCompiler.Semantics;

namespace CxCompiler.Tests;

public sealed class NativeInteropTests
{
    internal static CxProject Project(string source)
    {
        var project = CxProject.CreateDefaultApplicationProject("interop");
        project.NativeLibraries["fixture"] = new NativeLibraryDependency
        {
            Windows = new NativeLibraryTarget { LinkLibraries = ["fixture"], Headers = ["fixture.h"] },
            Linux = new NativeLibraryTarget { LinkLibraries = ["fixture"], Headers = ["fixture.h"] },
        };
        project.AddCompilationContext(CompilerTestHelper.Parse(source));
        return project;
    }

    [Fact]
    public void NativeImportsRetainExactSymbolsAndTargetDependencies()
    {
        var project = Project("""
            import System.Interop;
            @NativeImport("fixture", entryPoint = "native_add", callingConvention = NativeCallingConvention.Cdecl)
            public extern int Add(int left, int right);
            public unsafe int Main() { return Add(20, 22); }
            """);
        new SemanticBinder().Bind(project);
        var function = project.CompilationContexts[0].DeclarationScope.Declarations.OfType<FunctionDeclaration>().First();
        Assert.Equal("native_add", function.NativeImport!.EntryPoint);
        Assert.Equal("cxcore.System.Interop.NativeImport", function.Annotations[0].ResolvedTypeName);
        var files = Generate(project);
        Assert.Contains("native_add(20, 22)", files.Source);
        Assert.Contains("#include <fixture.h>", files.Header);
        Assert.DoesNotContain("interop_Add", files.Header);
        Assert.Contains("CMAKE_SYSTEM_NAME STREQUAL \"Linux\"", files.CMake);
        Assert.Contains("target_link_libraries(interop PUBLIC \"fixture\")", files.CMake);
    }

    [Theory]
    [InlineData("@NativeImport(\"fixture\", missing = 1)", "Unknown")]
    [InlineData("@NativeImport(\"fixture\", library = \"fixture\")", "Duplicate")]
    [InlineData("@NativeImport", "Missing")]
    [InlineData("@NativeImport(entryPoint = \"native_add\", \"fixture\")", "Positional")]
    [InlineData("@NativeImport(\"absent\")", "Unknown native library")]
    [InlineData("@NativeImport(\"fixture\", entryPoint = \"bad-name\")", "Malformed")]
    [InlineData("@NativeImport(\"fixture\") @NativeImport(\"fixture\")", "not repeatable")]
    [InlineData("@NativeImport(\"fixture\", callingConvention = Unknown.Cdecl)", "requires a member")]
    [InlineData("@NativeImport(\"fixture\", captureError = Unknown.Errno)", "requires a member")]
    public void InvalidNativeAnnotationsAreDiagnosed(string annotation, string diagnostic)
    {
        var project = Project($"import System.Interop; {annotation} public extern int Add(int x);");
        var error = Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
        Assert.Contains(diagnostic, error.Message);
    }

    [Theory]
    [InlineData("public extern string Add();", "managed types")]
    [InlineData("public int Add() { return 1; }", "non-generic extern")]
    [InlineData("public extern int Add<T>();", "non-generic extern")]
    [InlineData("public class Add;", "not valid")]
    public void InvalidImportPlacementAndSignaturesAreDiagnosed(string declaration, string diagnostic)
    {
        var project = Project("import System.Interop; @NativeImport(\"fixture\") " + declaration);
        Assert.Contains(diagnostic, Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project)).Message);
    }

    [Fact]
    public void RawCallRequiresUnsafeContextAndPlainExternKeepsCxAbi()
    {
        var project = Project("""
            import System.Interop;
            @NativeImport("fixture") public extern int Native();
            public int Main() { return Native(); }
            """);
        Assert.Contains("unsafe", Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project)).Message);
        project = Project("public extern int Native(); public int Main() { return Native(); }");
        new SemanticBinder().Bind(project);
        Assert.Contains("interop_Native", Generate(project).Source);
    }

    [Fact]
    public void PublicImportsCannotHideTheirNativeRequirement()
    {
        var project = Project("import System.Interop; @NativeImport(\"fixture\") public extern int Native();");
        project.NativeLibraries["fixture"].Visibility = "private";
        Assert.Contains("public native dependencies", Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project)).Message);
    }

    [Fact]
    public void NativeImportsCannotBelongToGenericOwners()
    {
        var project = Project("import System.Interop; public class Owner<T> { @NativeImport(\"fixture\") public static extern int Native(); }");
        Assert.Contains("non-generic", Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project)).Message);
    }

    [Fact]
    public void SdkAssertionsKeepHeaderOrderAndPointerIdentity()
    {
        var project = Project("import System.Interop; @NativeImport(\"fixture\") public extern int Native(ptr<const void> data);");
        project.NativeLibraries["fixture"].Windows!.Headers = ["windows.h", "GL/gl.h"];
        project.NativeLibraries["fixture"].Windows!.IncludeDirectories = ["sdk/include"];
        project.NativeLibraries["fixture"].Windows!.RuntimeArtifacts = ["sdk/native.dll"];
        new SemanticBinder().Bind(project);
        var files = Generate(project);
        Assert.True(files.Header.IndexOf("#include <windows.h>") < files.Header.IndexOf("#include <GL/gl.h>"));
        Assert.Contains("_Generic(&Native", files.Header);
        Assert.Contains("const void*", files.Header);
        Assert.Contains("${CMAKE_CURRENT_LIST_DIR}/sdk/include", files.CMake);
        Assert.Contains("INTERFACE_CX_NATIVE_RUNTIME_FILES", files.CMake);
    }

    [Fact]
    public void StaticPkgConfigRequirementsResolveArchivesAndPrivateTransitiveLibraries()
    {
        var project = Project("public int Main() { return 0; }");
        project.NativeLibraries["fixture"].Linux!.PkgConfig = "x11";
        project.NativeLibraries["fixture"].Linux!.Linkage = "static";
        new SemanticBinder().Bind(project);
        var cmake = Generate(project).CMake;
        Assert.Contains("_STATIC_LIBRARIES)", cmake);
        Assert.Contains("set(CMAKE_FIND_LIBRARY_SUFFIXES .a)", cmake);
        Assert.DoesNotContain("STATIC_LINK_LIBRARIES", cmake);
    }

    [Fact]
    public void UserAnnotationStoresConstantsWithoutExecutingConstructor()
    {
        var project = Project("""
            import System;
            @AnnotationUsage(targets = "function,parameter,return", repeatable = true)
            public class Tag : Annotation { public constructor(string text, int number = 7) {} }
            @Tag("one") @Tag(number = 8, text = "two") @return:Tag("result")
            public int Run(@Tag("input") int input) { return input; }
            """);
        new SemanticBinder().Bind(project);
        var function = project.CompilationContexts[0].DeclarationScope.Declarations.OfType<FunctionDeclaration>().Single();
        Assert.Equal("7", function.Annotations[0].BoundArguments["number"]);
        Assert.Equal("8", function.Annotations[1].BoundArguments["number"]);
        Assert.Equal("return", function.Annotations[2].Target);
        Assert.Single(function.Parameters[0].Annotations);
    }

    [Fact]
    public void UserAnnotationConstructorSupportsNegativeNumericDefaults()
    {
        var project = Project("""
            import System;
            @AnnotationUsage(targets = "function")
            public class Tag : Annotation { public constructor(int contextParameter = -1) {} }
            @Tag public int Run() { return 0; }
            """);
        new SemanticBinder().Bind(project);
        var function = project.CompilationContexts[0].DeclarationScope.Declarations
            .OfType<FunctionDeclaration>().Single();

        Assert.Equal("-1", function.Annotations[0].BoundArguments["contextParameter"]);
    }

    [Fact]
    public void ErrorSnapshotIsCapturedBeforeReturningToCx()
    {
        var project = Project("""
            import System.Interop;
            @NativeImport("fixture", captureError = NativeErrorPolicy.Errno)
            public extern int Native();
            public unsafe int Main() { return Native(); }
            """);
        new SemanticBinder().Bind(project);
        var header = Generate(project).Header;
        Assert.Contains("cx_int result = Native();", header);
        Assert.Contains("cx_interop_capture_errno();", header);
        Assert.True(header.IndexOf("cx_interop_capture_errno();") < header.IndexOf("return result;"));
    }

    internal static (string Source, string Header, string CMake) Generate(CxProject project)
    {
        var directory = Path.Combine(Path.GetTempPath(), "cxc-interop-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            return (File.ReadAllText(Path.Combine(directory, project.Name + ".c")),
                File.ReadAllText(Path.Combine(directory, project.Name + ".h")),
                File.ReadAllText(Path.Combine(directory, "CMakeLists.txt")));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}

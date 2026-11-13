using CxCompiler.Model.Project;
using CxCompiler.Model.Types;
using CxCompiler.Semantics;

namespace CxCompiler.Tests;

public sealed class LibraryAnnotationTests
{
    [Fact]
    public void TestAnnotationsAreOrdinaryLibraryMetadataWithoutGeneratedRegistries()
    {
        var project = CxProject.CreateDefaultApplicationProject("libraryAnnotations");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            import System;
            import System.Tests;
            namespace System.Tests;
            @AnnotationUsage(targets = "function")
            public class Fact : Annotation { public constructor() {} }
            @AnnotationUsage(targets = "function", repeatable = true, variadic = true)
            public class InlineData : Annotation { public constructor(object[] values) {} }
            public class Example {
                @Fact public static int ShapeValidatedByLibrary() { return 7; }
                @InlineData(1, true, null) private void DataValidatedByLibrary() {}
            }
            """));
        new SemanticBinder().Bind(project);
        Assert.DoesNotContain(project.CompilationContexts.SelectMany(context => context.DeclarationScope.Declarations),
            declaration => declaration is ClassDeclaration { Name: "TestRegistry" });
        var source = NativeInteropTests.Generate(project).Source;
        Assert.Contains("libraryAnnotations.System.Tests.Fact", source);
        Assert.Contains("libraryAnnotations.System.Tests.InlineData", source);
        Assert.Contains("\"function\", \"ShapeValidatedByLibrary\"", source);
        Assert.DoesNotContain("System_Tests_Generated", source);
    }
}

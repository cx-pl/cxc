using CxCompiler.Model.Project;
using CxCompiler.Semantics;

namespace CxCompiler.Tests;

public sealed class PartialDeclarationTests
{
    [Fact]
    public void ReportsDuplicateTypeAcrossSourceContexts()
    {
        var project = CxProject.CreateDefaultApplicationProject("duplicate_type");
        project.AddCompilationContext(CompilerTestHelper.Parse("public class Item {}"));
        project.AddCompilationContext(CompilerTestHelper.Parse("public class Item {}"));

        var error = Assert.Throws<CxCompiler.Model.Errors.CompilationErrorException>(
            () => new SemanticBinder().Bind(project));

        Assert.Contains("Type 'Item' is declared more than once", error.Message);
    }

    [Fact]
    public void ReportsConflictingPartialTypeKinds()
    {
        var project = CxProject.CreateDefaultApplicationProject("conflicting_partial");
        project.AddCompilationContext(CompilerTestHelper.Parse("public partial class Item {}"));
        project.AddCompilationContext(CompilerTestHelper.Parse("public partial struct Item {}"));

        var error = Assert.Throws<CxCompiler.Model.Errors.CompilationErrorException>(
            () => new SemanticBinder().Bind(project));

        Assert.Contains("Partial declarations of type 'Item' have conflicting kind", error.Message);
    }

    [Fact]
    public void ExplainsWhenCompatiblePartialDeclarationsCannotBeMerged()
    {
        var project = CxProject.CreateDefaultApplicationProject("partial_type");
        project.AddCompilationContext(CompilerTestHelper.Parse("public partial class Item {}"));
        project.AddCompilationContext(CompilerTestHelper.Parse("public partial class Item {}"));

        var error = Assert.Throws<CxCompiler.Model.Errors.CompilationErrorException>(
            () => new SemanticBinder().Bind(project));

        Assert.Contains("cannot yet be merged", error.Message);
    }
}

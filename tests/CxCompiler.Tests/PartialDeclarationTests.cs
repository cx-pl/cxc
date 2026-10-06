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
    public void MergesCompatiblePartialDeclarationsAcrossSourceContexts()
    {
        var project = CxProject.CreateDefaultApplicationProject("partial_type");
        project.AddCompilationContext(CompilerTestHelper.Parse(
            "public partial class Item { public int First; }"));
        project.AddCompilationContext(CompilerTestHelper.Parse(
            "public partial class Item { public int Second; }"));

        new SemanticBinder().Bind(project);

        var declarations = project.CompilationContexts
            .SelectMany(context => context.DeclarationScope.Declarations)
            .OfType<CxCompiler.Model.Types.ClassDeclaration>()
            .ToArray();
        var item = Assert.Single(declarations);
        Assert.Contains(item.MemberDeclarations.Declarations,
            declaration => declaration.Name == "First");
        Assert.Contains(item.MemberDeclarations.Declarations,
            declaration => declaration.Name == "Second");
    }
}

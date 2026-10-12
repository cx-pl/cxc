using CxCompiler.Model.Errors;
using CxCompiler.Model.Project;
using CxCompiler.Semantics;

namespace CxCompiler.Tests;

public sealed class AliasTests
{
    [Fact]
    public void AliasResolvesToTheSameUnderlyingTypeInDeclarations()
    {
        var project = CxProject.CreateDefaultApplicationProject("alias_type");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            alias Quantity = Number;
            public struct Number { public int Value; }
            public class Box { public Quantity Item; }
            public Quantity Echo(Quantity value) { return value; }
            """));

        new SemanticBinder().Bind(project);

        var declarations = project.CompilationContexts.Single().DeclarationScope.Declarations;
        var number = Assert.IsType<CxCompiler.Model.Types.ClassDeclaration>(
            declarations.Single(declaration => declaration.Name == "Number"));
        var box = Assert.IsType<CxCompiler.Model.Types.ClassDeclaration>(
            declarations.Single(declaration => declaration.Name == "Box"));
        var field = Assert.Single(box.MemberDeclarations.Declarations
            .OfType<CxCompiler.Model.Types.FieldDeclaration>());
        var echo = Assert.IsType<CxCompiler.Model.Types.FunctionDeclaration>(
            declarations.Single(declaration => declaration.Name == "Echo"));

        var fieldType = Assert.IsType<CxCompiler.Model.Types.NamedType>(field.Type);
        Assert.Equal(number.FullName.ToString(), string.Join('.',
            fieldType.ResolvedTypeFullName.Parts.Skip(1)));
        Assert.Equal("Number", Assert.IsType<CxCompiler.Model.Types.NamedType>(
            echo.ReturnType).ResolvedTypeFullName.Parts.Last());
        Assert.Equal("Number", Assert.IsType<CxCompiler.Model.Types.NamedType>(
            echo.Parameters.Single().ParameterType).ResolvedTypeFullName.Parts.Last());
    }

    [Fact]
    public void GenericAliasResolvesToItsConstructedUnderlyingType()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_alias");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            alias IntBox = Box<int>;
            public class Box<T> { public T Value; }
            public class Holder { public IntBox Item; }
            """));

        new SemanticBinder().Bind(project);

        var declarations = project.CompilationContexts.Single().DeclarationScope.Declarations;
        var holder = Assert.IsType<CxCompiler.Model.Types.ClassDeclaration>(
            declarations.Single(declaration => declaration.Name == "Holder"));
        var item = Assert.IsType<CxCompiler.Model.Types.NamedType>(
            Assert.Single(holder.MemberDeclarations.Declarations
                .OfType<CxCompiler.Model.Types.FieldDeclaration>()).Type);
        Assert.Equal("Box", item.ResolvedTypeFullName.Parts.Last());
        Assert.Equal("Int", item.TypeArguments.Single().Name);
        Assert.NotNull(item.ConstructedIdentity);
    }

    [Fact]
    public void ParameterizedAliasSubstitutesItsArgumentsIntoTheTarget()
    {
        var project = CxProject.CreateDefaultApplicationProject("parameterized_alias");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            alias BoxOf<T> = Box<T>;
            public class Box<T> { public T Value; }
            public class Holder { public BoxOf<int> Item; }
            """));

        new SemanticBinder().Bind(project);

        var declarations = project.CompilationContexts.Single().DeclarationScope.Declarations;
        var holder = Assert.IsType<CxCompiler.Model.Types.ClassDeclaration>(
            declarations.Single(declaration => declaration.Name == "Holder"));
        var item = Assert.IsType<CxCompiler.Model.Types.NamedType>(
            Assert.Single(holder.MemberDeclarations.Declarations
                .OfType<CxCompiler.Model.Types.FieldDeclaration>()).Type);
        Assert.Equal("Box", item.ResolvedTypeFullName.Parts.Last());
        Assert.Equal("Int", item.TypeArguments.Single().Name);
        Assert.NotNull(item.ConstructedIdentity);
    }

    [Fact]
    public void ParameterizedAliasRejectsWrongTypeArgumentCount()
    {
        var project = CxProject.CreateDefaultApplicationProject("parameterized_alias_arity");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            alias BoxOf<T> = Box<T>;
            public class Box<T> { public T Value; }
            public class Holder { public BoxOf<int, string> Item; }
            """));

        var error = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(project));

        Assert.Contains("Alias 'BoxOf' expects 1 generic arguments", error.Message);
    }

    [Fact]
    public void GenericAliasRejectsWrongTypeArgumentCount()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_alias_arity");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            alias IntBox = Box<int, string>;
            public class Box<T> { public T Value; }
            public class Holder { public IntBox Item; }
            """));

        var error = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(project));

        Assert.Contains("Type 'Box' expects 1 generic arguments", error.Message);
    }

    [Fact]
    public void AliasChainsResolveAcrossSourceFiles()
    {
        var project = CxProject.CreateDefaultApplicationProject("alias_chain");
        project.AddCompilationContext(CompilerTestHelper.Parse(
            "alias First = Second;", "first.cx"));
        project.AddCompilationContext(CompilerTestHelper.Parse(
            "alias Second = Number; public struct Number {} public First Echo(First value) { return value; }",
            "second.cx"));

        new SemanticBinder().Bind(project);
    }

    [Fact]
    public void CyclicAliasesReportACompilationError()
    {
        var project = CxProject.CreateDefaultApplicationProject("alias_cycle");
        project.AddCompilationContext(CompilerTestHelper.Parse(
            "alias First = Second; alias Second = First; public First Echo(First value) { return value; }"));

        var error = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(project));

        Assert.Contains("Cyclic alias", error.Message);
    }

    [Fact]
    public void DuplicateAliasNamesAreRejectedEvenWhenUnused()
    {
        var project = CxProject.CreateDefaultApplicationProject("alias_duplicate");
        project.AddCompilationContext(CompilerTestHelper.Parse(
            "alias Value = First; alias Value = Second; public struct First {} public struct Second {}"));

        var error = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(project));

        Assert.Contains("Alias 'Value' is declared more than once", error.Message);
    }
}

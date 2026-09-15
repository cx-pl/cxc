using CxCompiler.Model.Errors;
using CxCompiler.Model.Project;
using CxCompiler.Model.Statements;
using CxCompiler.Model.Types;
using CxCompiler.Model.Types.BuiltInTypes;
using CxCompiler.OutputGenerators;
using CxCompiler.Semantics;

namespace CxCompiler.Tests;

public sealed class ForeachTests
{
    [Fact]
    public void ParserPreservesForeachStatement()
    {
        var project = CreateProject("""
            void Visit(int[] values) {
                foreach (var value in values) {
                    continue;
                }
            }
            """);

        var function = Assert.IsType<FunctionDeclaration>(
            project.CompilationContexts.Single().DeclarationScope.Declarations.Single());
        var statement = Assert.IsType<ForeachStatement>(function.Body!.Single());

        Assert.Equal("value", statement.VariableName);
        Assert.IsType<AutoType>(statement.DeclaredType);
        Assert.IsType<BlockStatement>(statement.Body);
    }

    [Fact]
    public void BinderInfersArrayElementTypeAndScopesVariable()
    {
        var validProject = CreateProject("""
            void Visit(int[] values) {
                foreach (var value in values) {
                    int copy = value;
                    break;
                }
            }
            """);
        new SemanticBinder().Bind(validProject);
        var function = Assert.IsType<FunctionDeclaration>(
            validProject.CompilationContexts.Single().DeclarationScope.Declarations.Single());
        var statement = Assert.IsType<ForeachStatement>(function.Body!.Single());

        Assert.Same(BuiltInSystemTypes.Int, statement.VariableType);

        var invalidProject = CreateProject("""
            void Visit(int[] values) {
                foreach (var value in values) {
                }
                value = 1;
            }
            """);
        var exception = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(invalidProject));

        Assert.Contains("Cannot resolve value 'value'", exception.Message);
    }

    [Fact]
    public void BinderRejectsNonArrayCollectionsAndElementTypeMismatch()
    {
        var nonArrayProject = CreateProject("""
            void Visit(int value) {
                foreach (var item in value) {
                }
            }
            """);
        var nonArrayException = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(nonArrayProject));
        Assert.Contains("foreach collection must be an array", nonArrayException.Message);

        var mismatchProject = CreateProject("""
            void Visit(int[] values) {
                foreach (string item in values) {
                }
            }
            """);
        var mismatchException = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(mismatchProject));
        Assert.Contains("Cannot iterate array elements of type 'System.Int' as 'System.String'", mismatchException.Message);
    }

    [Fact]
    public void EmitsArrayForeachLoop()
    {
        var project = CreateProject("""
            void Visit(int[] values) {
                foreach (int value in values) {
                    if (value == 0) {
                        continue;
                    }
                }
            }
            """);
        new SemanticBinder().Bind(project);
        var outputDirectory = Path.Combine(
            Path.GetTempPath(),
            $"cxc-tests-{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(outputDirectory);
            CCodeOutputGenerator.GenerateOutput(
                project,
                Path.Combine(outputDirectory, "Foreach.cx"));
            var generatedSource = File.ReadAllText(
                Path.Combine(outputDirectory, "Unnamed.c"));

            Assert.Contains(
                "struct CX_ID_3(cxcore, System, Array)* __cx_foreach_collection_value = values;",
                generatedSource);
            Assert.Contains(
                "__cx_foreach_index_value < __cx_foreach_collection_value->_length",
                generatedSource);
            Assert.Contains(
                "cx_int value = ((cx_int*)__cx_foreach_collection_value->_data)[__cx_foreach_index_value];",
                generatedSource);
            Assert.Contains("continue;", generatedSource);
        }
        finally
        {
            if (Directory.Exists(outputDirectory))
            {
                Directory.Delete(outputDirectory, recursive: true);
            }
        }
    }

    private static CxProject CreateProject(string source)
    {
        var project = CxProject.CreateUnnamedApplicationProject();
        project.AddCompilationContext(CompilerTestHelper.Parse(source));
        return project;
    }
}

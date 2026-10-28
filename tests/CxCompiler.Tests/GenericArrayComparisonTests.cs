using CxCompiler.Model.Expressions;
using CxCompiler.Model.Project;
using CxCompiler.Model.Statements;
using CxCompiler.Model.Types;
using CxCompiler.OutputGenerators;
using CxCompiler.Semantics;

namespace CxCompiler.Tests;

public sealed class GenericArrayComparisonTests
{
    [Fact]
    public void SpecializesComparisonsAgainstMatchingGenericArrayLengths()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_array_comparison");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public bool HasValues<T>(T[] values) { return values.Length > 0u; }
            public bool IsAtMostTwo<T>(T[] values) { return 2u >= values.Length; }
            public bool IsAtLeast<T>(T[] values, uint minimum) {
                return values.Length >= minimum;
            }
            public bool IsAtLeastZero<T>(T[] values) { return values.Length >= 0; }
            public bool Run() {
                int[] empty = new int[0];
                int[] values = new int[2];
                return !HasValues(empty) && HasValues(values) && IsAtMostTwo(values) &&
                    IsAtLeast(values, 2u) && IsAtLeastZero(values);
            }
            """));
        new SemanticBinder().Bind(project);

        var functions = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<FunctionDeclaration>()
            .Where(function => function.GenericTypeNames.Length > 0)
            .ToArray();
        Assert.Equal(4, functions.Length);
        Assert.All(functions, function =>
            Assert.IsType<BinaryExpression>(
                Assert.IsType<ReturnStatement>(function.Body![0]).Expression));
        Assert.Equal(4, project.GenericFunctionInstances.Count);

        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-generic-array-comparison-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(Path.Combine(directory, "generic_array_comparison.c"));
            Assert.Contains("cxcore_System_Collections_Array_Length___const_get", source);
            Assert.Contains("> 0u", source);
            Assert.Contains("2u >=", source);
            Assert.Contains(">= minimum", source);
            Assert.Contains(">= 0", source);
            Assert.DoesNotContain("_unknowntype_", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void RejectsArrayLengthComparisonWithoutGenericArrayParameter()
    {
        var project = CxProject.CreateDefaultApplicationProject("array_length_comparison_non_generic");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public bool Invalid<T>(int[] values) { return values.Length > 0u; }
            """));

        var error = Assert.Throws<CxCompiler.Model.Errors.CompilationErrorException>(
            () => new SemanticBinder().Bind(project));
        Assert.Contains("currently supports only", error.Message);
    }

    [Fact]
    public void RejectsNegativeSignedLiteralForGenericArrayLengthComparison()
    {
        var project = CxProject.CreateDefaultApplicationProject("negative_array_length_comparison");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public bool Invalid<T>(T[] values) { return values.Length >= -1; }
            """));

        var error = Assert.Throws<CxCompiler.Model.Errors.CompilationErrorException>(
            () => new SemanticBinder().Bind(project));
        Assert.Contains("currently supports only", error.Message);
    }
}

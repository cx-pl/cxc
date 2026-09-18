using CxCompiler.Model.Errors;
using CxCompiler.Model.Expressions;
using CxCompiler.Model.Project;
using CxCompiler.Model.Statements;
using CxCompiler.Model.Types;
using CxCompiler.Model.Types.BuiltInTypes;
using CxCompiler.OutputGenerators;
using CxCompiler.Semantics;

namespace CxCompiler.Tests;

public sealed class ArrayExpressionTests
{
    [Fact]
    public void ParserPreservesArrayCreationAccessAndAssignment()
    {
        var project = CreateProject("""
            int Read() {
                int[] values = new int[3];
                values[0] = 42;
                return values[0];
            }
            """);

        var function = Assert.IsType<FunctionDeclaration>(
            project.CompilationContexts.Single().DeclarationScope.Declarations.Single());
        var declaration = Assert.IsType<LocalVariableDeclarationStatement>(function.Body![0]);
        var assignment = Assert.IsType<AssignmentExpression>(
            Assert.IsType<ExpressionStatement>(function.Body[1]).Expression);
        var returnedAccess = Assert.IsType<ArrayAccessExpression>(
            Assert.IsType<ReturnStatement>(function.Body[2]).Expression);

        Assert.IsType<ArrayCreationExpression>(declaration.Declarators.Single().Initializer);
        Assert.IsType<ArrayAccessExpression>(assignment.Target);
        Assert.Single(returnedAccess.Indices);
    }

    [Fact]
    public void BinderInfersArrayAndElementTypes()
    {
        var project = CreateProject("""
            int Read() {
                var values = new int[3];
                values[0] = 42;
                return values[0];
            }
            """);

        new SemanticBinder().Bind(project);

        var function = Assert.IsType<FunctionDeclaration>(
            project.CompilationContexts.Single().DeclarationScope.Declarations.Single());
        var declaration = Assert.IsType<LocalVariableDeclarationStatement>(function.Body![0]);
        var arrayType = Assert.IsType<ArrayType>(declaration.Declarators.Single().Type);
        var assignment = Assert.IsType<AssignmentExpression>(
            Assert.IsType<ExpressionStatement>(function.Body[1]).Expression);

        Assert.Same(BuiltInSystemTypes.Int, arrayType.ElementType);
        Assert.Same(BuiltInSystemTypes.Int, assignment.Target.InferredType);
        Assert.Same(BuiltInSystemTypes.Int, assignment.InferredType);
    }

    [Fact]
    public void BinderRejectsInvalidArrayLengthsAndIndices()
    {
        var invalidLength = CreateProject("""
            void Main() {
                int[] values = new int[true];
            }
            """);
        var lengthException = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(invalidLength));
        Assert.Contains("Array length must be an integer", lengthException.Message);

        var invalidIndex = CreateProject("""
            int Read(int[] values) {
                return values[false];
            }
            """);
        var indexException = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(invalidIndex));
        Assert.Contains("Array index must be an integer", indexException.Message);

        var tooManyIndices = CreateProject("""
            int Read(int[] values) {
                return values[0, 1];
            }
            """);
        var dimensionException = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(tooManyIndices));
        Assert.Contains("Only one-dimensional array indexing is supported", dimensionException.Message);
    }

    [Fact]
    public void BinderRejectsNonArrayAccessAndElementAssignmentMismatch()
    {
        var nonArray = CreateProject("""
            int Read(int value) {
                return value[0];
            }
            """);
        var accessException = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(nonArray));
        Assert.Contains("Cannot index expression of type 'System.Int'", accessException.Message);

        var wrongElementType = CreateProject("""
            void Main() {
                int[] values = new int[1];
                values[0] = "text";
            }
            """);
        var assignmentException = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(wrongElementType));
        Assert.Contains("Cannot assign 'System.String' to 'System.Int'", assignmentException.Message);
    }

    [Fact]
    public void EmitsCheckedArrayAllocationReadsAndWrites()
    {
        var project = CreateProject("""
            int Read() {
                int[] values = new int[3];
                values[0] = 42;
                return values[0];
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
                Path.Combine(outputDirectory, "Arrays.cx"));
            var generatedSource = File.ReadAllText(
                Path.Combine(outputDirectory, "unnamed.c"));

            Assert.Contains(
                "cx_array_new((cx_uint)(3), (cx_uint)sizeof(cx_int))",
                generatedSource);
            Assert.Contains(
                "(*(cx_int*)cx_array_at(values, (cx_uint)(0), (cx_uint)sizeof(cx_int))) = 42;",
                generatedSource);
            Assert.Contains(
                "return (*(cx_int*)cx_array_at(values, (cx_uint)(0), (cx_uint)sizeof(cx_int)));",
                generatedSource);
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
        var project = CxProject.CreateDefaultApplicationProject();
        project.AddCompilationContext(CompilerTestHelper.Parse(source));
        return project;
    }
}

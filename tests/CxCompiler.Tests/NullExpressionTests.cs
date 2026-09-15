using CxCompiler.Model.Errors;
using CxCompiler.Model.Expressions;
using CxCompiler.Model.Project;
using CxCompiler.Model.Statements;
using CxCompiler.Model.Types;
using CxCompiler.Model.Types.BuiltInTypes;
using CxCompiler.OutputGenerators;
using CxCompiler.Semantics;

namespace CxCompiler.Tests;

public sealed class NullExpressionTests
{
    [Fact]
    public void BindsNullAndReferenceCoalescing()
    {
        var project = CreateProject("""
            string Select(string value) {
                string fallback = null;
                return value ?? fallback;
            }
            """);

        new SemanticBinder().Bind(project);

        var function = GetFunction(project);
        var declaration = Assert.IsType<LocalVariableDeclarationStatement>(function.Body![0]);
        var nullLiteral = Assert.IsType<LiteralExpression>(
            declaration.Declarators.Single().Initializer);
        var returnStatement = Assert.IsType<ReturnStatement>(function.Body[1]);
        var coalescing = Assert.IsType<NullCoalescingExpression>(returnStatement.Expression);

        Assert.Same(BuiltInSystemTypes.String, nullLiteral.InferredType);
        Assert.Same(BuiltInSystemTypes.String, coalescing.InferredType);
    }

    [Fact]
    public void BindsNullableValueCoalescingToUnderlyingType()
    {
        var project = CreateProject("""
            int Select(int? value) {
                return value ?? 42;
            }
            """);

        new SemanticBinder().Bind(project);

        var function = GetFunction(project);
        var returnStatement = Assert.IsType<ReturnStatement>(Assert.Single(function.Body!));
        var coalescing = Assert.IsType<NullCoalescingExpression>(returnStatement.Expression);

        Assert.Same(BuiltInSystemTypes.Int, coalescing.InferredType);
    }

    [Fact]
    public void AllowsNullEqualityForNullableValues()
    {
        var project = CreateProject("""
            bool IsMissing(int? value) {
                return value == null;
            }
            """);

        new SemanticBinder().Bind(project);

        var function = GetFunction(project);
        var returnStatement = Assert.IsType<ReturnStatement>(Assert.Single(function.Body!));
        var equality = Assert.IsType<BinaryExpression>(returnStatement.Expression);
        Assert.Same(BuiltInSystemTypes.Bool, equality.InferredType);
    }

    [Fact]
    public void RejectsNullForNonNullableValueType()
    {
        var project = CreateProject("""
            void Main() {
                int value = null;
            }
            """);

        var exception = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(project));

        Assert.Contains("Cannot initialize local 'value'", exception.Message);
    }

    [Fact]
    public void RejectsImplicitlyTypedNullLocal()
    {
        var project = CreateProject("""
            void Main() {
                var value = null;
            }
            """);

        var exception = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(project));

        Assert.Contains("Cannot infer the type", exception.Message);
    }

    [Fact]
    public void RejectsNonNullableCoalescingOperand()
    {
        var project = CreateProject("""
            int Select(int value) {
                return value ?? 42;
            }
            """);

        var exception = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(project));

        Assert.Contains("requires a nullable or reference left operand", exception.Message);
    }

    [Fact]
    public void EmitsReferenceAndNullableCoalescing()
    {
        var project = CreateProject("""
            string SelectText(string value) {
                return value ?? "fallback";
            }

            int SelectNumber(int? value) {
                return value ?? 42;
            }

            bool IsMissing(int? value) {
                return value == null;
            }

            int? MissingNumber() {
                return null;
            }

            string MissingText() {
                return null;
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
                Path.Combine(outputDirectory, "NullExpressions.cx"));
            var generatedSource = File.ReadAllText(
                Path.Combine(outputDirectory, "Unnamed.c"));

            Assert.Contains("((value) != CX_NULL ? (value) : &CX_ID_2(Unnamed, __string_", generatedSource);
            Assert.Contains("((value)._obj != CX_NULL ? *(cx_int*)(value)._obj : 42)", generatedSource);
            Assert.Contains("((value)._obj == CX_NULL)", generatedSource);
            Assert.Contains("return ( struct CX_ID_3(cxcore, System, Nullable)){ CX_NULL };", generatedSource);
            Assert.Contains("return CX_NULL;", generatedSource);
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

    private static FunctionDeclaration GetFunction(CxProject project)
    {
        return Assert.IsType<FunctionDeclaration>(
            project.CompilationContexts.Single().DeclarationScope.Declarations.Single());
    }
}

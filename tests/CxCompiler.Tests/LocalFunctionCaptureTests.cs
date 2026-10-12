using CxCompiler.Model.Project;
using CxCompiler.Model.Statements;
using CxCompiler.Model.Types;
using CxCompiler.OutputGenerators;
using CxCompiler.Semantics;

namespace CxCompiler.Tests;

public sealed class LocalFunctionCaptureTests
{
    [Fact]
    public void LocalFunctionCapturesAndReadsTheEnclosingVariableCell()
    {
        var project = CreateProject("""
            public int Run() {
                int value = 7;
                int Read() { return value; }
                value = 42;
                return Read();
            }
            """);

        var function = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<FunctionDeclaration>().Single();
        var localFunction = Assert.IsType<LocalFunctionDeclarationStatement>(function.Body![1]);
        var owner = Assert.IsType<CxCompiler.Model.Expressions.LambdaExpression>(
            localFunction.Function.LambdaOwner);
        var capture = Assert.Single(owner.Captures);
        Assert.Equal("value", capture.Local.Name);
        Assert.True(capture.Local.IsCaptured);

        var directory = Path.Combine(Path.GetTempPath(), $"cxc-local-capture-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(Path.Combine(directory, "local_capture.c"));
            Assert.Contains($"struct {localFunction.Function.LocalCName}_environment", source);
            Assert.Contains("__cx_environment", source);
            Assert.Contains("__cx_generated_cell_", source);
            Assert.DoesNotContain("_unknowntype_", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void CapturingLocalFunctionCanBeUsedAsADelegateValue()
    {
        var project = CreateProject("""
            public delegate int Reader();
            public int Run() {
                int value = 7;
                int Read() { return value; }
                Reader reader = Read;
                value = 42;
                return reader();
            }
            """);
        var function = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<FunctionDeclaration>().Single();
        var directory = Path.Combine(Path.GetTempPath(), $"cxc-local-capture-value-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(Path.Combine(directory, "local_capture.c"));
            Assert.Contains("__cx_environment", source);
            Assert.Contains("__function = &", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        Assert.NotNull(function.Body);
    }

    [Fact]
    public void LocalFunctionCapturesEnclosingParameterAndItsFunctionRemainsCallable()
    {
        var project = CreateProject("""
            public int Captured(int value) {
                int Read() { return value; }
                return Read();
            }
            public int Run() { return Captured(42); }
            """);
        var declarations = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<FunctionDeclaration>().ToArray();
        var captured = Assert.Single(declarations, function => function.Name == "Captured");
        var localFunction = Assert.IsType<LocalFunctionDeclarationStatement>(captured.Body![0]);
        var capture = Assert.Single(Assert.IsType<CxCompiler.Model.Expressions.LambdaExpression>(
            localFunction.Function.LambdaOwner).Captures);
        Assert.Equal("value", capture.Local.Name);
        Assert.True(capture.Local.IsCaptured);
        var run = Assert.Single(declarations, function => function.Name == "Run");
        Assert.IsType<CxCompiler.Model.Expressions.InvocationExpression>(
            Assert.IsType<ReturnStatement>(Assert.Single(run.Body!)).Expression);
    }

    [Fact]
    public void GenericLocalIdentityFunctionIsInferredAndSpecialized()
    {
        var project = CreateProject("""
            public int Run() {
                T Identity<T>(T value) { return value; }
                return Identity(42) + Identity<int>(0);
            }
            """);

        var instance = Assert.Single(project.GenericFunctionInstances);
        Assert.Equal("System.Int", instance.ReturnType.FullName.ToString());
        Assert.NotNull(instance.SpecializationName);
        Assert.Equal("Identity", instance.Declaration!.Name);
        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-generic-local-function-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(Path.Combine(directory, "local_capture.c"));
            Assert.Contains($"{instance.SpecializationName}(NULL, 42)", source);
            Assert.Contains($"cx_int {instance.SpecializationName}(void *__cx_environment, cx_int value)",
                source);
            Assert.DoesNotContain("_unknowntype_", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void GenericLocalSpecializationSelectsCapturedCustomBody()
    {
        var project = CreateProject("""
            public delegate int LocalAdjustment(int value);
            public int Run() {
                int offset = 10;
                int Adjust<int>(int value) { return value + offset; }
                T Adjust<T>(T value) { return value; }
                Adjust<long>(7L);
                Adjust<int>(30);
                LocalAdjustment adjustment = Adjust;
                return adjustment(32);
            }
            """);

        var run = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<FunctionDeclaration>().Single(function => function.Name == "Run");
        var delegateDeclarator = Assert.Single(
            run.Body!.OfType<LocalVariableDeclarationStatement>()
                .SelectMany(statement => statement.Declarators),
            declarator => declarator.Name == "adjustment");
        var delegateInitializer = Assert.IsType<CxCompiler.Model.Expressions.IdentifierExpression>(
            delegateDeclarator.Initializer);
        var specialization = Assert.IsType<LocalFunctionDeclarationStatement>(run.Body![1])
            .Function;
        Assert.Same(specialization,
            delegateInitializer.FunctionValueSymbol!.ImplementationDeclaration);
        var fallback = Assert.IsType<CxCompiler.Model.Expressions.InvocationExpression>(
            Assert.IsType<ExpressionStatement>(run.Body![3]).Expression);
        Assert.Null(fallback.TargetSymbol!.ImplementationDeclaration);
        var instance = Assert.Single(project.GenericFunctionInstances,
            target => ReferenceEquals(target.ImplementationDeclaration, specialization));

        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-generic-local-specialization-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(Path.Combine(directory, "local_capture.c"));
            Assert.Contains(instance.SpecializationName!, source);
            Assert.Contains("__cx_generated_cell_", source);
            Assert.DoesNotContain("_unknowntype_", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("""
        public int Run() {
            int Keep<int>(int value) { return value; }
            return Keep(1);
        }
        """, "No generic local function matches specialization 'Keep'")]
    [InlineData("""
        public int Run() {
            T Keep<T>(T value) { return value; }
            long Keep<int>(int value) { return value; }
            return Keep(1);
        }
        """, "No generic local function matches specialization 'Keep'")]
    [InlineData("""
        public int Run() {
            T Keep<T>(T value) { return value; }
            int Keep<int>(int value) { return value; }
            int Keep<int>(int value) { return value + 1; }
            return Keep(1);
        }
        """, "Duplicate generic local function specialization")]
    public void ValidatesLocalFunctionSpecializationTargets(
        string source,
        string expectedMessage)
    {
        var project = CxProject.CreateDefaultApplicationProject("local_capture");
        project.AddCompilationContext(CompilerTestHelper.Parse(source));

        var exception = Assert.Throws<CxCompiler.Model.Errors.CompilationErrorException>(
            () => new SemanticBinder().Bind(project));

        Assert.Contains(expectedMessage, exception.Message);
    }

    private static CxProject CreateProject(string source)
    {
        var project = CxProject.CreateDefaultApplicationProject("local_capture");
        project.AddCompilationContext(CompilerTestHelper.Parse(source));
        new SemanticBinder().Bind(project);
        return project;
    }
}

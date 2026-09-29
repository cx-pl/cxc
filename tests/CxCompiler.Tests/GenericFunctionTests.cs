using CxCompiler.Model.Expressions;
using CxCompiler.Model.Errors;
using CxCompiler.Model.Project;
using CxCompiler.Model.Statements;
using CxCompiler.Model.Types;
using CxCompiler.Model.Types.BuiltInTypes;
using CxCompiler.OutputGenerators;
using CxCompiler.Semantics;

namespace CxCompiler.Tests;

public sealed class GenericFunctionTests
{
    private const string Source = """
        public extern T Echo<T>(T value);
        public int RunInt() { return Echo(41); }
        public int RunIntAgain() { return Echo(40); }
        public long RunLong() { return Echo(41L); }
        """;

    [Fact]
    public void InfersSeparateConcreteSignatures()
    {
        var project = CreateProject();
        var functions = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<FunctionDeclaration>().ToArray();
        Assert.Equal(["T"], functions[0].GenericTypeNames);
        Assert.IsType<GenericType>(functions[0].ReturnType);
        Assert.IsType<GenericType>(Assert.Single(functions[0].Parameters).ParameterType);

        var intCall = Assert.IsType<InvocationExpression>(
            Assert.IsType<ReturnStatement>(functions[1].Body![0]).Expression);
        var longCall = Assert.IsType<InvocationExpression>(
            Assert.IsType<ReturnStatement>(functions[3].Body![0]).Expression);
        Assert.Same(BuiltInSystemTypes.Int, intCall.TargetSymbol!.ReturnType);
        Assert.Same(BuiltInSystemTypes.Long, longCall.TargetSymbol!.ReturnType);
        Assert.NotEqual(intCall.TargetSymbol.SpecializationName,
            longCall.TargetSymbol.SpecializationName);
        Assert.Equal(2, project.GenericFunctionInstances.Count);
    }

    [Theory]
    [InlineData("public extern T Merge<T>(T first, T second); void Main() { Merge(1, 2L); }")]
    [InlineData("public extern T Make<T>(); void Main() { Make(); }")]
    public void RejectsConflictingOrUninferredGenerics(string source)
    {
        var project = CxProject.CreateDefaultApplicationProject();
        project.AddCompilationContext(CompilerTestHelper.Parse(source));
        Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
    }

    [Fact]
    public void ExactNonGenericOverloadWinsAConversionTie()
    {
        var project = CxProject.CreateDefaultApplicationProject();
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public extern T Echo<T>(T value);
            public int Echo(int value) { return value + 1; }
            public int Run() { return Echo(4); }
            """));
        new SemanticBinder().Bind(project);
        var run = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<FunctionDeclaration>().Single(item => item.Name == "Run");
        var call = Assert.IsType<InvocationExpression>(
            Assert.IsType<ReturnStatement>(run.Body![0]).Expression);
        Assert.Null(call.TargetSymbol!.SpecializationName);
        Assert.Empty(project.GenericFunctionInstances);
    }

    [Fact]
    public void EmitsOneConcreteDeclarationPerUsedInstantiation()
    {
        var project = CreateProject();
        var directory = Path.Combine(Path.GetTempPath(), $"cxc-generic-functions-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var header = File.ReadAllText(Path.Combine(directory, "generic_function.h"));
            var source = File.ReadAllText(Path.Combine(directory, "generic_function.c"));
            foreach (var instance in project.GenericFunctionInstances)
            {
                Assert.Equal(1, header.Split(instance.SpecializationName!).Length - 1);
                Assert.Contains(instance.SpecializationName!, source);
            }
            Assert.DoesNotContain("void* __returnValue", header);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static CxProject CreateProject()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_function");
        project.AddCompilationContext(CompilerTestHelper.Parse(Source));
        new SemanticBinder().Bind(project);
        return project;
    }
}

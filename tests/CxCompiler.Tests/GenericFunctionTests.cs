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

    [Fact]
    public void BindsAndEmitsExplicitPrimitiveFunctionTypeArguments()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_explicit_call");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public T Identity<T>(T value) { return value; }
            public int Run() { return Identity<int>(23); }
            """));
        new SemanticBinder().Bind(project);

        var run = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<FunctionDeclaration>().Single(function => function.Name == "Run");
        var call = Assert.IsType<InvocationExpression>(Assert.IsType<ReturnStatement>(
            Assert.Single(run.Body!)).Expression);
        Assert.Same(BuiltInSystemTypes.Int, Assert.Single(call.ExplicitTypeArguments));
        Assert.Same(BuiltInSystemTypes.Int, call.TargetSymbol!.ReturnType);
        var directory = Path.Combine(Path.GetTempPath(), $"cxc-explicit-generic-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(Path.Combine(directory, "generic_explicit_call.c"));
            Assert.Contains($"return {call.TargetSymbol.SpecializationName}(23);", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ExplicitAndInferredCallsReuseOneFunctionSpecialization()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_explicit_dedup");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public T Identity<T>(T value) { return value; }
            public int RunExplicit() { return Identity<int>(31); }
            public int RunInferred() { return Identity(37); }
            """));
        new SemanticBinder().Bind(project);

        var calls = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<FunctionDeclaration>()
            .Where(function => function.Name is "RunExplicit" or "RunInferred")
            .Select(function => Assert.IsType<InvocationExpression>(
                Assert.IsType<ReturnStatement>(Assert.Single(function.Body!)).Expression))
            .ToArray();
        Assert.Single(project.GenericFunctionInstances);
        Assert.Equal(calls[0].TargetSymbol!.SpecializationName,
            calls[1].TargetSymbol!.SpecializationName);
        var specializationName = calls[0].TargetSymbol!.SpecializationName!;

        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-generic-explicit-dedup-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(Path.Combine(directory, "generic_explicit_dedup.c"));
            Assert.Equal(1, source.Split($"cx_int {specializationName}(").Length - 1);
            Assert.Contains($"return {specializationName}(31);", source);
            Assert.Contains($"return {specializationName}(37);", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("public T Identity<T>(T value) { return value; } public int Run() { return Identity<int, long>(1); }")]
    [InlineData("public T Identity<T>(T value) { return value; } public int Run() { return Identity<int>(1L); }")]
    public void RejectsInvalidExplicitFunctionTypeArguments(string source)
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_explicit_invalid");
        project.AddCompilationContext(CompilerTestHelper.Parse(source));
        Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
    }

    [Fact]
    public void InfersFunctionTypeArgumentsNestedInConstructedTypes()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_nested_inference");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public class Box<T> {}
            public extern T Unbox<T>(Box<T> value);
            public int Run(Box<int> value) { return Unbox(value); }
            """));
        new SemanticBinder().Bind(project);

        var run = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<FunctionDeclaration>().Single(function => function.Name == "Run");
        var call = Assert.IsType<InvocationExpression>(Assert.IsType<ReturnStatement>(
            Assert.Single(run.Body!)).Expression);
        Assert.Same(BuiltInSystemTypes.Int, call.TargetSymbol!.ReturnType);
        Assert.Single(project.GenericFunctionInstances);

        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-generic-nested-inference-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var header = File.ReadAllText(Path.Combine(directory, "generic_nested_inference.h"));
            Assert.Contains("cx_int", header);
            Assert.Contains(call.TargetSymbol!.SpecializationName!, header);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
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

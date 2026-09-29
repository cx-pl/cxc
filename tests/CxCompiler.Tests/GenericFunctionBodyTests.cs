using CxCompiler.Model.Errors;
using CxCompiler.Model.Expressions;
using CxCompiler.Model.Project;
using CxCompiler.Model.Statements;
using CxCompiler.Model.Types;
using CxCompiler.Model.Types.BuiltInTypes;
using CxCompiler.OutputGenerators;
using CxCompiler.Semantics;

namespace CxCompiler.Tests;

public sealed class GenericFunctionBodyTests
{
    private const string Source = """
        public T Identity<T>(T value) { return value; }
        public int RunInt() { return Identity(7); }
        public long RunLong() { return Identity(9L); }
        public int RunIntAgain() { return Identity(11); }
        """;

    [Fact]
    public void BindsIdentityBodyForDistinctConcreteCalls()
    {
        var project = CreateProject();
        var functions = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<FunctionDeclaration>().ToArray();
        Assert.IsType<GenericType>(functions[0].ReturnType);
        Assert.IsType<GenericType>(Assert.Single(functions[0].Parameters).ParameterType);
        var intCall = Assert.IsType<InvocationExpression>(
            Assert.IsType<ReturnStatement>(functions[1].Body![0]).Expression);
        var longCall = Assert.IsType<InvocationExpression>(
            Assert.IsType<ReturnStatement>(functions[2].Body![0]).Expression);
        Assert.Same(BuiltInSystemTypes.Int, intCall.TargetSymbol!.ReturnType);
        Assert.Same(BuiltInSystemTypes.Long, longCall.TargetSymbol!.ReturnType);
        Assert.Equal(2, project.GenericFunctionInstances.Count);
    }

    [Fact]
    public void EmitsOneConcreteBodyPerInstantiation()
    {
        var project = CreateProject();
        var directory = Path.Combine(Path.GetTempPath(), $"cxc-generic-body-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var header = File.ReadAllText(Path.Combine(directory, "generic_body.h"));
            var source = File.ReadAllText(Path.Combine(directory, "generic_body.c"));
            foreach (var instance in project.GenericFunctionInstances)
            {
                Assert.Equal(1, header.Split(instance.SpecializationName!).Length - 1);
                Assert.Contains(instance.SpecializationName!, source);
            }
            Assert.Equal(2, source.Split("return value;").Length - 1);
            Assert.DoesNotContain("_unknowntype_", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void RejectsBodyThatNeedsPerInstantiationBinding()
    {
        var project = CxProject.CreateDefaultApplicationProject();
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public T Identity<T>(T value) { T copy = value; return value; }
            """));
        Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
    }

    [Fact]
    public void BindsOneGenericLocalFromMatchingParameter()
    {
        var project = CreateCopyProject();
        var function = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<FunctionDeclaration>().First();
        var local = Assert.IsType<LocalVariableDeclarationStatement>(function.Body![0]);
        Assert.IsType<GenericType>(local.DeclaredType);
        Assert.IsType<GenericType>(Assert.Single(local.Declarators).Type);
        Assert.Equal(2, project.GenericFunctionInstances.Count);
    }

    [Fact]
    public void EmitsConcreteLocalForEveryUsedCopyInstantiation()
    {
        var project = CreateCopyProject();
        var directory = Path.Combine(Path.GetTempPath(), $"cxc-generic-copy-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(Path.Combine(directory, "generic_copy.c"));
            Assert.Contains("cx_int copy = value;", source);
            Assert.Contains("cx_long copy = value;", source);
            Assert.Equal(2, source.Split("return copy;").Length - 1);
            Assert.DoesNotContain("_unknowntype_", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void InfersTwoParametersForLocalCopyBody()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_pair_copy");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public T Pick<T, U>(T first, U second) { T copy = first; return copy; }
            public int RunInt() { return Pick(7, true); }
            public long RunLong() { return Pick(9L, false); }
            """));
        new SemanticBinder().Bind(project);
        Assert.Equal(2, project.GenericFunctionInstances.Count);
        var directory = Path.Combine(Path.GetTempPath(), $"cxc-generic-pair-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(Path.Combine(directory, "generic_pair_copy.c"));
            Assert.Contains("cx_int copy = first;", source);
            Assert.Contains("cx_long copy = first;", source);
            Assert.Equal(2, source.Split("return copy;").Length - 1);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void InfersArrayElementTypesForIdentityBody()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_array_identity");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public class First {}
            public class Second {}
            public T[] IdentityArray<T>(T[] value) { return value; }
            public First[] RunFirst(First[] input) { return IdentityArray(input); }
            public Second[] RunSecond(Second[] input) { return IdentityArray(input); }
            """));
        new SemanticBinder().Bind(project);
        var calls = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<FunctionDeclaration>()
            .Where(function => function.Name is "RunFirst" or "RunSecond")
            .Select(function => Assert.IsType<InvocationExpression>(
                Assert.IsType<ReturnStatement>(Assert.Single(function.Body!)).Expression))
            .ToArray();
        Assert.Equal("First", Assert.IsType<NamedType>(
            Assert.IsType<ArrayType>(calls[0].TargetSymbol!.ReturnType).ElementType).Name);
        Assert.Equal("Second", Assert.IsType<NamedType>(
            Assert.IsType<ArrayType>(calls[1].TargetSymbol!.ReturnType).ElementType).Name);
        Assert.Equal(2, project.GenericFunctionInstances.Count);
    }

    [Fact]
    public void EmitsSeparateArrayIdentitySpecializations()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_array_identity");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public class First {}
            public class Second {}
            public T[] IdentityArray<T>(T[] value) { return value; }
            public First[] RunFirst(First[] input) { return IdentityArray(input); }
            public Second[] RunSecond(Second[] input) { return IdentityArray(input); }
            """));
        new SemanticBinder().Bind(project);
        var directory = Path.Combine(Path.GetTempPath(), $"cxc-generic-array-identity-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var header = File.ReadAllText(Path.Combine(directory, "generic_array_identity.h"));
            var source = File.ReadAllText(Path.Combine(directory, "generic_array_identity.c"));
            foreach (var instance in project.GenericFunctionInstances)
            {
                Assert.Contains(instance.SpecializationName!, header);
                Assert.Contains(instance.SpecializationName!, source);
            }
            Assert.Equal(2, project.GenericFunctionInstances.Select(instance =>
                instance.SpecializationName).Distinct().Count());
            Assert.Equal(2, source.Split("return value;").Length - 1);
            Assert.DoesNotContain("_unknowntype_", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void RepeatedArrayParameterRejectsDifferentElementTypes()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_array_mismatch");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public class First {}
            public class Second {}
            public T[] PickSame<T>(T[] first, T[] second) { return first; }
            public First[] Run(First[] first, Second[] second) {
                return PickSame(first, second);
            }
            """));
        var error = Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
        Assert.Contains("No overload", error.Message);
    }

    private static CxProject CreateProject()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_body");
        project.AddCompilationContext(CompilerTestHelper.Parse(Source));
        new SemanticBinder().Bind(project);
        return project;
    }

    private static CxProject CreateCopyProject()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_copy");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public T Copy<T>(T value) { T copy = value; return copy; }
            public int RunInt() { return Copy(7); }
            public long RunLong() { return Copy(9L); }
            public int RunIntAgain() { return Copy(11); }
            """));
        new SemanticBinder().Bind(project);
        return project;
    }
}

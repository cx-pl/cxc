using CxCompiler.Model.Errors;
using CxCompiler.Model.Expressions;
using CxCompiler.Model.Project;
using CxCompiler.Model.Statements;
using CxCompiler.Model.Types;
using CxCompiler.Model.Types.BuiltInTypes;
using CxCompiler.OutputGenerators;
using CxCompiler.Semantics;

namespace CxCompiler.Tests;

public sealed class GenericMethodTests
{
    private const string Source = """
        public class Utility {
            public static T Identity<T>(T value) { return value; }
        }
        public int RunInt() { return Utility.Identity(7); }
        public long RunLong() { return Utility.Identity(9L); }
        """;

    [Fact]
    public void BindsStaticMethodCallsToDistinctSpecializations()
    {
        var project = CreateProject();
        var functions = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<FunctionDeclaration>().ToArray();
        var intCall = Assert.IsType<InvocationExpression>(
            Assert.IsType<ReturnStatement>(functions[0].Body![0]).Expression);
        var longCall = Assert.IsType<InvocationExpression>(
            Assert.IsType<ReturnStatement>(functions[1].Body![0]).Expression);
        Assert.Same(BuiltInSystemTypes.Int, intCall.TargetSymbol!.ReturnType);
        Assert.Same(BuiltInSystemTypes.Long, longCall.TargetSymbol!.ReturnType);
        Assert.Equal(2, project.GenericFunctionInstances.Count);
    }

    [Fact]
    public void GeneratedCEmitsStaticMethodBodiesOnce()
    {
        var project = CreateProject();
        var directory = Path.Combine(Path.GetTempPath(), $"cxc-generic-method-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var header = File.ReadAllText(Path.Combine(directory, "generic_method.h"));
            var source = File.ReadAllText(Path.Combine(directory, "generic_method.c"));
            foreach (var instance in project.GenericFunctionInstances)
            {
                Assert.Equal(1, header.Split(instance.SpecializationName!).Length - 1);
                Assert.Equal(2, source.Split(instance.SpecializationName!).Length - 1);
            }
            Assert.Equal(2, source.Split("return value;").Length - 1);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void BindsInstanceGenericCallsWithImplicitReceiver()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_instance_method");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public class Utility {
                public T Identity<T>(T value) { return value; }
                public int RunInt() { return Identity(7); }
                public long RunLong() { return Identity(9L); }
            }
            """));
        new SemanticBinder().Bind(project);
        var utility = Assert.IsType<ClassDeclaration>(
            Assert.Single(project.CompilationContexts.Single().DeclarationScope.Declarations));
        var functions = utility.MemberDeclarations.Declarations.OfType<FunctionDeclaration>().ToArray();
        var intCall = Assert.IsType<InvocationExpression>(
            Assert.IsType<ReturnStatement>(functions[1].Body![0]).Expression);
        Assert.Same(BuiltInSystemTypes.Int, intCall.TargetSymbol!.ReturnType);
        Assert.Equal(2, project.GenericFunctionInstances.Count);
    }

    [Fact]
    public void GeneratedCIncludesReceiverInInstanceSpecializations()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_instance_method");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public class Utility {
                public T Identity<T>(T value) { return value; }
                public int RunInt() { return Identity(7); }
            }
            """));
        new SemanticBinder().Bind(project);
        var instance = Assert.Single(project.GenericFunctionInstances);
        var directory = Path.Combine(Path.GetTempPath(), $"cxc-generic-instance-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var header = File.ReadAllText(Path.Combine(directory, "generic_instance_method.h"));
            var source = File.ReadAllText(Path.Combine(directory, "generic_instance_method.c"));
            Assert.Contains($"{instance.SpecializationName}(" +
                "struct CX_ID_2(generic_instance_method, Utility)* __this, cx_int value)", header);
            Assert.Contains($"{instance.SpecializationName}(__this, 7)", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void BindsStaticGenericMethodOnGenericClass()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_class_static");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public class Utility<T> {
                public static U Identity<U>(U value) { return value; }
                public static int Run() { return Identity(7); }
            }
            """));
        new SemanticBinder().Bind(project);
        var utility = Assert.IsType<ClassDeclaration>(Assert.Single(
            project.CompilationContexts.Single().DeclarationScope.Declarations));
        var run = utility.MemberDeclarations.Declarations.OfType<FunctionDeclaration>()
            .Single(function => function.Name == "Run");
        var call = Assert.IsType<InvocationExpression>(Assert.IsType<ReturnStatement>(
            Assert.Single(run.Body!)).Expression);
        Assert.Same(BuiltInSystemTypes.Int, call.TargetSymbol!.ReturnType);
        Assert.Single(project.GenericFunctionInstances);
    }

    [Fact]
    public void GeneratedCEmitsStaticGenericMethodOnGenericClass()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_class_static");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public class Utility<T> {
                public static U Identity<U>(U value) { return value; }
                public static int Run() { return Identity(7); }
            }
            """));
        new SemanticBinder().Bind(project);
        var instance = Assert.Single(project.GenericFunctionInstances);
        var directory = Path.Combine(Path.GetTempPath(), $"cxc-generic-class-static-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var header = File.ReadAllText(Path.Combine(directory, "generic_class_static.h"));
            var source = File.ReadAllText(Path.Combine(directory, "generic_class_static.c"));
            Assert.Contains($"extern CX_GENERIC_CLASS_STATIC_API cx_int {instance.SpecializationName}(cx_int value);", header);
            Assert.Contains($"cx_int {instance.SpecializationName}(cx_int value)", source);
            Assert.Contains($"return {instance.SpecializationName}(7);", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ExplicitTypeArgumentsBindAndEmitStaticGenericMethodOnGenericClass()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_class_static");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public class Utility<T> {
                public static U Identity<U>(U value) { return value; }
                public static int Run() { return Identity<int>(17); }
            }
            """));
        new SemanticBinder().Bind(project);

        var utility = Assert.IsType<ClassDeclaration>(Assert.Single(
            project.CompilationContexts.Single().DeclarationScope.Declarations));
        var run = utility.MemberDeclarations.Declarations.OfType<FunctionDeclaration>()
            .Single(function => function.Name == "Run");
        var call = Assert.IsType<InvocationExpression>(Assert.IsType<ReturnStatement>(
            Assert.Single(run.Body!)).Expression);
        Assert.Same(BuiltInSystemTypes.Int, Assert.Single(call.ExplicitTypeArguments));
        Assert.Same(BuiltInSystemTypes.Int, call.TargetSymbol!.ReturnType);
        var instance = Assert.Single(project.GenericFunctionInstances);

        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-generic-class-static-explicit-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(Path.Combine(directory, "generic_class_static.c"));
            Assert.Contains($"return {instance.SpecializationName}(17);", source);
            Assert.Contains($"cx_int {instance.SpecializationName}(cx_int value)", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ExplicitTypeArgumentsBindInstanceGenericMethod()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_instance_explicit");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public class Utility {
                public T Identity<T>(T value) { return value; }
            }
            public int Run(Utility utility) { return utility.Identity<int>(29); }
            """));
        new SemanticBinder().Bind(project);

        var run = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<FunctionDeclaration>().Single(function => function.Name == "Run");
        var call = Assert.IsType<InvocationExpression>(Assert.IsType<ReturnStatement>(
            Assert.Single(run.Body!)).Expression);
        Assert.Same(BuiltInSystemTypes.Int, Assert.Single(call.ExplicitTypeArguments));
        Assert.Same(BuiltInSystemTypes.Int, call.TargetSymbol!.ReturnType);
        Assert.NotNull(call.Receiver);

        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-generic-instance-explicit-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(Path.Combine(directory, "generic_instance_explicit.c"));
            Assert.Contains(call.TargetSymbol.SpecializationName!, source);
            Assert.Contains("29);", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("public class Utility { public T Echo<T>(T value) { return value; } } public int Run(Utility item) { return item.Echo<int, long>(1); }")]
    [InlineData("public class Utility { public T Echo<T>(T value) { return value; } } public int Run(Utility item) { return item.Echo<int>(1L); }")]
    public void RejectsInvalidExplicitInstanceMethodTypeArguments(string source)
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_instance_invalid");
        project.AddCompilationContext(CompilerTestHelper.Parse(source));
        Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
    }

    [Fact]
    public void GenericMethodOnGenericClassSpecializesItsClosedReceiver()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_class_instance_method");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public class Box<T> {
                public U Identity<U>(U value) { return value; }
            }
            public int RunInt(Box<int> box) { return box.Identity<int>(41); }
            public long RunLong(Box<long> box) { return box.Identity<long>(43L); }
            """));
        new SemanticBinder().Bind(project);

        var instances = project.GenericFunctionInstances
            .OrderBy(instance => instance.SpecializationName, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(2, instances.Length);
        Assert.Equal(2, instances.Select(instance => instance.SpecializationName).Distinct().Count());
        Assert.All(instances, instance => Assert.NotNull(instance.ClosedContainingType));

        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-generic-class-instance-method-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var header = File.ReadAllText(Path.Combine(directory,
                "generic_class_instance_method.h"));
            var source = File.ReadAllText(Path.Combine(directory,
                "generic_class_instance_method.c"));
            foreach (var instance in instances)
            {
                var receiver = $"struct {instance.ClosedContainingType!.ConstructedIdentity!.CIdentifier}* __this";
                Assert.Contains(receiver, header);
                Assert.Contains(receiver, source);
            }
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void GenericClassSpecializesDirectGenericFieldGetterAndSetter()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_class_field_accessors");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public class Box<T> {
                public T value;
                public constructor() {}
                public T Get() { return value; }
                public void Set(T next) { value = next; }
            }
            public int Read(Box<int> box) { return box.Get(); }
            public void Write(Box<int> box, int next) { box.Set(next); }
            """));
        new SemanticBinder().Bind(project);

        var instances = project.GenericFunctionInstances
            .OrderBy(instance => instance.SpecializationName, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(2, instances.Length); // getter and setter; empty constructor uses shared ABI
        Assert.Equal(2, instances.Select(instance => instance.SpecializationName).Distinct().Count());
        Assert.All(instances, instance => Assert.NotNull(instance.ClosedContainingType));

        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-generic-class-field-accessors-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var header = File.ReadAllText(Path.Combine(directory,
                "generic_class_field_accessors.h"));
            var source = File.ReadAllText(Path.Combine(directory,
                "generic_class_field_accessors.c"));
            Assert.Contains("return __this->value;", source);
            Assert.Contains("__this->value = next;", source);
            Assert.All(instances, instance => Assert.Contains(instance.SpecializationName!, header));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void StaticAndInstanceMethodsWithSameSignatureHaveDistinctNames()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_method_kinds");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public class Utility {
                public static T Echo<T>(T value) { return value; }
                public T Echo<T>(T value) { return value; }
            }
            public int RunStatic() { return Utility.Echo(7); }
            public int RunInstance(Utility value) { return value.Echo(8); }
            """));
        new SemanticBinder().Bind(project);
        var names = project.GenericFunctionInstances
            .Select(instance => instance.SpecializationName)
            .ToArray();
        Assert.Equal(2, names.Length);
        Assert.Equal(2, names.Distinct().Count());
    }

    [Fact]
    public void StaticGenericMethodCanReturnOneConcreteLocalCopy()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_method_copy");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public class Utility {
                public static T Copy<T>(T value) { T copy = value; return copy; }
            }
            public int Run() { return Utility.Copy(13); }
            """));
        new SemanticBinder().Bind(project);
        var directory = Path.Combine(Path.GetTempPath(), $"cxc-generic-method-copy-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(Path.Combine(directory, "generic_method_copy.c"));
            Assert.Contains("cx_int copy = value;", source);
            Assert.Contains("return copy;", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static CxProject CreateProject()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_method");
        project.AddCompilationContext(CompilerTestHelper.Parse(Source));
        new SemanticBinder().Bind(project);
        return project;
    }
}

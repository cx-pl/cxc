using CxCompiler.Model.Expressions;
using CxCompiler.Model.Errors;
using CxCompiler.Model.Project;
using CxCompiler.Model.Statements;
using CxCompiler.Model.Types;
using CxCompiler.OutputGenerators;
using CxCompiler.Semantics;

namespace CxCompiler.Tests;

public sealed class GenericOperatorTests
{
    private const string Source = """
        public struct Box<T> {
            public T value;
            public constructor(T initial) { value = initial; }
            public Box<T> operator +(Box<T> other) { return other; }
        }
        public Box<int> Add(Box<int> first, Box<int> second) { return first + second; }
        public Box<long> AddLong(Box<long> first, Box<long> second) { return first + second; }
        """;

    [Fact]
    public void ClosesGenericStructOperatorForEachValueLayout()
    {
        var project = CreateProject();
        var functions = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<FunctionDeclaration>().ToArray();
        var intExpression = Assert.IsType<BinaryExpression>(
            Assert.IsType<ReturnStatement>(functions[0].Body![0]).Expression);
        var longExpression = Assert.IsType<BinaryExpression>(
            Assert.IsType<ReturnStatement>(functions[1].Body![0]).Expression);

        Assert.NotNull(intExpression.OperatorSymbol?.SpecializationName);
        Assert.NotNull(longExpression.OperatorSymbol?.SpecializationName);
        Assert.Equal(2, project.GenericFunctionInstances.Count);
        Assert.Equal(
            project.GenericFunctionInstances.Select(instance => instance.SpecializationName).Order(),
            new[]
            {
                intExpression.OperatorSymbol!.SpecializationName,
                longExpression.OperatorSymbol!.SpecializationName,
            }.Order());
    }

    [Fact]
    public void EmitsClosedGenericStructOperatorBodies()
    {
        var project = CreateProject();
        var directory = Path.Combine(Path.GetTempPath(), $"cxc-generic-operator-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var header = File.ReadAllText(Path.Combine(directory, "generic_operator.h"));
            var source = File.ReadAllText(Path.Combine(directory, "generic_operator.c"));
            foreach (var instance in project.GenericFunctionInstances)
            {
                Assert.Contains(instance.SpecializationName!, header);
                Assert.Contains($"{instance.SpecializationName}(", source);
            }
            Assert.True(source.Split("return other;").Length - 1 >= 2);
            Assert.DoesNotContain("_unknowntype_", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ClosesGenericClassOperatorForEachValueLayout()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_class_operator");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public class Box<T> {
                public T value;
                public constructor(T initial) { value = initial; }
                public Box<T> operator +(Box<T> other) { return other; }
            }
            public Box<int> Add(Box<int> first, Box<int> second) { return first + second; }
            public Box<long> AddLong(Box<long> first, Box<long> second) { return first + second; }
            """));
        new SemanticBinder().Bind(project);

        var instances = project.GenericFunctionInstances
            .Where(instance => instance.Declaration?.OperatorToken == "+")
            .ToArray();
        Assert.Equal(2, instances.Length);
        Assert.All(instances, instance => Assert.NotNull(instance.SpecializationName));

        var directory = Path.Combine(Path.GetTempPath(), $"cxc-generic-class-operator-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(Path.Combine(directory, "generic_class_operator.c"));
            foreach (var instance in instances)
            {
                Assert.Contains($"{instance.SpecializationName}(", source);
            }
            Assert.DoesNotContain("_unknowntype_", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ClosesGenericOperatorMethodFromOperandType()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_operator_method");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public struct GenericOperator {
                public T operator +<T>(T other) where T : struct { return other; }
            }
            public int Add(GenericOperator value, int other) { return value + other; }
            public long AddLong(GenericOperator value, long other) { return value + other; }
            """));

        new SemanticBinder().Bind(project);

        var functions = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<FunctionDeclaration>()
            .Where(function => function.Name is "Add" or "AddLong")
            .ToArray();
        var operators = functions.Select(function =>
            Assert.IsType<BinaryExpression>(
                Assert.IsType<ReturnStatement>(function.Body![0]).Expression)
                .OperatorSymbol!).ToArray();
        Assert.All(operators, symbol => Assert.NotNull(symbol.SpecializationName));
        Assert.Equal(2, project.GenericFunctionInstances.Count);
        Assert.Equal(
            project.GenericFunctionInstances.Select(instance => instance.SpecializationName).Order(),
            operators.Select(symbol => symbol.SpecializationName).Order());

        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-generic-operator-method-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(Path.Combine(directory, "generic_operator_method.c"));
            foreach (var symbol in operators)
            {
                Assert.Contains($"{symbol.SpecializationName}(", source);
            }
            Assert.DoesNotContain("_unknowntype_", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void SelectsSpecializedBodyForGenericOperatorMethod()
    {
        var project = CxProject.CreateDefaultApplicationProject(
            "generic_operator_method_specialization");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public struct GenericOperator {
                public T operator +<T>(T other) where T : struct { return other; }
                public int operator +<int>(int other) { return other + 5; }
            }
            public int Add(GenericOperator value, int other) { return value + other; }
            public long AddLong(GenericOperator value, long other) { return value + other; }
            """));

        new SemanticBinder().Bind(project);

        var functions = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<FunctionDeclaration>().Where(function =>
                function.Name is "Add" or "AddLong").ToArray();
        var binary = Assert.IsType<BinaryExpression>(
            Assert.IsType<ReturnStatement>(functions.Single(function =>
                function.Name == "Add").Body![0]).Expression);
        var fallbackBinary = Assert.IsType<BinaryExpression>(
            Assert.IsType<ReturnStatement>(functions.Single(function =>
                function.Name == "AddLong").Body![0]).Expression);
        var symbol = Assert.IsType<FunctionSymbol>(binary.OperatorSymbol);
        var specialization = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<ClassDeclaration>().Single(type => type.Name == "GenericOperator")
            .MemberDeclarations.Declarations.OfType<FunctionDeclaration>()
            .Single(function => function.IsGenericSpecialization);

        Assert.Same(specialization, symbol.ImplementationDeclaration);
        Assert.Null(fallbackBinary.OperatorSymbol!.ImplementationDeclaration);
        Assert.Equal(2, project.GenericFunctionInstances.Count);

        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-generic-operator-specialization-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(Path.Combine(
                directory, "generic_operator_method_specialization.c"));
            Assert.Contains(symbol.SpecializationName!, source);
            Assert.Contains("other + 5", source);
            Assert.DoesNotContain("_unknowntype_", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void SelectsSpecializedBodyForMutatingGenericOperatorMethod()
    {
        var project = CxProject.CreateDefaultApplicationProject(
            "generic_mutating_operator_method_specialization");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public interface IRecorder { void Record(); }
            public class Token : IRecorder { public int Value; public void Record() {} }
            public class OtherToken : IRecorder { public void Record() {} }
            public struct Accumulator {
                public int Value;
                public void operator +=<T>(T other) where T : IRecorder {
                    other.Record();
                }
                public void operator +=<Token>(Token other) { Value += other.Value; }
            }
            public void AddToken(Accumulator value, Token token) { value += token; }
            public void AddOther(Accumulator value, OtherToken other) { value += other; }
            """));

        new SemanticBinder().Bind(project);

        var functions = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<FunctionDeclaration>().Where(function =>
                function.Name is "AddToken" or "AddOther").ToArray();
        var specialized = Assert.IsType<AssignmentExpression>(
            Assert.IsType<ExpressionStatement>(functions.Single(function =>
                function.Name == "AddToken").Body![0]).Expression).OperatorSymbol!;
        var fallback = Assert.IsType<AssignmentExpression>(
            Assert.IsType<ExpressionStatement>(functions.Single(function =>
                function.Name == "AddOther").Body![0]).Expression).OperatorSymbol!;
        var declaration = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<ClassDeclaration>().Single(type => type.Name == "Accumulator")
            .MemberDeclarations.Declarations.OfType<FunctionDeclaration>()
            .Single(function => function.IsGenericSpecialization);

        Assert.Same(declaration, specialized.ImplementationDeclaration);
        Assert.Null(fallback.ImplementationDeclaration);
        Assert.Equal(2, project.GenericFunctionInstances.Count);
    }

    [Fact]
    public void SelectsSpecializedBodyForMutatingGenericSubtractAssignmentOperatorMethod()
    {
        var project = CxProject.CreateDefaultApplicationProject(
            "generic_mutating_subtract_assignment_specialization");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public interface IRecorder { void Record(); }
            public class Token : IRecorder { public int Value; public void Record() {} }
            public class OtherToken : IRecorder { public void Record() {} }
            public struct Accumulator {
                public int Value;
                public void operator -=<T>(T other) where T : IRecorder {
                    other.Record();
                }
                public void operator -=<Token>(Token other) { Value -= other.Value; }
            }
            public void SubtractToken(Accumulator value, Token token) { value -= token; }
            public void SubtractOther(Accumulator value, OtherToken other) { value -= other; }
            """));

        new SemanticBinder().Bind(project);

        var functions = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<FunctionDeclaration>().Where(function =>
                function.Name is "SubtractToken" or "SubtractOther").ToArray();
        var specialized = Assert.IsType<AssignmentExpression>(
            Assert.IsType<ExpressionStatement>(functions.Single(function =>
                function.Name == "SubtractToken").Body![0]).Expression).OperatorSymbol!;
        var fallback = Assert.IsType<AssignmentExpression>(
            Assert.IsType<ExpressionStatement>(functions.Single(function =>
                function.Name == "SubtractOther").Body![0]).Expression).OperatorSymbol!;
        var declaration = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<ClassDeclaration>().Single(type => type.Name == "Accumulator")
            .MemberDeclarations.Declarations.OfType<FunctionDeclaration>()
            .Single(function => function.IsGenericSpecialization);

        Assert.Same(declaration, specialized.ImplementationDeclaration);
        Assert.Null(fallback.ImplementationDeclaration);
        Assert.Equal(2, project.GenericFunctionInstances.Count);
    }

    [Fact]
    public void SelectsSpecializedBodiesForRemainingMutatingGenericOperators()
    {
        var project = CxProject.CreateDefaultApplicationProject(
            "generic_mutating_operator_specialization_matrix");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public interface IRecorder { void Record(); }
            public class Token : IRecorder { public int Value; public void Record() {} }
            public class OtherToken : IRecorder { public void Record() {} }
            public struct Accumulator {
                public int Value;
                public void operator *=<T>(T other) where T : IRecorder { other.Record(); }
                public void operator *=<Token>(Token other) { Value *= other.Value; }
                public void operator /=<T>(T other) where T : IRecorder { other.Record(); }
                public void operator /=<Token>(Token other) { Value /= other.Value; }
                public void operator %=<T>(T other) where T : IRecorder { other.Record(); }
                public void operator %=<Token>(Token other) { Value %= other.Value; }
            }
            public void ApplyCustom(Accumulator value, Token token) {
                value *= token; value /= token; value %= token;
            }
            public void ApplyFallback(Accumulator value, OtherToken token) {
                value *= token; value /= token; value %= token;
            }
            """));

        new SemanticBinder().Bind(project);

        var functions = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<FunctionDeclaration>().ToDictionary(function => function.Name);
        var specializedSymbols = functions["ApplyCustom"].Body!
            .OfType<ExpressionStatement>()
            .Select(statement => Assert.IsType<AssignmentExpression>(statement.Expression)
                .OperatorSymbol!).ToArray();
        var fallbackSymbols = functions["ApplyFallback"].Body!
            .OfType<ExpressionStatement>()
            .Select(statement => Assert.IsType<AssignmentExpression>(statement.Expression)
                .OperatorSymbol!).ToArray();

        Assert.Equal(3, specializedSymbols.Length);
        Assert.All(specializedSymbols, symbol =>
            Assert.True(symbol.ImplementationDeclaration?.IsGenericSpecialization));
        Assert.All(fallbackSymbols, symbol => Assert.Null(symbol.ImplementationDeclaration));
        Assert.Equal(6, project.GenericFunctionInstances.Count);
    }

    [Fact]
    public void ClosesGenericOperatorMethodOnConstructedClass()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_operator_method_class");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public class Marker {}
            public class GenericOperatorContainer<T> {
                public T marker;
                public U operator +<U>(U other) where U : struct { return other; }
                public int operator +<int>(int other) { return other + 9; }
            }
            public int Add(GenericOperatorContainer<Marker> value, int other) {
                return value + other;
            }
            """));

        new SemanticBinder().Bind(project);

        var function = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<FunctionDeclaration>()
            .Single(declaration => declaration.Name == "Add");
        var binary = Assert.IsType<BinaryExpression>(
            Assert.IsType<ReturnStatement>(function.Body![0]).Expression);
        var symbol = Assert.IsType<FunctionSymbol>(binary.OperatorSymbol);
        Assert.NotNull(symbol.SpecializationName);
        Assert.NotNull(symbol.ClosedContainingType);
        var specialization = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<ClassDeclaration>().Single(type => type.Name == "GenericOperatorContainer")
            .MemberDeclarations.Declarations.OfType<FunctionDeclaration>()
            .Single(member => member.IsGenericSpecialization);
        Assert.Same(specialization, symbol.ImplementationDeclaration);
        Assert.Equal("Marker", symbol.ClosedContainingType!.TypeArguments.Single().Name);
        Assert.Contains(project.GenericFunctionInstances,
            instance => instance.SpecializationName == symbol.SpecializationName &&
                ReferenceEquals(instance.ClosedContainingType, symbol.ClosedContainingType));

        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-generic-operator-method-class-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(
                Path.Combine(directory, "generic_operator_method_class.c"));
            Assert.Contains($"{symbol.SpecializationName}(", source);
            Assert.Contains("other + 9", source);
            Assert.DoesNotContain("_unknowntype_", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void InfersUnaryGenericOperatorTypeFromExpectedResultAndSelectsSpecialization()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_unary_operator");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public struct GenericUnaryOperator {
                public T operator -<T>() where T : struct { return default; }
                public int operator -<int>() { return 17; }
            }
            public int NegateInt(GenericUnaryOperator value) { return -value; }
            public int NegateLocal(GenericUnaryOperator value) {
                int result = -value;
                return result;
            }
            public long NegateLong(GenericUnaryOperator value) { return -value; }
            """));

        new SemanticBinder().Bind(project);

        var functions = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<FunctionDeclaration>()
            .Where(function => function.Name is "NegateInt" or "NegateLong")
            .ToDictionary(function => function.Name);
        var intUnary = Assert.IsType<UnaryExpression>(
            Assert.IsType<ReturnStatement>(functions["NegateInt"].Body![0]).Expression);
        var longUnary = Assert.IsType<UnaryExpression>(
            Assert.IsType<ReturnStatement>(functions["NegateLong"].Body![0]).Expression);
        var intSymbol = Assert.IsType<FunctionSymbol>(intUnary.OperatorSymbol);
        var longSymbol = Assert.IsType<FunctionSymbol>(longUnary.OperatorSymbol);

        Assert.Equal("Int", intSymbol.GenericTypeArguments["T"].Name);
        Assert.True(intSymbol.ImplementationDeclaration?.IsGenericSpecialization);
        Assert.Equal("Long", longSymbol.GenericTypeArguments["T"].Name);
        Assert.Null(longSymbol.ImplementationDeclaration);
        Assert.Equal(2, project.GenericFunctionInstances.Count);

        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-generic-unary-operator-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(Path.Combine(directory, "generic_unary_operator.c"));
            Assert.Contains($"{intSymbol.SpecializationName}(", source);
            Assert.Contains("return 17;", source);
            Assert.DoesNotContain("_unknowntype_", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void RejectsGenericUnaryOperatorWhenExpectedResultCannotBeInferred()
    {
        var project = CxProject.CreateDefaultApplicationProject(
            "generic_unary_operator_without_result_context");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public struct GenericUnaryOperator {
                public T operator -<T>() where T : struct { return default; }
            }
            public void Discard(GenericUnaryOperator value) { -value; }
            """));

        var exception = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(project));

        Assert.Contains("Operator '-' cannot be applied", exception.Message);
    }

    private static CxProject CreateProject()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_operator");
        project.AddCompilationContext(CompilerTestHelper.Parse(Source));
        new SemanticBinder().Bind(project);
        return project;
    }
}

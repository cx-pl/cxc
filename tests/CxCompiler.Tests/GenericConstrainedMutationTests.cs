using CxCompiler.Model.Project;
using CxCompiler.Model.Errors;
using CxCompiler.Model.Types;
using CxCompiler.OutputGenerators;
using CxCompiler.Semantics;

namespace CxCompiler.Tests;

public sealed class GenericConstrainedMutationTests
{
    [Fact]
    public void BindsBuiltInCompoundPropertyAndIndexerAssignments()
    {
        const string source = """
            public class BaseStore {
                private int stored;
                public int Value { get { return stored; } set(value) { stored = value; } }
                public int Item { get(uint index) { return stored; } set(uint index, int value) { stored = value; } }
            }
            public interface IStore {
                int Value { get; set; }
                int Item { get(uint index); set(uint index); }
            }
            public void MutateClass<T>(T item, int delta) where T : BaseStore {
                item.Value += delta;
                item.Item[1u] *= delta + 1;
            }
            public void MutateInterface<T>(T item, uint index, int delta) where T : IStore {
                item.Value -= delta;
                item.Item[index] += delta > 0 ? delta : 1;
            }
            """;

        var project = Compile(source);
        var functions = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<FunctionDeclaration>()
            .Where(function => function.Name is "MutateClass" or "MutateInterface")
            .ToDictionary(function => function.Name);

        Assert.Equal(["+=", "*="], GetAssignmentOperators(functions["MutateClass"]));
        Assert.Equal(["-=", "+="], GetAssignmentOperators(functions["MutateInterface"]));
    }

    [Fact]
    public void EmitsConstrainedGetterOperatorSetterSequence()
    {
        const string source = """
            public class BaseStore {
                private int stored;
                public int Value { get { return stored; } set(value) { stored = value; } }
            }
            public void Add<T>(T item, int delta) where T : BaseStore {
                item.Value += delta;
            }
            public void Run(BaseStore item, int delta) { Add(item, delta); }
            """;

        var project = Compile(source);
        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-constrained-compound-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var generated = File.ReadAllText(
                Path.Combine(directory, "constrained_mutation.c"));

            Assert.Contains("__cx_property_value_0 += delta", generated,
                StringComparison.Ordinal);
            Assert.Contains("Value___get", generated, StringComparison.Ordinal);
            Assert.Contains("Value___set", generated, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void RejectsCompoundOperatorsOutsideTheSupportedSlice()
    {
        var project = CxProject.CreateDefaultApplicationProject("constrained_mutation");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public interface IStore { int Value { get; set; } }
            public void Shift<T>(T item, int delta) where T : IStore {
                item.Value <<= delta;
            }
            """));

        var error = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(project));

        Assert.Contains("Generic function 'Shift' currently supports only", error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SupportsConstrainedMemberResultsInPropertyAndIndexerWrites()
    {
        var project = CxProject.CreateDefaultApplicationProject("constrained_mutation");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public interface IStore {
                int Value { get; set; }
                int Item { get(uint index); set(uint index); }
                int Adjust(int value);
            }
            public class Store : IStore {
                private int stored;
                public int Value { get { return stored; } set(value) { stored = value; } }
                public int Item { get(uint index) { return stored; } set(uint index, int value) { stored = value; } }
                public int Adjust(int value) { return stored + value; }
            }
            public void Apply<T>(T item, uint index, int amount) where T : IStore {
                item.Value = item.Adjust(amount) + item.Value;
                item.Item[index] = item.Adjust(amount) > 0 ? item.Value : amount;
            }
            public void Run(Store item, uint index, int amount) {
                Apply(item, index, amount);
            }
            """));
        new SemanticBinder().Bind(project);

        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-constrained-member-write-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var generated = File.ReadAllText(
                Path.Combine(directory, "constrained_mutation.c"));

            Assert.Contains("Adjust", generated, StringComparison.Ordinal);
            Assert.Contains("struct cx_iface_ref", generated, StringComparison.Ordinal);
            Assert.Contains("Value___set", generated, StringComparison.Ordinal);
            Assert.Contains("Item___set", generated, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ReturnsAConstrainedMemberResultThroughATypedLocal()
    {
        var project = CxProject.CreateDefaultApplicationProject("constrained_local_return");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public interface IReadable { int Read(int offset); }
            public class Readable : IReadable {
                public int Value;
                public int Read(int offset) { return Value + offset; }
            }
            public class BaseReadable {
                public int Read(int offset) { return offset * 2; }
            }
            public int ReadIntoLocal<T>(T item, int offset) where T : IReadable {
                int result = item.Read(offset) + item.Read(offset + 1);
                return result;
            }
            public int ReadClassIntoLocal<T>(T item, int offset)
                where T : BaseReadable {
                int result = item.Read(offset) + item.Read(offset + 1);
                return result;
            }
            public int Run(Readable item) { return ReadIntoLocal(item, 4); }
            public int RunClass(BaseReadable item) { return ReadClassIntoLocal(item, 4); }
            """));
        new SemanticBinder().Bind(project);

        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-constrained-local-return-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var generated = File.ReadAllText(
                Path.Combine(directory, "constrained_local_return.c"));

            Assert.Contains("result =", generated, StringComparison.Ordinal);
            Assert.Contains("Read", generated, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void RejectsInferredLocalForConstrainedMemberResult()
    {
        var project = CxProject.CreateDefaultApplicationProject("constrained_local_return");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public interface IReadable { int Read(int offset); }
            public int ReadIntoLocal<T>(T item, int offset) where T : IReadable {
                var result = item.Read(offset);
                return result;
            }
            """));

        var error = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(project));

        Assert.Contains("Generic function 'ReadIntoLocal' currently supports only",
            error.Message, StringComparison.Ordinal);
    }

    private static string[] GetAssignmentOperators(FunctionDeclaration function) =>
        function.Body!.Select(statement =>
                Assert.IsType<CxCompiler.Model.Expressions.AssignmentExpression>(
                    Assert.IsType<CxCompiler.Model.Statements.ExpressionStatement>(statement)
                        .Expression))
            .Select(assignment => assignment.Operator)
            .ToArray();

    private static CxProject Compile(string source)
    {
        var project = CxProject.CreateDefaultApplicationProject("constrained_mutation");
        project.AddCompilationContext(CompilerTestHelper.Parse(source));
        new SemanticBinder().Bind(project);
        return project;
    }
}

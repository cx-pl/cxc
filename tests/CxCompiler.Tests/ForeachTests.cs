using CxCompiler.Model.Errors;
using CxCompiler.Model.Expressions;
using CxCompiler.Model.Project;
using CxCompiler.Model.Statements;
using CxCompiler.Model.Types;
using CxCompiler.Model.Types.BuiltInTypes;
using CxCompiler.OutputGenerators;
using CxCompiler.Semantics;

namespace CxCompiler.Tests;

public sealed class ForeachTests
{
    [Fact]
    public void ParserPreservesForeachStatement()
    {
        var project = CreateProject("""
            void Visit(int[] values) {
                foreach (var value in values) {
                    continue;
                }
            }
            """);

        var function = Assert.IsType<FunctionDeclaration>(
            project.CompilationContexts.Single().DeclarationScope.Declarations.Single());
        var statement = Assert.IsType<ForeachStatement>(function.Body!.Single());

        Assert.Equal("value", statement.VariableName);
        Assert.IsType<AutoType>(statement.DeclaredType);
        Assert.IsType<BlockStatement>(statement.Body);
    }

    [Fact]
    public void BinderInfersArrayElementTypeAndScopesVariable()
    {
        var validProject = CreateProject("""
            void Visit(int[] values) {
                foreach (var value in values) {
                    int copy = value;
                    break;
                }
            }
            """);
        new SemanticBinder().Bind(validProject);
        var function = Assert.IsType<FunctionDeclaration>(
            validProject.CompilationContexts.Single().DeclarationScope.Declarations.Single());
        var statement = Assert.IsType<ForeachStatement>(function.Body!.Single());

        Assert.Same(BuiltInSystemTypes.Int, statement.VariableType);

        var invalidProject = CreateProject("""
            void Visit(int[] values) {
                foreach (var value in values) {
                }
                value = 1;
            }
            """);
        var exception = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(invalidProject));

        Assert.Contains("Cannot resolve value 'value'", exception.Message);
    }

    [Fact]
    public void BinderRejectsUnsupportedCollectionsAndArrayElementTypeMismatch()
    {
        var nonArrayProject = CreateProject("""
            void Visit(int value) {
                foreach (var item in value) {
                }
            }
            """);
        var nonArrayException = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(nonArrayProject));
        Assert.Contains("must be an array or expose a valid enumerator", nonArrayException.Message);

        var mismatchProject = CreateProject("""
            void Visit(int[] values) {
                foreach (string item in values) {
                }
            }
            """);
        var mismatchException = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(mismatchProject));
        Assert.Contains("Cannot iterate array elements of type 'System.Int' as 'System.String'", mismatchException.Message);
    }

    [Fact]
    public void BindsAndEmitsStringForeachUsingUnicodeScalarAccessors()
    {
        var project = CreateProject("""
            public int Sum(string text) {
                int sum = 0;
                foreach (var scalar in text) { sum += scalar; }
                return sum;
            }
            """);
        new SemanticBinder().Bind(project);

        var function = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<FunctionDeclaration>().Single();
        var foreachStatement = Assert.IsType<ForeachStatement>(function.Body![1]);
        Assert.Same(BuiltInSystemTypes.Char, foreachStatement.VariableType);
        Assert.True(foreachStatement.IsStringIteration);
        Assert.Null(foreachStatement.EnumeratorSymbol);

        var (_, source) = GenerateOutput(project);
        Assert.Contains("cxcore_System_String_Length___const_get", source);
        Assert.Contains("cxcore_System_String_Item___const_get", source);

        var invalidProject = CreateProject("""
            void Visit(string text) {
                foreach (int scalar in text) { }
            }
            """);
        var exception = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(invalidProject));
        Assert.Contains("Cannot iterate string characters", exception.Message);
    }

    [Fact]
    public void BindsAndEmitsEnumeratorPatternForeach()
    {
        var project = CreateProject("""
            public class Cursor {
                private int _index;
                public int Current { const get { return _index; } }
                public bool MoveNext() { _index += 1; return _index <= 3; }
            }
            public class Sequence {
                public Cursor GetEnumerator() const { return new Cursor(); }
            }
            public int Sum(Sequence sequence) {
                int sum = 0;
                foreach (var value in sequence) { sum += value; }
                return sum;
            }
            """);
        new SemanticBinder().Bind(project);
        var function = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<FunctionDeclaration>().Single(item => item.Name == "Sum");
        var foreachStatement = Assert.IsType<ForeachStatement>(function.Body![1]);

        Assert.Same(BuiltInSystemTypes.Int, foreachStatement.VariableType);
        Assert.NotNull(foreachStatement.GetEnumeratorInvocation?.TargetSymbol);
        Assert.NotNull(foreachStatement.MoveNextInvocation?.TargetSymbol);
        Assert.NotNull(foreachStatement.CurrentExpression);

        var outputDirectory = Path.Combine(Path.GetTempPath(), $"cxc-foreach-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(outputDirectory);
            CCodeOutputGenerator.GenerateOutput(
                project, Path.Combine(outputDirectory, "input.cx"));
            var source = File.ReadAllText(Path.Combine(outputDirectory, "unnamed.c"));
            Assert.Contains("GetEnumerator", source);
            Assert.Contains("MoveNext", source);
            Assert.Contains("Current", source);
            Assert.Contains("while (", source);
        }
        finally
        {
            if (Directory.Exists(outputDirectory)) Directory.Delete(outputDirectory, recursive: true);
        }
    }

    [Fact]
    public void BindsAndEmitsForeachThroughGenericEnumerableInterfaces()
    {
        var project = CreateProject("""
            public interface IEnumerator<T> {
                T Current { const get; }
                bool MoveNext();
            }
            public interface IEnumerable<T> {
                IEnumerator<T> GetEnumerator() const;
            }
            public class Cursor : IEnumerator<int> {
                private int _index;
                public int Current { const get { return _index; } }
                public bool MoveNext() { _index += 1; return _index <= 3; }
            }
            public class Sequence : IEnumerable<int> {
                public IEnumerator<int> GetEnumerator() const {
                    return new Cursor();
                }
            }
            public int Sum(IEnumerable<int> sequence) {
                int sum = 0;
                foreach (var value in sequence) { sum += value; }
                return sum;
            }
            """);

        new SemanticBinder().Bind(project);
        var (header, source) = GenerateOutput(project);

        Assert.Contains("IEnumerable", header);
        Assert.Contains("IEnumerator", header);
        Assert.DoesNotContain("_unknowntype_", source);
        Assert.Contains("while (", source);
        Assert.Contains(".vtable", source);
    }

    [Fact]
    public void ResolvesConstructedTypeInExplicitEnumeratorForeachVariable()
    {
        var project = CreateProject("""
            public struct Pair<T> {
                public T Value;
                public constructor(T value) { Value = value; }
            }
            public class Cursor<T> {
                public Pair<T> Current() { return default; }
                public bool MoveNext() { return false; }
            }
            public class Sequence<T> {
                public Cursor<T> GetEnumerator() const { return new Cursor<T>(); }
            }
            public void Visit(Sequence<int> sequence) {
                foreach (Pair<int> pair in sequence) { int value = pair.Value; }
            }
            """);
        new SemanticBinder().Bind(project);
        var function = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<FunctionDeclaration>().Single(item => item.Name == "Visit");
        var foreachStatement = Assert.IsType<ForeachStatement>(function.Body!.Single());
        var variableType = Assert.IsType<NamedType>(foreachStatement.VariableType);

        Assert.Single(variableType.TypeArguments);
        Assert.Same(BuiltInSystemTypes.Int, variableType.TypeArguments[0]);
        Assert.IsType<InvocationExpression>(foreachStatement.CurrentExpression);
    }

    [Fact]
    public void EmitsArrayForeachLoop()
    {
        var project = CreateProject("""
            void Visit(int[] values) {
                foreach (int value in values) {
                    if (value == 0) {
                        continue;
                    }
                }
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
                Path.Combine(outputDirectory, "Foreach.cx"));
            var generatedSource = File.ReadAllText(
                Path.Combine(outputDirectory, "unnamed.c"));

            Assert.Contains(
                "struct cxcore_System_Collections_Array* __cx_foreach_collection_value = values;",
                generatedSource);
            Assert.Contains(
                "__cx_foreach_index_value < __cx_foreach_collection_value->_length",
                generatedSource);
            Assert.Contains(
                "cx_int value = ((cx_int*)__cx_foreach_collection_value->_data)[__cx_foreach_index_value];",
                generatedSource);
            Assert.Contains("continue;", generatedSource);
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

    private static (string Header, string Source) GenerateOutput(CxProject project)
    {
        var outputDirectory = Path.Combine(
            Path.GetTempPath(),
            $"cxc-foreach-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(outputDirectory);
            CCodeOutputGenerator.GenerateOutput(
                project, Path.Combine(outputDirectory, "Foreach.cx"));
            return (
                File.ReadAllText(Path.Combine(outputDirectory, "unnamed.h")),
                File.ReadAllText(Path.Combine(outputDirectory, "unnamed.c")));
        }
        finally
        {
            if (Directory.Exists(outputDirectory))
            {
                Directory.Delete(outputDirectory, recursive: true);
            }
        }
    }
}

using CxCompiler.Model.Errors;
using CxCompiler.Model.Project;
using CxCompiler.Model.Types;
using CxCompiler.Semantics;
using CxCompiler.OutputGenerators;

namespace CxCompiler.Tests;

public sealed class IteratorSemanticTests
{
    [Fact]
    public void BindsYieldReturnAndYieldBreakAsAnIterator()
    {
        var project = CreateProject("""
            namespace System.Collections;
            public interface IEnumerable<T> { IEnumerator<T> GetEnumerator() const; }
            public interface IEnumerator<T> { T Current { const get; } bool MoveNext(); }
            public IEnumerable<int> Values() {
                yield return 7;
                return;
                yield break;
            }
            """);

        new SemanticBinder().Bind(project);

        var function = Assert.IsType<FunctionDeclaration>(
            project.CompilationContexts.Single().DeclarationScope.Declarations
                .Single(declaration => declaration.Name == "Values"));
        Assert.True(function.IsIterator);
        Assert.IsType<CxCompiler.Model.Types.BuiltInTypes.IntType>(
            function.IteratorElementType);

        var outputDirectory = Path.Combine(
            Path.GetTempPath(), $"cxc-iterator-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(outputDirectory);
            CCodeOutputGenerator.GenerateOutput(
                project, Path.Combine(outputDirectory, "Iterator.cx"));
            var generatedC = string.Join("\n", Directory.GetFiles(outputDirectory, "*.c")
                .Select(File.ReadAllText));
            Assert.Contains("MoveNext", generatedC);
            Assert.Contains("__CxIterator_Values_", generatedC);
            Assert.DoesNotContain("yield return", generatedC);
        }
        finally
        {
            if (Directory.Exists(outputDirectory))
            {
                Directory.Delete(outputDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public void LowersStaticClassMethodIterators()
    {
        var project = CreateProject("""
            namespace System.Collections;
            public interface IEnumerable<T> { IEnumerator<T> GetEnumerator() const; }
            public interface IEnumerator<T> { T Current { const get; } bool MoveNext(); }
            public class ValuesProvider {
                public static IEnumerable<int> Values(int start) {
                    yield return start;
                    yield return start + 1;
                }
            }
            """);

        new SemanticBinder().Bind(project);

        var method = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<ClassDeclaration>()
            .Single(declaration => declaration.Name == "ValuesProvider")
            .MemberDeclarations.Declarations.OfType<FunctionDeclaration>()
            .Single(declaration => declaration.Name == "Values");
        Assert.True(method.IsIteratorFactory);

        var outputDirectory = Path.Combine(
            Path.GetTempPath(), $"cxc-iterator-class-method-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(outputDirectory);
            CCodeOutputGenerator.GenerateOutput(
                project, Path.Combine(outputDirectory, "ClassMethodIterator.cx"));
            var generatedC = string.Join("\n", Directory.GetFiles(outputDirectory, "*.c")
                .Select(File.ReadAllText));
            Assert.Contains("__CxIterator_Values_", generatedC);
        }
        finally
        {
            if (Directory.Exists(outputDirectory))
            {
                Directory.Delete(outputDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public void LowersInstanceClassMethodIteratorsWithCapturedReceiver()
    {
        var project = CreateProject("""
            namespace System.Collections;
            public interface IEnumerable<T> { IEnumerator<T> GetEnumerator() const; }
            public interface IEnumerator<T> { T Current { const get; } bool MoveNext(); }
            public class ValuesProvider {
                private int _start;
                public constructor(int start) { _start = start; }
                private int Add(int value) { return _start + value; }
                public IEnumerable<int> Values() {
                    yield return this._start;
                    yield return this.Add(1);
                }
            }
            """);

        new SemanticBinder().Bind(project);

        var iterator = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<ClassDeclaration>()
            .Single(declaration => declaration.Name.StartsWith("__CxIterator_", StringComparison.Ordinal));
        Assert.Contains(iterator.MemberDeclarations.Declarations.OfType<FieldDeclaration>(),
            field => field.Name == "__cx_receiver");
        var method = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<ClassDeclaration>()
            .Single(declaration => declaration.Name == "ValuesProvider")
            .MemberDeclarations.Declarations.OfType<FunctionDeclaration>()
            .Single(declaration => declaration.Name == "Values");
        Assert.True(method.IsIteratorFactory);
    }

    [Fact]
    public void LowersIteratorMethodsOnGenericClasses()
    {
        var project = CreateProject("""
            namespace System.Collections;
            public interface IEnumerable<T> { IEnumerator<T> GetEnumerator() const; }
            public interface IEnumerator<T> { T Current { const get; } bool MoveNext(); }
            public class ValuesProvider<T> {
                private T _value;
                public constructor(T value) { _value = value; }
                public IEnumerable<T> Values() { yield return this._value; }
            }
            """);

        new SemanticBinder().Bind(project);

        var context = project.CompilationContexts.Single();
        var iterator = Assert.Single(context.DeclarationScope.Declarations
            .OfType<ClassDeclaration>(), declaration =>
                declaration.Name.StartsWith("__CxIterator_Values_", StringComparison.Ordinal));
        Assert.Equal(["T"], iterator.GenericTypeNames);
        var receiverField = Assert.Single(iterator.MemberDeclarations.Declarations
            .OfType<FieldDeclaration>(), field => field.Name == "__cx_receiver");
        var receiverType = Assert.IsType<NamedType>(receiverField.Type);
        Assert.Equal("T", Assert.Single(receiverType.TypeArguments).Name);
    }

    [Fact]
    public void EmitsTypedCurrentThunkForClosedGenericIteratorStructElement()
    {
        var project = CreateProject("""
            namespace System.Collections;
            public interface IEnumerable<T> { IEnumerator<T> GetEnumerator() const; }
            public interface IEnumerator<T> { T Current { const get; } bool MoveNext(); }
            public struct Payload {
                public int Number;
                public string Reference;
                public constructor(int number, string reference) {
                    Number = number;
                    Reference = reference;
                }
            }
            public class ValuesProvider<T> {
                public IEnumerable<T> Values(T value) { yield return value; }
            }
            public int SumPayload() {
                ValuesProvider<Payload> provider = new ValuesProvider<Payload>();
                Payload expected = new Payload(7, "generic-payload");
                int sum = 0;
                foreach (Payload value in provider.Values(expected)) {
                    if (value.Number != expected.Number || value.Reference != expected.Reference) return -1;
                    sum += value.Number;
                }
                return sum;
            }
            """);

        new SemanticBinder().Bind(project);

        var outputDirectory = Path.Combine(
            Path.GetTempPath(), $"cxc-generic-iterator-current-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(outputDirectory);
            CCodeOutputGenerator.GenerateOutput(
                project, Path.Combine(outputDirectory, "GenericIteratorCurrent.cx"));
            var generatedC = string.Join("\n", Directory.GetFiles(outputDirectory, "*.c")
                .Select(File.ReadAllText));
            var generatedHeader = File.ReadAllText(
                Directory.GetFiles(outputDirectory, "*.internal.h").Single());

            Assert.Contains("struct iterator_semantics_System_Collections_Payload __cx_current;",
                generatedHeader);
            Assert.Contains("return ((struct cx_generic_", generatedC);
            Assert.Contains(")->__cx_current;", generatedC);
            Assert.Contains(")->__cx_hasCurrent)", generatedC);
            Assert.DoesNotContain("(struct iterator_semantics_System_Collections_Payload)(size_t)",
                generatedC);
        }
        finally
        {
            if (Directory.Exists(outputDirectory))
            {
                Directory.Delete(outputDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public void RejectsYieldInAFunctionWithTheWrongReturnType()
    {
        var project = CreateProject("""
            public int Values() { yield return 7; }
            """);

        var error = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(project));

        Assert.Contains("must return System.Collections.IEnumerable<T>", error.Message);
    }

    [Fact]
    public void RejectsYieldOutsideAnIteratorAndMismatchedElements()
    {
        var outsideIterator = CreateProject("public void Run() { yield break; }");
        var outsideError = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(outsideIterator));
        Assert.Contains("must return System.Collections.IEnumerable<T>", outsideError.Message);

        var wrongElement = CreateProject("""
            namespace System.Collections;
            public interface IEnumerable<T> { IEnumerator<T> GetEnumerator() const; }
            public interface IEnumerator<T> { T Current { const get; } bool MoveNext(); }
            public IEnumerable<int> Values() { yield return "wrong"; }
            """);
        var elementError = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(wrongElement));
        Assert.Contains("Cannot yield value", elementError.Message);
    }

    [Fact]
    public void LowersYieldsInsideBothConditionalBranches()
    {
        var project = CreateProject("""
            namespace System.Collections;
            public interface IEnumerable<T> { IEnumerator<T> GetEnumerator() const; }
            public interface IEnumerator<T> { T Current { const get; } bool MoveNext(); }
            public IEnumerable<int> Values(bool enabled) {
                int value = 10;
                if (enabled) {
                    value += 2;
                    yield return value;
                    value += 3;
                } else {
                    value += 4;
                    yield return value;
                    value += 5;
                }
                yield return value;
            }
            public IEnumerable<int> CountSkipping(int limit) {
                int value = 0;
                while (value < limit) {
                    value += 1;
                    if (value == 2) continue;
                    yield return value;
                }
            }
            public IEnumerable<int> CountFor(int limit) {
                for (int index = 0; index < limit; index++) {
                    if (index == 1) continue;
                    yield return index + 1;
                }
            }
            """);

        new SemanticBinder().Bind(project);

        var function = Assert.IsType<FunctionDeclaration>(
            project.CompilationContexts.Single().DeclarationScope.Declarations
                .Single(declaration => declaration.Name == "Values"));
        Assert.True(function.IsIteratorFactory);
        var outputDirectory = Path.Combine(
            Path.GetTempPath(), $"cxc-iterator-branches-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(outputDirectory);
            CCodeOutputGenerator.GenerateOutput(
                project, Path.Combine(outputDirectory, "Branches.cx"));
            var generatedC = string.Join("\n", Directory.GetFiles(outputDirectory, "*.c")
                .Select(File.ReadAllText));
            Assert.Contains("while (CX_TRUE)", generatedC);
            Assert.Contains("__cx_state", generatedC);
        }
        finally
        {
            if (Directory.Exists(outputDirectory))
            {
                Directory.Delete(outputDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public void EmitsDirectCallsForSameModuleGeneratedIteratorFactories()
    {
        var project = CreateProject("""
            namespace System.Collections;
            public interface IDisposable { void Dispose(); }
            public interface IEnumerable<T> { IEnumerator<T> GetEnumerator() const; }
            public interface IEnumerator<T> : IDisposable {
                T Current { const get; }
                bool MoveNext();
            }
            public IEnumerable<int> Range(int count) {
                int index = 0;
                while (index < count) { yield return index; index++; }
            }
            public int Sum(int count) {
                int sum = 0;
                foreach (int value in Range(count)) sum += value;
                return sum;
            }
            public class DirectProvider {
                private int _offset;
                public constructor(int offset) { _offset = offset; }
                public IEnumerable<int> Values(int count) {
                    int index = 0;
                    while (index < count) {
                        yield return this._offset + index;
                        index++;
                    }
                }
            }
            public int SumInstance(DirectProvider provider, int count) {
                int sum = 0;
                foreach (int value in provider.Values(count)) sum += value;
                return sum;
            }
            public class GenericProvider<T> {
                private T _value;
                public constructor(T value) { _value = value; }
                public IEnumerable<T> Values(int count) {
                    int index = 0;
                    while (index < count) {
                        yield return this._value;
                        index++;
                    }
                }
            }
            public int SumGeneric(GenericProvider<int> provider, int count) {
                int sum = 0;
                foreach (int value in provider.Values(count)) sum += value;
                return sum;
            }
            """);

        new SemanticBinder().Bind(project);

        var outputDirectory = Path.Combine(
            Path.GetTempPath(), $"cxc-iterator-direct-foreach-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(outputDirectory);
            CCodeOutputGenerator.GenerateOutput(
                project, Path.Combine(outputDirectory, "DirectForeach.cx"));
            var generatedC = string.Join("\n", Directory.GetFiles(outputDirectory, "*.c")
                .Select(File.ReadAllText));
            var sumStart = generatedC.IndexOf("Sum(cx_int count)", StringComparison.Ordinal);
            Assert.True(sumStart >= 0, "Generated C did not contain the Sum function.");
            var sumEnd = generatedC.IndexOf("\n}", sumStart, StringComparison.Ordinal);
            Assert.True(sumEnd > sumStart, "Could not isolate the generated Sum body.");
            var sumBody = generatedC[sumStart..sumEnd];

            Assert.True(sumBody.Contains("GetEnumerator((const struct ", StringComparison.Ordinal), sumBody);
            Assert.Contains("MoveNext((struct ", sumBody);
            Assert.Contains("Current___const_get((const struct ", sumBody);
            Assert.Contains("Dispose((struct ", sumBody);
            Assert.DoesNotContain(".vtable", sumBody);

            var instanceSumStart = generatedC.IndexOf(
                "SumInstance(struct ", StringComparison.Ordinal);
            Assert.True(instanceSumStart >= 0,
                "Generated C did not contain the SumInstance function.");
            var instanceSumEnd = generatedC.IndexOf(
                "\n}", instanceSumStart, StringComparison.Ordinal);
            Assert.True(instanceSumEnd > instanceSumStart,
                "Could not isolate the generated SumInstance body.");
            var instanceSumBody = generatedC[instanceSumStart..instanceSumEnd];
            Assert.Contains("GetEnumerator((const struct ", instanceSumBody);
            Assert.Contains("MoveNext((struct ", instanceSumBody);
            Assert.Contains("Current___const_get((const struct ", instanceSumBody);
            Assert.Contains("Dispose((struct ", instanceSumBody);
            Assert.DoesNotContain(".vtable", instanceSumBody);

            var genericSumStart = generatedC.IndexOf(
                "SumGeneric(", StringComparison.Ordinal);
            Assert.True(genericSumStart >= 0,
                "Generated C did not contain the SumGeneric function.");
            var genericSumEnd = generatedC.IndexOf(
                "\n}", genericSumStart, StringComparison.Ordinal);
            Assert.True(genericSumEnd > genericSumStart,
                "Could not isolate the generated SumGeneric body.");
            var genericSumBody = generatedC[genericSumStart..genericSumEnd];
            Assert.Contains(".vtable", genericSumBody);
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
        var project = CxProject.CreateDefaultApplicationProject("iterator_semantics");
        project.AddCompilationContext(CompilerTestHelper.Parse(source));
        return project;
    }
}

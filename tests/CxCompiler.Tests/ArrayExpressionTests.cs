using CxCompiler.Model.Errors;
using CxCompiler.Model.Expressions;
using CxCompiler.Model.Project;
using CxCompiler.Model.Statements;
using CxCompiler.Model.Types;
using CxCompiler.Model.Types.BuiltInTypes;
using CxCompiler.OutputGenerators;
using CxCompiler.Semantics;

namespace CxCompiler.Tests;

public sealed class ArrayExpressionTests
{
    [Fact]
    [Trait("Area", "TypeIdentity")]
    public void BuiltInAndCollectionsArrayHaveTheSameCanonicalTypeIdentity()
    {
        var builtInArray = new ArrayType(BuiltInSystemTypes.Int);
        var collectionsArray = NamedType.CreateWithTypeArguments(
            "Array", [BuiltInSystemTypes.Int]);
        collectionsArray.SetResolvedType(
            new CxCompiler.Model.Common.QualifiedIdentifier("System", "Collections", "Array"),
            "cxcore",
            ClassType.Class);

        Assert.True(ArrayType.AreEquivalent(builtInArray, collectionsArray));
        var canonicalBuiltInArray = Assert.IsType<ArrayType>(
            ArrayType.Canonicalize(builtInArray));
        var publicArrayIdentity = Assert.IsAssignableFrom<NamedType>(
            canonicalBuiltInArray);
        Assert.Equal(
            "cxcore.System.Collections.Array",
            publicArrayIdentity.ResolvedTypeFullName.ToString());
        Assert.Same(BuiltInSystemTypes.Int, publicArrayIdentity.TypeArguments.Single());
        var canonicalPublicArray = Assert.IsAssignableFrom<NamedType>(
            ArrayType.Canonicalize(collectionsArray));
        Assert.Equal(
            publicArrayIdentity.ResolvedTypeFullName,
            canonicalPublicArray.ResolvedTypeFullName);
        Assert.True(ArrayType.AreEquivalent(canonicalBuiltInArray, canonicalPublicArray));
        Assert.Equal(
            GenericTypeIdentity.Create("sample", "Holder", [builtInArray]).CanonicalName,
            GenericTypeIdentity.Create("sample", "Holder", [collectionsArray]).CanonicalName);

        var jaggedBuiltInArray = new ArrayType(builtInArray);
        var jaggedCollectionsArray = NamedType.CreateWithTypeArguments(
            "Array", [collectionsArray]);
        jaggedCollectionsArray.SetResolvedType(
            new CxCompiler.Model.Common.QualifiedIdentifier("System", "Collections", "Array"),
            "cxcore",
            ClassType.Class);
        Assert.True(ArrayType.AreEquivalent(jaggedBuiltInArray, jaggedCollectionsArray));
    }

    [Fact]
    [Trait("Area", "Binding")]
    public void BuiltInAndCollectionsArrayTypesAreInterchangeableInSignatures()
    {
        var project = CxProject.CreateDefaultApplicationProject("cxcore");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            namespace System.Collections;
            public class Array<T> {}
            """));
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public class ArrayBridge {
                public System.Collections.Array<int> Wrap(int[] values) {
                    return values;
                }

                public int[] Unwrap(System.Collections.Array<int> values) {
                    return values;
                }
            }
            """));

        new SemanticBinder().Bind(project);

        var bridge = Assert.IsType<ClassDeclaration>(
            project.CompilationContexts
                .SelectMany(context => context.DeclarationScope.Declarations)
                .Single(declaration => declaration.Name == "ArrayBridge"));
        Assert.Equal(["Unwrap", "Wrap"], bridge.MemberDeclarations.Declarations
            .OfType<FunctionDeclaration>()
            .Where(function => function.Name is "Wrap" or "Unwrap")
            .Select(function => function.Name)
            .Order(StringComparer.Ordinal));
    }

    [Fact]
    [Trait("Area", "Lowering")]
    public void CollectionsArrayConstructionAndMethodsUseTheBuiltInRuntimeArray()
    {
        var project = CxProject.CreateDefaultApplicationProject("cxcore");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            namespace System.Collections;
            public final class Array<T> {
                public constructor(uint length) {}
                public uint Length { const get { return this.Length; } }
                public T Get(uint index) const { return this[index]; }
                public void Set(uint index, T value) { this[index] = value; }
            }
            """));
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public int Read() {
                System.Collections.Array<int> values =
                    new System.Collections.Array<int>(2u);
                values.Set(0u, 42);
                return values.Get(0u);
            }
            """));
        new SemanticBinder().Bind(project);
        var outputDirectory = Path.Combine(
            Path.GetTempPath(),
            $"cxc-array-unification-{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(outputDirectory);
            CCodeOutputGenerator.GenerateOutput(
                project,
                Path.Combine(outputDirectory, "ArrayUnification.cx"));
            var generatedSource = File.ReadAllText(
                Path.Combine(outputDirectory, "cxcore.c"));

            Assert.Contains(
                "cx_array_new((cx_uint)(2u), (cx_uint)sizeof(cx_int))",
                generatedSource);
            Assert.Contains("cx_array_at", generatedSource);
            Assert.Contains("struct CX_ID_4(cxcore, System, Collections, Array)*", generatedSource);
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
    [Trait("Area", "Binding")]
    public void BuiltInAndCollectionsArraysParticipateInGenericInference()
    {
        var project = CxProject.CreateDefaultApplicationProject("cxcore");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            namespace System.Collections;
            public class Array<T> {}
            """));
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public T[] EchoBuiltIn<T>(T[] values) { return values; }
            public System.Collections.Array<T> EchoPublic<T>(System.Collections.Array<T> values) {
                return values;
            }
            public System.Collections.Array<int> FromBuiltIn(int[] values) {
                return EchoPublic(values);
            }
            public int[] FromPublic(System.Collections.Array<int> values) {
                return EchoBuiltIn(values);
            }
            """));

        new SemanticBinder().Bind(project);

        Assert.Equal(2, project.GenericFunctionInstances.Count(instance =>
            instance.FullName.Parts[^1] is "EchoBuiltIn" or "EchoPublic"));
    }

    [Fact]
    [Trait("Area", "TypeIdentity")]
    public void GenericIdentityBodiesTreatBothArraySpellingsAsTheSameType()
    {
        var project = CxProject.CreateDefaultApplicationProject("cxcore");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            namespace System.Collections;
            public class Array<T> {}
            """));
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public System.Collections.Array<T> ToPublic<T>(T[] values) { return values; }
            public T[] ToBuiltIn<T>(System.Collections.Array<T> values) { return values; }

            public System.Collections.Array<int> Wrap(int[] values) {
                return ToPublic(values);
            }

            public int[] Unwrap(System.Collections.Array<int> values) {
                return ToBuiltIn(values);
            }
            """));

        new SemanticBinder().Bind(project);

        Assert.Equal(2, project.GenericFunctionInstances.Count(instance =>
            instance.FullName.Parts[^1] is "ToPublic" or "ToBuiltIn"));
        foreach (var instance in project.GenericFunctionInstances.Where(instance =>
            instance.FullName.Parts[^1] is "ToPublic" or "ToBuiltIn"))
        {
            Assert.True(ArrayType.AreEquivalent(
                instance.ReturnType,
                instance.ParameterTypes.Single()));
        }
    }

    [Fact]
    [Trait("Area", "TypeIdentity")]
    public void PublicAndBuiltInArraySpellingsUseCanonicalSemanticTypes()
    {
        var project = CxProject.CreateDefaultApplicationProject("cxcore");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            namespace System.Collections;
            public class Array<T> {
                public constructor(uint length) {}
            }
            public delegate int ArrayConsumer(Array<int> values);
            """));
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public class Box<T> {}
            """));
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public System.Collections.Array<int> FromBuiltIn(int[] values) {
                System.Collections.Array<int> local = values;
                return local;
            }

            public int[] FromPublic(System.Collections.Array<int> values) {
                int[] local = values;
                return local;
            }

            public System.Collections.Array<int> Construct() {
                return new System.Collections.Array<int>(1u);
            }

            public Box<System.Collections.Array<int>> Nested(Box<int[]> value) {
                return value;
            }
            """));

        new SemanticBinder().Bind(project);

        var functions = project.CompilationContexts
            .SelectMany(context => context.DeclarationScope.Declarations)
            .OfType<FunctionDeclaration>()
            .Where(function => function.Name is
                "FromBuiltIn" or "FromPublic" or "Construct" or "Nested")
            .ToDictionary(function => function.Name, StringComparer.Ordinal);
        foreach (var function in functions.Values.Where(function => function.Name != "Nested"))
        {
            var returnType = Assert.IsAssignableFrom<NamedType>(function.ReturnType);
            Assert.Equal("cxcore.System.Collections.Array", returnType.ResolvedTypeFullName.ToString());
            foreach (var parameter in function.Parameters)
            {
                var parameterType = Assert.IsAssignableFrom<NamedType>(parameter.ParameterType);
                Assert.Equal("cxcore.System.Collections.Array", parameterType.ResolvedTypeFullName.ToString());
            }
        }

        var nestedFunction = functions["Nested"];
        var nestedReturn = Assert.IsType<NamedType>(nestedFunction.ReturnType);
        var nestedParameter = Assert.IsType<NamedType>(
            nestedFunction.Parameters.Single().ParameterType);
        Assert.IsAssignableFrom<NamedType>(nestedReturn.TypeArguments.Single());
        Assert.IsAssignableFrom<NamedType>(nestedParameter.TypeArguments.Single());
        Assert.True(ArrayType.AreEquivalent(nestedReturn, nestedParameter));

        var arrayConsumer = project.CompilationContexts
            .SelectMany(context => context.DeclarationScope.Declarations)
            .OfType<DelegateDeclaration>()
            .Single(declaration => declaration.Name == "ArrayConsumer");
        Assert.IsAssignableFrom<NamedType>(arrayConsumer.Signature.ParameterTypes.Single());

        foreach (var function in functions.Values.Where(function =>
            function.Name is "FromBuiltIn" or "FromPublic"))
        {
            var local = Assert.IsType<LocalVariableDeclarationStatement>(function.Body!.First());
            Assert.IsAssignableFrom<NamedType>(local.DeclaredType);
            Assert.IsAssignableFrom<NamedType>(local.Declarators.Single().Type);
        }

        var construct = functions["Construct"];
        var returnedCreation = Assert.IsType<ObjectCreationExpression>(
            Assert.IsType<ReturnStatement>(construct.Body!.Single()).Expression);
        Assert.IsAssignableFrom<NamedType>(returnedCreation.InferredType);
        Assert.IsType<NamedType>(returnedCreation.RequestedType);
    }

    [Fact]
    [Trait("Area", "OverloadResolution")]
    public void BuiltInArraySelectsOverloadTakingCollectionsArray()
    {
        var project = CxProject.CreateDefaultApplicationProject("cxcore");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            namespace System.Collections;
            public class Array<T> {}
            """));
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public static class ArrayOverloads {
                public static int Select(System.Collections.Array<int> values) { return 1; }
                public static int Select(long value) { return 2; }
                public static int Run(int[] values) { return Select(values); }
            }
            """));

        new SemanticBinder().Bind(project);

        var overloads = project.CompilationContexts
            .SelectMany(context => context.DeclarationScope.Declarations)
            .OfType<ClassDeclaration>()
            .Single(declaration => declaration.Name == "ArrayOverloads");
        var run = overloads.MemberDeclarations.Declarations
            .OfType<FunctionDeclaration>()
            .Single(function => function.Name == "Run");
        var invocation = Assert.IsType<InvocationExpression>(
            Assert.IsType<ReturnStatement>(run.Body!.Single()).Expression);

        Assert.Equal("Select", invocation.TargetSymbol!.FullName.Parts[^1]);
        Assert.IsType<ArrayType>(invocation.TargetSymbol.ParameterTypes.Single());
    }

    [Fact]
    [Trait("Area", "Alias")]
    public void ArrayAliasesPreserveInterchangeabilityAndCanonicalIdentity()
    {
        var project = CxProject.CreateDefaultApplicationProject("cxcore");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            namespace System.Collections;
            public class Array<T> {}
            """));
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            alias PublicInts = System.Collections.Array<int>;
            public PublicInts FromBuiltIn(int[] values) { return values; }
            public int[] FromPublic(PublicInts values) { return values; }
            """));

        new SemanticBinder().Bind(project);

        var declarations = project.CompilationContexts
            .SelectMany(context => context.DeclarationScope.Declarations)
            .OfType<FunctionDeclaration>()
            .Where(function => function.Name is "FromBuiltIn" or "FromPublic")
            .ToArray();
        Assert.Equal(2, declarations.Length);
        foreach (var declaration in declarations)
        {
            Assert.True(ArrayType.AreEquivalent(
                declaration.Parameters.Single().ParameterType,
                declaration.ReturnType));
        }
    }

    [Fact]
    [Trait("Area", "Nullability")]
    public void BuiltInAndCollectionsArraysAcceptNullAndDefaultInterchangeably()
    {
        var project = CxProject.CreateDefaultApplicationProject("cxcore");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            namespace System.Collections;
            public class Array<T> {}
            """));
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public int[] DefaultBuiltIn() { return default; }
            public System.Collections.Array<int> DefaultPublic() { return default; }
            public bool PublicIsNull(int[] values) {
                System.Collections.Array<int> copy = values;
                return copy == null;
            }
            public bool BuiltInIsNull(System.Collections.Array<int> values) {
                int[] copy = values;
                return copy == null;
            }
            """));

        new SemanticBinder().Bind(project);
    }

    [Fact]
    [Trait("Area", "Binding")]
    public void BuiltInArraysCanCallPublicCollectionsArrayMethods()
    {
        var project = CxProject.CreateDefaultApplicationProject("cxcore");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            namespace System.Collections;
            public final class Array<T> {
                public uint Length { const get { return this.Length; } }
                public T Get(uint index) const { return this[index]; }
                public void Set(uint index, T value) { this[index] = value; }
            }
            """));
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public int ReadAndWrite(int[] values) {
                values.Set(0u, values.Get(0u) + 1);
                return values.Get(0u);
            }
            public uint GetLength(System.Collections.Array<int> values) {
                return values.Length;
            }
            """));

        new SemanticBinder().Bind(project);

        var outputDirectory = Path.Combine(
            Path.GetTempPath(),
            $"cxc-array-api-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(outputDirectory);
            CCodeOutputGenerator.GenerateOutput(
                project,
                Path.Combine(outputDirectory, "ArrayApi.cx"));
            var generatedSource = File.ReadAllText(
                Path.Combine(outputDirectory, "cxcore.c"));
            Assert.Contains("cx_array_at", generatedSource);
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
    [Trait("Area", "GenericBinding")]
    public void GenericArrayParametersCanCallPublicCollectionsArrayMethods()
    {
        var project = CxProject.CreateDefaultApplicationProject("cxcore");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            namespace System.Collections;
            public final class Array<T> {
                public T Get(uint index) const { return this[index]; }
                public void Set(uint index, T value) { this[index] = value; }
            }
            """));
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public T First<T>(T[] values) { return values.Get(0u); }
            public T FirstPublic<T>(System.Collections.Array<T> values) {
                return values.Get(0u);
            }
            public int Run(int[] values) { return First(values) + FirstPublic(values); }
            """));

        new SemanticBinder().Bind(project);

        Assert.Equal(2, project.GenericFunctionInstances.Count(instance =>
            instance.FullName.Parts[^1] is "First" or "FirstPublic"));

        var outputDirectory = Path.Combine(
            Path.GetTempPath(),
            $"cxc-generic-array-api-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(outputDirectory);
            CCodeOutputGenerator.GenerateOutput(
                project,
                Path.Combine(outputDirectory, "GenericArrayApi.cx"));
            var generatedSource = File.ReadAllText(
                Path.Combine(outputDirectory, "cxcore.c"));
            Assert.Contains("cx_array_at", generatedSource);
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
    [Trait("Area", "Syntax")]
    public void ParserPreservesArrayCreationAccessAndAssignment()
    {
        var project = CreateProject("""
            int Read() {
                int[] values = new int[3];
                values[0] = 42;
                return values[0];
            }
            """);

        var function = Assert.IsType<FunctionDeclaration>(
            project.CompilationContexts.Single().DeclarationScope.Declarations.Single());
        var declaration = Assert.IsType<LocalVariableDeclarationStatement>(function.Body![0]);
        var assignment = Assert.IsType<AssignmentExpression>(
            Assert.IsType<ExpressionStatement>(function.Body[1]).Expression);
        var returnedAccess = Assert.IsType<ArrayAccessExpression>(
            Assert.IsType<ReturnStatement>(function.Body[2]).Expression);

        Assert.IsType<ArrayCreationExpression>(declaration.Declarators.Single().Initializer);
        Assert.IsType<ArrayAccessExpression>(assignment.Target);
        Assert.Single(returnedAccess.Indices);
    }

    [Fact]
    [Trait("Area", "Binding")]
    public void BinderInfersArrayAndElementTypes()
    {
        var project = CreateProject("""
            int Read() {
                var values = new int[3];
                values[0] = 42;
                return values[0];
            }
            """);

        new SemanticBinder().Bind(project);

        var function = Assert.IsType<FunctionDeclaration>(
            project.CompilationContexts.Single().DeclarationScope.Declarations.Single());
        var declaration = Assert.IsType<LocalVariableDeclarationStatement>(function.Body![0]);
        var arrayType = Assert.IsType<ArrayType>(declaration.Declarators.Single().Type);
        var assignment = Assert.IsType<AssignmentExpression>(
            Assert.IsType<ExpressionStatement>(function.Body[1]).Expression);

        Assert.Same(BuiltInSystemTypes.Int, arrayType.ElementType);
        Assert.Same(BuiltInSystemTypes.Int, assignment.Target.InferredType);
        Assert.Same(BuiltInSystemTypes.Int, assignment.InferredType);
    }

    [Fact]
    [Trait("Area", "NegativeDiagnostics")]
    public void BinderRejectsInvalidArrayLengthsAndIndices()
    {
        var invalidLength = CreateProject("""
            void Main() {
                int[] values = new int[true];
            }
            """);
        var lengthException = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(invalidLength));
        Assert.Contains("Array length must be an integer", lengthException.Message);

        var invalidIndex = CreateProject("""
            int Read(int[] values) {
                return values[false];
            }
            """);
        var indexException = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(invalidIndex));
        Assert.Contains("Array index must be an integer", indexException.Message);

        var tooManyIndices = CreateProject("""
            int Read(int[] values) {
                return values[0, 1];
            }
            """);
        var dimensionException = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(tooManyIndices));
        Assert.Contains("Only one-dimensional array indexing is supported", dimensionException.Message);
    }

    [Fact]
    [Trait("Area", "NegativeDiagnostics")]
    public void BinderRejectsNonArrayAccessAndElementAssignmentMismatch()
    {
        var nonArray = CreateProject("""
            int Read(int value) {
                return value[0];
            }
            """);
        var accessException = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(nonArray));
        Assert.Contains("Cannot index expression of type 'System.Int'", accessException.Message);

        var wrongElementType = CreateProject("""
            void Main() {
                int[] values = new int[1];
                values[0] = "text";
            }
            """);
        var assignmentException = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(wrongElementType));
        Assert.Contains("Cannot assign 'System.String' to 'System.Int'", assignmentException.Message);
    }

    [Fact]
    [Trait("Area", "Lowering")]
    public void EmitsCheckedArrayAllocationReadsAndWrites()
    {
        var project = CreateProject("""
            int Read() {
                int[] values = new int[3];
                values[0] = 42;
                return values[0];
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
                Path.Combine(outputDirectory, "Arrays.cx"));
            var generatedSource = File.ReadAllText(
                Path.Combine(outputDirectory, "unnamed.c"));

            Assert.Contains(
                "cx_array_new((cx_uint)(3), (cx_uint)sizeof(cx_int))",
                generatedSource);
            Assert.Contains(
                "(*(cx_int*)cx_array_at(values, (cx_uint)(0), (cx_uint)sizeof(cx_int))) = 42;",
                generatedSource);
            Assert.Contains(
                "return (*(cx_int*)cx_array_at(values, (cx_uint)(0), (cx_uint)sizeof(cx_int)));",
                generatedSource);
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
    [Trait("Area", "BindingAndLowering")]
    public void BindsAndEmitsArrayLengthGetter()
    {
        var project = CreateProject("""
            uint Count(string[] values) {
                return values.Length;
            }
            """);
        new SemanticBinder().Bind(project);
        var outputDirectory = Path.Combine(
            Path.GetTempPath(),
            $"cxc-array-length-{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(outputDirectory);
            CCodeOutputGenerator.GenerateOutput(
                project,
                Path.Combine(outputDirectory, "ArrayLength.cx"));
            var generatedSource = File.ReadAllText(
                Path.Combine(outputDirectory, "unnamed.c"));

            Assert.Contains(
                "return CX_ID_6(cxcore, System, Collections, Array, Length, __const_get)(values);",
                generatedSource);
        }
        finally
        {
            if (Directory.Exists(outputDirectory))
                Directory.Delete(outputDirectory, recursive: true);
        }
    }

    [Fact]
    [Trait("Area", "InterfaceDispatch")]
    public void BuiltInArrayToEnumerableClosesThePublicArrayInterfaceInItsOwnerModule()
    {
        var project = CxProject.CreateDefaultApplicationProject("cxcore");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            namespace System.Collections;
            public interface IEnumerable<T> {
                IEnumerator<T> GetEnumerator() const;
            }
            public interface IEnumerator<T> {
                T Current { const get; }
                bool MoveNext();
            }
            public final class Array<T> : IEnumerable<T> {
                public IEnumerator<T> GetEnumerator() const { return default; }
            }
            """));
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            import System.Collections;
            public IEnumerable<int> AsSequence(int[] values) { return values; }
            """));

        new SemanticBinder().Bind(project);

        var closedArray = Assert.Single(project.GenericTypeInstances,
            instance => instance.Declaration.FullName.ToString() ==
                "System.Collections.Array" &&
                instance.Type.TypeArguments.Single() is IntType);
        Assert.NotNull(closedArray.Type.ConstructedIdentity);

        var outputDirectory = Path.Combine(
            Path.GetTempPath(),
            $"cxc-array-enumerable-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(outputDirectory);
            CCodeOutputGenerator.GenerateOutput(
                project,
                Path.Combine(outputDirectory, "ArrayEnumerable.cx"));
            var generatedSource = File.ReadAllText(
                Path.Combine(outputDirectory, "cxcore.c"));
            var closedIdentity = closedArray.Type.ConstructedIdentity!.CIdentifier;

            var arrayInterfaceLines = string.Join(Environment.NewLine,
                generatedSource.Split(Environment.NewLine).Where(line =>
                    line.Contains(closedIdentity, StringComparison.Ordinal) ||
                    line.Contains("iface", StringComparison.OrdinalIgnoreCase)));
            Assert.Contains(closedIdentity, generatedSource);
            Assert.True(arrayInterfaceLines.Contains(
                "IEnumerable", StringComparison.Ordinal), arrayInterfaceLines);
        }
        finally
        {
            if (Directory.Exists(outputDirectory))
                Directory.Delete(outputDirectory, recursive: true);
        }
    }

    private static CxProject CreateProject(string source)
    {
        var project = CxProject.CreateDefaultApplicationProject();
        project.AddCompilationContext(CompilerTestHelper.Parse(source));
        return project;
    }
}

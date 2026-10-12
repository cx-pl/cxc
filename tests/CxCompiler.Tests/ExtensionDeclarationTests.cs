using CxCompiler.Model.Errors;
using CxCompiler.Model.Project;
using CxCompiler.Model.Types;
using CxCompiler.Model.Types.BuiltInTypes;
using CxCompiler.OutputGenerators;
using CxCompiler.Semantics;

namespace CxCompiler.Tests;

public sealed class ExtensionDeclarationTests
{
    [Fact]
    public void ConcreteArrayExtensionMethodsUseTheArrayReceiver()
    {
        var project = CreateProject("""
            public extension IntArrayExtensions : int[] {
                public int FirstPlus(int fallback) const {
                    return this.Length == 0 ? fallback : this[0] + fallback;
                }
            }
            public int Main() {
                int[] values = new int[1];
                values[0] = 37;
                return values.FirstPlus(5);
            }
            """);

        new SemanticBinder().Bind(project);

        var extension = Assert.IsType<ExtensionDeclaration>(project.CompilationContexts.Single()
            .DeclarationScope.Declarations.Single(declaration => declaration.Name == "IntArrayExtensions"));
        var method = Assert.Single(extension.Members.MemberDeclarations.Declarations
            .OfType<FunctionDeclaration>(), function => function.Name == "FirstPlus");
        Assert.True(method.IsExtension);
        Assert.IsType<CxCompiler.Model.Types.ArrayType>(method.ExtensionTargetType);

        var outputDirectory = Path.Combine(Path.GetTempPath(), $"cxc-array-extension-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(outputDirectory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(outputDirectory, "ArrayExtension.cx"));
            var source = File.ReadAllText(Directory.GetFiles(outputDirectory, "*.c").Single());
            Assert.Contains("FirstPlus", source);
            Assert.Contains("__this", source);
            Assert.DoesNotContain("_unknowntype_", source);
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
    public void ConcreteArrayExtensionMethodsRequireAnExactArrayType()
    {
        var project = CreateProject("""
            public extension IntArrayExtensions : int[] {
                public int First() const { return this.Length; }
            }
            public int Main() {
                string[] values = new string[1];
                return values.First();
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(project));

        Assert.Contains("Cannot resolve function 'First'", error.Message);
    }

    [Fact]
    public void ConcreteArrayExtensionPropertiesReadTheArrayReceiver()
    {
        var project = CreateProject("""
            public extension IntArrayExtensions : int[] {
                public uint Count { const get => this.Length; }
            }
            public int Main() {
                int[] values = new int[1];
                return values.Count == 1u ? 42 : 0;
            }
            """);

        new SemanticBinder().Bind(project);

        var outputDirectory = Path.Combine(Path.GetTempPath(),
            $"cxc-array-extension-property-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(outputDirectory);
            CCodeOutputGenerator.GenerateOutput(project,
                Path.Combine(outputDirectory, "ArrayExtensionProperty.cx"));
            var source = File.ReadAllText(Directory.GetFiles(outputDirectory, "*.c").Single());
            Assert.Contains("Count", source);
            Assert.DoesNotContain("_unknowntype_", source);
        }
        finally
        {
            if (Directory.Exists(outputDirectory))
            {
                Directory.Delete(outputDirectory, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData("public extension ArrayExtensions<T> : T[] { public T First { const get => this[0]; } }",
        "Generic array-target extension properties require concrete signatures")]
    [InlineData("public extension ArrayExtensions : int[] { public uint Count { get => this.Length; set {} } }",
        "Array-target extension properties must be read-only")]
    public void ArrayExtensionPropertiesRejectUnsupportedForms(
        string declaration,
        string expectedDiagnostic)
    {
        var project = CreateProject(declaration);

        var error = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(project));

        Assert.Contains(expectedDiagnostic, error.Message);
    }

    [Fact]
    public void GenericArrayExtensionPropertiesUseConcreteSignatures()
    {
        var project = CreateProject("""
            public extension GenericArrayProperties<T> : T[] {
                public uint Count { const get => this.Length; }
            }
            public extension IntArrayPropertyHelper : int[] {
                public uint FirstPlus(uint value) const { return this.Length + value; }
            }
            public int Main() {
                int[] values = new int[2];
                return values.Count == 2u ? 42 : 0;
            }
            """);

        new SemanticBinder().Bind(project);

        var outputDirectory = Path.Combine(Path.GetTempPath(),
            $"cxc-generic-array-extension-property-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(outputDirectory);
            CCodeOutputGenerator.GenerateOutput(project,
                Path.Combine(outputDirectory, "GenericArrayExtensionProperty.cx"));
            var source = File.ReadAllText(Directory.GetFiles(outputDirectory, "*.c").Single());
            Assert.Contains("Count", source);
            Assert.DoesNotContain("_unknowntype_", source);
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
    public void ConcreteArrayExtensionOperatorsUseTheArrayReceiver()
    {
        var project = CreateProject("""
            public extension IntArrayOperators : int[] {
                public uint operator +(int[] other) const {
                    return this.Length + other.Length;
                }
            }
            public int Main() {
                int[] first = new int[2];
                int[] second = new int[3];
                return first + second == 5u ? 42 : 0;
            }
            """);

        new SemanticBinder().Bind(project);

        var outputDirectory = Path.Combine(Path.GetTempPath(),
            $"cxc-array-extension-operator-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(outputDirectory);
            CCodeOutputGenerator.GenerateOutput(project,
                Path.Combine(outputDirectory, "ArrayExtensionOperator.cx"));
            var source = File.ReadAllText(Directory.GetFiles(outputDirectory, "*.c").Single());
            Assert.Contains("__cx_operator_plus", source);
            Assert.DoesNotContain("_unknowntype_", source);
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
    public void GenericArrayExtensionOperatorsCloseTheElementType()
    {
        var project = CreateProject("""
            public extension GenericArrayOperators<T> : T[][] {
                public uint operator +(T[][] other) const {
                    return this.Length + other.Length;
                }
            }
            public int Main() {
                string[][] first = new string[1][];
                string[][] second = new string[2][];
                return first + second == 3u ? 42 : 0;
            }
            """);

        new SemanticBinder().Bind(project);

        var main = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<FunctionDeclaration>().Single(function => function.Name == "Main");
        var returned = Assert.IsType<CxCompiler.Model.Statements.ReturnStatement>(
            main.Body!.OfType<CxCompiler.Model.Statements.ReturnStatement>().Single());
        var conditional = Assert.IsType<CxCompiler.Model.Expressions.ConditionalExpression>(
            returned.Expression);
        var comparison = Assert.IsType<CxCompiler.Model.Expressions.BinaryExpression>(
            conditional.Condition);
        var operatorExpression = Assert.IsType<CxCompiler.Model.Expressions.BinaryExpression>(
            comparison.Left);
        var operatorSymbol = Assert.IsType<FunctionSymbol>(
            operatorExpression.OperatorSymbol);
        Assert.Equal(BuiltInSystemTypes.String,
            operatorSymbol.GenericTypeArguments["T"]);

        var outputDirectory = Path.Combine(Path.GetTempPath(),
            $"cxc-generic-array-extension-operator-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(outputDirectory);
            CCodeOutputGenerator.GenerateOutput(project,
                Path.Combine(outputDirectory, "GenericArrayExtensionOperator.cx"));
            var source = File.ReadAllText(Directory.GetFiles(outputDirectory, "*.c").Single());
            Assert.Contains(operatorSymbol.SpecializationName!, source);
            Assert.DoesNotContain("_unknowntype_", source);
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
    public void ConcreteArrayExtensionCompoundAssignmentUsesReturnedArray()
    {
        var project = CreateProject("""
            public extension ArrayOperators : int[] {
                public int[] operator +=(int[] other) const { return other; }
                public int[] operator -=(int[] other) const { return other; }
                public int[] operator *=(int[] other) const { return other; }
                public int[] operator /=(int[] other) const { return other; }
                public int[] operator %=(int[] other) const { return other; }
            }
            public int Main() {
                int[] values = new int[1];
                int[] replacement = new int[1];
                replacement[0] = 42;
                values += replacement;
                values -= replacement;
                values *= replacement;
                values /= replacement;
                values %= replacement;
                return values[0];
            }
            """);

        new SemanticBinder().Bind(project);

        var outputDirectory = Path.Combine(Path.GetTempPath(),
            $"cxc-array-extension-compound-assignment-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(outputDirectory);
            CCodeOutputGenerator.GenerateOutput(project,
                Path.Combine(outputDirectory, "ArrayExtensionCompoundAssignment.cx"));
            var source = File.ReadAllText(Directory.GetFiles(outputDirectory, "*.c").Single());
            Assert.Contains("__cx_operator_plus_assign", source);
            Assert.Contains("__cx_operator_minus_assign", source);
            Assert.Contains("__cx_operator_multiply_assign", source);
            Assert.Contains("__cx_operator_divide_assign", source);
            Assert.Contains("__cx_operator_remainder_assign", source);
            Assert.DoesNotContain("_unknowntype_", source);
        }
        finally
        {
            if (Directory.Exists(outputDirectory))
            {
                Directory.Delete(outputDirectory, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData("public extension ArrayOperators : int[] { public uint operator ++() const { return 1u; } }")]
    [InlineData("public extension ArrayOperators<T> : T[] { public uint operator ++() const { return 1u; } }")]
    public void ArrayExtensionOperatorsRejectUnsupportedMutation(string extension)
    {
        var error = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(CreateProject(extension)));

        Assert.Contains("Array-target extension operators cannot mutate the array receiver",
            error.Message);
    }

    [Fact]
    public void GenericArrayExtensionCompoundAssignmentSpecializesTheReturnedArray()
    {
        var project = CreateProject("""
            public extension GenericArrayReplacement<T> : T[] {
                public T[] operator +=(T[] replacement) const { return replacement; }
                public T[] operator -=(T[] replacement) const { return replacement; }
                public T[] operator *=(T[] replacement) const { return replacement; }
                public T[] operator /=(T[] replacement) const { return replacement; }
                public T[] operator %=(T[] replacement) const { return replacement; }
            }
            public int Main() {
                int[] values = new int[1];
                int[] replacement = new int[1];
                replacement[0] = 42;
                values += replacement;
                values -= replacement;
                values *= replacement;
                values /= replacement;
                values %= replacement;
                return values[0];
            }
            """);

        new SemanticBinder().Bind(project);

        var main = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<FunctionDeclaration>().Single(function => function.Name == "Main");
        var compoundAssignment = Assert.IsType<CxCompiler.Model.Expressions.AssignmentExpression>(
            Assert.IsType<CxCompiler.Model.Statements.ExpressionStatement>(main.Body![3]).Expression);
        var operatorSymbol = Assert.IsType<FunctionSymbol>(compoundAssignment.OperatorSymbol);
        Assert.NotNull(operatorSymbol.SpecializationName);

        var outputDirectory = Path.Combine(Path.GetTempPath(),
            $"cxc-generic-array-extension-compound-assignment-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(outputDirectory);
            CCodeOutputGenerator.GenerateOutput(project,
                Path.Combine(outputDirectory, "GenericArrayExtensionCompoundAssignment.cx"));
            var source = File.ReadAllText(Directory.GetFiles(outputDirectory, "*.c").Single());
            Assert.Contains(operatorSymbol.SpecializationName!, source);
            Assert.DoesNotContain("_unknowntype_", source);
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
    public void ConcreteArrayIncrementAndDecrementWriteBackReturnedArrays()
    {
        var project = CreateProject("""
            public extension ArrayReplacement : int[] {
                public int[] operator ++() const {
                    int[] replacement = new int[1];
                    replacement[0] = 42;
                    return replacement;
                }
                public int[] operator --() const {
                    int[] replacement = new int[1];
                    replacement[0] = 21;
                    return replacement;
                }
            }
            public int Main() {
                int[] values = new int[1];
                values[0] = 7;
                int[] previous = values++;
                if (previous[0] != 7 || values[0] != 42) return 0;
                int[] current = --values;
                if (current[0] != 21 || values[0] != 21) return 0;
                return 42;
            }
            """);

        new SemanticBinder().Bind(project);

        var outputDirectory = Path.Combine(Path.GetTempPath(),
            $"cxc-array-extension-increment-decrement-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(outputDirectory);
            CCodeOutputGenerator.GenerateOutput(project,
                Path.Combine(outputDirectory, "ArrayExtensionIncrementDecrement.cx"));
            var source = File.ReadAllText(Directory.GetFiles(outputDirectory, "*.c").Single());
            Assert.Contains("__cx_operator_increment", source);
            Assert.Contains("__cx_operator_decrement", source);
            Assert.DoesNotContain("_unknowntype_", source);
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
    public void GenericArrayIncrementAndDecrementSpecializeReturnedArrays()
    {
        var project = CreateProject("""
            public extension GenericArrayReplacement<T> : T[] {
                public T[] operator ++() const { return this; }
                public T[] operator --() const { return this; }
            }
            public int Main() {
                int[] values = new int[1];
                values++;
                --values;
                return values.Length == 1u ? 42 : 0;
            }
            """);

        new SemanticBinder().Bind(project);

        var outputDirectory = Path.Combine(Path.GetTempPath(),
            $"cxc-generic-array-extension-increment-decrement-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(outputDirectory);
            CCodeOutputGenerator.GenerateOutput(project,
                Path.Combine(outputDirectory, "GenericArrayExtensionIncrementDecrement.cx"));
            var source = File.ReadAllText(Directory.GetFiles(outputDirectory, "*.c").Single());
            Assert.DoesNotContain("_unknowntype_", source);
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
    public void GenericArrayExtensionMethodsCloseTheElementType()
    {
        var project = CreateProject("""
            public extension ArrayExtensions<T> : T[] {
                public T First() const { return this[0]; }
                public U Echo<U>(U value) const { return value; }
            }
            public int Main() {
                int[] values = new int[1];
                values[0] = 42;
                return values.First() + values.Echo(0);
            }
            """);

        new SemanticBinder().Bind(project);

        var main = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<FunctionDeclaration>().Single(function => function.Name == "Main");
        var returnExpression = Assert.IsType<CxCompiler.Model.Expressions.BinaryExpression>(
            Assert.IsType<CxCompiler.Model.Statements.ReturnStatement>(
                main.Body!.OfType<CxCompiler.Model.Statements.ReturnStatement>().Single()).Expression);
        var first = Assert.IsType<CxCompiler.Model.Expressions.InvocationExpression>(
            returnExpression.Left);
        Assert.Equal(BuiltInSystemTypes.Int, first.TargetSymbol!.ReturnType);
        Assert.NotNull(first.TargetSymbol.ClosedContainingType?.ConstructedIdentity);
        Assert.Contains("T", first.TargetSymbol.GenericTypeArguments.Keys);
        Assert.Equal(BuiltInSystemTypes.Int, first.TargetSymbol.GenericTypeArguments["T"]);

        var outputDirectory = Path.Combine(Path.GetTempPath(), $"cxc-generic-array-extension-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(outputDirectory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(outputDirectory, "GenericArrayExtension.cx"));
            var source = File.ReadAllText(Directory.GetFiles(outputDirectory, "*.c").Single());
            Assert.Contains(first.TargetSymbol.SpecializationName!, source);
            Assert.DoesNotContain("_unknowntype_", source);
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
    public void GenericJaggedArrayExtensionMethodsCloseTheElementType()
    {
        var project = CreateProject("""
            public extension JaggedArrayExtensions<T> : T[][] {
                public T FirstNested() const { return this[0][0]; }
            }
            public int Main() {
                int[][] values = new int[1][];
                values[0] = new int[1];
                values[0][0] = 42;
                return values.FirstNested();
            }
            """);

        new SemanticBinder().Bind(project);

        var main = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<FunctionDeclaration>().Single(function => function.Name == "Main");
        var returned = Assert.IsType<CxCompiler.Model.Statements.ReturnStatement>(
            main.Body!.OfType<CxCompiler.Model.Statements.ReturnStatement>().Single());
        var invocation = Assert.IsType<CxCompiler.Model.Expressions.InvocationExpression>(
            returned.Expression);
        Assert.Equal(BuiltInSystemTypes.Int, invocation.TargetSymbol!.ReturnType);
        Assert.Equal(BuiltInSystemTypes.Int, invocation.TargetSymbol.GenericTypeArguments["T"]);

        var outputDirectory = Path.Combine(Path.GetTempPath(),
            $"cxc-generic-jagged-array-extension-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(outputDirectory);
            CCodeOutputGenerator.GenerateOutput(project,
                Path.Combine(outputDirectory, "GenericJaggedArrayExtension.cx"));
            var source = File.ReadAllText(Directory.GetFiles(outputDirectory, "*.c").Single());
            Assert.Contains(invocation.TargetSymbol.SpecializationName!, source);
            Assert.DoesNotContain("_unknowntype_", source);
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
    public void GenericArrayExtensionMethodsCloseConstructedElementTypes()
    {
        var project = CreateProject("""
            public class Box<T> { public T Value; }
            public extension BoxArrayExtensions<T> : Box<T>[] {
                public Box<T> First() const { return this[0]; }
            }
            public int Main() {
                Box<int> box = new Box<int>();
                box.Value = 42;
                Box<int>[] values = new Box<int>[1];
                values[0] = box;
                return values.First().Value;
            }
            """);

        new SemanticBinder().Bind(project);

        var main = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<FunctionDeclaration>().Single(function => function.Name == "Main");
        var returned = Assert.IsType<CxCompiler.Model.Statements.ReturnStatement>(
            main.Body!.OfType<CxCompiler.Model.Statements.ReturnStatement>().Single());
        var memberAccess = Assert.IsType<CxCompiler.Model.Expressions.MemberAccessExpression>(
            returned.Expression);
        var invocation = Assert.IsType<CxCompiler.Model.Expressions.InvocationExpression>(
            memberAccess.Target);
        Assert.Equal("Box", Assert.IsType<NamedType>(
            invocation.TargetSymbol!.ReturnType).Name);
        Assert.Equal(BuiltInSystemTypes.Int, invocation.TargetSymbol.GenericTypeArguments["T"]);

        var outputDirectory = Path.Combine(Path.GetTempPath(),
            $"cxc-generic-constructed-array-extension-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(outputDirectory);
            CCodeOutputGenerator.GenerateOutput(project,
                Path.Combine(outputDirectory, "GenericConstructedArrayExtension.cx"));
            var source = File.ReadAllText(Directory.GetFiles(outputDirectory, "*.c").Single());
            Assert.Contains(invocation.TargetSymbol.SpecializationName!, source);
            Assert.DoesNotContain("_unknowntype_", source);
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
    public void GenericArrayExtensionMethodsCloseMultipleElementTypeArguments()
    {
        var project = CreateProject("""
            public class Pair<T, U> { public T First; public U Second; }
            public extension PairArrayExtensions<T, U> : Pair<T, U>[] {
                public Pair<T, U> FirstPair() const { return this[0]; }
            }
            public int Main() {
                Pair<int, string> pair = new Pair<int, string>();
                pair.First = 40;
                pair.Second = "xy";
                Pair<int, string>[] values = new Pair<int, string>[1];
                values[0] = pair;
                return values.FirstPair().First + 2;
            }
            """);

        new SemanticBinder().Bind(project);

        var main = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<FunctionDeclaration>().Single(function => function.Name == "Main");
        var returnStatement = Assert.IsType<CxCompiler.Model.Statements.ReturnStatement>(
            main.Body!.OfType<CxCompiler.Model.Statements.ReturnStatement>().Single());
        var binary = Assert.IsType<CxCompiler.Model.Expressions.BinaryExpression>(
            returnStatement.Expression);
        var firstMember = Assert.IsType<CxCompiler.Model.Expressions.MemberAccessExpression>(
            binary.Left);
        var invocation = Assert.IsType<CxCompiler.Model.Expressions.InvocationExpression>(
            firstMember.Target);
        Assert.Equal(BuiltInSystemTypes.Int,
            invocation.TargetSymbol!.GenericTypeArguments["T"]);
        Assert.Equal(BuiltInSystemTypes.String,
            invocation.TargetSymbol.GenericTypeArguments["U"]);

        var outputDirectory = Path.Combine(Path.GetTempPath(),
            $"cxc-generic-pair-array-extension-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(outputDirectory);
            CCodeOutputGenerator.GenerateOutput(project,
                Path.Combine(outputDirectory, "GenericPairArrayExtension.cx"));
            var source = File.ReadAllText(Directory.GetFiles(outputDirectory, "*.c").Single());
            Assert.Contains(invocation.TargetSymbol.SpecializationName!, source);
            Assert.DoesNotContain("_unknowntype_", source);
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
    public void ExtensionPropertiesAndMethodsBindToTheTargetReceiver()
    {
        var project = CreateProject("""
            public class Foo {
                public int Value { const get; set; }
            }

            public extension FooExtensions : Foo {
                public int DoubledValue { const get => Value * 2; }
                public void Invert() { Value = -Value; }
            }

            public int Main() {
                var foo = new Foo();
                foo.Value = 5;
                foo.Invert();
                return foo.DoubledValue;
            }
            """);

        new SemanticBinder().Bind(project);
        var foo = Assert.IsType<ClassDeclaration>(project.CompilationContexts.Single()
            .DeclarationScope.Declarations.Single(declaration => declaration.Name == "Foo"));
        var extension = Assert.IsType<ExtensionDeclaration>(project.CompilationContexts.Single()
            .DeclarationScope.Declarations.Single(declaration => declaration.Name == "FooExtensions"));
        var extensionMethod = Assert.Single(extension.Members.MemberDeclarations.Declarations
            .OfType<FunctionDeclaration>(), function => function.Name == "Invert");
        var extensionProperty = Assert.Single(extension.Members.MemberDeclarations.Declarations
            .OfType<PropertyDeclaration>(), property => property.Name == "DoubledValue");
        Assert.Same(foo, extensionMethod.ParentClassDeclaration);
        Assert.Same(foo, extensionProperty.ParentClassDeclaration);

        var outputDirectory = Path.Combine(Path.GetTempPath(), $"cxc-extension-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(outputDirectory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(outputDirectory, "Extensions.cx"));
            var source = File.ReadAllText(Directory.GetFiles(outputDirectory, "*.c").Single());
            Assert.Contains("Invert", source);
            Assert.Contains("DoubledValue", source);
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
    public void GenericExtensionTargetBindsToClosedGenericReceiver()
    {
        var project = CreateProject("""
            public class Box<T> { }
            public extension BoxExtensions<T> : Box<T> {
                public int Answer() const { return 42; }
                public T Echo(T value) const { return value; }
                public U Convert<U>(U value) const { return value; }
                public U[] Copy<U>(U[] source) const {
                    U[] result = new U[source.Length];
                    uint index = 0u;
                    foreach (U item in source) {
                        result[index] = item;
                        index++;
                    }
                    return result;
                }
            }
            public int Main() {
                Box<int> box = new Box<int>();
                int[] values = new int[1];
                values[0] = 7;
                int[] copy = box.Copy(values);
                return box.Answer() + box.Convert(copy[0]);
            }
            """);

        new SemanticBinder().Bind(project);
        Assert.Contains(project.GenericFunctionInstances,
            instance => instance.Declaration?.Name == "Convert");
        Assert.Contains(project.GenericFunctionInstances,
            instance => instance.Declaration?.Name == "Copy");

        var extension = Assert.IsType<ExtensionDeclaration>(project.CompilationContexts.Single()
            .DeclarationScope.Declarations.Single(declaration => declaration.Name == "BoxExtensions"));
        Assert.Equal(["T"], extension.GenericTypeNames);
        var method = Assert.Single(extension.Members.MemberDeclarations.Declarations
            .OfType<FunctionDeclaration>(), function => function.Name == "Answer");
        Assert.Same(project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<ClassDeclaration>().Single(declaration => declaration.Name == "Box"),
            method.ParentClassDeclaration);

        var outputDirectory = Path.Combine(Path.GetTempPath(), $"cxc-generic-extension-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(outputDirectory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(outputDirectory, "GenericExtension.cx"));
            var source = File.ReadAllText(Directory.GetFiles(outputDirectory, "*.c").Single());
            Assert.Contains("return 42;", source);
            Assert.Contains("return value;", source);
            Assert.Contains("__cx_concept_foreach_collection_item", source);
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
    public void InterfaceExtensionMethodsAndPropertiesUseTheInterfaceReceiver()
    {
        var project = CreateProject("""
            public interface ICounter { public int Get() const; }
            public class Counter : ICounter {
                public int Get() const { return 17; }
            }
            public extension CounterExtensions : ICounter {
                public int Twice { const get => this.Get() * 2; }
                public int ReadTwice() const { return this.Get() * 2; }
            }
            public int Main() {
                ICounter counter = new Counter();
                return counter.ReadTwice() + counter.Twice;
            }
            """);

        new SemanticBinder().Bind(project);

        var extension = Assert.IsType<ExtensionDeclaration>(project.CompilationContexts.Single()
            .DeclarationScope.Declarations.Single(declaration => declaration.Name == "CounterExtensions"));
        var extensionMethod = Assert.Single(extension.Members.MemberDeclarations.Declarations
            .OfType<FunctionDeclaration>(), function => function.Name == "ReadTwice");
        var extensionProperty = Assert.Single(extension.Members.MemberDeclarations.Declarations
            .OfType<PropertyDeclaration>(), property => property.Name == "Twice");
        Assert.True(extensionMethod.IsExtension);
        Assert.True(extensionProperty.IsExtension);
        Assert.Equal(ClassType.Interface, extensionMethod.ParentClassDeclaration!.ClassType);

        var outputDirectory = Path.Combine(Path.GetTempPath(), $"cxc-interface-extension-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(outputDirectory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(outputDirectory, "Extensions.cx"));
            var source = File.ReadAllText(Directory.GetFiles(outputDirectory, "*.c").Single());
            var header = File.ReadAllText(Directory.GetFiles(outputDirectory, "*.h")
                .Single(path => !path.EndsWith(".internal.h", StringComparison.Ordinal)));
            Assert.Contains("struct cx_iface_ref __this", source);
            Assert.Contains("struct cx_iface_ref __this", header);
            Assert.Contains("ReadTwice", source);
            Assert.Contains("Twice", source);
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
    public void PrimitiveExtensionMethodsUseValueReceivers()
    {
        var project = CreateProject("""
            public delegate int IntReader();
            public extension IntExtensions : int {
                public int AddOne() const { return this - (-1); }
                public int Next { const get => this - (-1); }
                public int operator +(int other) const { return this * 10 - other; }
                public int operator ~() const { return -this; }
            }
            public int Main() {
                int value = 41;
                int left = 40;
                int right = 2;
                IntReader reader = value.AddOne;
                if (left + right != 398) return 0;
                if (~left != -40) return 0;
                if (value.AddOne() != 42) return 0;
                if (reader() != 42) return 0;
                if (value.Next != 42) return 0;
                return 42;
            }
            """);

        new SemanticBinder().Bind(project);

        var extension = Assert.IsType<ExtensionDeclaration>(project.CompilationContexts.Single()
            .DeclarationScope.Declarations.Single(declaration => declaration.Name == "IntExtensions"));
        var method = Assert.Single(extension.Members.MemberDeclarations.Declarations
            .OfType<FunctionDeclaration>(), function => function.Name == "AddOne");
        Assert.True(method.IsExtension);
        Assert.Same(CxCompiler.Model.Types.BuiltInTypes.BuiltInSystemTypes.Int,
            method.ExtensionTargetType);

        var outputDirectory = Path.Combine(Path.GetTempPath(), $"cxc-primitive-extension-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(outputDirectory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(outputDirectory, "PrimitiveExtension.cx"));
            var source = File.ReadAllText(Directory.GetFiles(outputDirectory, "*.c").Single());
            var header = File.ReadAllText(Directory.GetFiles(outputDirectory, "*.h")
                .Single(path => !path.EndsWith(".internal.h", StringComparison.Ordinal)));
            Assert.Contains("cx_int __this", source);
            Assert.Contains("cx_int __this", header);
            Assert.Contains("__this", source);
            Assert.Contains("AddOne", source);
            Assert.Contains("Next", source);
            Assert.Contains("__cx_operator_plus", source);
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
    public void PrimitiveExtensionPropertiesCannotDeclareSetters()
    {
        var project = CreateProject("""
            public extension IntExtensions : int {
                public int Value { get; set; }
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(project));

        Assert.Contains("Primitive-target extension properties must be read-only", error.Message);
    }

    [Fact]
    public void PrimitiveExtensionIncrementAndCompoundOperatorsWriteBackValues()
    {
        var project = CreateProject("""
            public extension IntExtensions : int {
                public int operator ++() const { return this + 1; }
                public int operator --() const { return this - 1; }
                public int operator +=(int value) const { return this + value; }
                public int operator -=(int value) const { return this - value; }
            }
            public enum Counter { Zero, One }
            public extension CounterExtensions : Counter {
                public Counter operator ++() const { return Counter.One; }
                public Counter operator +=(Counter value) const { return value; }
            }
            public int Main() {
                int value = 3;
                int old = value++;
                --value;
                value += 5;
                value -= 2;
                Counter counter = Counter.Zero;
                Counter oldCounter = counter++;
                counter += Counter.Zero;
                return old + value + (oldCounter == Counter.Zero ? 0 : 1) +
                    (counter == Counter.One ? 0 : 1);
            }
            """);

        new SemanticBinder().Bind(project);

        var outputDirectory = Path.Combine(Path.GetTempPath(),
            $"cxc-primitive-extension-mutation-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(outputDirectory);
            CCodeOutputGenerator.GenerateOutput(project,
                Path.Combine(outputDirectory, "PrimitiveExtensionMutation.cx"));
            var source = File.ReadAllText(Directory.GetFiles(outputDirectory, "*.c").Single());
            Assert.Contains("__cx_operator_increment", source);
            Assert.Contains("__cx_operator_plus_assign", source);
            Assert.Contains("__cx_operator_minus_assign", source);
            Assert.Contains("&(", source);
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
    public void PrimitiveExtensionMutationOperatorsMustReturnTheirTargetValue()
    {
        var project = CreateProject("""
            public extension InvalidIntExtensions : int {
                public void operator ++() const { }
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(project));

        Assert.Contains("Operator 'operator ++'", error.Message);
        Assert.Contains("has an invalid signature", error.Message);
    }

    [Fact]
    public void EnumExtensionsUseValueReceivers()
    {
        var project = CreateProject("""
            public enum Shade { Dark, Light }
            public extension ShadeExtensions : Shade {
                public bool IsLight() const { return this == Shade.Light; }
                public int operator +(Shade other) const {
                    return this == other ? 42 : 0;
                }
            }
            public int Main() {
                Shade shade = Shade.Light;
                return shade.IsLight() && shade + Shade.Light == 42 ? 42 : 0;
            }
            """);

        new SemanticBinder().Bind(project);

        var extension = Assert.IsType<ExtensionDeclaration>(project.CompilationContexts.Single()
            .DeclarationScope.Declarations.Single(declaration => declaration.Name == "ShadeExtensions"));
        var method = Assert.Single(extension.Members.MemberDeclarations.Declarations
            .OfType<FunctionDeclaration>(), function => function.Name == "IsLight");
        Assert.Equal(ClassType.Enum, Assert.IsType<NamedType>(method.ExtensionTargetType).ClassType);

        var outputDirectory = Path.Combine(Path.GetTempPath(), $"cxc-enum-extension-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(outputDirectory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(outputDirectory, "EnumExtension.cx"));
            var source = File.ReadAllText(Directory.GetFiles(outputDirectory, "*.c").Single());
            Assert.Contains("IsLight", source);
            Assert.Contains("__this", source);
            Assert.Contains("__cx_operator_plus", source);
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
    public void GenericExtensionTargetMustForwardItsParametersInOrder()
    {
        var project = CreateProject("""
            public class Pair<TLeft, TRight> { }
            public extension Invalid<TLeft, TRight> : Pair<TRight, TLeft> {
                public void Run() { }
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(project));

        Assert.Contains("using each target type parameter exactly once in declaration order", error.Message);
    }

    [Fact]
    public void ExtensionCannotDeclareInstanceFields()
    {
        var project = CreateProject("""
            public class Foo { }
            public extension Invalid : Foo {
                public int Value;
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(project));

        Assert.Contains("can contain only methods and properties", error.Message);
    }

    [Fact]
    public void ExtensionOperatorMustHaveAValidOperatorSignature()
    {
        var project = CreateProject("""
            public class Foo { }
            public extension FooOperators : Foo {
                public Foo operator +(Foo left, Foo right) { return left; }
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(project));

        Assert.Contains("Operator 'operator +'", error.Message);
        Assert.Contains("has an invalid signature", error.Message);
    }

    [Fact]
    [Trait("Area", "CrossModule")]
    public void ImportedExtensionModuleExportsMethodsAndProperties()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cxc-extension-reference-{Guid.NewGuid():N}");
        var types = Path.Combine(root, "types");
        var extensions = Path.Combine(root, "extensions");
        var app = Path.Combine(root, "app");
        Directory.CreateDirectory(types);
        Directory.CreateDirectory(extensions);
        Directory.CreateDirectory(app);
        try
        {
            File.WriteAllText(Path.Combine(types, "types.cxproj"), "name: types\ntype: Library\n");
            File.WriteAllText(Path.Combine(types, "foo.cx"),
                "namespace Types; public class Foo { public int Value; } public class Box<T> { }");
            File.WriteAllText(Path.Combine(extensions, "extensions.cxproj"),
                "name: extensions\ntype: Library\nproject_references:\n- ../types/types.cxproj\n");
            File.WriteAllText(Path.Combine(extensions, "api.cx"), """
                import Types;
                namespace Extensions;
                public interface IFoo { public int Get() const; }
                public extension FooExtensions : Foo {
                    public int DoubledValue { const get => Value * 2; }
                    public void Invert() { Value = -Value; }
                }
                public extension IFooExtensions : IFoo {
                    public int DoubleGet() const { return this.Get() * 2; }
                }
                public extension BoxExtensions<T> : Box<T> {
                    public int Answer() const { return 42; }
                    public T Echo(T value) const { return value; }
                    public U Convert<U>(U value) const { return value; }
                }
                public extension IntExtensions : int {
                    public int AddOne() const { return this + 1; }
                    public int Next { const get => this + 1; }
                }
                """);
            File.WriteAllText(Path.Combine(app, "app.cxproj"),
                "name: app\ntype: Executable\nproject_references:\n- ../extensions/extensions.cxproj\n");
            File.WriteAllText(Path.Combine(app, "main.cx"), """
                import Types;
                import Extensions;
                namespace App;
                public class AppFoo : IFoo {
                    public int Get() const { return 5; }
                }
                public int Run() {
                    Foo foo = new Foo();
                    foo.Value = 5;
                    foo.Invert();
                    Box<int> box = new Box<int>();
                    IFoo interfaceFoo = new AppFoo();
                    int value = 41;
                    return foo.DoubledValue + interfaceFoo.DoubleGet() +
                        box.Answer() + box.Echo(1) + box.Convert(7) +
                        value.AddOne() + value.Next;
                }
                """);

            new Compiler().Compile([Path.Combine(app, "app.cxproj")]);

            var extensionHeader = File.ReadAllText(Path.Combine(extensions, ".obj", "extensions.h"));
            var extensionSource = File.ReadAllText(Path.Combine(extensions, ".obj", "extensions.c"));
            var appSource = File.ReadAllText(Path.Combine(app, ".obj", "app.c"));
            Assert.Contains("CX_EXTENSIONS_API", extensionHeader);
            Assert.Contains("FooExtensions", extensionHeader);
            Assert.Contains("Invert", extensionHeader);
            Assert.Contains("Types, Foo", extensionHeader);
            Assert.Contains("IFooExtensions", extensionHeader);
            Assert.Contains("DoubleGet", extensionHeader);
            Assert.Contains("BoxExtensions", extensionHeader);
            Assert.Contains("Answer", extensionHeader);
            Assert.Contains("Echo", extensionHeader);
            Assert.Contains("cx_int __this", extensionHeader);
            Assert.Contains("Next", extensionHeader);
            Assert.Contains("FooExtensions", extensionSource);
            Assert.Contains("Invert", extensionSource);
            Assert.Contains("struct cx_iface_ref __this", extensionSource);
            Assert.Contains("DoubleGet", extensionSource);
            Assert.Contains("cx_generic_", appSource);
            Assert.Contains("return 42;", appSource);
            Assert.Contains("AddOne", extensionSource);
            Assert.Contains("Next", extensionSource);
            Assert.Contains("DoubledValue", appSource);
            Assert.Contains("Invert", appSource);
            Assert.Contains("DoubleGet", appSource);
            Assert.True(appSource.Split("return value;").Length - 1 >= 2);
            Assert.Contains("AddOne", appSource);
            Assert.Contains("Next", appSource);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    [Trait("Area", "CrossModule")]
    public void ImportedExtensionOperatorBindsToLeftOperandAndIsEmittedByDeclaringModule()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cxc-extension-operator-{Guid.NewGuid():N}");
        var types = Path.Combine(root, "types");
        var extensions = Path.Combine(root, "extensions");
        var app = Path.Combine(root, "app");
        Directory.CreateDirectory(types);
        Directory.CreateDirectory(extensions);
        Directory.CreateDirectory(app);
        try
        {
            File.WriteAllText(Path.Combine(types, "types.cxproj"), "name: types\ntype: Library\n");
            File.WriteAllText(Path.Combine(types, "foo.cx"), "namespace Types; public class Foo { public int Value; }");
            File.WriteAllText(Path.Combine(extensions, "extensions.cxproj"),
                "name: extensions\ntype: Library\nproject_references:\n- ../types/types.cxproj\n");
            File.WriteAllText(Path.Combine(extensions, "api.cx"), """
                import Types;
                namespace Extensions;
                public extension FooOperators : Foo {
                    public Foo operator +(Foo other) {
                        Foo result = new Foo();
                        result.Value = Value + other.Value;
                        return result;
                    }
                }
                """);
            File.WriteAllText(Path.Combine(app, "app.cxproj"),
                "name: app\ntype: Executable\nproject_references:\n- ../extensions/extensions.cxproj\n");
            File.WriteAllText(Path.Combine(app, "main.cx"), """
                import Types;
                import Extensions;
                namespace App;
                public int Run() {
                    Foo left = new Foo();
                    Foo right = new Foo();
                    left.Value = 19;
                    right.Value = 23;
                    return (left + right).Value;
                }
                """);

            new Compiler().Compile([Path.Combine(app, "app.cxproj")]);

            var extensionSource = File.ReadAllText(Path.Combine(extensions, ".obj", "extensions.c"));
            var appSource = File.ReadAllText(Path.Combine(app, ".obj", "app.c"));
            Assert.Contains("__cx_operator_plus", extensionSource);
            Assert.Contains("__cx_operator_plus", appSource);
            Assert.DoesNotContain("(left + right)", appSource);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    [Trait("Area", "CrossModule")]
    public void ExtensionMethodsRequireTheirNamespaceImport()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cxc-extension-import-{Guid.NewGuid():N}");
        var types = Path.Combine(root, "types");
        var extensions = Path.Combine(root, "extensions");
        var app = Path.Combine(root, "app");
        Directory.CreateDirectory(types);
        Directory.CreateDirectory(extensions);
        Directory.CreateDirectory(app);
        try
        {
            File.WriteAllText(Path.Combine(types, "types.cxproj"), "name: types\ntype: Library\n");
            File.WriteAllText(Path.Combine(types, "foo.cx"), "namespace Types; public class Foo { }");
            File.WriteAllText(Path.Combine(extensions, "extensions.cxproj"),
                "name: extensions\ntype: Library\nproject_references:\n- ../types/types.cxproj\n");
            File.WriteAllText(Path.Combine(extensions, "api.cx"), """
                import Types;
                namespace Extensions;
                public extension FooExtensions : Foo { public void Run() { } }
                """);
            File.WriteAllText(Path.Combine(app, "app.cxproj"),
                "name: app\ntype: Executable\nproject_references:\n- ../extensions/extensions.cxproj\n");
            File.WriteAllText(Path.Combine(app, "main.cx"), """
                import Types;
                namespace App;
                public int Run() { Foo foo = new Foo(); foo.Run(); return 0; }
                """);

            var error = Assert.Throws<CompilationErrorException>(
                () => new Compiler().Compile([Path.Combine(app, "app.cxproj")]));

            Assert.Contains("Cannot resolve function 'Run'", error.Message);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static CxProject CreateProject(string source)
    {
        var project = CxProject.CreateDefaultApplicationProject("extension_tests");
        project.AddCompilationContext(CompilerTestHelper.Parse(source));
        return project;
    }
}


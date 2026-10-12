using CxCompiler.Model.Project;
using CxCompiler.Model.Errors;
using CxCompiler.Model.Expressions;
using CxCompiler.Model.Statements;
using CxCompiler.OutputGenerators;
using CxCompiler.Model.Types;
using CxCompiler.Model.Types.BuiltInTypes;
using CxCompiler.Semantics;

namespace CxCompiler.Tests;

public sealed class GenericFoundationTests
{
    [Fact]
    public void ParsesGenericTypeArgumentsInSourceOrder()
    {
        var context = CompilerTestHelper.Parse("""
            public struct First {}
            public struct Second {}
            public class Pair<T, U> {}
            public Pair<First, Second> Create();
            """);
        var function = context.DeclarationScope.Declarations
            .OfType<FunctionDeclaration>().Single();
        var pair = Assert.IsType<NamedType>(function.ReturnType);

        Assert.Equal(["First", "Second"], pair.TypeArguments.Select(argument => argument.Name));
    }

    [Fact]
    public void ClosesGenericValueFieldsOverPrimitiveTypeArguments()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_primitive_box");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public class Box<T> {
                public T value;
                public constructor() {}
            }
            public Box<int> Create() { return new Box<int>(); }
            public void Write(Box<int> box, int value) { box.value = value; }
            public int Read(Box<int> box) { return box.value; }
            """));
        new SemanticBinder().Bind(project);

        var instance = Assert.Single(project.GenericTypeInstances);
        Assert.Same(BuiltInSystemTypes.Int, Assert.Single(instance.Type.TypeArguments));
        var directory = Path.Combine(Path.GetTempPath(), $"cxc-primitive-generic-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var header = File.ReadAllText(Path.Combine(directory, "generic_primitive_box.h"));
            Assert.Contains($"struct {instance.Type.ConstructedIdentity!.CIdentifier} {{", header);
            Assert.Contains("cx_int value;", header);
            Assert.DoesNotContain("_unknowntype_", header);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ClosesGenericArrayFieldsOverPrimitiveTypeArguments()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_array_field");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public class Buffer<T> {
                public T[] values;
                public constructor() {}
            }
            public Buffer<int> Create() { return new Buffer<int>(); }
            public void Store(Buffer<int> buffer, int[] values) { buffer.values = values; }
            public int[] Load(Buffer<int> buffer) { return buffer.values; }
            """));
        new SemanticBinder().Bind(project);

        var instance = Assert.Single(project.GenericTypeInstances);
        Assert.Same(BuiltInSystemTypes.Int, Assert.Single(instance.Type.TypeArguments));
        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-generic-array-field-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var header = File.ReadAllText(Path.Combine(directory, "generic_array_field.h"));
            Assert.Contains($"struct {instance.Type.ConstructedIdentity!.CIdentifier} {{", header);
            Assert.Contains("Array)* values;", header);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ClosedGenericOwnerCanAllocateConcreteArrayFieldsFromLiterals()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_concrete_array_field");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public class Storage<T> {
                private uint[] buckets;
                public constructor() { buckets = new uint[8u]; }
                public uint Read(uint index) { return buckets[index]; }
                public void Write(uint index, uint value) { buckets[index] = value; }
            }
            public Storage<int> Create() { return new Storage<int>(); }
            """));
        new SemanticBinder().Bind(project);

        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-generic-concrete-array-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var header = File.ReadAllText(Path.Combine(directory, "generic_concrete_array_field.h"));
            var source = File.ReadAllText(Path.Combine(directory, "generic_concrete_array_field.c"));

            Assert.Contains("Array)* buckets;", header);
            Assert.Contains("cx_array_new((cx_uint)(8u), (cx_uint)sizeof(cx_uint))", source);
            Assert.Contains("cx_array_at", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ClosesNestedConstructedReferenceFieldsAndProperties()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_nested_reference_field");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public class Item {}
            public class Box<T> {
                public T value;
                public constructor(T initial) { value = initial; }
            }
            public class Holder<T> {
                public Box<T> box;
                public Box<T>[] boxes;
                public constructor(Box<T> initial) { box = initial; }
                public Box<T> Value {
                    get { return box; }
                    set { box = value; }
                }
            }
            public Holder<Item> Create(Box<Item> box) {
                return new Holder<Item>(box);
            }
            public Box<Item> Read(Holder<Item> holder) { return holder.box; }
            public void Write(Holder<Item> holder, Box<Item> box) { holder.box = box; }
            public Box<Item> ReadProperty(Holder<Item> holder) { return holder.Value; }
            public void WriteProperty(Holder<Item> holder, Box<Item> box) {
                holder.Value = box;
            }
            """));

        new SemanticBinder().Bind(project);

        Assert.Contains(project.GenericTypeInstances, instance =>
            instance.Declaration.Name == "Holder");
        Assert.Contains(project.GenericTypeInstances, instance =>
            instance.Declaration.Name == "Box");
        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-generic-nested-reference-field-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var header = File.ReadAllText(
                Path.Combine(directory, "generic_nested_reference_field.h"));
            var source = File.ReadAllText(
                Path.Combine(directory, "generic_nested_reference_field.c"));
            Assert.Contains("box", header);
            Assert.Contains("box", source);
            Assert.DoesNotContain("_unknowntype_", header + source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void SpecializesConstructorForClosedMultiFieldValueLayout()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_value_constructor");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public class Pair<T, U> {
                public T first;
                public U second;
                public constructor(T firstValue, U secondValue) {
                    first = firstValue;
                    second = secondValue;
                }
            }
            public Pair<int, long> Create() { return new Pair<int, long>(23, 41L); }
            public int ReadFirst(Pair<int, long> pair) { return pair.first; }
            public long ReadSecond(Pair<int, long> pair) { return pair.second; }
            """));
        new SemanticBinder().Bind(project);

        Assert.Single(project.GenericTypeInstances);
        var constructor = Assert.Single(project.GenericFunctionInstances);
        Assert.IsType<ConstructorDeclaration>(constructor.Declaration);
        Assert.NotNull(constructor.ClosedContainingType);
        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-generic-value-constructor-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var header = File.ReadAllText(Path.Combine(directory,
                "generic_value_constructor.h"));
            var source = File.ReadAllText(Path.Combine(directory,
                "generic_value_constructor.c"));
            Assert.Contains(constructor.SpecializationName!, header);
            Assert.Contains("__this->first = firstValue;", source);
            Assert.Contains("__this->second = secondValue;", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void BindingSubstitutesClassParametersInFieldsMethodsAndArrays()
    {
        var project = CxProject.CreateDefaultApplicationProject();
        var context = CompilerTestHelper.Parse("""
            public class Box<T> {
                public T value;
                public T[] values;
                public constructor(T initial) { value = initial; }
                public T Value { extern get; }
                public T Echo(T input) { return input; }
            }
            """);
        project.AddCompilationContext(context);
        new SemanticBinder().Bind(project);

        var box = Assert.Single(context.DeclarationScope.Declarations.OfType<ClassDeclaration>());
        var fields = box.MemberDeclarations.Declarations.OfType<FieldDeclaration>().ToArray();
        Assert.IsType<GenericType>(fields[0].Type);
        Assert.IsType<GenericType>(Assert.IsType<ArrayType>(fields[1].Type).ElementType);
        var constructor = Assert.Single(box.MemberDeclarations.Declarations.OfType<ConstructorDeclaration>());
        Assert.IsType<GenericType>(Assert.Single(constructor.Parameters).ParameterType);
        var property = Assert.Single(box.MemberDeclarations.Declarations.OfType<PropertyDeclaration>());
        Assert.IsType<GenericType>(property.Type);
        var echo = Assert.Single(box.MemberDeclarations.Declarations.OfType<FunctionDeclaration>(),
            item => item is not ConstructorDeclaration);
        Assert.IsType<GenericType>(echo.ReturnType);
        Assert.IsType<GenericType>(Assert.Single(echo.Parameters).ParameterType);
    }

    [Fact]
    public void SubstitutionPreservesWrappersAndDoesNotMutateTemplate()
    {
        var template = new ArrayType(new ConstType(new GenericType("T")));
        var closed = GenericTypeSubstitution.Substitute(template,
            new Dictionary<string, TypeBase> { ["T"] = BuiltInSystemTypes.Int });

        Assert.Same(BuiltInSystemTypes.Int,
            Assert.IsType<ConstType>(Assert.IsType<ArrayType>(closed).ElementType).UnderlyingType);
        Assert.IsType<GenericType>(Assert.IsType<ConstType>(template.ElementType).UnderlyingType);
    }

    [Fact]
    public void ConstructedRuntimeIdentityIsStableAndDistinguishesArgumentsAndModules()
    {
        var first = GenericTypeIdentity.Create("lib", "Example.Box", [BuiltInSystemTypes.Int]);
        var again = GenericTypeIdentity.Create("lib", "Example.Box", [BuiltInSystemTypes.Int]);
        var otherArgument = GenericTypeIdentity.Create("lib", "Example.Box", [BuiltInSystemTypes.Long]);
        var otherModule = GenericTypeIdentity.Create("other", "Example.Box", [BuiltInSystemTypes.Int]);

        Assert.Equal(first.CanonicalName, again.CanonicalName);
        Assert.Equal(first.CIdentifier, again.CIdentifier);
        Assert.Equal(first.RuntimeHash, again.RuntimeHash);
        Assert.NotEqual(first.CIdentifier, otherArgument.CIdentifier);
        Assert.NotEqual(first.RuntimeHash, otherArgument.RuntimeHash);
        Assert.NotEqual(first.RuntimeHash, otherModule.RuntimeHash);

        var stringAlias = new NamedType("String", []);
        Assert.Equal(
            GenericTypeIdentity.Create("lib", "Example.Box", [BuiltInSystemTypes.String]).RuntimeHash,
            GenericTypeIdentity.Create("lib", "Example.Box", [stringAlias]).RuntimeHash);
    }

    [Fact]
    public void BindingResolvesTypeArgumentsAndRejectsWrongArity()
    {
        var project = CxProject.CreateDefaultApplicationProject();
        var context = CompilerTestHelper.Parse("""
            public class Payload {}
            public class Box<T> {}
            public class Holder { public Box<Payload> value; }
            """);
        project.AddCompilationContext(context);
        new SemanticBinder().Bind(project);
        var holder = context.DeclarationScope.Declarations.OfType<ClassDeclaration>()
            .Single(item => item.Name == "Holder");
        var field = Assert.Single(holder.MemberDeclarations.Declarations.OfType<FieldDeclaration>());
        var constructed = Assert.IsType<NamedType>(field.Type);
        Assert.Equal("unnamed.Payload",
            Assert.IsType<NamedType>(Assert.Single(constructed.TypeArguments))
                .ResolvedTypeFullName.ToString());
        Assert.NotNull(constructed.ConstructedIdentity);
        Assert.Equal(GenericTypeIdentity.Create("unnamed", "Box", constructed.TypeArguments).RuntimeHash,
            constructed.ConstructedIdentity.RuntimeHash);

        var invalid = CxProject.CreateDefaultApplicationProject();
        invalid.AddCompilationContext(CompilerTestHelper.Parse("""
            public class Box<T> {}
            public class Holder { public Box value; }
            """));
        Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(invalid));
    }

    [Fact]
    public void GeneratedCDeclaresGenericReferenceFieldAndOpenTypeMetadata()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_type_reference");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public class Payload {}
            public class Box<T> {}
            public class Holder { public Box<Payload> value; }
            """));
        new SemanticBinder().Bind(project);
        var directory = Path.Combine(Path.GetTempPath(), $"cxc-generics-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var header = File.ReadAllText(Path.Combine(directory, "generic_type_reference.h"));
            var source = File.ReadAllText(Path.Combine(directory, "generic_type_reference.c"));
            Assert.Contains("struct CX_ID_2(generic_type_reference, Box)* value;", header);
            Assert.Contains(".GenericArity = 1", source);
            Assert.Contains("CX_REFLECTION_FLAG_TYPE_GENERIC", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ConstructedTypesWithDifferentArgumentsAreNotAssignable()
    {
        var project = CxProject.CreateDefaultApplicationProject();
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public class Payload {}
            public class Other {}
            public class Box<T> {}
            void Main() {
                Box<Payload> first = null;
                Box<Other> second = first;
            }
            """));
        Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
    }

    [Fact]
    public void BindingRegistersDistinctClosedMarkerTypesOnce()
    {
        var project = CxProject.CreateDefaultApplicationProject("closed_markers");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public class First {}
            public class Second {}
            public class Marker<T> {}
            public class Holder {
                public Marker<First> first;
                public Marker<First> again;
                public Marker<Second> second;
            }
            """));
        new SemanticBinder().Bind(project);

        var instances = project.GenericTypeInstances.ToArray();
        Assert.Equal(2, instances.Length);
        Assert.All(instances, instance => Assert.Equal("Marker", instance.Declaration.Name));
        Assert.Equal(2, instances.Select(instance =>
            instance.Type.ConstructedIdentity!.RuntimeHash).Distinct().Count());
    }

    [Fact]
    public void GeneratedCDefinesDistinctClosedMarkerDescriptors()
    {
        var project = CxProject.CreateDefaultApplicationProject("closed_markers");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public class First {}
            public class Second {}
            public class Marker<T> {}
            public class Holder {
                public Marker<First> first;
                public Marker<Second> second;
            }
            """));
        new SemanticBinder().Bind(project);
        var directory = Path.Combine(Path.GetTempPath(), $"cxc-closed-markers-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var header = File.ReadAllText(Path.Combine(directory, "closed_markers.h"));
            var source = File.ReadAllText(Path.Combine(directory, "closed_markers.c"));
            foreach (var instance in project.GenericTypeInstances)
            {
                var identity = instance.Type.ConstructedIdentity!;
                Assert.Contains($"CX_TYPEINFO_DECL({identity.CIdentifier})", header);
                Assert.Contains($"CX_BEGIN_VTABLE_DEF({identity.CIdentifier})", source);
                Assert.Contains($".Hash = 0x{identity.RuntimeHash:X}ULL", source);
            }
            foreach (var instance in project.GenericTypeInstances)
            {
                Assert.Contains(
                    $"&CX_ID_2({instance.Type.ConstructedIdentity!.CIdentifier}, __typeinfo)",
                    source);
            }
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ClassesWithoutConstructorsGenerateImplicitConstructors()
    {
        var project = CxProject.CreateDefaultApplicationProject("closed_member");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public struct Payload {}
            public class Box<T> { public T value; }
            public class Holder { public Box<Payload> box; }
            """));
        new SemanticBinder().Bind(project);
        var directory = Path.Combine(Path.GetTempPath(), $"cxc-closed-member-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var generated = File.ReadAllText(Path.Combine(directory, "closed_member.c"));
            Assert.Contains("__constructor", generated);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void SubstitutesValueStructFieldTypeOnClosedReceiver()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_value_box");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public struct Payload { public int number; }
            public class Box<T> {
                public T value;
                public constructor() {}
            }
            public Box<Payload> Create() { return new Box<Payload>(); }
            public Payload Read(Box<Payload> box) { return box.value; }
            public void Write(Box<Payload> box, Payload input) { box.value = input; }
            """));
        new SemanticBinder().Bind(project);
        var functions = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<FunctionDeclaration>().ToArray();
        var read = Assert.IsType<MemberAccessExpression>(
            Assert.IsType<ReturnStatement>(Assert.Single(functions[1].Body!)).Expression);
        var write = Assert.IsType<AssignmentExpression>(
            Assert.IsType<ExpressionStatement>(Assert.Single(functions[2].Body!)).Expression);
        Assert.Equal("Payload", Assert.IsType<NamedType>(read.InferredType).Name);
        Assert.Equal("Payload", Assert.IsType<NamedType>(write.Target.InferredType).Name);
        Assert.NotNull(Assert.Single(project.GenericTypeInstances).Type.ConstructedIdentity);
    }

    [Fact]
    public void SubstitutesEveryDirectValueFieldOnClosedReceiver()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_value_pair");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public struct Payload { public int number; }
            public class Box<T> {
                public T first;
                public T second;
                public constructor() {}
            }
            public Payload ReadFirst(Box<Payload> box) { return box.first; }
            public Payload ReadSecond(Box<Payload> box) { return box.second; }
            """));
        new SemanticBinder().Bind(project);

        var functions = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<FunctionDeclaration>().ToArray();
        var reads = functions.Select(function => Assert.IsType<MemberAccessExpression>(
            Assert.IsType<ReturnStatement>(Assert.Single(function.Body!)).Expression)).ToArray();

        Assert.Equal(["first", "second"], reads.Select(read =>
            read.TargetField!.Declaration.Name));
        Assert.All(reads, read => Assert.Equal("Payload", Assert.IsType<NamedType>(
            read.InferredType).Name));
        Assert.Single(project.GenericTypeInstances);
    }

    [Fact]
    public void GeneratedCUsesClosedValueStructLayoutAndMetadata()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_value_box");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public struct Payload { public int number; }
            public class Box<T> {
                public T value;
                public constructor() {}
            }
            public Box<Payload> Create() { return new Box<Payload>(); }
            public Payload Read(Box<Payload> box) { return box.value; }
            public void Write(Box<Payload> box, Payload input) { box.value = input; }
            """));
        new SemanticBinder().Bind(project);
        var identity = Assert.Single(project.GenericTypeInstances).Type.ConstructedIdentity!;
        var directory = Path.Combine(Path.GetTempPath(), $"cxc-generic-value-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var header = File.ReadAllText(Path.Combine(directory, "generic_value_box.h"));
            var source = File.ReadAllText(Path.Combine(directory, "generic_value_box.c"));
            Assert.Contains($"struct {identity.CIdentifier}", header);
            Assert.Contains("struct CX_ID_2(generic_value_box, Payload) value;", header);
            Assert.Contains($"sizeof(struct {identity.CIdentifier})", source);
            Assert.Contains($"offsetof(struct {identity.CIdentifier}, value)", source);
            Assert.Contains($"CX_INIT_VTABLE(__cx_new_0, {identity.CIdentifier})", source);
            Assert.DoesNotContain("_unknowntype_", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ClosedGenericValueLayoutAllowsConcretePrimitiveFields()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_value_mixed_fields");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public struct Payload { public int number; }
            public class Box<T> {
                public int id = 7;
                public int computed = (2 + 5) * 3;
                public int index;
                public T value;
                public Payload extra;
                public constructor(int initialIndex, T initial, Payload initialExtra) {
                    index = initialIndex;
                    value = initial;
                    extra = initialExtra;
                }
            }
            public Box<Payload> Create(Payload value) { return new Box<Payload>(9, value, value); }
            public int ReadId(Box<Payload> box) { return box.id; }
            public void WriteId(Box<Payload> box, int id) { box.id = id; }
            """));
        new SemanticBinder().Bind(project);

        var identity = Assert.Single(project.GenericTypeInstances).Type.ConstructedIdentity!;
        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-generic-value-mixed-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var header = File.ReadAllText(Path.Combine(directory, "generic_value_mixed_fields.h"));
            var source = File.ReadAllText(Path.Combine(directory, "generic_value_mixed_fields.c"));

            Assert.Contains($"struct {identity.CIdentifier} {{", header);
            Assert.Contains("cx_int id;", header);
            Assert.Contains("cx_int computed;", header);
            Assert.Contains("struct CX_ID_2(generic_value_mixed_fields, Payload) value;", header);
            Assert.Contains("struct CX_ID_2(generic_value_mixed_fields, Payload) extra;", header);
            Assert.Contains("__this->id = 7;", source);
            Assert.Contains("__this->computed = ((2 + 5) * 3);", source);
            Assert.Contains("__this->index = initialIndex;", source);
            Assert.Contains("__this->extra = initialExtra;", source);
            Assert.Contains($"offsetof(struct {identity.CIdentifier}, id)", source);
            Assert.Contains("->id = id;", source);
            Assert.DoesNotContain("_unknowntype_", header);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ClosedGenericValueLayoutAllowsClosedGenericStructFieldsByValue()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_value_nested_struct");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public struct AOuter<T> {
                public ZInner<T> inner;
                public constructor(ZInner<T> initial) { inner = initial; }
            }
            public struct ZInner<T> {
                public T value;
                public constructor(T initial) { value = initial; }
            }
            public int Create() {
                AOuter<int> value = new AOuter<int>(new ZInner<int>(37));
                return ReadNested(value);
            }
            public int ReadNested(AOuter<int> value) { return value.inner.value; }
            """));
        new SemanticBinder().Bind(project);
        Assert.Contains(project.GenericTypeInstances, instance =>
            instance.Type.Name == "ZInner" && instance.Type.TypeArguments.Count == 1);
        var outerFieldType = Assert.Single(project.CompilationContexts.Single()
            .DeclarationScope.Declarations.OfType<ClassDeclaration>()
            .Single(declaration => declaration.Name == "AOuter")
            .MemberDeclarations.Declarations.OfType<FieldDeclaration>()).Type;
        Assert.True(outerFieldType is NamedType, $"{outerFieldType.GetType().Name}: {outerFieldType.FullName}");

        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-generic-value-nested-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var header = File.ReadAllText(Path.Combine(directory, "generic_value_nested_struct.h"));
            var source = File.ReadAllText(Path.Combine(directory, "generic_value_nested_struct.c"));
            var innerType = project.GenericTypeInstances.Single(instance =>
                instance.Type.Name == "ZInner").Type.ConstructedIdentity!.CIdentifier;
            var outerType = project.GenericTypeInstances.Single(instance =>
                instance.Type.Name == "AOuter").Type.ConstructedIdentity!.CIdentifier;

            var innerDefinition = header.IndexOf($"struct {innerType} {{", StringComparison.Ordinal);
            var outerDefinition = header.IndexOf($"struct {outerType} {{", StringComparison.Ordinal);
            var innerTemplate = header.IndexOf(
                "CX_TYPE_DEF(CX_ID_2(generic_value_nested_struct, ZInner))", StringComparison.Ordinal);
            var outerTemplate = header.IndexOf(
                "CX_TYPE_DEF(CX_ID_2(generic_value_nested_struct, AOuter))", StringComparison.Ordinal);
            Assert.True(innerDefinition >= 0, header);
            Assert.True(outerDefinition > innerDefinition, header);
            Assert.True(innerTemplate >= 0 && outerTemplate > innerTemplate, header);
            Assert.Contains($"struct {innerType} inner;", header);
            Assert.Contains($"struct {innerType} initial", header);
            Assert.Contains("__this->inner = initial;", source);
            Assert.DoesNotContain("_unknowntype_", header);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void RejectsRecursiveClosedGenericStructFields()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_value_nested_cycle");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public struct Recursive<T> {
                public Recursive<T> next;
                public constructor() {}
            }
            public int Create() {
                Recursive<int> value = new Recursive<int>();
                return 1;
            }
            """));
        new SemanticBinder().Bind(project);

        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-generic-value-nested-cycle-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            var error = Assert.Throws<CxCompiler.Model.Errors.CompilationErrorException>(
                () => CCodeOutputGenerator.GenerateOutput(
                    project, Path.Combine(directory, "input.cx")));

            Assert.Contains("Closed generic value layouts contain a by-value cycle", error.Message);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ReferenceConstrainedAndArrayFieldsCanInitializeToNull()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_null_initializers");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public class NullFields<T> where T : class {
                public T value = null;
                public T[] values = null;
            }
            public int Main() {
                NullFields<string> fields = new NullFields<string>();
                return fields.value == null && fields.values == null ? 42 : 0;
            }
            """));
        new SemanticBinder().Bind(project);

        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-generic-null-initializers-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(Path.Combine(directory, "generic_null_initializers.c"));

            Assert.Contains("__this->value = CX_NULL;", source);
            Assert.Contains("__this->values = CX_NULL;", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void RejectsNullInitializerForUnconstrainedGenericField()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_null_initializer_invalid");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public class Box<T> { public T value = null; }
            """));

        var error = Assert.Throws<CxCompiler.Model.Errors.CompilationErrorException>(
            () => new SemanticBinder().Bind(project));

        Assert.Contains("Cannot initialize field", error.Message);
    }

    [Fact]
    public void GeneratedCUsesConcreteFieldsInGenericReferenceLayout()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_reference_mixed_layout");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public struct Payload { public int Number; }
            public class Box<T> {
                public int Count = 40 + 2;
                public Payload Payload;
                public T Value;
                public int CountProperty {
                    get { return Count; }
                    set { Count = value; }
                }
                public constructor(Payload payload, T value) {
                    Payload = payload;
                    Value = value;
                }
            }
            public int Main() {
                Payload payload;
                payload.Number = 29;
                Box<string> box = new Box<string>(payload, "reference");
                if (box.Value != "reference") return 0;
                box.CountProperty = 31;
                if (box.CountProperty != 31) return 0;
                return box.Count + box.Payload.Number;
            }
            """));
        new SemanticBinder().Bind(project);

        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-generic-reference-layout-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var header = File.ReadAllText(Path.Combine(directory,
                "generic_reference_mixed_layout.h"));
            var source = File.ReadAllText(Path.Combine(directory,
                "generic_reference_mixed_layout.c"));

            Assert.Contains("int Count;", header);
            Assert.Contains("Payload", header);
            Assert.Contains("Value", header);
            Assert.Contains("__this->Count = value;", source);
            Assert.Contains("return __this->Count;", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ClosedGenericReferenceLayoutPreservesClosedGenericClassBase()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_reference_base");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public class Base<T> where T : class {
                public int Number = 42;
                public T Value;
                public constructor() {}
            }
            public class Intermediate<U> where U : class : Base<U> {}
            public class Derived<T> where T : class : Intermediate<T> {}
            public int Main() {
                Derived<string> derived = new Derived<string>();
                derived.Value = "direct";
                Base<string> value = derived;
                if (derived.Value != "direct" || value.Value != "direct") return 0;
                return value.Number;
            }
            """));
        new SemanticBinder().Bind(project);

        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-generic-reference-base-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(Path.Combine(directory, "generic_reference_base.c"));
            var baseInstance = Assert.Single(project.GenericTypeInstances,
                instance => instance.Declaration.Name == "Base");

            Assert.Contains(
                $"CX_TYPEINFO_NAME({baseInstance.Type.ConstructedIdentity!.CIdentifier})",
                source);
            Assert.Contains("__this->__base", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void TypeTestRejectsDifferentClosedGenericArguments()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_type_test_arguments");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public class Box<T> {}
            public bool IsDifferent(Box<string> value) {
                return value is Box<int>;
            }
            """));

        var error = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(project));

        Assert.Contains("can never succeed", error.Message);
    }

    [Fact]
    public void GenericBaseTypeRequiresForwardedBaseConstraints()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_base_constraints");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public class Base<T> where T : class {}
            public class Derived<T> : Base<T> {}
            """));

        var error = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(project));

        Assert.Contains("does not satisfy the reference-type constraint", error.Message);
    }

    [Fact]
    public void GeneratedCUsesClosedValueLayoutForMultipleFields()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_value_pair");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public struct Payload { public int number; }
            public class Box<T> {
                public T first;
                public T second;
                public constructor() {}
            }
            public void WriteFirst(Box<Payload> box, Payload value) { box.first = value; }
            public void WriteSecond(Box<Payload> box, Payload value) { box.second = value; }
            public Payload ReadFirst(Box<Payload> box) { return box.first; }
            public Payload ReadSecond(Box<Payload> box) { return box.second; }
            """));
        new SemanticBinder().Bind(project);
        var identity = Assert.Single(project.GenericTypeInstances).Type.ConstructedIdentity!;
        var directory = Path.Combine(Path.GetTempPath(), $"cxc-generic-value-pair-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var header = File.ReadAllText(Path.Combine(directory, "generic_value_pair.h"));
            var source = File.ReadAllText(Path.Combine(directory, "generic_value_pair.c"));

            Assert.Contains($"struct {identity.CIdentifier} {{", header);
            Assert.Contains("struct CX_ID_2(generic_value_pair, Payload) first;", header);
            Assert.Contains("struct CX_ID_2(generic_value_pair, Payload) second;", header);
            Assert.Contains($"offsetof(struct {identity.CIdentifier}, first)", source);
            Assert.Contains($"offsetof(struct {identity.CIdentifier}, second)", source);
            Assert.Contains($"((struct {identity.CIdentifier}*)", source);
            Assert.Contains("->first = value;", source);
            Assert.Contains("return ((struct ", source);
            Assert.Contains(".fieldCount = 2", source);
            Assert.DoesNotContain("_unknowntype_", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void SubstitutesDistinctArgumentsAcrossTwoGenericValueParameters()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_struct_pair");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public struct First { public int value; }
            public struct Second { public long value; }
            public class Pair<T, U> {
                public T first;
                public U second;
                public constructor() {}
            }
            public First ReadFirst(Pair<First, Second> pair) { return pair.first; }
            public Second ReadSecond(Pair<First, Second> pair) { return pair.second; }
            """));
        new SemanticBinder().Bind(project);

        var pair = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<ClassDeclaration>().Single(declaration => declaration.Name == "Pair");
        var fields = pair.MemberDeclarations.Declarations.OfType<FieldDeclaration>().ToArray();
        Assert.Equal(["T", "U"], fields.Select(field => Assert.IsType<GenericType>(field.Type).Name));
        Assert.Equal(2, project.GenericTypeInstances.Single().Type.TypeArguments.Count);
        var identity = Assert.Single(project.GenericTypeInstances).Type.ConstructedIdentity!;
        var directory = Path.Combine(Path.GetTempPath(), $"cxc-generic-struct-pair-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var header = File.ReadAllText(Path.Combine(directory, "generic_struct_pair.h"));
            Assert.Contains($"struct {identity.CIdentifier} {{", header);
            Assert.Contains("struct CX_ID_2(generic_struct_pair, First) first;", header);
            Assert.Contains("struct CX_ID_2(generic_struct_pair, Second) second;", header);
            Assert.DoesNotContain("_unknowntype_", header);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void BindsClosedMarkerConstructionToItsRequestedType()
    {
        var project = CxProject.CreateDefaultApplicationProject("closed_construction");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public class First {}
            public class Marker<T> { public constructor() {} }
            Marker<First> Create() { return new Marker<First>(); }
            """));
        new SemanticBinder().Bind(project);

        var function = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<FunctionDeclaration>().Single();
        var statement = Assert.IsType<CxCompiler.Model.Statements.ReturnStatement>(
            Assert.Single(function.Body!));
        var creation = Assert.IsType<CxCompiler.Model.Expressions.ObjectCreationExpression>(
            statement.Expression);
        Assert.Equal(
            Assert.IsType<NamedType>(function.ReturnType).ConstructedIdentity!.CanonicalName,
            Assert.IsType<NamedType>(creation.RequestedType).ConstructedIdentity!.CanonicalName);
        Assert.NotNull(creation.Constructor);
    }

    [Fact]
    public void GeneratedCInstallsClosedVTableAfterMarkerConstructor()
    {
        var project = CxProject.CreateDefaultApplicationProject("closed_construction");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public class First {}
            public class Marker<T> { public constructor() {} }
            public Marker<First> Create() { return new Marker<First>(); }
            """));
        new SemanticBinder().Bind(project);
        var identity = Assert.Single(project.GenericTypeInstances).Type.ConstructedIdentity!;
        var marker = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<ClassDeclaration>().Single(item => item.Name == "Marker");
        var markerConstructor = Assert.IsType<ConstructorDeclaration>(
            Assert.Single(marker.MemberDeclarations.Declarations));
        Assert.Empty(markerConstructor.Parameters);
        Assert.Empty(markerConstructor.Body!);
        Assert.Equal(ConstructorInitializerKind.Base, markerConstructor.Initializer!.Kind);
        Assert.Empty(marker.BaseTypes);
        Assert.Equal(
            ["ToString", "Equals", "GetHashCode"],
            marker.VirtualMethodSlots.Select(slot => slot.Contract.Name));
        Assert.Empty(marker.InterfaceDispatchTables);
        var directory = Path.Combine(Path.GetTempPath(), $"cxc-closed-construction-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(Path.Combine(directory, "closed_construction.c"));
            var header = File.ReadAllText(Path.Combine(directory, "closed_construction.h"));
            Assert.Contains($"CX_INIT_VTABLE(__cx_new_0, {identity.CIdentifier})", source);
            Assert.Contains($"CX_BEGIN_VTABLE_DEF({identity.CIdentifier})", source);
            Assert.Contains(".functionCount = 1", source);
            Assert.Contains("extern CX_CLOSED_CONSTRUCTION_API struct CX_ID_2(closed_construction, Marker)* " +
                "CX_ID_2(closed_construction, Create)()", header);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void BindsClosedArrayFieldTypesAndKeepsDistinctOwners()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_array_field");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public class First {}
            public class Second {}
            public class Box<T> {
                public T[] values;
                public constructor() {}
            }
            Box<First> CreateFirst() { return new Box<First>(); }
            Box<Second> CreateSecond() { return new Box<Second>(); }
            """));
        new SemanticBinder().Bind(project);

        var box = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<ClassDeclaration>().Single(item => item.Name == "Box");
        Assert.IsType<GenericType>(Assert.IsType<ArrayType>(
            Assert.Single(box.MemberDeclarations.Declarations.OfType<FieldDeclaration>()).Type)
            .ElementType);
        Assert.Equal(2, project.GenericTypeInstances.Count);
        Assert.Equal(2, project.GenericTypeInstances.Select(item =>
            item.Type.ConstructedIdentity!.RuntimeHash).Distinct().Count());
    }

    [Fact]
    public void GeneratedCUsesArrayReferenceLayoutForClosedBoxTypes()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_array_field");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public class First {}
            public class Second {}
            public class Box<T> {
                public T[] values;
                public constructor() {}
            }
            Box<First> CreateFirst() { return new Box<First>(); }
            Box<Second> CreateSecond() { return new Box<Second>(); }
            """));
        new SemanticBinder().Bind(project);
        var directory = Path.Combine(Path.GetTempPath(), $"cxc-generic-array-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var header = File.ReadAllText(Path.Combine(directory, "generic_array_field.h"));
            var source = File.ReadAllText(Path.Combine(directory, "generic_array_field.c"));
            Assert.Contains("struct CX_ID_4(cxcore, System, Collections, Array)* values;", header);
            foreach (var instance in project.GenericTypeInstances)
            {
                var identity = instance.Type.ConstructedIdentity!;
                Assert.Contains($"CX_BEGIN_VTABLE_DEF({identity.CIdentifier})", source);
                Assert.Contains($".Hash = 0x{identity.RuntimeHash:X}ULL", source);
            }
            // The shared runtime metadata descriptor is emitted once per closed type.
            Assert.Equal(3, source.Split(".fieldCount = 1").Length - 1);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void BindsDirectGenericReferenceFieldForDistinctClassArguments()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_reference_field");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public class First {}
            public class Second {}
            public class Box<T> {
                public T value;
                public constructor() {}
            }
            Box<First> CreateFirst() { return new Box<First>(); }
            Box<Second> CreateSecond() { return new Box<Second>(); }
            """));
        new SemanticBinder().Bind(project);

        var box = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<ClassDeclaration>().Single(item => item.Name == "Box");
        var field = Assert.Single(box.MemberDeclarations.Declarations.OfType<FieldDeclaration>());
        Assert.Equal("T", Assert.IsType<GenericType>(field.Type).Name);
        Assert.Equal(2, project.GenericTypeInstances.Count);
        Assert.Equal(2, project.GenericTypeInstances.Select(item =>
            item.Type.ConstructedIdentity!.RuntimeHash).Distinct().Count());
    }

    [Fact]
    public void SubstitutesClosedConstructorParameterAtCallSite()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_constructor_value");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public class First {}
            public class Second {}
            public class Box<T> {
                public T value;
                public constructor(T initial) { value = initial; }
            }
            Box<First> CreateFirst(First value) { return new Box<First>(value); }
            Box<Second> CreateSecond(Second value) { return new Box<Second>(value); }
            """));
        new SemanticBinder().Bind(project);

        var creations = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<FunctionDeclaration>()
            .Where(item => item.Name is "CreateFirst" or "CreateSecond")
            .Select(item => Assert.IsType<ObjectCreationExpression>(
                Assert.IsType<ReturnStatement>(Assert.Single(item.Body!)).Expression))
            .ToArray();
        Assert.Equal(2, creations.Length);
        Assert.Equal("First", Assert.IsType<NamedType>(
            Assert.Single(creations[0].Constructor!.ParameterTypes)).Name);
        Assert.Equal("Second", Assert.IsType<NamedType>(
            Assert.Single(creations[1].Constructor!.ParameterTypes)).Name);
    }

    [Fact]
    public void GeneratedCUsesSharedPointerConstructorAndClosedVtables()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_constructor_value");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public class First {}
            public class Second {}
            public class Box<T> {
                public T value;
                public constructor(T initial) { value = initial; }
            }
            Box<First> CreateFirst(First value) { return new Box<First>(value); }
            Box<Second> CreateSecond(Second value) { return new Box<Second>(value); }
            """));
        new SemanticBinder().Bind(project);
        var directory = Path.Combine(Path.GetTempPath(), $"cxc-generic-ctor-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var header = File.ReadAllText(Path.Combine(directory, "generic_constructor_value.h"));
            var source = File.ReadAllText(Path.Combine(directory, "generic_constructor_value.c"));
            Assert.Contains("void* initial", header);
            Assert.Contains("void* initial", source);
            Assert.Contains("__this->value = initial;", source);
            foreach (var instance in project.GenericTypeInstances)
            {
                var identity = instance.Type.ConstructedIdentity!;
                var argument = Assert.IsType<NamedType>(Assert.Single(instance.Type.TypeArguments));
                Assert.Contains($"CX_INIT_VTABLE(__cx_new_", source);
                Assert.Contains($"{identity.CIdentifier})", source);
                Assert.Contains($".Hash = 0x{identity.RuntimeHash:X}ULL", source);
                Assert.Contains($"CX_ID_2({identity.CIdentifier}, __reflection_parameters_0)", source);
                Assert.Contains($"&CX_ID_3(generic_constructor_value, {argument.Name}, __typeinfo)",
                    source);
            }
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ClosedConstructorRejectsDifferentReferenceArgument()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_constructor_mismatch");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public class First {}
            public class Second {}
            public class Box<T> {
                public T value;
                public constructor(T initial) { value = initial; }
            }
            Box<First> Create(Second value) { return new Box<First>(value); }
            """));
        var error = Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
        Assert.Contains("No constructor", error.Message);
    }

    [Fact]
    public void SubstitutesBothClosedConstructorParameters()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_pair");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public class First {}
            public class Second {}
            public class Pair<T, U> {
                public T first;
                public U second;
                public constructor(T left, U right) { first = left; second = right; }
            }
            Pair<First, Second> CreateForward(First left, Second right) {
                return new Pair<First, Second>(left, right);
            }
            Pair<Second, First> CreateReverse(Second left, First right) {
                return new Pair<Second, First>(left, right);
            }
            """));
        new SemanticBinder().Bind(project);
        var creations = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<FunctionDeclaration>()
            .Where(item => item.Name is "CreateForward" or "CreateReverse")
            .Select(item => Assert.IsType<ObjectCreationExpression>(
                Assert.IsType<ReturnStatement>(Assert.Single(item.Body!)).Expression))
            .ToArray();
        Assert.Equal(new[] { "First", "Second" }, creations[0].Constructor!.ParameterTypes
            .Select(type => Assert.IsType<NamedType>(type).Name));
        Assert.Equal(new[] { "Second", "First" }, creations[1].Constructor!.ParameterTypes
            .Select(type => Assert.IsType<NamedType>(type).Name));
        Assert.Equal(2, project.GenericTypeInstances.Select(instance =>
            instance.Type.ConstructedIdentity!.RuntimeHash).Distinct().Count());
    }

    [Fact]
    public void SubstitutesClosedGenericPropertyGetterType()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_property_getter");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public class First {}
            public class Second {}
            public class Box<T> {
                public T value;
                public constructor(T initial) { value = initial; }
                public T Value { get { return value; } }
            }
            First ReadFirst(Box<First> box) { return box.Value; }
            Second ReadSecond(Box<Second> box) { return box.Value; }
            """));
        new SemanticBinder().Bind(project);
        var reads = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<FunctionDeclaration>()
            .Where(item => item.Name is "ReadFirst" or "ReadSecond")
            .Select(item => Assert.IsType<MemberAccessExpression>(
                Assert.IsType<ReturnStatement>(Assert.Single(item.Body!)).Expression))
            .ToArray();
        Assert.Equal("First", Assert.IsType<NamedType>(reads[0].TargetProperty!.Type).Name);
        Assert.Equal("Second", Assert.IsType<NamedType>(reads[1].TargetProperty!.Type).Name);
    }

    [Fact]
    public void GeneratedCUsesPointerReturnForClosedGenericPropertyGetter()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_property_getter");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public class First {}
            public class Second {}
            public class Box<T> {
                public T value;
                public constructor(T initial) { value = initial; }
                public T Value { get { return value; } }
            }
            First ReadFirst(Box<First> box) { return box.Value; }
            Second ReadSecond(Box<Second> box) { return box.Value; }
            """));
        new SemanticBinder().Bind(project);
        var directory = Path.Combine(Path.GetTempPath(), $"cxc-generic-property-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var header = File.ReadAllText(Path.Combine(directory, "generic_property_getter.h"));
            var source = File.ReadAllText(Path.Combine(directory, "generic_property_getter.c"));
            Assert.Contains("void* CX_ID_4(generic_property_getter, Box, Value, __get)", header);
            Assert.Contains("void* CX_ID_4(generic_property_getter, Box, Value, __get)", source);
            Assert.Contains("return __this->value;", source);
            foreach (var instance in project.GenericTypeInstances)
            {
                var identity = instance.Type.ConstructedIdentity!;
                Assert.Contains($"CX_ID_2({identity.CIdentifier}, __reflection_functions)", source);
            }
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void GenericPropertyAccessorsSupportThisQualifiedFieldAccess()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_property_this");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public class Item { }
            public class Box<T> {
                public T value;
                public T Value {
                    get { return this.value; }
                    set { this.value = value; }
                }
            }
            Item Read(Box<Item> box) { return box.Value; }
            void Write(Box<Item> box, Item value) { box.Value = value; }
            """));
        new SemanticBinder().Bind(project);
        var directory = Path.Combine(Path.GetTempPath(), $"cxc-generic-property-this-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(Path.Combine(directory, "generic_property_this.c"));
            Assert.Contains("return (__this)->value;", source);
            Assert.Contains("(__this)->value = value;", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void GenericPropertyGetterRejectsUnsupportedBody()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_property_body");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public class Box<T> {
                public T value;
                public T Value { get { return null; } }
            }
            """));
        var error = Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
        Assert.Contains("Generic property accessor bodies", error.Message);
    }

    [Fact]
    public void ClosedGenericClassSupportsGenericResultConditionalPropertyGetter()
    {
        var project = CxProject.CreateDefaultApplicationProject(
            "closed_generic_class_generic_property_expression");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public class OptionalValue<T> {
                public bool HasValue;
                public bool Ready;
                public T Payload;
                public T Fallback;
                public T Value { const get { return HasValue ? Payload : default; } }
                public T ValueOrFallback { const get { return HasValue ? Payload : Fallback; } }
                public T ConditionalCopy {
                    const get { T copy = HasValue && Ready ? Payload : default; return copy; }
                }
                public T FieldSelectionCopy {
                    const get { T copy = HasValue ? Payload : Fallback; return copy; }
                }
                public T Copy { const get { T copy = Payload; return copy; } }
                public T InferredCopy { const get { var copy = Payload; return copy; } }
                public T ThisCopy { const get { T copy = this.Payload; return copy; } }
            }
            public int Run() {
                OptionalValue<int> present = new OptionalValue<int>();
                OptionalValue<int> absent = new OptionalValue<int>();
                return present.Value + absent.Value + present.ValueOrFallback +
                    absent.ValueOrFallback + present.Copy + present.InferredCopy +
                    present.ThisCopy + present.ConditionalCopy +
                    present.FieldSelectionCopy + absent.FieldSelectionCopy;
            }
            """));

        new SemanticBinder().Bind(project);

        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-closed-generic-class-generic-property-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(
                Path.Combine(directory, "closed_generic_class_generic_property_expression.c"));

            Assert.Contains("Payload", source);
            Assert.DoesNotContain("_unknowntype_", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void SubstitutesClosedGenericPropertySetterType()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_property_setter");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public class First {}
            public class Second {}
            public class Box<T> {
                public T stored;
                public constructor() {}
                public T Value {
                    get { return stored; }
                    set { stored = value; }
                }
            }
            void WriteFirst(Box<First> box, First value) { box.Value = value; }
            void WriteSecond(Box<Second> box, Second value) { box.Value = value; }
            """));
        new SemanticBinder().Bind(project);
        var writes = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<FunctionDeclaration>()
            .Where(item => item.Name is "WriteFirst" or "WriteSecond")
            .Select(item => Assert.IsType<AssignmentExpression>(
                Assert.IsType<ExpressionStatement>(Assert.Single(item.Body!)).Expression))
            .ToArray();
        Assert.Equal("First", Assert.IsType<NamedType>(writes[0].TargetProperty!.Type).Name);
        Assert.Equal("Second", Assert.IsType<NamedType>(writes[1].TargetProperty!.Type).Name);
    }

    [Fact]
    public void GeneratedCUsesPointerParameterForClosedGenericPropertySetter()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_property_setter");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public class First {}
            public class Second {}
            public class Box<T> {
                public T stored;
                public constructor() {}
                public T Value {
                    get { return stored; }
                    set { stored = value; }
                }
            }
            void WriteFirst(Box<First> box, First value) { box.Value = value; }
            void WriteSecond(Box<Second> box, Second value) { box.Value = value; }
            """));
        new SemanticBinder().Bind(project);
        var directory = Path.Combine(Path.GetTempPath(), $"cxc-generic-setter-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var header = File.ReadAllText(Path.Combine(directory, "generic_property_setter.h"));
            var source = File.ReadAllText(Path.Combine(directory, "generic_property_setter.c"));
            Assert.Contains("void CX_ID_4(generic_property_setter, Box, Value, __set)", header);
            Assert.Contains("void* value", header);
            Assert.Contains("void* value", source);
            Assert.Contains("__this->stored = value;", source);
            foreach (var instance in project.GenericTypeInstances)
            {
                var identity = instance.Type.ConstructedIdentity!;
                Assert.Contains($"CX_ID_2({identity.CIdentifier}, __reflection_parameters_2)",
                    source);
            }
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void GenericPropertySetterRejectsDifferentReferenceArgument()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_property_setter_mismatch");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public class First {}
            public class Second {}
            public class Box<T> {
                public T stored;
                public constructor() {}
                public T Value {
                    get { return stored; }
                    set { stored = value; }
                }
            }
            void WriteWrong(Box<First> box, Second value) { box.Value = value; }
            """));
        var error = Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
        Assert.Contains("Cannot assign", error.Message);
    }

    [Fact]
    public void GeneratedCReflectsBothClosedPairArguments()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_pair");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public class First {}
            public class Second {}
            public class Pair<T, U> {
                public T first;
                public U second;
                public constructor(T left, U right) { first = left; second = right; }
            }
            Pair<First, Second> CreateForward(First left, Second right) {
                return new Pair<First, Second>(left, right);
            }
            Pair<Second, First> CreateReverse(Second left, First right) {
                return new Pair<Second, First>(left, right);
            }
            """));
        new SemanticBinder().Bind(project);
        var directory = Path.Combine(Path.GetTempPath(), $"cxc-generic-pair-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var header = File.ReadAllText(Path.Combine(directory, "generic_pair.h"));
            var source = File.ReadAllText(Path.Combine(directory, "generic_pair.c"));
            Assert.Contains("cx_ptr first;", header);
            Assert.Contains("cx_ptr second;", header);
            Assert.Contains("void* left", header);
            Assert.Contains("void* right", header);
            Assert.Contains("__this->first = left;", source);
            Assert.Contains("__this->second = right;", source);
            foreach (var instance in project.GenericTypeInstances)
            {
                var identity = instance.Type.ConstructedIdentity!;
                Assert.Contains($"CX_ID_2({identity.CIdentifier}, __reflection_fields)", source);
                Assert.Contains($"CX_ID_2({identity.CIdentifier}, __reflection_parameters_0)",
                    source);
                Assert.Contains($".Hash = 0x{identity.RuntimeHash:X}ULL", source);
            }
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void TwoParameterConstructorRejectsWrongSecondArgument()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_pair_mismatch");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public class First {}
            public class Second {}
            public class Pair<T, U> {
                public T first;
                public U second;
                public constructor(T left, U right) { first = left; second = right; }
            }
            Pair<First, Second> Create(First left) {
                return new Pair<First, Second>(left, left);
            }
            """));
        var error = Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
        Assert.Contains("No constructor", error.Message);
    }

    [Fact]
    public void SubstitutesGenericClassMethodParameterAtCallSite()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_class_method");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public class First {}
            public class Second {}
            public class Box<T> {
                public T value;
                public constructor() {}
                public void Set(T initial) { value = initial; }
            }
            void SetFirst(Box<First> box, First value) { box.Set(value); }
            void SetSecond(Box<Second> box, Second value) { box.Set(value); }
            """));
        new SemanticBinder().Bind(project);

        var calls = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<FunctionDeclaration>()
            .Where(item => item.Name is "SetFirst" or "SetSecond")
            .Select(item => Assert.IsType<InvocationExpression>(
                Assert.IsType<ExpressionStatement>(Assert.Single(item.Body!)).Expression))
            .ToArray();
        Assert.Equal("First", Assert.IsType<NamedType>(
            Assert.Single(calls[0].TargetSymbol!.ParameterTypes)).Name);
        Assert.Equal("Second", Assert.IsType<NamedType>(
            Assert.Single(calls[1].TargetSymbol!.ParameterTypes)).Name);
    }

    [Fact]
    public void GeneratedCUsesSharedGenericClassMethodWithClosedReflection()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_class_method");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public class First {}
            public class Second {}
            public class Box<T> {
                public T value;
                public constructor() {}
                public void Set(T initial) { value = initial; }
            }
            void SetFirst(Box<First> box, First value) { box.Set(value); }
            void SetSecond(Box<Second> box, Second value) { box.Set(value); }
            """));
        new SemanticBinder().Bind(project);
        var directory = Path.Combine(Path.GetTempPath(), $"cxc-generic-class-method-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(Path.Combine(directory, "generic_class_method.c"));
            var setters = project.GenericFunctionInstances
                .Where(instance => instance.Declaration?.Name == "Set")
                .ToArray();
            Assert.Equal(2, setters.Length);
            foreach (var setter in setters)
            {
                Assert.Contains(setter.SpecializationName!, source);
                var parameterType = Assert.IsType<NamedType>(setter.ParameterTypes[0]);
                Assert.Contains(
                    $"struct CX_ID_2(generic_class_method, {parameterType.Name})* initial",
                    source);
            }
            Assert.Contains("__this->value = initial;", source);
            foreach (var instance in project.GenericTypeInstances)
            {
                var identity = instance.Type.ConstructedIdentity!;
                var argument = Assert.IsType<NamedType>(Assert.Single(instance.Type.TypeArguments));
                Assert.Contains($"CX_ID_2({identity.CIdentifier}, __reflection_functions)", source);
                Assert.Contains($"&CX_ID_3(generic_class_method, {argument.Name}, __typeinfo)",
                    source);
            }
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void GenericClassMethodRejectsDifferentReferenceArgument()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_class_method_mismatch");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public class First {}
            public class Second {}
            public class Box<T> {
                public T value;
                public constructor() {}
                public void Set(T initial) { value = initial; }
            }
            void SetWrong(Box<First> box, Second value) { box.Set(value); }
            """));
        var error = Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
        Assert.Contains("No overload", error.Message);
    }

    [Fact]
    public void SubstitutesGenericClassGetterReturnAtCallSite()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_class_getter");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public class First {}
            public class Second {}
            public class Box<T> {
                public T value;
                public constructor() {}
                public T Get() { return value; }
            }
            First ReadFirst(Box<First> box) { return box.Get(); }
            Second ReadSecond(Box<Second> box) { return box.Get(); }
            """));
        new SemanticBinder().Bind(project);
        var calls = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<FunctionDeclaration>()
            .Where(item => item.Name is "ReadFirst" or "ReadSecond")
            .Select(item => Assert.IsType<InvocationExpression>(
                Assert.IsType<ReturnStatement>(Assert.Single(item.Body!)).Expression))
            .ToArray();
        Assert.Equal("First", Assert.IsType<NamedType>(calls[0].TargetSymbol!.ReturnType).Name);
        Assert.Equal("Second", Assert.IsType<NamedType>(calls[1].TargetSymbol!.ReturnType).Name);
    }

    [Fact]
    public void GeneratedCUsesPointerReturnForGenericClassGetter()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_class_getter");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public class First {}
            public class Second {}
            public class Box<T> {
                public T value;
                public constructor() {}
                public T Get() { return value; }
            }
            First ReadFirst(Box<First> box) { return box.Get(); }
            Second ReadSecond(Box<Second> box) { return box.Get(); }
            """));
        new SemanticBinder().Bind(project);
        var directory = Path.Combine(Path.GetTempPath(), $"cxc-generic-getter-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(Path.Combine(directory, "generic_class_getter.c"));
            var getters = project.GenericFunctionInstances
                .Where(instance => instance.Declaration?.Name == "Get")
                .ToArray();
            Assert.Equal(2, getters.Length);
            foreach (var getter in getters)
            {
                Assert.Contains(getter.SpecializationName!, source);
                var returnType = Assert.IsType<NamedType>(getter.ReturnType);
                Assert.Contains(
                    $"struct CX_ID_2(generic_class_getter, {returnType.Name})* {getter.SpecializationName}(",
                    source);
            }
            Assert.Contains("return __this->value;", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void EmitsClosedGenericClassArrayStorageMethods()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_class_array_methods");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public class Buffer<T> {
                private T[] _items;
                public constructor(uint length) { _items = new T[length]; }
                public uint Length { const get { return _items.Length; } }
                public T Get(uint index) const { return _items[index]; }
                public void Set(uint index, T value) { _items[index] = value; }
            }
            public int ReadWriteInt() {
                Buffer<int> values = new Buffer<int>(2u);
                values.Set(1u, 42);
                return values.Get(1u) + (values.Length > 0u ? 1 : 0);
            }
            public long ReadWriteLong() {
                Buffer<long> values = new Buffer<long>(3u);
                values.Set(2u, 91L);
                return values.Get(2u) + (values.Length > 0u ? 1L : 0L);
            }
            """));
        new SemanticBinder().Bind(project);

        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-generic-class-array-methods-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(
                Path.Combine(directory, "generic_class_array_methods.c"));

            Assert.Contains("cx_array_new", source);
            Assert.Contains("cx_array_at", source);
            Assert.DoesNotContain("_unknowntype_", source);
            Assert.DoesNotContain("struct void*", source);
            Assert.Contains("sizeof(cx_int)", source);
            Assert.Contains("sizeof(cx_long)", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void EmitsConstructedGenericClassReferenceFieldInClosedGenericOwner()
    {
        var project = CxProject.CreateDefaultApplicationProject(
            "generic_constructed_reference_field");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public class Storage<T> {
                private T[] _items;
                public constructor(uint length) { _items = new T[length]; }
                public uint Length { const get { return _items.Length; } }
                public T Get(uint index) const { return _items[index]; }
                public void Set(uint index, T value) { _items[index] = value; }
            }
            public class Holder<T> {
                public Storage<T> Value;
                public constructor(Storage<T> value) { Value = value; }
            }
            public int Create() {
                Storage<int> box = new Storage<int>(1u);
                Holder<int> holder = new Holder<int>(box);
                return holder.Value == box && holder.Value.Length == 1u ? 42 : 0;
            }
            """));
        new SemanticBinder().Bind(project);

        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-generic-constructed-reference-field-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(
                Path.Combine(directory, "generic_constructed_reference_field.c"));

            Assert.DoesNotContain("_unknowntype_", source);
            Assert.Contains("Value", source);
            Assert.Contains("box", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void BindsOpenConstructedObjectCreationUsingEnclosingGenericParameters()
    {
        var project = CxProject.CreateDefaultApplicationProject(
            "generic_open_constructed_object_creation");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public class Box<T> { }
            public class Holder<T> {
                public Box<T> Value;
                public constructor() { Value = new Box<T>(); }
            }
            public int Create() {
                Holder<int> holder = new Holder<int>();
                return holder.Value == null ? 0 : 42;
            }
            """));

        new SemanticBinder().Bind(project);

        var holder = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<ClassDeclaration>().Single(item => item.Name == "Holder");
        var constructor = holder.MemberDeclarations.Declarations
            .OfType<ConstructorDeclaration>().Single();
        var creation = Assert.IsType<ObjectCreationExpression>(
            Assert.IsType<AssignmentExpression>(
                Assert.IsType<ExpressionStatement>(Assert.Single(constructor.Body!)).Expression)
                .Value);
        var requestedType = Assert.IsType<NamedType>(creation.RequestedType);
        var argument = Assert.IsType<GenericType>(Assert.Single(requestedType.TypeArguments));
        Assert.Equal("T", argument.Name);
        Assert.NotNull(creation.Constructor);
        var caller = Assert.Single(project.GenericFunctionInstances, instance =>
            instance.Declaration == constructor);
        Assert.True(creation.TryGetClosedConstruction(
            caller.SpecializationName!, out var closedRequestedType, out _));
        Assert.IsType<IntType>(Assert.Single(closedRequestedType.TypeArguments));
    }

    [Fact]
    public void EmitsClosedOwnerConstructedGenericObjectCreation()
    {
        var project = CxProject.CreateDefaultApplicationProject(
            "generic_owner_constructed_object_creation");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public class Box<T> { }
            public class Holder<T> {
                public Box<T> Value;
                public constructor() { Value = new Box<T>(); }
            }
            public int Create() {
                Holder<int> holder = new Holder<int>();
                return holder.Value == null ? 0 : 42;
            }
            """));
        new SemanticBinder().Bind(project);
        var holder = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<ClassDeclaration>().Single(item => item.Name == "Holder");
        var constructor = holder.MemberDeclarations.Declarations
            .OfType<ConstructorDeclaration>().Single();
        var creation = Assert.IsType<ObjectCreationExpression>(
            Assert.IsType<AssignmentExpression>(
                Assert.IsType<ExpressionStatement>(Assert.Single(constructor.Body!)).Expression)
                .Value);
        var caller = Assert.Single(project.GenericFunctionInstances, instance =>
            instance.Declaration == constructor);
        Assert.True(creation.TryGetClosedConstruction(
            caller.SpecializationName!, out var closedRequestedType,
            out var closedConstructor));
        Assert.Equal("Box", closedRequestedType.Name);
        Assert.IsType<IntType>(Assert.Single(closedRequestedType.TypeArguments));
        Assert.NotNull(closedConstructor.SpecializationName);

        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-generic-owner-object-creation-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(
                Path.Combine(directory, "generic_owner_constructed_object_creation.c"));

            Assert.DoesNotContain("_unknowntype_", source);
            Assert.Contains(closedConstructor.SpecializationName!, source);
            Assert.Contains("__this->Value = (struct", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void GeneratedCReflectsEachSubstitutedReferenceFieldType()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_reference_field");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public class First {}
            public class Second {}
            public class Box<T> {
                public T value;
                public constructor() {}
            }
            Box<First> CreateFirst() { return new Box<First>(); }
            Box<Second> CreateSecond() { return new Box<Second>(); }
            """));
        new SemanticBinder().Bind(project);
        var directory = Path.Combine(Path.GetTempPath(), $"cxc-generic-reference-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var header = File.ReadAllText(Path.Combine(directory, "generic_reference_field.h"));
            var source = File.ReadAllText(Path.Combine(directory, "generic_reference_field.c"));
            Assert.Contains("cx_ptr value;", header);
            foreach (var instance in project.GenericTypeInstances)
            {
                var identity = instance.Type.ConstructedIdentity!;
                var argument = Assert.IsType<NamedType>(Assert.Single(instance.Type.TypeArguments));
                Assert.Contains($"CX_ID_2({identity.CIdentifier}, __reflection_fields)", source);
                Assert.Contains($"&CX_ID_3(generic_reference_field, {argument.Name}, __typeinfo)",
                    source);
            }
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void RepeatedClosedTypeAcrossContextsHasOneDefinition()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_multi_context");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public class First {}
            public class Marker<T> {}
            public class HolderA { public Marker<First> value; }
            """));
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public class HolderB { public Marker<First> value; }
            """));
        new SemanticBinder().Bind(project);
        var identity = Assert.Single(project.GenericTypeInstances).Type.ConstructedIdentity!;
        var directory = Path.Combine(Path.GetTempPath(), $"cxc-generic-multi-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var header = File.ReadAllText(Path.Combine(directory, "generic_multi_context.h"));
            var source = File.ReadAllText(Path.Combine(directory, "generic_multi_context.c"));
            Assert.Equal(1, header.Split($"CX_TYPEINFO_DECL({identity.CIdentifier})").Length - 1);
            Assert.Equal(1, source.Split($"CX_BEGIN_VTABLE_DEF({identity.CIdentifier})").Length - 1);
            // One definition is selected by each mutually exclusive link-mode branch.
            Assert.Equal(2, source.Split($"CX_TYPEINFO_NAME({identity.CIdentifier}) =").Length - 1);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}

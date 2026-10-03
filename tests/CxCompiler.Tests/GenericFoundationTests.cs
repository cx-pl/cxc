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
    public void ClosedValueTypeWithoutConstructorRejectsUnsupportedCodeGeneration()
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
            var error = Assert.Throws<CompilationErrorException>(() =>
                CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx")));
            Assert.Contains("direct generic-parameter and generic-array fields", error.Message);
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
        Assert.Empty(marker.VirtualMethodSlots);
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
            Assert.Contains("struct CX_ID_3(cxcore, System, Array)* values;", header);
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
            var header = File.ReadAllText(Path.Combine(directory, "generic_class_method.h"));
            var source = File.ReadAllText(Path.Combine(directory, "generic_class_method.c"));
            Assert.Contains("void* initial", header);
            Assert.Contains("void* initial", source);
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
            var header = File.ReadAllText(Path.Combine(directory, "generic_class_getter.h"));
            var source = File.ReadAllText(Path.Combine(directory, "generic_class_getter.c"));
            Assert.Contains("void* CX_ID_3(generic_class_getter, Box, Get)", header);
            Assert.Contains("void* CX_ID_3(generic_class_getter, Box, Get)", source);
            Assert.Contains("void* __cx_return_value;", source);
            Assert.Contains("return __this->value;", source);
            foreach (var instance in project.GenericTypeInstances)
            {
                var argument = Assert.IsType<NamedType>(Assert.Single(instance.Type.TypeArguments));
                Assert.Contains($"&CX_ID_3(generic_class_getter, {argument.Name}, __typeinfo)",
                    source);
            }
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

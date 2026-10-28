using CxCompiler.Model.Project;
using CxCompiler.Model.Errors;
using CxCompiler.Model.Statements;
using CxCompiler.Model.Types;
using CxCompiler.OutputGenerators;
using CxCompiler.Semantics;

namespace CxCompiler.Tests;

public sealed class GenericConstrainedMemberTests
{
    private const string Source = """
        public class BaseValue {
            public int value;
            public int other;
            public int Read(int offset) { return value + offset; }
            public int Current { const get => value; }
            public int Stored { get { return value; } set(value) { this.value = value; } }
            public int StoredOther { get { return other; } set(value) { other = value; } }
        }
        public interface IReadValue { int ReadInterface(int offset); }
        public interface IExtraValue { int ReadExtra(); }
        public interface IReadCurrent { int Current { const get; } }
        public interface IValueStore {
            int Value { get; set; }
            int Other { get; set; }
            int Item { const get(uint index); set(uint index); }
        }
        public class InterfaceValue : IReadValue, IExtraValue, IReadCurrent, IValueStore {
            private int stored;
            private int other;
            public int ReadInterface(int offset) { return offset + 1; }
            public int ReadExtra() { return 9; }
            public int Current { const get => 12; }
            public int Value {
                get { return stored; }
                set(value) { stored = value; }
            }
            public int Other {
                get { return other; }
                set(value) { other = value; }
            }
            public int Item {
                const get(uint index) { return stored + 1; }
                set(uint index, int value) { stored = value; }
            }
        }
        public class DerivedValue : BaseValue {}
        public int ReadConstrained<T>(T item, int offset) where T : BaseValue {
            return item.Read(offset + 1) + item.Read(offset + 2);
        }
        public int ReadField<T>(T item) where T : BaseValue { return item.value; }
        public int ReadProperty<T>(T item) where T : BaseValue { return item.Current; }
        public int ReadStored<T>(T item) where T : BaseValue { return item.Stored; }
        public int ReadStoredOther<T>(T item) where T : BaseValue { return item.StoredOther; }
        public void SetStored<T>(T item, int value) where T : BaseValue {
            item.Stored = value;
            item.StoredOther = value + 1;
        }
        public int ReadInterface<T>(T item, int offset) where T : IReadValue {
            return item.ReadInterface(offset + 1) + item.ReadInterface(offset + 2);
        }
        public int ReadExtra<T>(T item) where T : IReadValue, IExtraValue {
            return item.ReadExtra();
        }
        public int ReadInterfaceProperty<T>(T item) where T : IReadCurrent {
            return item.Current;
        }
        public int ReadCombinedInterfaceProperty<T>(T item) where T : IReadCurrent {
            return item.Current + item.Current;
        }
        public void SetInterfaceProperty<T>(T item, int value) where T : IValueStore {
            item.Value = value;
            item.Other = value > 0 ? value + 1 : 0;
        }
        public int ReadInterfaceIndexer<T>(T item, uint index) where T : IValueStore {
            return item.Item[index];
        }
        public int ReadCombinedInterfaceIndexer<T>(T item, uint index) where T : IValueStore {
            return item.Item[index] + item.Item[index + 1u];
        }
        public void SetInterfaceIndexer<T>(T item, uint index, int value) where T : IValueStore {
            item.Item[index] = value;
        }
        public int Run() {
            DerivedValue item = new DerivedValue();
            item.value = 149;
            SetStored(item, 149);
            InterfaceValue interfaceItem = new InterfaceValue();
            SetInterfaceProperty(interfaceItem, 20);
            SetInterfaceIndexer(interfaceItem, 2u, 30);
            return ReadConstrained(item, 2) + ReadField(item) + ReadProperty(item) +
                ReadStored(item) +
                ReadInterface(interfaceItem, 4) + ReadExtra(interfaceItem) +
                ReadInterfaceProperty(interfaceItem) +
                ReadCombinedInterfaceProperty(interfaceItem) +
                ReadInterfaceIndexer(interfaceItem, 2u) +
                ReadCombinedInterfaceIndexer(interfaceItem, 2u);
        }
        """;

    [Fact]
    public void BindsMembersExposedByAClassConstraint()
    {
        var project = CreateProject();
        var function = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<FunctionDeclaration>()
            .Single(declaration => declaration.Name == "ReadConstrained");
        var callExpression = Assert.IsType<CxCompiler.Model.Expressions.BinaryExpression>(
            Assert.IsType<ReturnStatement>(function.Body![0]).Expression);
        var invocation = Assert.IsType<CxCompiler.Model.Expressions.InvocationExpression>(
            callExpression.Left);
        Assert.IsType<CxCompiler.Model.Expressions.InvocationExpression>(callExpression.Right);

        Assert.Equal("Read", invocation.TargetSymbol!.Declaration!.Name);
        var instance = Assert.Single(project.GenericFunctionInstances,
            item => item.Declaration?.Name == "ReadConstrained");
        Assert.Equal("DerivedValue", instance.ParameterTypes[0].Name);
    }

    [Fact]
    public void BindsMembersExposedByAConstructedGenericClassConstraint()
    {
        var project = CxProject.CreateDefaultApplicationProject(
            "constructed_generic_class_constraint");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public class Box<T> {
                public int Value;
                public constructor(int value) { Value = value; }
                public int Read(bool useValue) const {
                    return useValue ? Value : Value + 1;
                }
            }
            public int ReadBox<U>(U box, bool useValue) where U : Box<int> {
                return box.Read(useValue);
            }
            public int Run() {
                Box<int> box = new Box<int>(41);
                return ReadBox(box, true);
            }
            """));

        new SemanticBinder().Bind(project);
        Assert.Contains(project.GenericFunctionInstances,
            instance => instance.Declaration?.Name == "ReadBox");

        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-generic-class-constraint-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(Path.Combine(directory,
                "constructed_generic_class_constraint.c"));

            Assert.Contains("Value + 1", source);
            Assert.DoesNotContain("_unknowntype_", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void EmitsAConstrainedBaseCallForTheClosedType()
    {
        var project = CreateProject();
        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-generic-constrained-member-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(Path.Combine(directory, "generic_constrained_member.c"));
            Assert.Contains("(struct generic_constrained_member_BaseValue*)", source);
            Assert.Contains("generic_constrained_member_BaseValue_Read", source);
            Assert.Contains("generic_constrained_member_BaseValue_Current___const_get", source);
            Assert.Contains("generic_constrained_member_BaseValue_Stored___get", source);
            Assert.Contains("generic_constrained_member_BaseValue_Stored___set", source);
            Assert.Contains("cx_checked_cast_object_to_interface", source);
            Assert.Contains(".vtable", source);
            Assert.Contains("->value", source);
            Assert.DoesNotContain("_unknowntype_", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void BindsAndDispatchesMembersExposedByAnInterfaceConstraint()
    {
        var project = CreateProject();
        var function = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<FunctionDeclaration>()
            .Single(declaration => declaration.Name == "ReadInterface");
        var callExpression = Assert.IsType<CxCompiler.Model.Expressions.BinaryExpression>(
            Assert.IsType<ReturnStatement>(function.Body![0]).Expression);
        var invocation = Assert.IsType<CxCompiler.Model.Expressions.InvocationExpression>(
            callExpression.Left);
        Assert.IsType<CxCompiler.Model.Expressions.InvocationExpression>(callExpression.Right);

        Assert.Equal("ReadInterface", invocation.TargetSymbol!.Declaration!.Name);
        Assert.NotNull(invocation.DispatchSlotIndex);
        Assert.Single(project.GenericFunctionInstances,
            item => item.Declaration?.Name == "ReadInterface");

        var extraFunction = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<FunctionDeclaration>()
            .Single(declaration => declaration.Name == "ReadExtra");
        var extraInvocation = Assert.IsType<CxCompiler.Model.Expressions.InvocationExpression>(
            Assert.IsType<ReturnStatement>(extraFunction.Body![0]).Expression);
        Assert.Equal("ReadExtra", extraInvocation.TargetSymbol!.Declaration!.Name);
        Assert.NotNull(extraInvocation.DispatchSlotIndex);

        var propertyFunction = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<FunctionDeclaration>()
            .Single(declaration => declaration.Name == "ReadInterfaceProperty");
        var propertyReturn = Assert.IsType<ReturnStatement>(propertyFunction.Body![0]);
        var propertyAccess = Assert.IsType<CxCompiler.Model.Expressions.MemberAccessExpression>(
            propertyReturn.Expression);
        Assert.Equal("Current", propertyAccess.TargetProperty!.FullName.Parts[^1]);
        Assert.NotNull(propertyAccess.InterfaceDispatchSlotIndex);

        var combinedPropertyFunction = project.CompilationContexts.Single()
            .DeclarationScope.Declarations.OfType<FunctionDeclaration>()
            .Single(declaration => declaration.Name == "ReadCombinedInterfaceProperty");
        var combinedPropertyReturn = Assert.IsType<ReturnStatement>(
            combinedPropertyFunction.Body![0]);
        var combinedProperty = Assert.IsType<CxCompiler.Model.Expressions.BinaryExpression>(
            combinedPropertyReturn.Expression);
        Assert.NotNull(Assert.IsType<CxCompiler.Model.Expressions.MemberAccessExpression>(
            combinedProperty.Left).InterfaceDispatchSlotIndex);
        Assert.NotNull(Assert.IsType<CxCompiler.Model.Expressions.MemberAccessExpression>(
            combinedProperty.Right).InterfaceDispatchSlotIndex);

        var indexerFunction = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<FunctionDeclaration>()
            .Single(declaration => declaration.Name == "ReadInterfaceIndexer");
        var indexerReturn = Assert.IsType<ReturnStatement>(indexerFunction.Body![0]);
        var indexerAccess = Assert.IsType<CxCompiler.Model.Expressions.ArrayAccessExpression>(
            indexerReturn.Expression);
        Assert.NotNull(indexerAccess.InterfaceDispatchSlotIndex);

        var combinedIndexerFunction = project.CompilationContexts.Single()
            .DeclarationScope.Declarations.OfType<FunctionDeclaration>()
            .Single(declaration => declaration.Name == "ReadCombinedInterfaceIndexer");
        var combinedIndexerReturn = Assert.IsType<ReturnStatement>(
            combinedIndexerFunction.Body![0]);
        var combinedIndexer = Assert.IsType<CxCompiler.Model.Expressions.BinaryExpression>(
            combinedIndexerReturn.Expression);
        Assert.NotNull(Assert.IsType<CxCompiler.Model.Expressions.ArrayAccessExpression>(
            combinedIndexer.Left).InterfaceDispatchSlotIndex);
        Assert.NotNull(Assert.IsType<CxCompiler.Model.Expressions.ArrayAccessExpression>(
            combinedIndexer.Right).InterfaceDispatchSlotIndex);

        var setterFunction = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<FunctionDeclaration>()
            .Single(declaration => declaration.Name == "SetInterfaceProperty");
        var setter = Assert.IsType<CxCompiler.Model.Expressions.AssignmentExpression>(
            Assert.IsType<CxCompiler.Model.Statements.ExpressionStatement>(
                setterFunction.Body![0]).Expression);
        Assert.NotNull(setter.InterfaceDispatchSlotIndex);

        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-generic-interface-composed-read-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(Path.Combine(
                directory, "generic_constrained_member.c"));
            Assert.Contains("cx_iface", source);
            Assert.DoesNotContain("_unknowntype_", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void SupportsValueTypeArgumentsForInterfaceConstrainedMemberDispatch()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_interface_struct");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public interface IReadValue { int Read(int offset); }
            public interface IStore { int Value { get; set; } }
            public interface IIndexed { int Item { const get(uint index); set(uint index); } }
            public struct Value : IReadValue, IStore, IIndexed {
                public int stored;
                public int Read(int offset) { return stored + offset; }
                public int Value {
                    get { return stored; }
                    set(value) { stored = value; }
                }
                public int Item {
                    const get(uint index) { return stored; }
                    set(uint index, int value) { stored = value; }
                }
            }
            public int ReadConstrained<T>(T item, int offset) where T : IReadValue {
                return item.Read(offset + 1);
            }
            public void SetConstrained<T>(T item, int value) where T : IStore {
                item.Value = value;
            }
            public int ReadIndexer<T>(T item, uint index) where T : IIndexed {
                return item.Item[index];
            }
            public void SetIndexer<T>(T item, uint index, int value) where T : IIndexed {
                item.Item[index] = value;
            }
            public int Run() {
                Value item;
                item.stored = 20;
                SetConstrained(item, 7);
                SetIndexer(item, 0u, 8);
                return ReadConstrained(item, 2) + ReadIndexer(item, 0u) - 8;
            }
            """));

        new SemanticBinder().Bind(project);
        Assert.Contains(project.GenericFunctionInstances,
            item => item.Declaration?.Name == "ReadConstrained" &&
                item.ParameterTypes[0] is NamedType { ClassType: ClassType.Struct });
        Assert.Contains(project.GenericFunctionInstances,
            item => item.Declaration?.Name == "SetConstrained" &&
                item.ParameterTypes[0] is NamedType { ClassType: ClassType.Struct });
        Assert.Contains(project.GenericFunctionInstances,
            item => item.Declaration?.Name == "ReadIndexer" &&
                item.ParameterTypes[0] is NamedType { ClassType: ClassType.Struct });
        Assert.Contains(project.GenericFunctionInstances,
            item => item.Declaration?.Name == "SetIndexer" &&
                item.ParameterTypes[0] is NamedType { ClassType: ClassType.Struct });
        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-generic-interface-struct-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(Path.Combine(directory, "generic_interface_struct.c"));
            Assert.Contains("__iface", source);
            Assert.Contains("(cx_ptr)&(item)", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void CallsVoidMethodsOnClassAndInterfaceConstrainedParameters()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_constrained_void_calls");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public interface ICounter {
                void Add(int amount);
                void Set(int value);
            }
            public class Counter : ICounter {
                public int Value;
                public void Add(int amount) { Value += amount; }
                public void Set(int value) { Value = value; }
            }
            public class BaseCounter {
                public int Value;
                public void Add(int amount) { Value += amount; }
            }
            public void ApplyInterface<T>(T item, int amount) where T : ICounter {
                item.Add(amount + 1);
                item.Set(amount > 0 ? amount : 0);
                if (amount > 0) {
                    item.Add(amount + 2);
                } else {
                    item.Set(0);
                }
            }
            public void ApplyClass<T>(T item, int amount) where T : BaseCounter {
                item.Add(amount + 1);
            }
            public void ApplyBoth<T, U>(T item, U other, int amount)
                where T : ICounter
                where U : BaseCounter {
                item.Add(amount + 1);
                other.Add(amount);
            }
            public void Run(Counter item) {
                ApplyInterface(item, 4);
                BaseCounter other = new BaseCounter();
                ApplyClass(other, 2);
                ApplyBoth(item, other, 3);
            }
            """));

        new SemanticBinder().Bind(project);

        var interfaceFunction = project.CompilationContexts.Single()
            .DeclarationScope.Declarations.OfType<FunctionDeclaration>()
            .Single(function => function.Name == "ApplyInterface");
        var conditional = Assert.IsType<CxCompiler.Model.Statements.IfStatement>(
            interfaceFunction.Body![2]);
        var thenBlock = Assert.IsType<CxCompiler.Model.Statements.BlockStatement>(
            conditional.ThenStatement);
        var elseBlock = Assert.IsType<CxCompiler.Model.Statements.BlockStatement>(
            conditional.ElseStatement);
        var interfaceCalls = new[]
        {
            Assert.IsType<CxCompiler.Model.Expressions.InvocationExpression>(
                Assert.IsType<CxCompiler.Model.Statements.ExpressionStatement>(
                    Assert.Single(thenBlock.Statements)).Expression),
            Assert.IsType<CxCompiler.Model.Expressions.InvocationExpression>(
                Assert.IsType<CxCompiler.Model.Statements.ExpressionStatement>(
                    Assert.Single(elseBlock.Statements)).Expression),
        };
        Assert.All(interfaceCalls, invocation => Assert.NotNull(invocation.DispatchSlotIndex));

        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-generic-constrained-void-calls-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var generated = File.ReadAllText(
                Path.Combine(directory, "generic_constrained_void_calls.c"));
            Assert.Contains("struct cx_iface_ref", generated, StringComparison.Ordinal);
            Assert.Contains("Add", generated, StringComparison.Ordinal);
            Assert.Contains("Set", generated, StringComparison.Ordinal);
            Assert.Contains("BaseCounter_Add", generated, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void RejectsStatementFormCallsToNonVoidConstrainedMethods()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_constrained_void_calls");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public interface IReadable { int Read(); }
            public void Ignore<T>(T item) where T : IReadable { item.Read(); }
            """));

        var error = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(project));

        Assert.Contains("Generic function 'Ignore' supports only statement-form calls",
            error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void CombinesResultsFromMultipleConstrainedParametersDirectlyAndThroughLocal()
    {
        var project = CxProject.CreateDefaultApplicationProject(
            "multiple_constrained_member_results");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public interface IValueReader { int Read(); }
            public class ClassReader : IValueReader {
                public int Value;
                public int Read() { return Value; }
            }
            public struct StructReader : IValueReader {
                public int Value;
                public int Read() { return Value; }
            }
            public int Sum<TLeft, TRight>(TLeft left, TRight right)
                where TLeft : IValueReader
                where TRight : IValueReader {
                return left.Read() + right.Read();
            }
            public int SumLocal<TLeft, TRight>(TLeft left, TRight right)
                where TLeft : IValueReader
                where TRight : IValueReader {
                int result = left.Read() + right.Read();
                return result;
            }
            public int Run(ClassReader left, StructReader right) {
                return Sum(left, right) + SumLocal(left, right);
            }
            """));

        new SemanticBinder().Bind(project);
        Assert.Contains(project.GenericFunctionInstances,
            instance => instance.Declaration?.Name == "Sum" &&
                instance.ParameterTypes[0] is NamedType { Name: "ClassReader" } &&
                instance.ParameterTypes[1] is NamedType { Name: "StructReader" });
        Assert.Contains(project.GenericFunctionInstances,
            instance => instance.Declaration?.Name == "SumLocal");

        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-multiple-constrained-results-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var generated = File.ReadAllText(
                Path.Combine(directory, "multiple_constrained_member_results.c"));
            Assert.Contains("Read", generated, StringComparison.Ordinal);
            Assert.Contains("struct cx_iface_ref", generated, StringComparison.Ordinal);
            Assert.Contains("result =", generated, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void SupportsClosedGenericStructArgumentsForInterfaceConstrainedDispatch()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_interface_generic_struct");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public interface IReadCell { int Read(); }
            public interface ICountCell { int Count { const get; } }
            public interface IStoreCell { int Stored { get; set; } }
            public struct Cell<T> : IReadCell, ICountCell, IStoreCell {
                public T value;
                public int storage;
                public constructor() {}
                public int Read() { return 42; }
                public int Count { const get => 24; }
                public int Stored {
                    get { return storage; }
                    set(value) { storage = value; }
                }
            }
            public int ReadConstrained<T>(T item) where T : IReadCell {
                return item.Read();
            }
            public int ReadCount<T>(T item) where T : ICountCell {
                return item.Count;
            }
            public void SetStored<T>(T item, int value) where T : IStoreCell {
                item.Stored = value;
            }
            public int ReadStored<T>(T item) where T : IStoreCell {
                return item.Stored;
            }
            public int Run() {
                Cell<int> item;
                item.value = 7;
                item.storage = 0;
                SetStored(item, 31);
                return ReadConstrained(item) + ReadCount(item) + ReadStored(item);
            }
            """));

        new SemanticBinder().Bind(project);
        Assert.Contains(project.GenericFunctionInstances,
            instance => instance.Declaration?.Name == "ReadConstrained" &&
                instance.ParameterTypes[0] is NamedType
                {
                    ClassType: ClassType.Struct,
                    TypeArguments.Count: 1,
                });
        Assert.Contains(project.GenericFunctionInstances,
            instance => instance.Declaration?.Name == "SetStored" &&
                instance.ParameterTypes[0] is NamedType
                {
                    ClassType: ClassType.Struct,
                    TypeArguments.Count: 1,
                });
        var closedStruct = Assert.Single(project.GenericTypeInstances,
            instance => instance.Declaration.Name == "Cell").Type;
        var closedStructIdentifier = closedStruct.ConstructedIdentity!.CIdentifier;
        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-generic-interface-generic-struct-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(Path.Combine(directory, "generic_interface_generic_struct.c"));
            Assert.Contains("__iface", source);
            Assert.Contains($"{closedStructIdentifier}___iface", source);
            Assert.Contains($".data = &CX_TYPEINFO_NAME({closedStructIdentifier})", source);
            Assert.Contains($"{closedStructIdentifier}___interfaces", source);
            Assert.Contains(".interfaceCount = 3", source);
            Assert.Contains("ICountCell", source);
            Assert.Contains("IStoreCell", source);
            Assert.Contains("Cell_Stored___set", source);
            Assert.DoesNotContain("_unknowntype_", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ClosedGenericInterfaceSlotCanSetATypeParameterField()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_interface_field_setter");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public interface ICell<T> {
                T Read();
                void Write(T value);
                T Current { get; set; }
                int ReadConstant(int ignored);
                int ReadCount();
                void WriteCount(int value);
            }
            public struct Cell<T> : ICell<T> {
                public T Value;
                public int Count;
                public constructor(T value) { Value = value; }
                public T Read() { return Value; }
                public void Write(T value) { Value = value; }
                public T Current { get { return Value; } set(value) { Value = value; } }
                public int ReadConstant(int ignored) { return 76; }
                public int ReadCount() { return Count; }
                public void WriteCount(int value) { Count = value; }
            }
            public int Run() {
                Cell<int> cell = new Cell<int>(1);
                object box = cell;
                ICell<int> boxed = (ICell<int>)box;
                boxed.Write(73);
                boxed.WriteCount(88);
                if (boxed.ReadCount() != 88) return -1;
                return boxed.Read() + boxed.ReadConstant(1);
            }
            """));

        new SemanticBinder().Bind(project);
        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-generic-interface-field-setter-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(Path.Combine(directory, "generic_interface_field_setter.c"));
            Assert.Contains("__iface_thunk", source);
            Assert.Contains("__root)->Value = value;", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static CxProject CreateProject()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_constrained_member");
        project.AddCompilationContext(CompilerTestHelper.Parse(Source));
        new SemanticBinder().Bind(project);
        return project;
    }
}

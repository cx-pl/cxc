using CxCompiler.Model.Errors;
using CxCompiler.Model.Expressions;
using CxCompiler.Model.Project;
using CxCompiler.Model.Statements;
using CxCompiler.Model.Types;
using CxCompiler.Model.Types.BuiltInTypes;
using CxCompiler.OutputGenerators;
using CxCompiler.Semantics;

namespace CxCompiler.Tests;

public sealed class PropertyExpressionTests
{
    [Fact]
    public void BindsImplicitPropertyGetterAndSetter()
    {
        var project = CreateProject("""
            public class Counter {
                public int Value { extern get; extern set; }

                public int Update() {
                    Value = 3;
                    return Value;
                }
            }
            """);

        new SemanticBinder().Bind(project);

        var function = GetFunction(project, "Update");
        var assignment = Assert.IsType<AssignmentExpression>(
            Assert.IsType<ExpressionStatement>(function.Body![0]).Expression);
        Assert.NotNull(assignment.PropertySetter);
        Assert.Equal("__cx_property_value_0", assignment.TemporaryName);

        var getter = Assert.IsType<IdentifierExpression>(
            Assert.IsType<ReturnStatement>(function.Body[1]).Expression);
        Assert.NotNull(getter.PropertyGetter);
    }

    [Fact]
    public void SelectsConstGetterForConstReceiver()
    {
        var project = CreateProject("""
            public struct Value {
                public int Number { extern get; extern const get; }

                public int Read() const {
                    return this.Number;
                }
            }
            """);

        new SemanticBinder().Bind(project);

        var function = GetFunction(project, "Read");
        var access = Assert.IsType<MemberAccessExpression>(
            Assert.IsType<ReturnStatement>(Assert.Single(function.Body!)).Expression);
        Assert.True(access.PropertyGetter!.Const);
    }

    [Fact]
    public void BindsStaticPropertyAccess()
    {
        var project = CreateProject("""
            public static class Settings {
                public static int Current { extern get; extern set; }

                public static int Update() {
                    Settings.Current = 4;
                    return Settings.Current;
                }
            }
            """);

        new SemanticBinder().Bind(project);

        var function = GetFunction(project, "Update");
        var assignment = Assert.IsType<AssignmentExpression>(
            Assert.IsType<ExpressionStatement>(function.Body![0]).Expression);
        Assert.True(assignment.TargetProperty!.IsStatic);
    }

    [Fact]
    public void SimpleIndexedPropertyAssignmentEvaluatesReceiverIndexAndValueOnceInOrder()
    {
        var project = CreateProject("""
            public class Values {
                public uint Trace;
                public int Stored;
                public Values GetValues() {
                    Trace = Trace * 10u + 1u;
                    return this;
                }
                public uint NextIndex() {
                    Trace = Trace * 10u + 2u;
                    return 0u;
                }
                public int NextValue() {
                    Trace = Trace * 10u + 3u;
                    return 99;
                }
                public int Item {
                    get(uint index) { return Stored; }
                    set(uint index, int value) { Stored = value; }
                }
                public int Assign() {
                    return GetValues().Item[NextIndex()] = NextValue();
                }
            }
            """);

        new SemanticBinder().Bind(project);

        var function = GetFunction(project, "Assign");
        var assignment = Assert.IsType<AssignmentExpression>(
            Assert.IsType<ReturnStatement>(Assert.Single(function.Body!)).Expression);
        Assert.NotNull(assignment.PropertyReceiverTemporaryName);
        Assert.Single(assignment.PropertyIndexTemporaryNames);

        var source = GenerateSource(project);
        var receiverSetup = source.IndexOf("__cx_property_receiver_0 = unnamed_", StringComparison.Ordinal);
        var indexSetup = source.IndexOf("__cx_property_index_0_0 = unnamed_", StringComparison.Ordinal);
        var valueSetup = source.IndexOf("__cx_property_value_0 = unnamed_", StringComparison.Ordinal);
        var setterCall = source.IndexOf("Item___set(__cx_property_receiver_", StringComparison.Ordinal);
        Assert.True(receiverSetup >= 0 && receiverSetup < indexSetup, source);
        Assert.True(indexSetup < valueSetup && valueSetup < setterCall, source);
        Assert.Contains("Item___set(__cx_property_receiver_0, __cx_property_index_0_0, __cx_property_value_0)", source);
    }

    [Fact]
    public void SimpleStructPropertyAssignmentWritesThroughTheOriginalStorage()
    {
        var project = CreateProject("""
            public struct Value {
                public int Stored;
                public int Current {
                    get { return Stored; }
                    set { Stored = value; }
                }
            }
            public static class Operations {
                public static int Write(Value item) {
                    item.Current = 27;
                    return item.Stored;
                }
            }
            """);

        new SemanticBinder().Bind(project);

        var source = GenerateSource(project);
        Assert.Contains("__cx_property_receiver_0 = &(item)", source);
        Assert.Contains("Current___set(&(*__cx_property_receiver_0), __cx_property_value_0)", source);
    }

    [Fact]
    public void CompoundStructPropertyAssignmentRetainsTheOriginalReceiver()
    {
        var project = CreateProject("""
            public struct Value {
                public int Stored;
                public int Current {
                    get { return Stored; }
                    set { Stored = value; }
                }
            }
            public static class Operations {
                public static int Add(Value item) {
                    item.Current += 5;
                    return item.Stored;
                }
            }
            """);

        new SemanticBinder().Bind(project);

        var source = GenerateSource(project);
        Assert.Contains("__cx_property_receiver_0 = &(item)", source);
        Assert.Contains("Current___get(&(*__cx_property_receiver_0))", source);
        Assert.Contains("Current___set(&(*__cx_property_receiver_0), __cx_property_value_0)", source);
    }

    [Fact]
    public void CompoundAssignmentReadsUpdatesAndWritesAProperty()
    {
        var project = CreateProject("""
            public class Counter {
                public int Value { extern get; extern set; }

                public int Add(Counter other, int amount) {
                    other.Value += amount;
                    return other.Value;
                }
            }
            """);

        new SemanticBinder().Bind(project);

        var function = GetFunction(project, "Add");
        var assignment = Assert.IsType<AssignmentExpression>(
            Assert.IsType<ExpressionStatement>(function.Body![0]).Expression);
        Assert.NotNull(assignment.PropertyGetter);
        Assert.NotNull(assignment.PropertySetter);
        Assert.Equal("+=", assignment.Operator);

        var source = GenerateSource(project);
        var getter = source.IndexOf(
            "unnamed_Counter_Value___get(__cx_property_receiver_0)",
            StringComparison.Ordinal);
        var update = source.IndexOf("__cx_property_value_0 += amount", StringComparison.Ordinal);
        var setter = source.IndexOf(
            "unnamed_Counter_Value___set(__cx_property_receiver_0, __cx_property_value_0)",
            StringComparison.Ordinal);
        Assert.True(getter >= 0 && getter < update);
        Assert.True(update < setter, source);
    }

    [Fact]
    public void CompoundPropertyAssignmentEvaluatesItsReceiverOnce()
    {
        var project = CreateProject("""
            public class Counter {
                public int Value { extern get; extern set; }
                public Counter GetCounter() { return this; }
                public int Add(int amount) {
                    GetCounter().Value += amount;
                    return 0;
                }
            }
            """);

        new SemanticBinder().Bind(project);

        var source = GenerateSource(project);
        var receiverSetup = Assert.Single(source.Split(Environment.NewLine),
            line => line.Contains("__cx_property_receiver_0 =", StringComparison.Ordinal));
        Assert.True(CountOccurrences(receiverSetup, "GetCounter") == 1, receiverSetup);
        Assert.Contains("Value___get(__cx_property_receiver_0)", source);
        Assert.Contains("Value___set(__cx_property_receiver_0, __cx_property_value_0)", source);
    }

    [Fact]
    public void CompoundDelegatePropertyAssignmentUsesInvocationListHelpers()
    {
        var project = CreateProject("""
            public delegate void Handler();
            public class Dispatcher {
                public Handler Callback { extern get; extern set; }
                public void Add(Handler handler) { Callback += handler; }
            }
            """);

        new SemanticBinder().Bind(project);

        var dispatcher = Assert.IsType<ClassDeclaration>(
            project.CompilationContexts.Single().DeclarationScope.Declarations
                .Single(declaration => declaration.Name == "Dispatcher"));
        var function = Assert.IsType<FunctionDeclaration>(dispatcher.MemberDeclarations
            .Declarations.Single(declaration => declaration.Name == "Add"));
        var assignment = Assert.IsType<AssignmentExpression>(
            Assert.IsType<ExpressionStatement>(Assert.Single(function.Body!)).Expression);
        Assert.IsType<VoidType>(assignment.InferredType);

        var source = GenerateSource(project);
        Assert.Contains("__cx_delegate_combine_CX_FN_", source);
        Assert.Contains("Callback___get(__this)", source);
        Assert.Contains("Callback___set(__this, __cx_property_value_0)", source);
    }

    [Fact]
    public void CompoundAssignmentDispatchesThroughInterfaceProperties()
    {
        var project = CreateProject("""
            public interface ICounter { int Value { get; set; } }
            public class Counter : ICounter {
                public int stored;
                public int Value { get { return stored; } set { stored = value; } }
            }
            public static class Operations {
                public static void Add(ICounter counter, int amount) {
                    counter.Value += amount;
                }
            }
            """);

        new SemanticBinder().Bind(project);

        var source = GenerateSource(project);
        Assert.Contains("struct cx_iface_ref __cx_property_iface_get_0;", source);
        Assert.Contains("struct cx_iface_ref __cx_property_iface_set_0;", source);
        Assert.Contains("__cx_property_value_0 += amount", source);
    }

    [Fact]
    public void CompoundPropertyAssignmentCallsMutatingUserOperator()
    {
        var project = CreateProject("""
            public struct Amount {
                public int Value;
                public void operator +=(int amount) { Value += amount; }
            }
            public class Holder {
                public Amount Stored;
                public Amount Current { extern get; extern set; }
                public void Add(int amount) { Current += amount; }
            }
            """);

        new SemanticBinder().Bind(project);

        var holder = Assert.IsType<ClassDeclaration>(project.CompilationContexts.Single()
            .DeclarationScope.Declarations.Single(declaration => declaration.Name == "Holder"));
        var function = Assert.IsType<FunctionDeclaration>(holder.MemberDeclarations
            .Declarations.Single(declaration => declaration.Name == "Add"));
        var assignment = Assert.IsType<AssignmentExpression>(
            Assert.IsType<ExpressionStatement>(Assert.Single(function.Body!)).Expression);
        Assert.IsType<VoidType>(assignment.OperatorSymbol!.ReturnType);

        var source = GenerateSource(project);
        Assert.Contains("__cx_property_value_0 = unnamed_", source);
        Assert.Contains("(&(__cx_property_value_0), amount)", source);
        Assert.Contains("Current___set(__this, __cx_property_value_0)", source);
    }

    [Fact]
    public void CompoundIndexedPropertyAssignmentEvaluatesIndexesOnceAndReusesThem()
    {
        var project = CreateProject("""
            public class Values {
                public int Stored;
                public uint IndexEvaluations;
                public uint NextIndex() { IndexEvaluations++; return IndexEvaluations; }
                public int Item {
                    get(uint index) { return Stored; }
                    set(uint index, int value) { Stored = value; }
                }
                public int Add(int amount) {
                    Item[NextIndex()] += amount;
                    return Stored;
                }
            }
            """);

        new SemanticBinder().Bind(project);

        var values = Assert.IsType<ClassDeclaration>(project.CompilationContexts.Single()
            .DeclarationScope.Declarations.Single(declaration => declaration.Name == "Values"));
        var function = Assert.IsType<FunctionDeclaration>(values.MemberDeclarations
            .Declarations.Single(declaration => declaration.Name == "Add"));
        var assignment = Assert.IsType<AssignmentExpression>(
            Assert.IsType<ExpressionStatement>(function.Body![0]).Expression);
        Assert.NotNull(assignment.PropertyGetter);
        Assert.NotNull(assignment.PropertySetter);
        Assert.Single(assignment.PropertyIndexTemporaryNames);

        var source = GenerateSource(project);
        var indexSetup = Assert.Single(source.Split(Environment.NewLine),
            line => line.Contains("__cx_property_index_0_0 =", StringComparison.Ordinal));
        Assert.Equal(1, CountOccurrences(indexSetup, "NextIndex"));
        Assert.Contains("Item___get(__this, __cx_property_index_0_0)", source);
        Assert.Contains("Item___set(__this, __cx_property_index_0_0, __cx_property_value_0)", source);
    }

    [Fact]
    public void PropertyIncrementPreservesPrefixAndPostfixResults()
    {
        var project = CreateProject("""
            public class Counter {
                public int Value { extern get; extern set; }
                public int Prefix() { return ++Value; }
                public int Postfix() { return Value++; }
            }
            """);

        new SemanticBinder().Bind(project);

        var source = GenerateSource(project);
        Assert.Contains("__cx_property_mutation_value_0 = unnamed_Counter_Value___get(__this)", source);
        Assert.Contains("__cx_property_mutation_value_0++, unnamed_Counter_Value___set(__this, __cx_property_mutation_value_0), __cx_property_mutation_value_0)", source);
        Assert.Contains("__cx_property_mutation_old_", source);
        Assert.Contains("= __cx_property_mutation_value_", source);
    }

    [Fact]
    public void IndexedPropertyMutationEvaluatesReceiverAndIndexOnce()
    {
        var project = CreateProject("""
            public class Values {
                public int IndexCalls;
                public int ReceiverCalls;
                public int Stored;
                public Values GetValues() { ReceiverCalls++; return this; }
                public uint NextIndex() { IndexCalls++; return 0u; }
                public int Item {
                    get(uint index) { return Stored; }
                    set(uint index, int value) { Stored = value; }
                }
                public int Increment() { return GetValues().Item[NextIndex()]++; }
            }
            """);

        new SemanticBinder().Bind(project);

        var source = GenerateSource(project);
        var receiverSetup = Assert.Single(source.Split(Environment.NewLine),
            line => line.Contains("__cx_property_mutation_receiver_0 =", StringComparison.Ordinal));
        var indexSetup = Assert.Single(source.Split(Environment.NewLine),
            line => line.Contains("__cx_property_mutation_index_0_0 =", StringComparison.Ordinal));
        Assert.Equal(1, CountOccurrences(receiverSetup, "GetValues"));
        Assert.Equal(1, CountOccurrences(indexSetup, "NextIndex"));
        Assert.Contains("Item___get(__cx_property_mutation_receiver_0, __cx_property_mutation_index_0_0)", source);
        Assert.Contains("Item___set(__cx_property_mutation_receiver_0, __cx_property_mutation_index_0_0, __cx_property_mutation_value_0)", source);
    }

    [Fact]
    public void PropertyMutationDispatchesThroughInterfaceAndMutatingOperator()
    {
        var project = CreateProject("""
            public interface ICounter { int Value { get; set; } }
            public struct Amount {
                public int Value;
                public void operator ++() { Value++; }
            }
            public class Holder {
                public Amount Stored;
                public Amount Current { get { return Stored; } set { Stored = value; } }
            }
            public static class Operations {
                public static int Increment(ICounter counter) { return counter.Value++; }
                public static void Increment(Holder holder) { ++holder.Current; }
            }
            """);

        new SemanticBinder().Bind(project);

        var source = GenerateSource(project);
        Assert.Contains("struct cx_iface_ref __cx_property_mutation_iface_get_", source);
        Assert.Contains("struct cx_iface_ref __cx_property_mutation_iface_set_", source);
        Assert.Contains("__cx_property_mutation_old_", source);
        Assert.Contains("__cx_operator_increment", source);
        Assert.Contains("(&(__cx_property_mutation_value_", source);
    }

    [Fact]
    public void BindsIndexedPropertyGetterAndSetter()
    {
        var project = CreateProject("""
            public class Values {
                public int Item {
                    extern const get(uint index);
                    extern set(uint index);
                }

                public int Read(uint index) const {
                    return this.Item[index];
                }

                public void Write(uint index) {
                    this.Item[index] = 9;
                }
            }
            """);

        new SemanticBinder().Bind(project);

        var read = GetFunction(project, "Read");
        var indexedGetter = Assert.IsType<ArrayAccessExpression>(
            Assert.IsType<ReturnStatement>(Assert.Single(read.Body!)).Expression);
        Assert.NotNull(indexedGetter.PropertyGetter);

        var write = GetFunction(project, "Write");
        var assignment = Assert.IsType<AssignmentExpression>(
            Assert.IsType<ExpressionStatement>(Assert.Single(write.Body!)).Expression);
        Assert.NotNull(assignment.PropertySetter);
    }

    [Fact]
    public void BindsCoreStringProperties()
    {
        var project = CreateProject("""
            uint Length(string text) {
                return text.Length;
            }

            char First(string text) {
                return text.Item[0u];
            }
            """);

        new SemanticBinder().Bind(project);

        var declarations = project.CompilationContexts.Single().DeclarationScope.Declarations;
        var length = Assert.IsType<FunctionDeclaration>(declarations[0]);
        var lengthAccess = Assert.IsType<MemberAccessExpression>(
            Assert.IsType<ReturnStatement>(Assert.Single(length.Body!)).Expression);
        Assert.Equal("System.String.Length", lengthAccess.TargetProperty!.FullName.ToString());

        var first = Assert.IsType<FunctionDeclaration>(declarations[1]);
        var itemAccess = Assert.IsType<ArrayAccessExpression>(
            Assert.IsType<ReturnStatement>(Assert.Single(first.Body!)).Expression);
        Assert.Equal("System.String.Item", itemAccess.TargetProperty!.FullName.ToString());
    }

    [Fact]
    public void RejectsWritingReadOnlyProperty()
    {
        var project = CreateProject("""
            public class Value {
                public int Number { extern get; }

                public void Change() {
                    Number = 2;
                }
            }
            """);

        var exception = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(project));

        Assert.Contains("not writable", exception.Message);
    }

    [Fact]
    public void EmitsAccessorCallsForAllPropertyForms()
    {
        var project = CreateProject("""
            public class Values {
                public static int Count { extern get; }
                public int Number { extern get; extern set; }
                public int Item { extern const get(uint index); }

                public int Read(Values other) {
                    Number = other.Number;
                    return this.Item[0u] + Values.Count;
                }
            }
            """);
        new SemanticBinder().Bind(project);
        var source = GenerateSource(project);

        Assert.Contains("cx_int __cx_property_value_0;", source);
        Assert.Contains("unnamed_Values_Number___get(other)", source);
        Assert.Contains("__cx_property_value_0 = unnamed_Values_Number___get(other)", source);
        Assert.Contains("unnamed_Values_Number___set(__this, __cx_property_value_0)", source);
        Assert.Contains("unnamed_Values_Item___const_get(__this, 0u)", source);
        Assert.Contains("unnamed_Values_Count___get()", source);
    }

    [Fact]
    public void EmitsPointerReceiverForExplicitThisOnStructProperty()
    {
        var project = CreateProject("""
            public struct Value {
                public int Number { extern const get; }

                public int Read() const {
                    return this.Number;
                }
            }
            """);
        new SemanticBinder().Bind(project);

        var source = GenerateSource(project);

        Assert.Contains("unnamed_Value_Number___const_get(__this)", source);
        Assert.DoesNotContain("unnamed_Value_Number___const_get(&(__this))", source);
    }

    private static CxProject CreateProject(string source)
    {
        var project = CxProject.CreateDefaultApplicationProject();
        project.AddCompilationContext(CompilerTestHelper.Parse(source));
        return project;
    }

    private static FunctionDeclaration GetFunction(CxProject project, string name)
    {
        var type = Assert.IsType<ClassDeclaration>(
            project.CompilationContexts.Single().DeclarationScope.Declarations.Single());
        return Assert.IsType<FunctionDeclaration>(
            type.MemberDeclarations.Declarations.Single(member => member.Name == name));
    }

    private static string GenerateSource(CxProject project)
    {
        var outputDirectory = Path.Combine(
            Path.GetTempPath(),
            $"cxc-tests-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(outputDirectory);
            CCodeOutputGenerator.GenerateOutput(
                project,
                Path.Combine(outputDirectory, "Properties.cx"));
            return File.ReadAllText(Path.Combine(outputDirectory, "unnamed.c"));
        }
        finally
        {
            if (Directory.Exists(outputDirectory))
            {
                Directory.Delete(outputDirectory, recursive: true);
            }
        }
    }

    private static int CountOccurrences(string value, string search)
    {
        var count = 0;
        var index = 0;
        while ((index = value.IndexOf(search, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += search.Length;
        }
        return count;
    }
}

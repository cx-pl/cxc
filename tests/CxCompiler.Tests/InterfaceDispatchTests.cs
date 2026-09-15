using CxCompiler.Model.Errors;
using CxCompiler.Model.Project;
using CxCompiler.Model.Types;
using CxCompiler.OutputGenerators;
using CxCompiler.Semantics;

namespace CxCompiler.Tests;

public sealed class InterfaceDispatchTests
{
    [Fact]
    public void BindsInterfaceContractsAndImplementations()
    {
        var project = CreateProject("""
            public interface IBase {
                int First();
            }
            public interface ICombined : IBase {
                int Second();
            }
            public class Service : ICombined {
                public int First() { return 1; }
                public int Second() { return 2; }
            }
            """);

        new SemanticBinder().Bind(project);

        var types = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<ClassDeclaration>()
            .ToDictionary(type => type.Name);
        Assert.Equal(["First", "Second"], types["ICombined"].VirtualMethodSlots
            .Select(slot => slot.Contract.Name));
        Assert.Equal(["IBase", "ICombined"], types["Service"].InterfaceDispatchTables
            .Select(table => table.Interface.Name)
            .Order());
        Assert.All(types["Service"].InterfaceDispatchTables, table =>
            Assert.All(table.Slots, slot => Assert.NotNull(slot.Implementation)));
    }

    [Fact]
    public void EmitsInterfaceReferencesTablesThunksAndCalls()
    {
        var project = CreateProject("""
            public interface ICalc {
                int Sum(int left, int right);
            }
            public class Calc : ICalc {
                public int Sum(int left, int right) { return left + right; }
                public constructor() {}
            }
            public static class Program {
                public static int Evaluate(ICalc calc) { return calc.Sum(3, 4); }
                public static bool IsMissing(ICalc calc) { return calc == null; }
                public static ICalc OrElse(ICalc first, ICalc second) { return first ?? second; }
                public static int EvaluateChoice(ICalc first, ICalc second) {
                    return OrElse(first, second).Sum(1, 2);
                }
                public static int Main() {
                    ICalc calc = new Calc();
                    return Evaluate(calc);
                }
            }
            """);
        new SemanticBinder().Bind(project);

        var (header, source) = GenerateOutput(project);

        Assert.Contains("CX_TYPE_DEF(CX_ID_2(Unnamed, ICalc)) {", header);
        Assert.Contains("CX_BEGIN_INTERFACE_VTABLE_DEF(", source);
        Assert.Contains("CX_ID_5(Unnamed, Calc, __iface, ICalc, __vtable)", source);
        Assert.Contains("CX_ID_6(Unnamed, Calc, __iface, ICalc, __vtable, slot_1)", source);
        Assert.Contains(".instance", source);
        Assert.Contains(".vtable", source);
        Assert.Contains(".instance == CX_NULL", source);
        Assert.Contains(".instance != CX_NULL", source);
        Assert.Contains("struct cx_iface_ref calc =", source);
        Assert.Contains("struct cx_iface_ref __cx_iface_receiver_", source);
    }

    [Fact]
    public void BindsAndEmitsInterfacePropertyAndIndexerDispatch()
    {
        var project = CreateProject("""
            public interface IValues {
                int Value { get; set; }
                int Item { const get(uint index); set(uint index); }
            }
            public class Values : IValues {
                private int _value;
                public int Value {
                    get { return _value; }
                    set(value) { _value = value; }
                }
                public int Item {
                    const get(uint index) { return 20; }
                    set(uint index, int value) { _value = value; }
                }
                public constructor() {}
            }
            public static class Program {
                public static int Read(IValues values) {
                    values.Value = 3;
                    values.Item[1u] = 4;
                    return values.Value + values.Item[2u];
                }
            }
            """);

        new SemanticBinder().Bind(project);

        var types = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<ClassDeclaration>()
            .ToDictionary(type => type.Name);
        Assert.Equal(
            [InterfaceDispatchSlotKind.PropertyGetter,
             InterfaceDispatchSlotKind.PropertySetter,
             InterfaceDispatchSlotKind.PropertyGetter,
             InterfaceDispatchSlotKind.PropertySetter],
            types["IValues"].InterfaceDispatchSlots.Select(slot => slot.Kind));
        Assert.All(types["Values"].InterfaceDispatchTables.Single().Slots, slot =>
            Assert.NotNull(slot.Implementation));

        var (_, source) = GenerateOutput(project);
        Assert.Contains("__iface, IValues, __vtable, slot_1", source);
        Assert.Contains("__iface, IValues, __vtable, slot_4", source);
        Assert.Contains("struct cx_iface_ref __cx_iface_receiver_", source);
        Assert.DoesNotContain("extern CX_EXPORT cx_int CX_ID_4(Unnamed, IValues, Value", source);
    }

    [Fact]
    public void EmitsSafeUpcastsAcrossMultipleInheritedInterfaces()
    {
        var project = CreateProject("""
            public interface ILeft { int Left(); }
            public interface IRight { int Right(); }
            public interface ICombined : ILeft, IRight { int Combined(); }
            public class Service : ICombined {
                public int Left() { return 1; }
                public int Right() { return 2; }
                public int Combined() { return 3; }
                public constructor() {}
            }
            public static class Program {
                public static int ReadLeft(ILeft value) { return value.Left(); }
                public static int ReadRight(IRight value) { return value.Right(); }
                public static ILeft Choose(bool first, ILeft left, ICombined combined) {
                    return first ? left : combined;
                }
                public static ILeft OrElse(ILeft left, ICombined combined) {
                    return left ?? combined;
                }
                public static int Run(ICombined value) {
                    return ReadLeft(value) + ReadRight(value);
                }
            }
            """);

        new SemanticBinder().Bind(project);

        var combined = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<ClassDeclaration>()
            .Single(type => type.Name == "ICombined");
        Assert.Equal(["ILeft", "IRight"], combined.InterfaceUpcastTargets.Select(type => type.Name));

        var (_, source) = GenerateOutput(project);
        Assert.Contains("cx_iface_upcast(value, 4)", source);
        Assert.Contains("cx_iface_upcast(value, 5)", source);
        Assert.Contains("first ? left : cx_iface_upcast(combined, 4)", source);
        Assert.Contains("cx_iface_upcast(combined, 4)", source);
        Assert.Contains("{ .data = CX_ID_5(Unnamed, Service, __iface, ILeft, __vtable) }", source);
        Assert.Contains("{ .data = CX_ID_5(Unnamed, Service, __iface, IRight, __vtable) }", source);
    }

    [Theory]
    [InlineData(
        "public interface IValue { int Get(); } public class Value : IValue {}",
        "does not implement interface function")]
    [InlineData(
        "public interface IValue { int Get(); } public class Value : IValue { private int Get() { return 1; } }",
        "must be public")]
    [InlineData(
        "public interface IValue { int Get(); } public class Value : IValue { public uint Get() { return 1; } }",
        "must return")]
    [InlineData(
        "public interface IValue { int Get() { return 1; } }",
        "cannot have a body")]
    [InlineData(
        "public interface IValue { int Value { get; } } public class Value : IValue {}",
        "does not implement interface property accessor")]
    [InlineData(
        "public interface IValue { int Value { get; } } public class Value : IValue { private int Value { extern get; } }",
        "must be public")]
    [InlineData(
        "public interface IValue { int Value { get; } } public class Value : IValue { public uint Value { extern get; } }",
        "must have type")]
    public void RejectsInvalidInterfaceImplementations(string source, string expectedMessage)
    {
        var exception = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(CreateProject(source)));

        Assert.Contains(expectedMessage, exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static CxProject CreateProject(string source)
    {
        var project = CxProject.CreateUnnamedApplicationProject();
        project.AddCompilationContext(CompilerTestHelper.Parse(source));
        return project;
    }

    private static (string Header, string Source) GenerateOutput(CxProject project)
    {
        var outputDirectory = Path.Combine(
            Path.GetTempPath(),
            $"cxc-tests-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(outputDirectory);
            CCodeOutputGenerator.GenerateOutput(
                project,
                Path.Combine(outputDirectory, "Interfaces.cx"));
            return (
                File.ReadAllText(Path.Combine(outputDirectory, "Unnamed.h")),
                File.ReadAllText(Path.Combine(outputDirectory, "Unnamed.c")));
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

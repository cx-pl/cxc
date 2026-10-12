using CxCompiler.Model.Errors;
using CxCompiler.Model.Project;
using CxCompiler.OutputGenerators;
using CxCompiler.Semantics;

namespace CxCompiler.Tests;

public sealed class ClosedGenericInterfaceMethodTests
{
    [Fact]
    public void ClosedGenericStructInterfaceThunkSupportsConcreteWhileMutations()
    {
        var project = CxProject.CreateDefaultApplicationProject(
            "closed_generic_interface_concrete_while_mutations");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public interface ICounter<T> {
                int Read();
                void FillTo(int limit, int step);
                void BumpThrough(int limit, int step);
                void FillFor(int limit, int step);
            }
            public struct Counter<T> : ICounter<T> {
                public int Count;
                public int Mirror;
                public constructor(int count) { Count = count; }
                public int Read() { return Count; }
                public void FillTo(int limit, int step) {
                    while (Count < limit) { Count += step; }
                }
                public void BumpThrough(int limit, int step) {
                    do { Count += step; } while (Count < limit);
                }
                public void FillFor(int limit, int step) {
                    for (Count = 0; Count < limit; Count += step) {
                        Mirror = Count;
                    }
                }
            }
            public int Run() {
                Counter<int> direct = new Counter<int>(1);
                direct.FillTo(9, 2);
                if (direct.Read() != 9) return -1;
                direct.BumpThrough(14, 2);
                if (direct.Read() != 15) return -3;
                direct.FillFor(10, 3);
                if (direct.Read() != 21) return -5;
                Counter<int> source = new Counter<int>(2);
                ICounter<int> boxed = (ICounter<int>)source;
                boxed.FillTo(10, 2);
                if (boxed.Read() != 10) return -2;
                boxed.BumpThrough(15, 2);
                if (boxed.Read() != 16) return -4;
                boxed.FillFor(10, 3);
                if (boxed.Read() != 21) return -6;
                return 0;
            }
            """));

        new SemanticBinder().Bind(project);

        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-closed-generic-interface-concrete-while-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(Path.Combine(
                directory, "closed_generic_interface_concrete_while_mutations.c"));

            Assert.Contains("while ((", source);
            Assert.Contains("do {", source);
            Assert.Contains("for (", source);
            Assert.Contains("->Count +=", source);
            Assert.DoesNotContain("_unknowntype_", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ClosedGenericStructInterfaceThunkSupportsConditionalDefaultResets()
    {
        var project = CxProject.CreateDefaultApplicationProject(
            "closed_generic_interface_conditional_default_reset");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public interface IResetPair<T> {
                T ReadFirst();
                T ReadSecond();
                void Reset(bool resetFirst);
            }
            public struct ResetPair<T> : IResetPair<T> {
                public T First;
                public T Second;
                public constructor(T first, T second) {
                    First = first;
                    Second = second;
                }
                public T ReadFirst() { return First; }
                public T ReadSecond() { return Second; }
                public void Reset(bool resetFirst) {
                    if (resetFirst) { First = default; }
                    else { Second = default; }
                }
            }
            public int Run() {
                ResetPair<int> direct = new ResetPair<int>(2, 3);
                direct.Reset(true);
                if (direct.ReadFirst() != 0 || direct.ReadSecond() != 3) return -1;
                ResetPair<int> source = new ResetPair<int>(5, 7);
                IResetPair<int> boxed = (IResetPair<int>)source;
                boxed.Reset(false);
                if (boxed.ReadFirst() != 5 || boxed.ReadSecond() != 0) return -2;
                return 0;
            }
            """));

        new SemanticBinder().Bind(project);

        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-closed-generic-interface-conditional-reset-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(Path.Combine(
                directory, "closed_generic_interface_conditional_default_reset.c"));

            Assert.Contains("if (resetFirst)", source);
            Assert.Contains("->First", source);
            Assert.Contains("->Second", source);
            Assert.DoesNotContain("_unknowntype_", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ClosedGenericStructInterfaceThunkSupportsConcreteReturnBranches()
    {
        var project = CxProject.CreateDefaultApplicationProject(
            "closed_generic_interface_concrete_return_branches");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public interface IConcreteChoice<T> {
                int Select(bool first, int offset);
            }
            public struct ConcreteChoice<T> : IConcreteChoice<T> {
                public int Count;
                public constructor(int count) { Count = count; }
                public int Select(bool first, int offset) {
                    if (first) { return Count + offset; }
                    else { return offset * 2; }
                }
            }
            public int Run() {
                ConcreteChoice<int> direct = new ConcreteChoice<int>(10);
                if (direct.Select(true, 2) != 12 || direct.Select(false, 3) != 6)
                    return -1;
                IConcreteChoice<int> boxed = (IConcreteChoice<int>)direct;
                if (boxed.Select(true, 4) != 14 || boxed.Select(false, 5) != 10)
                    return -2;
                return 0;
            }
            """));

        new SemanticBinder().Bind(project);

        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-closed-generic-interface-concrete-returns-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(Path.Combine(
                directory, "closed_generic_interface_concrete_return_branches.c"));

            Assert.Contains("?", source);
            Assert.Contains("->Count", source);
            Assert.DoesNotContain("_unknowntype_", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ClosedGenericStructInterfaceThunkSupportsConditionalGenericFieldWrites()
    {
        var project = CxProject.CreateDefaultApplicationProject(
            "closed_generic_interface_conditional_field_writes");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public interface IWritePair<T> {
                T ReadFirst();
                T ReadSecond();
                void Store(bool shouldWrite, T item);
                void StorePair(bool writeFirst, T first, T second);
                void StoreOrClear(bool writeFirst, T item);
                void StoreIf(bool writeFirst, T item);
                void StoreMixed(bool writeFirst, T item, int amount);
                int ReadFirstCount();
                int ReadSecondCount();
                void StoreCount(bool shouldWrite, int amount);
                void StoreIfCount(bool shouldWrite, int amount);
            }
            public struct WritePair<T> : IWritePair<T> {
                public T First;
                public T Second;
                public int FirstCount = 10;
                public int SecondCount = 20;
                public constructor(T first, T second) {
                    First = first;
                    Second = second;
                }
                public T ReadFirst() { return First; }
                public T ReadSecond() { return Second; }
                public int ReadFirstCount() { return FirstCount; }
                public int ReadSecondCount() { return SecondCount; }
                public void Store(bool first, T value) {
                    if (first) { First = value; }
                    else { Second = value; }
                }
                public void StorePair(bool first, T left, T right) {
                    if (first) {
                        First = left;
                        Second = right;
                    } else {
                        Second = right;
                        First = left;
                    }
                }
                public void StoreOrClear(bool first, T item) {
                    if (first) {
                        First = item;
                        Second = default;
                    } else {
                        Second = item;
                        First = default;
                    }
                }
                public void StoreIf(bool first, T item) {
                    if (first) { First = item; }
                }
                public void StoreMixed(bool first, T item, int amount) {
                    if (first) {
                        First = item;
                        FirstCount = amount + 1;
                        Second = default;
                        SecondCount = FirstCount + amount;
                    } else {
                        Second = item;
                        SecondCount = amount * 2;
                        First = default;
                        FirstCount = SecondCount + amount;
                    }
                }
                public void StoreCount(bool first, int value) {
                    if (first) {
                        FirstCount += value;
                        SecondCount = FirstCount + value;
                    } else {
                        SecondCount -= value;
                        FirstCount = SecondCount - value;
                    }
                }
                public void StoreIfCount(bool first, int value) {
                    if (first) { FirstCount += value; }
                }
            }
            public int Run() {
                WritePair<int> direct = new WritePair<int>(2, 3);
                direct.Store(true, 5);
                if (direct.ReadFirst() != 5 || direct.ReadSecond() != 3) return -1;
                direct.StorePair(true, 17, 19);
                if (direct.ReadFirst() != 17 || direct.ReadSecond() != 19) return -5;
                direct.StoreOrClear(true, 31);
                if (direct.ReadFirst() != 31 || direct.ReadSecond() != 0) return -7;
                direct.StoreIf(false, 41);
                if (direct.ReadFirst() != 31 || direct.ReadSecond() != 0) return -9;
                direct.StoreIf(true, 43);
                if (direct.ReadFirst() != 43 || direct.ReadSecond() != 0) return -10;
                direct.StoreMixed(true, 41, 6);
                if (direct.ReadFirst() != 41 || direct.ReadSecond() != 0 ||
                    direct.ReadFirstCount() != 7 || direct.ReadSecondCount() != 13)
                    return -13;
                direct.StoreCount(true, 6);
                if (direct.ReadFirstCount() != 13 || direct.ReadSecondCount() != 19) return -3;
                direct.StoreIfCount(false, 100);
                if (direct.ReadFirstCount() != 13 || direct.ReadSecondCount() != 19) return -15;
                direct.StoreIfCount(true, 2);
                if (direct.ReadFirstCount() != 15 || direct.ReadSecondCount() != 19) return -16;
                WritePair<int> source = new WritePair<int>(7, 11);
                IWritePair<int> boxed = (IWritePair<int>)source;
                boxed.Store(false, 13);
                if (boxed.ReadFirst() != 7 || boxed.ReadSecond() != 13) return -2;
                boxed.StorePair(false, 23, 29);
                if (boxed.ReadFirst() != 23 || boxed.ReadSecond() != 29) return -6;
                boxed.StoreOrClear(false, 37);
                if (boxed.ReadFirst() != 0 || boxed.ReadSecond() != 37) return -8;
                boxed.StoreIf(false, 47);
                if (boxed.ReadFirst() != 0 || boxed.ReadSecond() != 37) return -11;
                boxed.StoreIf(true, 53);
                if (boxed.ReadFirst() != 53 || boxed.ReadSecond() != 37) return -12;
                boxed.StoreMixed(false, 43, 8);
                if (boxed.ReadFirst() != 0 || boxed.ReadSecond() != 43 ||
                    boxed.ReadFirstCount() != 24 || boxed.ReadSecondCount() != 16)
                    return -14;
                boxed.StoreCount(false, 8);
                if (boxed.ReadFirstCount() != 0 || boxed.ReadSecondCount() != 8) return -4;
                boxed.StoreIfCount(false, 100);
                if (boxed.ReadFirstCount() != 0 || boxed.ReadSecondCount() != 8) return -17;
                boxed.StoreIfCount(true, 3);
                if (boxed.ReadFirstCount() != 3 || boxed.ReadSecondCount() != 8) return -18;
                return 0;
            }
            """));

        new SemanticBinder().Bind(project);

        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-closed-generic-interface-conditional-writes-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(Path.Combine(
                directory, "closed_generic_interface_conditional_field_writes.c"));

            Assert.Contains("if (first)", source);
            Assert.Contains("->First = value", source);
            Assert.Contains("->Second = value", source);
            Assert.Contains("->First = left", source);
            Assert.Contains("->Second = right", source);
            Assert.Contains("StoreOrClear", source);
            Assert.Contains("StoreIf", source);
            Assert.Contains("StoreMixed", source);
            Assert.Contains("->FirstCount += value", source);
            Assert.Contains("->SecondCount -= value", source);
            Assert.Contains("if (first)", source);
            var firstWrite = source.IndexOf("->First = left;", StringComparison.Ordinal);
            var secondWrite = source.IndexOf("->Second = right;", StringComparison.Ordinal);
            Assert.True(firstWrite >= 0 && secondWrite > firstWrite);
            Assert.Contains("->FirstCount", source);
            Assert.Contains("->SecondCount", source);
            Assert.DoesNotContain("_unknowntype_", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ClosedGenericStructInterfaceThunkSupportsReturnBranches()
    {
        var project = CxProject.CreateDefaultApplicationProject(
            "closed_generic_interface_return_branches");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public interface IChoice<T> { T Choose(bool useValue); }
            public struct Choice<T> : IChoice<T> {
                public T Value;
                public constructor(T value) { Value = value; }
                public T Choose(bool useValue) {
                    if (useValue) { return Value; }
                    else { return default; }
                }
            }
            public int Run(bool useValue) {
                Choice<int> choice = new Choice<int>(42);
                IChoice<int> boxed = (IChoice<int>)choice;
                return boxed.Choose(useValue);
            }
            """));

        new SemanticBinder().Bind(project);

        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-closed-generic-interface-return-branches-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(Path.Combine(
                directory, "closed_generic_interface_return_branches.c"));

            Assert.Contains("? ", source);
            Assert.Contains("->Value", source);
            Assert.DoesNotContain("_unknowntype_", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ClosedGenericStructInterfaceThunkSupportsConcreteExpressionGetter()
    {
        var project = CxProject.CreateDefaultApplicationProject("closed_generic_interface_property");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public interface IScore { int Score { const get; } }
            public struct Counter<T> : IScore {
                public int Count;
                public constructor(int count) { Count = count; }
                public int Score { const get { return Count + 1; } }
            }
            public int Run() {
                Counter<int> counter = new Counter<int>(0);
                IScore boxed = (IScore)counter;
                return boxed.Score;
            }
            """));

        new SemanticBinder().Bind(project);

        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-closed-generic-interface-property-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(
                Path.Combine(directory, "closed_generic_interface_property.c"));

            Assert.Contains("Count + 1", source);
            Assert.DoesNotContain("_unknowntype_", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ClosedGenericStructInterfaceThunkSupportsBuiltInCompoundFieldMutation()
    {
        var project = CxProject.CreateDefaultApplicationProject(
            "closed_generic_interface_compound_field_mutation");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public interface ICounter<T> {
                int Read();
                void Add(int amount);
            }
            public struct Counter<T> : ICounter<T> {
                public int Count;
                public constructor(int count) { Count = count; }
                public int Read() { return Count; }
                public void Add(int amount) { Count += amount; }
            }
            public int Run() {
                Counter<int> counter = new Counter<int>(3);
                ICounter<int> boxed = (ICounter<int>)counter;
                boxed.Add(4);
                return boxed.Read();
            }
            """));

        new SemanticBinder().Bind(project);

        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-closed-generic-interface-compound-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(Path.Combine(
                directory, "closed_generic_interface_compound_field_mutation.c"));

            Assert.Contains("->Count += amount", source);
            Assert.DoesNotContain("_unknowntype_", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ClosedGenericStructThunkSupportsConcreteExpressionOnConstructedInterface()
    {
        var project = CxProject.CreateDefaultApplicationProject(
            "closed_generic_constructed_interface_property");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public interface IScore<T> {
                T Echo { const get; }
                int Score { const get; }
            }
            public struct Counter<T> : IScore<T> {
                public T Payload;
                public int Count;
                public constructor(T payload, int count) {
                    Payload = payload;
                    Count = count;
                }
                public T Echo { const get { return Payload; } }
                public int Score { const get { return Count + 1; } }
            }
            public int Run() {
                Counter<int> counter = new Counter<int>(42, 0);
                IScore<int> boxed = (IScore<int>)counter;
                return boxed.Score + boxed.Echo;
            }
            """));

        new SemanticBinder().Bind(project);

        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-closed-generic-constructed-interface-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(
                Path.Combine(directory, "closed_generic_constructed_interface_property.c"));

            Assert.Contains("Count + 1", source);
            Assert.Contains("->Payload", source);
            Assert.DoesNotContain("_unknowntype_", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ClosedGenericStructInterfaceThunkSupportsArgumentReturnsAndConstantFieldMutation()
    {
        var project = CxProject.CreateDefaultApplicationProject("closed_generic_interface_method");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public interface IIdentity<T> {
                T Echo(T first, T value, int ignored);
                void Write(T ignored, T value, int unused);
                void WritePair(T first, T second);
                void ClearPair();
                void SetValueAndCounter(T value, int count);
                T ReadFirst();
                T ReadSecond();
                T ReadValue();
                T ReadDefault(T ignored);
                void ClearValue();
                void SetCounter(int unused, int value);
                void ResetCounter();
                int ReadCounter();
                int AddToCounter(int amount);
                void IncreaseCounter(int amount);
                int ReadOtherCounter();
                void InitializeCounters(int amount);
            }
            public struct Identity<T> : IIdentity<T> {
                public T Value;
                public T First;
                public T Second;
                public int Counter;
                public int OtherCounter;
                public constructor() { }
                public T Echo(T ignored, T result, int unused) { return result; }
                public void Write(T ignored, T result, int unused) { Value = result; }
                public void WritePair(T first, T second) {
                    First = second;
                    Second = first;
                }
                public void ClearPair() {
                    First = default;
                    Second = default;
                }
                public void SetValueAndCounter(T value, int count) {
                    Value = value;
                    Counter = count;
                    OtherCounter = Counter + count;
                }
                public T ReadFirst() { return First; }
                public T ReadSecond() { return Second; }
                public T ReadValue() { return Value; }
                public T ReadDefault(T ignored) { return default; }
                public void ClearValue() { Value = default; }
                public void SetCounter(int firstUnused, int actual) { Counter = actual; }
                public void ResetCounter() { Counter = 4 - 4; }
                public int ReadCounter() { return Counter; }
                public int AddToCounter(int delta) { return Counter + delta; }
                public void IncreaseCounter(int delta) { Counter = Counter + delta; }
                public int ReadOtherCounter() { return OtherCounter; }
                public void InitializeCounters(int value) {
                    Counter = value;
                    OtherCounter = Counter + value;
                }
            }
            public int Run() {
                Identity<int> direct = default;
                direct.SetValueAndCounter(31, 5);
                if (direct.Value != 31 || direct.Counter != 5 ||
                    direct.OtherCounter != 10) return -10;
                if (direct.ReadDefault(0) != 0) return -11;
                Identity<int> identity = default;
                IIdentity<int> boxed = (IIdentity<int>)identity;
                boxed.Write(0, 42, 0);
                boxed.ClearValue();
                if (boxed.ReadValue() != 0) return -6;
                if (boxed.ReadDefault(0) != 0) return -8;
                boxed.Write(0, 42, 0);
                boxed.WritePair(17, 29);
                if (boxed.ReadFirst() != 17 || boxed.ReadSecond() != 29) return -5;
                boxed.ClearPair();
                if (boxed.ReadFirst() != 0 || boxed.ReadSecond() != 0) return -7;
                boxed.WritePair(17, 29);
                boxed.SetValueAndCounter(31, 5);
                if (boxed.ReadValue() != 31 || boxed.ReadCounter() != 5 ||
                    boxed.ReadOtherCounter() != 10) return -9;
                boxed.SetCounter(0, 8);
                boxed.ResetCounter();
                if (boxed.ReadCounter() != 0) return -1;
                if (boxed.AddToCounter(5) != 5) return -2;
                boxed.IncreaseCounter(7);
                if (boxed.ReadCounter() != 7) return -3;
                boxed.InitializeCounters(11);
                if (boxed.ReadCounter() != 11 || boxed.ReadOtherCounter() != 22) return -4;
                return boxed.Echo(0, 42, 0);
            }
            """));

        new SemanticBinder().Bind(project);

        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-closed-generic-interface-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(
                Path.Combine(directory, "closed_generic_interface_method.c"));

            Assert.Contains("return value;", source);
            Assert.Contains("->Value = value;", source);
            Assert.Contains("->First = second;", source);
            Assert.Contains("->Second = first;", source);
            Assert.Contains("->First = ((cx_int){ 0 });", source);
            Assert.Contains("->Second = ((cx_int){ 0 });", source);
            Assert.Contains("->OtherCounter =", source);
            Assert.Contains("->Value = ((cx_int){ 0 });", source);
            Assert.Contains("return ((cx_int){ 0 });", source);
            Assert.Contains("->Counter = value;", source);
            Assert.Contains("->Counter = (4 - 4);", source);
            Assert.Contains("Counter + amount", source);
            Assert.Contains("Counter =", source);
            Assert.Contains("OtherCounter", source);
            Assert.DoesNotContain("_unknowntype_", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void RejectsParameterizedDefaultResetMethodOnClosedGenericStruct()
    {
        var project = CxProject.CreateDefaultApplicationProject(
            "closed_generic_interface_parameterized_default_reset");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public interface IReset<T> { void Clear(int ignored); }
            public struct Resettable<T> : IReset<T> {
                public T First;
                public T Second;
                public void Clear(int ignored) {
                    First = default;
                    Second = default;
                }
            }
            public void Run() {
                Resettable<int> value = default;
                IReset<int> boxed = (IReset<int>)value;
                boxed.Clear(0);
            }
            """));

        new SemanticBinder().Bind(project);

        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-closed-generic-interface-invalid-reset-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            Assert.Throws<CompilationErrorException>(() =>
                CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx")));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}

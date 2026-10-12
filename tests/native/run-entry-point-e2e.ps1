param(
    [string] $Cxc = 'cxc',
    [string] $CxCoreDir = (Join-Path $PSScriptRoot '..\..\..\cxcore'),
    [string] $CMake = 'cmake',
    [string] $Generator = '',
    [string] $CCompiler = ''
)

$ErrorActionPreference = 'Stop'
$compilerPath = if ([IO.Path]::IsPathRooted($Cxc)) {
    $Cxc
} else {
    (Get-Command $Cxc -ErrorAction Stop).Source
}
$cxCorePath = (Resolve-Path $CxCoreDir).Path
$cmakePath = if (Test-Path -LiteralPath $CMake -PathType Leaf) {
    (Resolve-Path $CMake).Path
} else {
    (Get-Command $CMake -ErrorAction Stop).Source
}
$buildRoot = Join-Path ([IO.Path]::GetTempPath()) "cx-entry-point-e2e-$([guid]::NewGuid().ToString('N'))"
$outputDirectory = Join-Path $buildRoot 'generated'
$binaryDirectory = Join-Path $buildRoot '.bin'
$sourcePath = Join-Path $buildRoot 'entry_point_e2e.cx'
$previousPath = $env:PATH
$previousGenerator = $env:CMAKE_GENERATOR
$previousCCompiler = $env:CC
$completed = $false

try {
    New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
    [IO.File]::WriteAllText($sourcePath, @'
import System;

public delegate int SpecializationTransform(int value);
public extension IntArrayExtensions : int[] {
    public uint Count { const get => this.Length; }
    public uint operator +(int[] other) const {
        return this.Length + other.Length;
    }
    public int FirstPlus(int fallback) const {
        return this.Length == 0 ? fallback : this[0] + fallback;
    }
}
public extension IntArrayReplacement : int[] {
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
    public int[] operator +=(int[] replacement) const { return replacement; }
    public int[] operator -=(int[] replacement) const { return replacement; }
    public int[] operator *=(int[] replacement) const { return replacement; }
    public int[] operator /=(int[] replacement) const { return replacement; }
    public int[] operator %=(int[] replacement) const { return replacement; }
}
public extension GenericArrayExtensions<T> : T[] {
    public uint GenericCount { const get => this.Length; }
    public T GetFirstElement() const { return this[0]; }
}
public extension GenericArrayReplacement<T> : T[] {
    public T[] operator ++() const { return this; }
    public T[] operator --() const { return this; }
    public T[] operator +=(T[] replacement) const { return replacement; }
    public T[] operator -=(T[] replacement) const { return replacement; }
    public T[] operator *=(T[] replacement) const { return replacement; }
    public T[] operator /=(T[] replacement) const { return replacement; }
    public T[] operator %=(T[] replacement) const { return replacement; }
}
public class GenericPropertyLocalCopy<T> {
    public T Value;
    public T Copied { const get { T copy = Value; return copy; } }
}
public class GenericArrayStorage<T> {
    private T[] _items;
    public constructor(uint length) { _items = new T[length]; }
    public uint Length { const get { return _items.Length; } }
    public T Get(uint index) const { return _items[index]; }
    public void Set(uint index, T value) { _items[index] = value; }
}
public extension GenericJaggedArrayExtensions<T> : T[][] {
    public T GetFirstNestedElement() const { return this[0][0]; }
    public uint operator +(T[][] other) const {
        return this.Length + other.Length;
    }
}
public class ArrayBox<T> { public T Value; }
public extension ConstructedArrayExtensions<T> : ArrayBox<T>[] {
    public ArrayBox<T> GetFirstBox() const { return this[0]; }
}
public class TwoPart<T, U> { public T First; public U Second; }
public extension TwoPartArrayExtensions<T, U> : TwoPart<T, U>[] {
    public TwoPart<T, U> GetFirstPair() const { return this[0]; }
}
public struct ConstructorExpressionPair<T> {
    public T Value;
    public int Count;
    public constructor(T value, int count) {
        Value = value;
        Count = count + 1;
    }
}
public T Adjust<T>(T value) { return value; }
public int Adjust <int>(int value) { return value + 1; }
public T SpecializationFirst<T, U>(T value, U ignored) { return value; }
public int SpecializationFirst <int, string>(int value, string ignored) {
    return value + 2;
}
public T ForwardSpecialization<T>(T value) { return value; }
public int ForwardSpecialization <int>(int value) { return Adjust(value); }
public T ForwardOpenGeneric<T>(T value) { return Adjust(value); }
public int RunGenericSpecialization() {
    SpecializationTransform transform = Adjust;
    if (Adjust(4) != 5 || Adjust<int>(6) != 7 || Adjust<long>(11L) != 11L ||
        SpecializationFirst(3, "ignored") != 5 || ForwardSpecialization(2) != 3 ||
        ForwardOpenGeneric(8) != 9 || ForwardOpenGeneric(11L) != 11L ||
        transform(12) != 13)
        return 171;
    return 0;
}
public T Step<T>(T value) { return value; }
public int Step <int>(int value) {
    if (value == 0) return 42;
    return Step(value - 1);
}
public int RunRecursiveSpecialization() { return Step(3); }
public class SpecializationBox<T> {
    public int Value;
    public constructor(int value) { Value = value; }
}
alias SpecializationIntBox = SpecializationBox<int>;
public T EchoSpecialized<T>(T value) { return value; }
public string EchoSpecialized <string>(string value) { return "special"; }
public int[] EchoSpecialized <int[]>(int[] value) { return value; }
public SpecializationIntBox EchoSpecialized <SpecializationIntBox>(
    SpecializationIntBox value) { return value; }
public int RunSpecializationShapes() {
    if (EchoSpecialized("input") != "special") return 174;
    int[] values = new int[1];
    values[0] = 23;
    if (EchoSpecialized(values)[0] != 23) return 175;
    SpecializationIntBox box = new SpecializationIntBox(31);
    return EchoSpecialized(box).Value;
}
public class GenericSpecializationCounter {
    public int Value = 10;
    public T Adjust<T>(T value) { return value; }
    public int Adjust <int>(int value) { return Value + value; }
    public static T Offset<T>(T value) { return value; }
    public static int Offset <int>(int value) { return value + 3; }
}
public class GenericContainingSpecializationRouter<TState> {
    public TState Last;
    public U Route<U>(TState state, U value) { return value; }
    public int Route<int>(TState state, int value) {
        Last = state;
        return value + 1;
    }
}
public struct GenericContainingSpecializationValueRouter<TState> {
    public TState Last;
    public constructor() {}
    public U Route<U>(TState state, U value) { return value; }
    public int Route<int>(TState state, int value) {
        Last = state;
        return value + 1;
    }
}
public class GenericStaticSpecializationUtility<TState> {
    public static U Select<U>(TState state, U value) { return value; }
    public static int Select <int>(TState state, int value) { return value + 20; }
}
public class GenericExtensionSpecializationTarget { }
public extension GenericExtensionSpecializationMethods : GenericExtensionSpecializationTarget {
    public T Echo<T>(T value) const { return value; }
    public int Echo<int>(int value) const { return value + 5; }
}
public int RunGenericExtensionSpecialization() {
    return new GenericExtensionSpecializationTarget().Echo<int>(37);
}
public class GenericExtensionSpecializationBox<TState> { }
public extension GenericExtensionSpecializationBoxMethods<TState> : GenericExtensionSpecializationBox<TState> {
    public U Echo<U>(TState state, U value) const { return value; }
    public int Echo<int>(TState state, int value) const { return value + 6; }
}
public int RunClosedGenericExtensionSpecialization() {
    return new GenericExtensionSpecializationBox<string>().Echo<int>("state", 36);
}
public struct GenericExtensionStructTarget { public int Value; }
public extension GenericExtensionStructMethods : GenericExtensionStructTarget {
    public U Echo<U>(U value) const { return value; }
    public int Echo<int>(int value) const { return value + 7; }
}
public int RunGenericStructExtensionSpecialization() {
    GenericExtensionStructTarget receiver = default;
    if (receiver.Echo<long>(11L) != 11L) return 199;
    return receiver.Echo<int>(35);
}
public interface IGenericExtensionValue { int Read() const; }
public class GenericExtensionValue : IGenericExtensionValue {
    public int Read() const { return 1; }
}
public extension GenericInterfaceExtensionMethods : IGenericExtensionValue {
    public U Echo<U>(U value) const { return value; }
    public int Echo<int>(int value) const { return value + 8; }
}
public int RunGenericInterfaceExtensionSpecialization() {
    IGenericExtensionValue receiver = new GenericExtensionValue();
    if (receiver.Echo<long>(11L) != 11L) return 201;
    return receiver.Echo<int>(34);
}
public enum GenericExtensionShade { Dark, Light }
public extension GenericEnumExtensionMethods : GenericExtensionShade {
    public U Echo<U>(U value) const { return value; }
    public int Echo<int>(int value) const { return value + 9; }
}
public int RunGenericEnumExtensionSpecialization() {
    GenericExtensionShade receiver = GenericExtensionShade.Light;
    if (receiver.Echo<long>(11L) != 11L) return 203;
    return receiver.Echo<int>(33);
}
public interface IGenericClosedExtensionBox<T> { T Get() const; }
public extension GenericClosedInterfaceExtensionMethods<T> : IGenericClosedExtensionBox<T> {
    public U Echo<U>(T state, U value) const { return value; }
    public int Echo<int>(T state, int value) const { return value + 10; }
}
public int RunGenericClosedInterfaceExtensionSpecialization() {
    IGenericClosedExtensionBox<string> receiver = null;
    if (receiver.Echo<long>("state", 23L) != 23L) return 209;
    return receiver.Echo<int>("state", 32);
}
public delegate int GenericLocalAdjustment(int value);
public int RunGenericLocalSpecialization() {
    int offset = 10;
    int Adjust<int>(int value) { return value + offset; }
    T Adjust<T>(T value) { return value; }
    if (Adjust<long>(7L) != 7L) return 189;
    if (Adjust<int>(30) != 40) return 191;
    GenericLocalAdjustment adjustment = Adjust;
    return adjustment(32);
}
public struct GenericOperatorSpecialization {
    public T operator +<T>(T other) where T : struct { return other; }
    public int operator +<int>(int other) { return other + 5; }
}
public int RunGenericOperatorSpecialization() {
    GenericOperatorSpecialization value = default;
    if (value + 7L != 7L) return 193;
    return value + 37;
}
public extension GenericIntExtensions : int {
    public U Echo<U>(U value) const { return value; }
    public int Echo<int>(int value) const { return value + 5; }
}
public int RunGenericPrimitiveExtensionSpecialization() {
    int receiver = 1;
    if (receiver.Echo<long>(7L) != 7L) return 194;
    return receiver.Echo<int>(37);
}
public int RunGenericStaticContainingTypeSpecialization() {
    if (GenericStaticSpecializationUtility<string>.Select<int>("text", 3) != 23) return 1;
    if (GenericStaticSpecializationUtility<int>.Select<int>(17, 3) != 23) return 2;
    return 0;
}
public int RunGenericContainingTypeSpecialization() {
    GenericContainingSpecializationRouter<string> text =
        new GenericContainingSpecializationRouter<string>();
    if (text.Route<int>("custom", 3) != 4 || text.Last != "custom") return 180;
    GenericContainingSpecializationRouter<string[]> array =
        new GenericContainingSpecializationRouter<string[]>();
    if (array.Route<int>(null, 5) != 6 || array.Last != null) return 181;
    if (text.Route<long>("fallback", 7L) != 7L || text.Last != "custom") return 182;
    GenericContainingSpecializationValueRouter<int> value = default;
    if (value.Route<int>(5, 8) != 9 || value.Last != 5) return 184;
    if (value.Route<long>(6, 8L) != 8L || value.Last != 5) return 185;
    return 0;
}
public class GenericForwardingRoutes {
    public static int FactoryCalls = 0;
    public T Echo<T>(T value) { return value; }
    public int Echo <int>(int value) { return value + 1; }
    public T Echo<T, U>(T value, U other) { return value; }
    public int Echo <int, int>(int value, int other) { return value + other; }
    public int Echo <int, string>(int value, string other) { return value + 2; }
    public static GenericForwardingRoutes Create() {
        FactoryCalls += 1;
        return new GenericForwardingRoutes();
    }
    public T Forward<T>(T value) { return Echo(value); }
}
public class GenericForwardingInvoker {
    public T Forward<T>(GenericForwardingRoutes routes, T value) {
        return routes.Echo(value);
    }
    public T ForwardFromComputedReceiver<T>(T value) {
        return GenericForwardingRoutes.Create().Echo(value);
    }
    public T ForwardPair<T, U>(GenericForwardingRoutes routes, T value, U other) {
        return routes.Echo(value, other);
    }
    public T ForwardWithOffset<T>(GenericForwardingRoutes routes, T value, int offset) {
        return routes.Echo(value, offset + 1);
    }
}
public struct GenericSpecializationStruct {
    public int Value;
    public int Calls;
    public static int FactoryCalls;
    public static GenericSpecializationStruct Create() {
        FactoryCalls += 1;
        return default;
    }
    public T Add<T>(T value) { return value; }
    public int Add <int>(int value) {
        Value += value;
        return Value;
    }
    public T Echo<T>(T value) { return value; }
    public int Echo <int>(int value) {
        Calls += 1;
        return Calls + value;
    }
}
public class GenericStructForwardingInvoker {
    public T Forward<T>(GenericSpecializationStruct routes, T value) {
        return routes.Echo(value);
    }
    public T ForwardFromComputedReceiver<T>(T value) {
        return GenericSpecializationStruct.Create().Echo(value);
    }
}
public int RunGenericMethodSpecialization() {
    GenericSpecializationCounter counter = new GenericSpecializationCounter();
    GenericForwardingRoutes routes = new GenericForwardingRoutes();
    GenericForwardingInvoker invoker = new GenericForwardingInvoker();
    GenericStructForwardingInvoker structInvoker = new GenericStructForwardingInvoker();
    SpecializationTransform instanceTransform = counter.Adjust;
    SpecializationTransform staticTransform = GenericSpecializationCounter.Offset;
    if (counter.Adjust(2) != 12 || instanceTransform(2) != 12 ||
        GenericSpecializationCounter.Offset<int>(4) != 7 ||
        staticTransform(4) != 7 ||
        counter.Adjust<long>(9L) != 9L ||
        routes.Forward(6) != 7 || routes.Forward("ok") != "ok" ||
        invoker.Forward(routes, 6) != 7 ||
        invoker.Forward(routes, "ok") != "ok" ||
        invoker.ForwardFromComputedReceiver(6) != 7 ||
        invoker.ForwardFromComputedReceiver("ok") != "ok" ||
        GenericForwardingRoutes.FactoryCalls != 2 ||
        invoker.ForwardPair(routes, 6, "ignored") != 8 ||
        invoker.ForwardPair(routes, "ok", 6) != "ok") return 172;
    if (invoker.ForwardWithOffset(routes, 4, 2) != 7) return 177;
    if (invoker.ForwardWithOffset(routes, "ok", 2) != "ok") return 178;
    GenericSpecializationStruct value = default;
    value.Value = 5;
    if (value.Add(2) != 7 || value.Value != 7) return 173;
    GenericSpecializationStruct structRoutes = default;
    if (structInvoker.Forward(structRoutes, 4) != 5 || structRoutes.Calls != 0 ||
        structInvoker.Forward(structRoutes, "ok") != "ok" || structRoutes.Calls != 0 ||
        structInvoker.ForwardFromComputedReceiver(4) != 5 ||
        structInvoker.ForwardFromComputedReceiver("ok") != "ok" ||
        GenericSpecializationStruct.FactoryCalls != 2)
        return 179;
    return 0;
}

public concept CEquatable {
    bool operator ==(const CEquatable other) const;
}
public concept CComparable {
    bool operator >(const CEquatable other) const;
}
public concept CAddable {
    CAddable operator +(const CAddable other) const;
}
public concept CTruth {
    bool operator !() const;
}
public concept CInvertible {
    CInvertible operator ~() const;
}
public concept CNegatable {
    CNegatable operator -() const;
}
public concept CReadable {
    int GetValue(int multiplier) const;
}
public concept CScoredReadable {
    int Score { const get; }
    int GetValue(int multiplier) const;
}
public concept CEcho {
    T Echo<T>(T value, int ignored = 1) const;
}
public concept CStructEcho {
    T Echo<T>(T value) where T : struct, IMarker;
}
public concept CHasMarker {
    int Marker { const get; }
}
public concept CBounds {
    int Low { const get; }
    int High { const get; set; }
}
public concept CMarkedEcho {
    T Echo<T>(T value) : T = CHasMarker;
}
public interface IMarker { }
public interface IBoxedStructRead { int ReadBoxedValue() const; }
public interface IBoxedComputedScore { int Score { const get; } }
public interface IBoxedGenericComputedScore<T> {
    T Echo { const get; }
    int Score { const get; }
}
public interface IBoxedGenericCounter<T> {
    int Read();
    int ReadCount();
    void Add(int amount);
    void FillTo(int limit, int step);
    void BumpThrough(int limit, int step);
    void FillFor(int limit, int step);
    void Adjust(bool add, int amount);
    void AdjustIf(bool add, int amount);
}
public interface IAdjustedCounter<T> { int Adjusted { set; } int Read() const; }
public interface IGenericVoidMutation { void Add(int amount); }
public interface IGenericValueReader { int Read(); }
public class GenericVoidMutation : IGenericVoidMutation {
    public int Value;
    public void Add(int amount) { Value += amount; }
}
public struct GenericVoidMutationValue : IGenericVoidMutation {
    public int Value;
    public void Add(int amount) { Value += amount; }
}
public class GenericValueReader : IGenericValueReader {
    public int Value;
    public int Read() { return Value; }
}
public struct GenericValueReaderValue : IGenericValueReader {
    public int Value;
    public int Read() { return Value; }
}
public struct BoxedStructRead : IBoxedStructRead {
    public int Value;
    public constructor(int value) { Value = value; }
    public int ReadBoxedValue() const { return Value; }
}
public struct BoxedGenericStruct<T> : IBoxedStructRead {
    public T Payload;
    public constructor(T payload) { Payload = payload; }
    public int ReadBoxedValue() const { return 61; }
}
public struct BoxedComputedScore<T> : IBoxedComputedScore {
    public int Count;
    public constructor(int count) { Count = count; }
    public int Score { const get { return Count + 1; } }
}
public struct RepeatedGenericField<T> {
    public T First;
    public T Second;
    public constructor(T value) {
        First = value;
        Second = value;
    }
}
public interface IOptionalValue<T> {
    T Value { const get; }
    T ConditionalCopy { const get; }
    T FieldSelectionCopy { const get; }
    T BooleanExpressionCopy { const get; }
    T Read(bool usePayload) const;
    T ReadBranch(bool usePayload) const;
    int SelectConcrete(bool usePayload, int offset) const;
}
public struct OptionalValue<T> : IOptionalValue<T> {
    public bool HasValue;
    public bool Ready;
    public T Payload;
    public T Fallback;
    public int Count = 10;
    public constructor(bool hasValue, T payload, T fallback) {
        HasValue = hasValue;
        Ready = true;
        Payload = payload;
        Fallback = fallback;
    }
    public T Value { const get { return HasValue ? Payload : default; } }
    public T ValueOrFallback { const get { return HasValue ? Payload : Fallback; } }
    public T ConditionalCopy {
        const get { T copy = HasValue && Ready ? Payload : default; return copy; }
    }
    public T FieldSelectionCopy {
        const get { var copy = HasValue ? Payload : Fallback; return copy; }
    }
    public T BooleanExpressionCopy {
        const get { T copy = !HasValue || !Ready ? Payload : default; return copy; }
    }
    public T Read(bool usePayload) const {
        return usePayload ? Payload : Fallback;
    }
    public T ReadBranch(bool usePayload) const {
        if (usePayload) { return Payload; }
        else { return default; }
    }
    public int SelectConcrete(bool usePayload, int offset) const {
        if (usePayload) { return Count + offset; }
        else { return offset * 2; }
    }
}
public interface IGenericLocalCopy<T> {
    T Copy { const get; }
    T InferredCopy { const get; }
    T ThisCopy { const get; }
}
public struct GenericLocalCopy<T> : IGenericLocalCopy<T> {
    public T Value;
    public constructor(T value) { Value = value; }
    public T Copy { const get { T copy = Value; return copy; } }
    public T InferredCopy { const get { var copy = Value; return copy; } }
    public T ThisCopy { const get { T copy = this.Value; return copy; } }
}
public int RunGenericPropertyLocalCopyThroughInterface() {
    GenericLocalCopy<int> direct = new GenericLocalCopy<int>(21);
    IGenericLocalCopy<int> boxed = (IGenericLocalCopy<int>)direct;
    return direct.Copy + boxed.Copy + direct.InferredCopy + boxed.InferredCopy +
        direct.ThisCopy + boxed.ThisCopy;
}
public int ReadOptionalThroughConstraint<U>(U value, bool usePayload)
    where U : IOptionalValue<int> {
    return value.Read(usePayload);
}
public int ReadOptionalBranchThroughConstraint<U>(U value, bool usePayload)
    where U : IOptionalValue<int> {
    return value.ReadBranch(usePayload);
}
public int SelectOptionalConcreteThroughConstraint<U>(
    U value, bool usePayload, int offset) where U : IOptionalValue<int> {
    return value.SelectConcrete(usePayload, offset);
}
public interface IResetPair<T> {
    T ReadFirst();
    T ReadSecond();
    int ReadFirstCount();
    int ReadSecondCount();
    void Reset(bool resetFirst);
    void Store(bool shouldWrite, T item);
    void StorePair(bool writeFirst, T first, T second);
    void StoreOrClear(bool writeFirst, T item);
    void StoreIf(bool writeFirst, T item);
    void StoreMixed(bool writeFirst, T item, int amount);
    void StoreCount(bool shouldWrite, int amount);
}
public struct ResetPair<T> : IResetPair<T> {
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
    public void Reset(bool resetFirst) {
        if (resetFirst) { First = default; }
        else { Second = default; }
    }
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
            FirstCount = value + 1;
            SecondCount = FirstCount + value;
        } else {
            SecondCount = value * 2;
            FirstCount = SecondCount + value;
        }
    }
}
public int RunConditionalDefaultReset() {
    ResetPair<int> direct = new ResetPair<int>(2, 3);
    direct.Reset(true);
    if (direct.ReadFirst() != 0 || direct.ReadSecond() != 3) return -1;
    direct.Store(false, 4);
    if (direct.ReadFirst() != 0 || direct.ReadSecond() != 4) return -3;
    direct.StorePair(true, 11, 13);
    if (direct.ReadFirst() != 11 || direct.ReadSecond() != 13) return -7;
    direct.StoreOrClear(true, 31);
    if (direct.ReadFirst() != 31 || direct.ReadSecond() != 0) return -9;
    direct.StoreIf(false, 41);
    if (direct.ReadFirst() != 31 || direct.ReadSecond() != 0) return -11;
    direct.StoreIf(true, 43);
    if (direct.ReadFirst() != 43 || direct.ReadSecond() != 0) return -12;
    direct.StoreMixed(true, 41, 6);
    if (direct.ReadFirst() != 41 || direct.ReadSecond() != 0 ||
        direct.ReadFirstCount() != 7 || direct.ReadSecondCount() != 13) return -15;
    direct.StoreCount(true, 6);
    if (direct.ReadFirstCount() != 7 || direct.ReadSecondCount() != 13) return -5;
    ResetPair<int> source = new ResetPair<int>(5, 7);
    IResetPair<int> boxed = (IResetPair<int>)source;
    boxed.Reset(false);
    if (boxed.ReadFirst() != 5 || boxed.ReadSecond() != 0) return -2;
    boxed.Store(true, 17);
    if (boxed.ReadFirst() != 17 || boxed.ReadSecond() != 0) return -4;
    boxed.StorePair(false, 19, 23);
    if (boxed.ReadFirst() != 19 || boxed.ReadSecond() != 23) return -8;
    boxed.StoreOrClear(false, 37);
    if (boxed.ReadFirst() != 0 || boxed.ReadSecond() != 37) return -10;
    boxed.StoreIf(false, 47);
    if (boxed.ReadFirst() != 0 || boxed.ReadSecond() != 37) return -13;
    boxed.StoreIf(true, 53);
    if (boxed.ReadFirst() != 53 || boxed.ReadSecond() != 37) return -14;
    boxed.StoreMixed(false, 43, 8);
    if (boxed.ReadFirst() != 0 || boxed.ReadSecond() != 43 ||
        boxed.ReadFirstCount() != 24 || boxed.ReadSecondCount() != 16) return -16;
    boxed.StoreCount(false, 8);
    if (boxed.ReadFirstCount() != 24 || boxed.ReadSecondCount() != 16) return -6;
    return 0;
}
public class ConstrainedBox<T> {
    public int Value;
    public constructor(int value) { Value = value; }
    public int Read(bool useValue) const { return useValue ? Value : Value + 1; }
}
public int ReadConstrainedBox<U>(U value, bool useValue)
    where U : ConstrainedBox<int> {
    return value.Read(useValue);
}
public struct BoxedGenericComputedScore<T> : IBoxedGenericComputedScore<T> {
    public T Payload;
    public int Count;
    public constructor(T payload, int count) {
        Payload = payload;
        Count = count;
    }
    public T Echo { const get { return Payload; } }
    public int Score { const get { return Count + 1; } }
}
public struct BoxedGenericCounter<T> : IBoxedGenericCounter<T> {
    public int Count;
    public int Mirror;
    public constructor(int count) { Count = count; }
    public int Read() { return Count + Mirror; }
    public int ReadCount() { return Count; }
    public void Add(int amount) { Count += amount; }
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
    public void Adjust(bool add, int amount) {
        if (add) {
            Count += amount;
            Mirror = Count + 1;
        } else {
            Count -= amount;
            Mirror = Count - 1;
        }
    }
    public void AdjustIf(bool add, int amount) {
        if (add) {
            Count += amount;
            Mirror = Count + 1;
        }
    }
}
public struct GenericComputedScore<T> {
    public int Count;
    public constructor(int count) { Count = count; }
    public int Score { const get { return Count + 1; } }
    public int Adjusted { set { Count = value + 1; } }
}
public struct GenericAdjustedCounter<T> : IAdjustedCounter<T> {
    public int Count;
    public constructor(int count) { Count = count; }
    public int Adjusted { set { Count = value + 1; } }
    public int Read() const { return Count; }
}
public void ApplyGenericVoidMutation<T>(T value, int amount)
    where T : IGenericVoidMutation {
    value.Add(amount + 1);
    value.Add(amount > 0 ? amount : 0);
}
public void ApplyGenericVoidMutationConditional<T>(T value, int amount)
    where T : IGenericVoidMutation {
    if (amount > 0) {
        value.Add(amount * 2);
    } else {
        value.Add(3);
    }
}
public void ApplyGenericVoidMutationPair<T, U>(T first, U second, int amount)
    where T : IGenericVoidMutation
    where U : IGenericVoidMutation {
    first.Add(amount);
    second.Add(amount + 1);
}
public int SumGenericValueReaders<T, U>(T first, U second)
    where T : IGenericValueReader
    where U : IGenericValueReader {
    return first.Read() + second.Read();
}
public int SumGenericValueReadersLocal<T, U>(T first, U second)
    where T : IGenericValueReader
    where U : IGenericValueReader {
    int result = first.Read() + second.Read();
    return result;
}
public interface IBoxedGenericValue<T> {
    T ReadGenericValue();
    T ReadDefaultGenericValue(T ignored);
    void WriteGenericValue(T ignored, T value, int unused);
    void ClearGenericValue();
    void ClearPair();
    void SetValueAndCounters(T value, int amount);
    void WritePair(T first, T second);
    T ReadFirst();
    T ReadSecond();
    T EchoGenericValue(T first, T value, int ignored);
    T Current { get; set; }
    int ReadConstant(int ignored);
    int ReadCounter();
    void WriteCounter(int ignored, int value);
    void ResetCounter();
    int AddToCounter(int amount);
    void IncreaseCounter(int amount);
    int ReadOtherCounter();
    void InitializeCounters(int amount);
}
public interface ICompoundPropertyCounter { int Value { get; set; } }
public struct CompoundPropertyAmount {
    public int Value;
    public constructor(int value) { Value = value; }
    public void operator +=(int amount) { Value += amount; }
    public void operator ++() { Value++; }
}
public class CompoundPropertyHolder {
    public CompoundPropertyAmount Stored;
    public constructor(int value) { Stored = new CompoundPropertyAmount(value); }
    public CompoundPropertyAmount Current {
        get { return Stored; }
        set { Stored = value; }
    }
}
public interface IIndexedCompoundProperty {
    int Item { get(uint index); set(uint index, int value); }
}
public class IndexedCompoundProperty : IIndexedCompoundProperty {
    public int Stored;
    public uint IndexCalls;
    public uint NextIndex() { IndexCalls++; return IndexCalls; }
    public int Item {
        get(uint index) { return Stored; }
        set(uint index, int value) { Stored = value; }
    }
    public int Add(int amount) {
        Item[NextIndex()] += amount;
        return Stored;
    }
    public int PostIncrement() { return Item[NextIndex()]++; }
}
public class OrderedPropertyAssignment {
    public uint Trace;
    public int Stored;
    public OrderedPropertyAssignment GetReceiver() {
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
    public int Assign() { return GetReceiver().Item[NextIndex()] = NextValue(); }
}
public struct DirectPropertyStruct {
    public int Stored;
    public constructor(int value) { Stored = value; }
    public int Current {
        get { return Stored; }
        set { Stored = value; }
    }
}
public class CompoundPropertyCounter : ICompoundPropertyCounter {
    public int stored;
    public constructor(int value) { stored = value; }
    public int Value {
        get { return stored; }
        set { stored = value; }
    }
}
public struct GenericInterfaceStruct<T> : IBoxedGenericValue<T> {
    public T Value;
    public T First;
    public T Second;
    public int Counter;
    public int OtherCounter;
    public constructor(T value) { Value = value; }
    public T ReadGenericValue() { return Value; }
    public T ReadDefaultGenericValue(T ignored) { return default; }
    public void WriteGenericValue(T ignored, T result, int unused) { Value = result; }
    public void ClearGenericValue() { Value = default; }
    public void ClearPair() {
        First = default;
        Second = default;
    }
    public void SetValueAndCounters(T value, int amount) {
        Value = value;
        Counter = amount;
        OtherCounter = Counter + amount;
    }
    public void WritePair(T first, T second) {
        First = second;
        Second = first;
    }
    public T ReadFirst() { return First; }
    public T ReadSecond() { return Second; }
    public T EchoGenericValue(T ignored, T result, int unused) { return result; }
    public T Current { get { return Value; } set(value) { Value = value; } }
    public int ReadConstant(int ignored) { return 76; }
    public int ReadCounter() { return Counter; }
    public void WriteCounter(int ignored, int actual) { Counter = actual; }
    public void ResetCounter() { Counter = 4 - 4; }
    public int AddToCounter(int delta) { return Counter + delta; }
    public void IncreaseCounter(int delta) { Counter = Counter + delta; }
    public int ReadOtherCounter() { return OtherCounter; }
    public void InitializeCounters(int value) {
        Counter = value;
        OtherCounter = Counter + value;
    }
}
public concept CAdjustable {
    int Adjust(int amount);
}
public concept CResetOperations {
    void Reset(int value);
    void Add(int amount);
}
public concept CResetPrimary : CResetOperations { }
public concept CResetSecondary : CResetOperations { }
public concept CResettable : CResetPrimary, CResetSecondary {
    int Value { const get; set; }
    int Adjustment { const get; }
    void Merge(CResettable other);
    void Merge(CResettable[] others);
}
public concept CStringResettable {
    string ReadName() const;
    void Add(int amount);
}
public concept CUnsignedAccumulator {
    uint ReadCount() const;
    void Add(uint amount);
}
public class ConceptAddress {
    public int Value;
    public constructor(int value) { Value = value; }
    public int Score { const get => Value; }
    public int GetValue(int multiplier) const { return Value * multiplier; }
    public T Echo<T>(T value, int ignored) const { return value; }
    public bool operator ==(const CEquatable other) const { return Value == other.Value; }
    public bool operator >(const CEquatable other) const { return Value > other.Value; }
}
public class ConceptClassWrapper<T> : T = CScoredReadable {
    public int Read(T value) { return value.Score + value.GetValue(2); }
}
public class ConceptClassArithmetic<T> : T = CAddable {
    public T Add(T first, T second) { return first + second; }
}
public class ConceptClassVoidResetter<T> : T = CResettable {
    public void SetAndAdd(T value, int amount) {
        value.Value = amount + value.Adjustment;
        value.Add(amount + value.Adjustment);
    }
    public void AddTwice(T value, int amount) {
        value.Value += amount;
        value.Add(amount);
    }
    public void ApplyBuiltInCompounds(T value) {
        value.Value += 2;
        value.Value -= 1;
        value.Value *= 2;
        value.Value /= 3;
        value.Value %= 6;
    }
    public void Choose(T value, bool increase, int amount) {
        if (increase) {
            value.Value += amount;
            value.Add(amount);
        } else {
            value.Value -= amount;
            value.Add(amount);
        }
    }
    public void Drain(T value) {
        while (value.Value > 0) {
            value.Add(-1);
        }
    }
    public void DecrementOnce(T value) {
        do { value.Add(-1); } while (value.Value > 0);
    }
    public void SwitchAdd(T value, int amount, bool enabled) {
        switch (value.Value) {
            case 0 when enabled:
                value.Add(amount);
                break;
            case 0 when !enabled:
                value.Add(-amount);
                break;
            case 1:
                value.Add(10);
                break;
            default:
                value.Add(-amount);
                break;
        }
    }
    public void AddIndices(T value, int count) {
        for (int index = 0; index < count; index++) {
            value.Add(index);
        }
    }
    public void AddDescending(T value, int count) {
        for (int index = count; index > 0; index--) {
            value.Add(index);
        }
    }
    public void AddFromExistingCounter(T value, int count) {
        int index = 0;
        for (index = count; index > 0; index--) {
            value.Add(index);
        }
    }
    public void AddUnless(T value, bool stop) {
        if (stop) return;
        value.Add(3);
    }
    public void AddAll(T[] values, int amount) {
        foreach (var item in values) {
            item.Add(amount);
        }
    }
    public void AddEach(T value, int[] amounts) {
        foreach (var amount in amounts) {
            value.Add(amount);
        }
    }
    public void AddFromValue(T value, int amount) {
        int delta = value.Value + amount;
        delta -= amount;
        delta++;
        delta += 2;
        delta *= 2;
        delta /= 2;
        delta %= 7;
        ++delta;
        value.Add(delta);
    }
    public void AddWhenReady(T value, bool enabled) {
        bool shouldAdd = enabled && value.Value >= 0;
        if (shouldAdd) value.Add(2);
    }
    public void AddThroughCopy(T value, int amount) {
        T copy = value;
        copy.Add(amount);
    }
    public void ResetTwice(T value) {
        value.Reset(7);
        value.Reset(8);
    }
}
public class ConceptStringSwitcher<T> : T = CStringResettable {
    public void Apply(T value, int amount, bool enabled) {
        string name = value.ReadName();
        switch (name) {
            case "zero" when enabled:
                value.Add(amount);
                break;
            case "zero" when !enabled:
                value.Add(-amount);
                break;
            case null:
                value.Add(7);
                break;
            default:
                value.Add(-amount);
                break;
        }
    }
}
public class ConceptUnsignedAccumulatorRunner<T> : T = CUnsignedAccumulator {
    public void AddNext(T value, uint amount) {
        uint next = value.ReadCount();
        next++;
        value.Add(next + amount);
    }
}
public class ConstrainedEchoProvider {
    public T Echo<T>(T value) where T : struct, IMarker { return value; }
}
public class ConceptConstrainedEchoProvider {
    public U Echo<U>(U value) : U = CHasMarker { return value; }
}
public struct EchoMarker : IMarker {
    public int Value;
    public constructor(int value) { Value = value; }
}
public struct MarkedValue {
    public int Marker { const get; set; }
}
public struct ConceptBounds {
    public int Low { const get; set; }
    public int High { const get; set; }
    public constructor(int low, int high) { Low = low; High = high; }
}
public class ConceptBoundClass {
    public int Low { const get; set; }
    public int High { const get; set; }
    public constructor(int low, int high) { Low = low; High = high; }
}
public struct ConceptPoint {
    public int Value;
    public constructor(int value) { Value = value; }
    public int GetValue(int multiplier) const { return Value * multiplier; }
    public T Echo<T>(T value, int ignored) const { return value; }
    public bool operator ==(const CEquatable other) const { return Value == other.Value; }
    public bool operator >(const CEquatable other) const { return Value > other.Value; }
    public ConceptPoint operator +(const CAddable other) const {
        return new ConceptPoint(Value + other.Value);
    }
    public ConceptPoint operator ~() const { return new ConceptPoint(~Value); }
    public ConceptPoint operator -() const { return new ConceptPoint(-Value); }
}
public class ConceptClassNumber {
    public int Value;
    public constructor(int value) { Value = value; }
    public ConceptClassNumber operator -() const {
        return new ConceptClassNumber(-Value);
    }
}
public class ConceptClassCounter {
    public int Value;
    public constructor(int value) { Value = value; }
    public int Adjust(int amount) { Value += amount; return Value; }
}
public struct ConceptStructCounter {
    public int Value;
    public constructor(int value) { Value = value; }
    public int Adjust(int amount) { Value += amount; return Value; }
}
public class ConceptClassResettable {
    public int Value { const get; set; }
    public int Adjustment { const get { return 2; } }
    public void Reset(int value) { Value = value; }
    public void Add(int amount) { Value = Value + amount; }
    public void Merge(ConceptClassResettable other) { Value = Value + other.Value; }
    public void Merge(ConceptClassResettable[] others) {
        foreach (var other in others) { Value = Value + other.Value; }
    }
}
public class ConceptNamedResettable {
    public int Value;
    public string Name;
    public string ReadName() const { return Name; }
    public void Add(int amount) { Value = Value + amount; }
}
public class ConceptUnsignedAccumulator {
    public uint Value;
    public uint ReadCount() const { return Value; }
    public void Add(uint amount) { Value += amount; }
}
public struct ConceptStructResettable {
    public int Value { const get; set; }
    public int Adjustment { const get { return 2; } }
    public void Reset(int value) { Value = value; }
    public void Add(int amount) { Value = Value + amount; }
    public void Merge(ConceptStructResettable other) { Value = Value + other.Value; }
    public void Merge(ConceptStructResettable[] others) {
        foreach (var other in others) { Value = Value + other.Value; }
    }
}
public struct ConceptStructWrapper<T> : T = CResettable {
    public int ReadValue(T value) { return value.Value; }
    public void AddTo(T value, int amount) { value.Add(amount); }
    public void Drain(T value) {
        while (value.Value > 0) { value.Add(-1); }
    }
}
public struct ConceptTruth {
    public bool Value;
    public constructor(bool value) { Value = value; }
    public bool operator !() const { return !Value; }
}
public class AutoPropertyProbe {
    public int Value { const get; set; }
}
public static class ConceptUtils {
public static void UpdateValue<T>(T value, int amount) : T = CResettable {
        value.Value += amount;
    value.Value -= amount;
    value.Value *= amount;
    value.Value /= amount;
    value.Value %= amount;
    }
    public static void AddAdjustment<T>(T value, int amount) : T = CResettable {
        value.Value += value.Adjustment + amount;
    }
    public static T First<T, U>(T first, U second)
        : T = CComparable : U = CComparable {
        return first;
    }
    public static bool IsGreater<T>(T first, T second)
        : T = CEquatable, CComparable {
        return first > second;
    }
    public static T Max<T>(T first, T second)
        : T = CEquatable, CComparable {
        return (first > second) ? first : second;
    }
    public static T Add<T>(T first, T second) : T = CAddable {
        return first + second;
    }
    public static bool IsFalse<T>(T value) : T = CTruth {
        return !value;
    }
    public static T Invert<T>(T value) : T = CInvertible {
        return ~value;
    }
    public static T Negate<T>(T value) : T = CNegatable {
        return -value;
    }
    public static int Read<T>(T value, int multiplier) : T = CReadable {
        return value.GetValue(multiplier);
    }
    public static int ReadWithLiteral<T>(T value) : T = CReadable {
        return value.GetValue(2);
    }
    public static int Width<T>(T value) : T = CBounds {
        return value.High - value.Low;
    }
    public static void SetHigh<T>(T value, int delta) : T = CBounds {
        value.High = value.Low < 10
            ? value.Low + delta
            : value.Low - delta;
    }
    public static U Echo<T, U>(T value, U next) : T = CEcho {
        return value.Echo(next);
    }
    public static U EchoConstrained<T, U>(T value, U next) : T = CStructEcho {
        return value.Echo<U>(next);
    }
    public static U EchoConceptConstrained<T, U>(T value, U next)
        : T = CMarkedEcho {
        return value.Echo(next);
    }
    public static int ApplyAdjustment<T>(T value, int amount) : T = CAdjustable {
        return value.Adjust(amount);
    }
    public static void ResetValue<T>(T value, int next, bool add, int[] adjustments, T[] peers, T[][] peerRows) : T = CResettable {
        int adjusted = value.Value + next;
        for (int iteration = 0; iteration < 3; iteration = iteration + 1) {
            if (iteration == 0) continue;
            if (iteration == 2) break;
            adjusted = adjusted + 1;
        }
        int finalIteration = 0;
        do {
            adjusted = adjusted + 1;
            finalIteration = finalIteration + 1;
        } while (finalIteration < 1);
        foreach (int adjustment in adjustments) {
            adjusted = adjusted + adjustment;
        }
        value.Reset(next);
        bool shouldAdd = add;
        if (!shouldAdd) return;
        switch (next) {
            case 11:
                value.Value = adjusted;
                break;
            default:
                value.Value = adjusted + 1;
                break;
        }
        if (shouldAdd) {
            value.Value = adjusted + 1;
            value.Add(2);
        } else {
            value.Value = adjusted + 2;
        }
        foreach (T peer in peers) {
            value.Merge(peer);
        }
        foreach (var row in peerRows) {
            value.Merge(row);
        }
    }
}

public delegate int Transform(int value);

public static class Effects {
    public static int State;
    public static int Initialized = Initialize();

    public static int Initialize() {
        State = 7;
        return 42;
    }

    public static bool Mark(bool result, int digit) {
        State = State * 10 + digit;
        return result;
    }
}

public class StaticInitProbe {
    public static int Value = Initialize();
    public static int Runs;

    public static int Initialize() { return 40; }

    static constructor() {
        Value += 2;
        Runs += 1;
    }
}

public int FirstTransform(int value) {
    Effects.State = Effects.State * 10 + 1;
    return value + 1;
}

public class Level0 {
    public constructor() {}
    public int Number() { return 41; }
}
public class Level1 : Level0 { public constructor() : base() {} }
public class Level2 : Level1 { public constructor() : base() {} }
public class Level3 : Level2 { public constructor() : base() {} }
public class Level4 : Level3 { public constructor() : base() {} }

public class ReflectionBase {
    public ReflectionBase EchoBase(ReflectionBase value) { return value; }
    public virtual ReflectionBase VirtualEcho(ReflectionBase value) { return value; }
}

public class ReflectionTarget : ReflectionBase {
    public int Value;
    public static int Instances;
    public constructor() { Instances += 1; Value = 42; }
    public ReflectionTarget Echo(ReflectionTarget value) { return value; }
    public int AddValues(int left, int right) { return left + right; }
    public int EchoNumber(int value) { return value + 1; }
    public long EchoNumber(long value) { return value + 2; }
    public int IntOnly(int value) { return value; }
    public ReflectionPoint EchoPoint(ReflectionPoint value) { return value; }
    public static long DoubleValue(long value) { return value * 2; }
    public override ReflectionBase VirtualEcho(ReflectionBase value) { return value; }
    public static ReflectionTarget StaticEcho(ReflectionTarget value) { return value; }
    public void UseInterface(IReflectable value) { value.Mark(); }
    public IReflectable GetInterface() {
        Reflectable result = new Reflectable();
        result.Value = 74;
        return result;
    }
    public void SetValue(ReflectionTarget value) { Value = value.Value; }
}

public struct ReflectionPoint {
    public int X;
    public constructor(int x) { X = x; }
    public int Read(int offset) const { return X + offset; }
    public void Add(int amount) { X += amount; }
}

public struct GenericReflectionPoint<T> {
    public T Value;
    public T OtherValue;
    public int Counter = 17;
    public int OtherCounter;
    public constructor(T value) { Value = value; }
    public void SetValue(T value) { Value = value; }
    public void SetValueFromSecond(T ignored, T value) { Value = value; }
    public void SetPair(T first, T second) {
        Value = second;
        OtherValue = first;
    }
    public T GetValue() { return Value; }
    public void ResetValue() { Value = default; }
    public int AdjustCounter(int amount) { return Counter + amount; }
    public void UpdateCounter(int amount) {
        Counter = amount;
        OtherCounter = Counter + 1;
    }
}

public struct GenericDefaultField<T> {
    public T Value = default;
    public constructor() {}
}

public class GenericDefaultReference<T> where T : class {
    public T Value = default;
    public constructor() {}
}

public class ReflectionDerived : ReflectionTarget {
    public override ReflectionBase VirtualEcho(ReflectionBase value) { return this; }
}

public class ParameterizedReflectionTarget {
    public ReflectionBase Value;
    public constructor(ReflectionBase value) { Value = value; }
}

public class OtherReflectionTarget { public constructor() {} }

public interface IReadConstrainedValue { int ReadInterface(int offset); }
public interface IExtraConstrainedValue { int ReadExtra(); }
public interface IReadConstrainedProperty { int Current { const get; } }
public interface IWritableConstrainedValue {
    int Stored { get; set; }
    int StoredOther { get; set; }
}
public interface IIndexedConstrainedValue {
    int Item { const get(uint index); set(uint index); }
}
public interface IReadStructConstrainedValue { int ReadStruct(int offset); }
public interface IReadStructConstrainedProperty { int CurrentStruct { const get; } }
public interface IWritableStructConstrainedValue {
    int StoredStruct { get; set; }
    int StoredOtherStruct { get; set; }
}
public interface IIndexedStructConstrainedValue {
    int ItemStruct { const get(uint index); set(uint index); }
}
public interface IReadGenericStructValue { int ReadGeneric(); }
public interface ICountGenericStructValue { int CountGeneric { const get; } }
public interface IGenericStructStore { int Stored { get; set; } }
public struct MixedLayoutValue { public int Number; }
public struct AOuter<T> {
    public ZInner<T> Inner;
    public constructor(ZInner<T> initial) { Inner = initial; }
}
public struct ZInner<T> {
    public T Value;
    public constructor(T initial) { Value = initial; }
}
public int ReadNested(AOuter<int> value) { return value.Inner.Value; }
public int CreateNested() {
    AOuter<int> nestedOuter = new AOuter<int>(new ZInner<int>(37));
    return ReadNested(nestedOuter);
}
public class GenericMixedField<T> {
    public int Id = 7;
    public int Computed = (2 + 5) * 3;
    public int Index;
    public T Value;
    public MixedLayoutValue Extra;
    public constructor(int index, T value, MixedLayoutValue extra) {
        Index = index;
        Value = value;
        Extra = extra;
    }
}
public class GenericNullFields<T> where T : class {
    public T Value = null;
    public T[] Values = null;
}
public class GenericReferenceBase<T> where T : class {
    public int Number = 42;
    public T Value;
    public constructor() {}
}
public class GenericReferenceIntermediate<U> where U : class : GenericReferenceBase<U> {}
public class GenericReferenceDerived<T> where T : class : GenericReferenceIntermediate<T> {}
public bool IsNull<T>(T value) where T : class { return value == null; }
public interface INullMarker {}
public class NullMarker : INullMarker {}
public bool IsNullWithInterfaceConstraint<T>(T value) where T : INullMarker {
    return null == value;
}
public bool IsNullArray<T>(T[] values) { return values == null; }
public class GenericReferenceMixed<T> {
    public int Count = 40 + 2;
    public MixedLayoutValue Extra;
    public T Value;
    public int[] Values;
    public int CountProperty {
        get { return Count; }
        set { Count = value; }
    }
    public constructor(MixedLayoutValue extra, T value, int[] values) {
        Extra = extra;
        Value = value;
        Values = values;
    }
}
public struct GenericStructValue<T> : IReadGenericStructValue,
    ICountGenericStructValue, IGenericStructStore {
    public T Value;
    public int StoredValue;
    public constructor() {}
    public int ReadGeneric() { return 42; }
    public int CountGeneric { const get => 24; }
    public int Stored {
        get { return StoredValue; }
        set(value) { StoredValue = value; }
    }
}
public struct StructConstrainedValue : IReadStructConstrainedValue,
    IReadStructConstrainedProperty, IWritableStructConstrainedValue,
    IIndexedStructConstrainedValue {
    public int Value;
    public int ReadStruct(int offset) { return Value + offset; }
    public int CurrentStruct { const get => Value; }
    public int StoredStruct { get { return Value; } set(value) { Value = value; } }
    public int StoredOtherStruct { get; set; }
    public int ItemStruct {
        const get(uint index) { return Value; }
        set(uint index, int value) { Value = value; }
    }
}
public int ReadStructConstrained<T>(T value, int offset)
    where T : IReadStructConstrainedValue {
    return value.ReadStruct(offset + 1) + value.ReadStruct(offset + 2);
}
public int ReadStructConstrainedProperty<T>(T value)
    where T : IReadStructConstrainedProperty { return value.CurrentStruct; }
public void SetStructConstrainedProperty<T>(T value, int stored)
    where T : IWritableStructConstrainedValue {
    value.StoredStruct = stored;
    value.StoredOtherStruct = stored + 1;
}
public int ReadStructConstrainedIndexer<T>(T value, uint index)
    where T : IIndexedStructConstrainedValue { return value.ItemStruct[index]; }
public int ReadGenericStructConstrained<T>(T value)
    where T : IReadGenericStructValue { return value.ReadGeneric(); }
public int ReadGenericStructProperty<T>(T value)
    where T : ICountGenericStructValue { return value.CountGeneric; }
public int ReadGenericStructStored<T>(T value)
    where T : IGenericStructStore { return value.Stored; }
public void SetGenericStructStored<T>(T value, int stored)
    where T : IGenericStructStore { value.Stored = stored; }
public void SetStructConstrainedIndexer<T>(T value, uint index, int item)
    where T : IIndexedStructConstrainedValue { value.ItemStruct[index] = item; }
public class ConstrainedBase : IReadConstrainedValue, IExtraConstrainedValue,
    IReadConstrainedProperty, IWritableConstrainedValue, IIndexedConstrainedValue {
    public int Value;
    public int Read(int offset) { return Value + offset; }
    public int ReadInterface(int offset) { return Value + offset; }
    public int ReadExtra() { return 173; }
    public int Current { const get => Value; }
    public int Stored { get { return Value; } set(value) { Value = value; } }
    public int StoredOther { get; set; }
    public int Item {
        const get(uint index) { return Value; }
        set(uint index, int value) { Value = value; }
    }
}
public class ConstrainedDerived : ConstrainedBase {}
public class ObjectOverrideProbe {
    public override string ToString() const { return "override"; }
    public override bool Equals(object other) const { return true; }
    public override int GetHashCode() const { return 42; }
}
public int ReadConstrained<T>(T value, int offset) where T : ConstrainedBase {
    return value.Read(offset + 1) + value.Read(offset + 2);
}
public int ReadConstrainedClassLocal<T>(T value, int offset)
    where T : ConstrainedBase {
    int result = value.Read(offset + 1) + value.Read(offset + 2);
    return result;
}
public int ReadConstrainedField<T>(T value) where T : ConstrainedBase {
    return value.Value;
}
public int ReadConstrainedProperty<T>(T value) where T : ConstrainedBase {
    return value.Current;
}
public int ReadConstrainedClassProperty<T>(T value) where T : ConstrainedBase {
    return value.Stored;
}
public void SetConstrainedClassProperty<T>(T value, int stored)
    where T : ConstrainedBase {
    value.Stored = stored;
    value.StoredOther = stored + 1;
}
public int ReadConstrainedInterface<T>(T value, int offset) where T : IReadConstrainedValue {
    return value.ReadInterface(offset + 1) + value.ReadInterface(offset + 2);
}
public int ReadConstrainedInterfaceLocal<T>(T value, int offset)
    where T : IReadConstrainedValue {
    int result = value.ReadInterface(offset) + value.ReadInterface(offset + 1);
    return result;
}
public int ReadConstrainedSecondInterface<T>(T value)
    where T : IReadConstrainedValue, IExtraConstrainedValue {
    return value.ReadExtra();
}
public int ReadConstrainedInterfaceProperty<T>(T value) where T : IReadConstrainedProperty {
    return value.Current;
}
public void SetConstrainedInterfaceProperty<T>(T value, int stored)
    where T : IWritableConstrainedValue {
    value.Stored = stored;
    value.StoredOther = stored > 0 ? stored + 1 : 0;
}
public int ReadConstrainedIndexer<T>(T value, uint index)
    where T : IIndexedConstrainedValue { return value.Item[index]; }
public void SetConstrainedIndexer<T>(T value, uint index, int item)
    where T : IIndexedConstrainedValue { value.Item[index] = item; }
public void SetConstrainedMembers<T>(T value, int amount)
    where T : IWritableConstrainedValue, IIndexedConstrainedValue,
        IReadConstrainedValue {
    value.Stored = value.ReadInterface(amount) + value.StoredOther;
    value.Item[amount > 0 ? 1u : 0u] =
        value.ReadInterface(amount + 1) > 0 ? value.Stored : amount;
}

public class PrivateReflectionTarget {
    private constructor() {}
    public static TypeInfo GetTypeInfo() {
        PrivateReflectionTarget value = new PrivateReflectionTarget();
        return value.Type;
    }
}

public class GenericReflectionTarget<T> {
    public T Value;
    public int Counter;
    public constructor() {}
    public void SetValue(T value) { Value = value; }
    public T GetValue() { return Value; }
    public void SetCounter(int value) { Counter = value; }
    public int GetCounter() { return Counter; }
}

public class GenericDefaultChild<T> { public T Value; }
public class GenericDefaultHolder<T> {
    public GenericDefaultChild<T> Child = default;
    public constructor() {}
}
public struct GenericDefaultChildValue<T> {
    public T Value;
    public constructor() {}
}
public struct GenericDefaultHolderValue<T> {
    public GenericDefaultChildValue<T> Child = default;
    public constructor() {}
}

public T FirstNonNull<T>(T first, T second, T fallback) where T : class {
    return first ?? second ?? fallback;
}
public T[] FirstNonNullArray<T>(T[] first, T[] fallback) {
    return first ?? fallback;
}

public enum Shade { Dark, Light }
public Shade DefaultShade(Shade value = Shade.Light) { return value; }
public struct DefaultPoint { public int Value; }
public int DefaultInteger(int value = default) { return value; }
public DefaultPoint DefaultPointValue(DefaultPoint value = default) { return value; }
public int SelectStringSwitch(string value, bool enabled) {
    return value switch {
        "beta" when enabled => 11,
        "beta" => 12,
        null => 13,
        _ => 14,
    };
}
public int SelectStringSwitchStatement(string value) {
    int result = 0;
    switch (value) {
        case null:
            result = 13;
            break;
        case "beta":
            result = 11;
            break;
        default:
            result = 14;
            break;
    }
    return result;
}

public interface IBaseValue { int Get(); }
public interface ILeftValue : IBaseValue {}
public interface IRightValue : IBaseValue {}
public interface IDiamondValue : ILeftValue, IRightValue {}
public class DiamondValue : IDiamondValue {
    public constructor() {}
    public int Get() { return 42; }
}

public string Choose(string value) {
    return value ?? "fallback";
}

public T First<T>(T[] values) { return values[0]; }
public T AtThroughLocals<T>(T[] values, uint index) {
    T item = values[index];
    T copy = item;
    T result = copy;
    copy = result;
    return copy;
}
public T AtInferred<T>(T[] values, uint index) {
    var item = values[index];
    var copy = item;
    var result = copy;
    return result;
}
public uint Count<T>(T[] values) { return values.Length; }
public bool HasValues<T>(T[] values) { return values.Length > 0u; }
public bool IsAtMostTwo<T>(T[] values) { return 2u >= values.Length; }
public bool IsAtLeast<T>(T[] values, uint minimum) {
    return values.Length >= minimum;
}
public bool HasLengthIfPresent<T>(T[] values, uint minimum) {
    return values != null && values.Length >= minimum;
}
public bool IsNullOrLongEnough<T>(T[] values, uint minimum) {
    return values == null || values.Length >= minimum;
}
public void SetAt<T>(T[] values, int index, T value) { values[index] = value; }
public void CopyAt<T>(T[] source, T[] target, int sourceIndex, int targetIndex) {
    target[targetIndex] = source[sourceIndex];
}
public T[] Create<T>(uint length, uint index, T value) {
    T[] values = new T[length];
    values[index] = value;
    return values;
}
public T[] Copy<T>(T[] source) {
    T[] result = new T[source.Length];
    for (uint i = 0u; i < source.Length; i++) {
        result[i] = source[i];
    }
    return result;
}
public T[] CopyWhile<T>(T[] source) {
    T[] result = new T[source.Length];
    uint i = 0u;
    while (i < source.Length) {
        result[i] = source[i];
        i = i + 1u;
    }
    return result;
}
public T[] CopyForeach<T>(T[] source) {
    T[] result = new T[source.Length];
    uint index = 0u;
    foreach (T item in source) {
        result[index] = item;
        index++;
    }
    return result;
}
public T ElementAtLocal<T>(T[] values, uint index) {
    T item = values[index];
    return item;
}
public T Select<T>(bool chooseFirst, T first, T second) {
    return chooseFirst ? first : second;
}
public T SelectLocal<T>(bool chooseFirst, T first, T second) {
    var selected = chooseFirst ? first : second;
    return selected;
}
public struct OperatorBox<T> {
    public T value;
    public constructor(T initial) { value = initial; }
    public OperatorBox<T> operator +(OperatorBox<T> other) { return other; }
}
public struct GenericOperatorMethodBox {
    public int marker;
    public T operator +<T>(T other) where T : struct { return other; }
}
public interface IMutatingOperatorRecorder { void Record(); }
public class MutatingOperatorToken : IMutatingOperatorRecorder {
    public int Value;
    public constructor(int value) { Value = value; }
    public void Record() { MutatingOperatorOther.Records += 1; }
}
public class MutatingOperatorOther : IMutatingOperatorRecorder {
    public static int Records = 0;
    public void Record() { Records += 1; }
}
public struct MutatingGenericOperatorAccumulator {
    public int Value;
    public void operator +=<T>(T other) where T : IMutatingOperatorRecorder {
        other.Record();
    }
    public void operator +=<MutatingOperatorToken>(MutatingOperatorToken other) {
        Value += other.Value;
    }
    public void operator -=<T>(T other) where T : IMutatingOperatorRecorder {
        other.Record();
    }
    public void operator -=<MutatingOperatorToken>(MutatingOperatorToken other) {
        Value -= other.Value;
    }
    public void operator *=<T>(T other) where T : IMutatingOperatorRecorder {
        other.Record();
    }
    public void operator *=<MutatingOperatorToken>(MutatingOperatorToken other) {
        Value *= other.Value;
    }
    public void operator /=<T>(T other) where T : IMutatingOperatorRecorder {
        other.Record();
    }
    public void operator /=<MutatingOperatorToken>(MutatingOperatorToken other) {
        Value /= other.Value;
    }
    public void operator %=<T>(T other) where T : IMutatingOperatorRecorder {
        other.Record();
    }
    public void operator %=<MutatingOperatorToken>(MutatingOperatorToken other) {
        Value %= other.Value;
    }
}
public int RunMutatingGenericOperatorSpecialization() {
    MutatingGenericOperatorAccumulator custom = default;
    custom.Value = 42;
    custom -= new MutatingOperatorToken(32);
    if (custom.Value != 10 || MutatingOperatorOther.Records != 0) return 205;
    custom.Value = 10;
    custom += new MutatingOperatorToken(32);
    if (custom.Value != 42 || MutatingOperatorOther.Records != 0) return 196;
    custom.Value = 48;
    custom *= new MutatingOperatorToken(2);
    custom /= new MutatingOperatorToken(4);
    custom %= new MutatingOperatorToken(5);
    if (custom.Value != 4 || MutatingOperatorOther.Records != 0) return 207;
    MutatingGenericOperatorAccumulator fallback = default;
    fallback.Value = 9;
    fallback += new MutatingOperatorOther();
    if (fallback.Value != 9 || MutatingOperatorOther.Records != 1) return 197;
    fallback -= new MutatingOperatorOther();
    if (fallback.Value != 9 || MutatingOperatorOther.Records != 2) return 206;
    fallback *= new MutatingOperatorOther();
    fallback /= new MutatingOperatorOther();
    fallback %= new MutatingOperatorOther();
    if (fallback.Value != 9 || MutatingOperatorOther.Records != 5) return 208;
    return 0;
}
public class GenericOperatorMethodContainer<T> {
    public T marker;
    public U operator +<U>(U other) where U : struct { return other; }
}
public class GenericOperatorMarker {}
public interface IReflectable { void Mark(); }
public class Reflectable : IReflectable {
    public int Value;
    public void Mark() { Value = 73; }
}
public class OperatorClass<T> {
    public T value;
    public constructor(T initial) { value = initial; }
    public OperatorClass<T> operator +(OperatorClass<T> other) { return other; }
}
public extension IntExtensions : int {
    public int AddOne() const { return this + 1; }
    public int Next { const get => this + 1; }
}

public long ReadLongMaximum() {
    return 9223372036854775807L;
}

public long ReadLongMinimum() {
    return -9223372036854775807L - 1L;
}

public int CaptureParameter(int value) {
    int Read() { return value; }
    return Read();
}
public int GenericLocalIdentity() {
    T Identity<T>(T value) { return value; }
    int ReadValue<T>(T value, int offset) where T : IReadConstrainedValue {
        return value.ReadInterface(offset);
    }
    ConstrainedBase constrained = new ConstrainedBase();
    constrained.Value = 41;
    return Identity(42) + Identity<int>(0) + ReadValue(constrained, 1) - 42;
}
public T FirstNested<T>(T[][] values) { return values[0][0]; }
public uint FirstRowLength<T>(T[][] values) { return values[0].Length; }
public bool HasNonEmptyFirstRow<T>(T[][] values) {
    return values[0].Length > 0u;
}
public void SetNested<T>(T[][] values, int row, int column, T value) {
    values[row][column] = value;
}
public void CopyNested<T>(T[][] source, T[][] target, int row, int column) {
    target[row][column] = source[row][column];
}

public int RunBoxedStructReflection(ReflectionTarget target) {
    ReflectionPoint point = new ReflectionPoint(42);
    object boxedPoint = point;
    TypeInfo pointType = boxedPoint.Type;
    object[] methodArguments = new object[1];
    methodArguments[0] = 2;
    int read = (int)pointType.Invoke(boxedPoint, "Read", methodArguments);
    if (read != 44) return 1;
    methodArguments[0] = 3;
    pointType.Invoke(boxedPoint, "Add", methodArguments);
    ReflectionPoint changed = (ReflectionPoint)boxedPoint;
    if (changed.X != 45) return 2;
    object[] echoArguments = new object[1];
    echoArguments[0] = point;
    TypeInfo targetType = target.Type;
    ReflectionPoint echoed = (ReflectionPoint)targetType.Invoke(
        target, "EchoPoint", echoArguments);
    if (echoed.X != 42) return 3;
    return 0;
}

public int RunBoxedStructInterface() {
    BoxedStructRead boxedStructSource = new BoxedStructRead(59);
    IBoxedStructRead directlyConverted = boxedStructSource;
    if (directlyConverted.ReadBoxedValue() != 59) return 57;
    IBoxedStructRead explicitlyConverted = (IBoxedStructRead)boxedStructSource;
    if (explicitlyConverted.ReadBoxedValue() != 59) return 56;
    object boxedStructObject = boxedStructSource;
    IBoxedStructRead boxedStructInterface = (IBoxedStructRead)boxedStructObject;
    object recoveredBox = (object)boxedStructInterface;
    if (recoveredBox != boxedStructObject || !(boxedStructInterface is object)) return 58;
    return boxedStructInterface.ReadBoxedValue();
}

public int RunBoxedGenericStructInterface() {
    BoxedGenericStruct<int> source = new BoxedGenericStruct<int>(17);
    object boxed = source;
    IBoxedStructRead value = (IBoxedStructRead)boxed;
    object recovered = (object)value;
    if (recovered != boxed) return 62;
    return value.ReadBoxedValue();
}

public int RunBoxedComputedScore() {
    BoxedComputedScore<int> source = new BoxedComputedScore<int>(0);
    IBoxedComputedScore value = (IBoxedComputedScore)source;
    return value.Score;
}

public int RunBoxedGenericComputedScore() {
    BoxedGenericComputedScore<int> source = new BoxedGenericComputedScore<int>(42, 0);
    IBoxedGenericComputedScore<int> value = (IBoxedGenericComputedScore<int>)source;
    return value.Score + value.Echo;
}

public int RunConcreteArrayExtension() {
    int[] values = new int[1];
    int[] other = new int[2];
    values[0] = 37;
    if (values.Count != 1u || values + other != 3u) return 0;
    return values.FirstPlus(5);
}

public int RunConcreteArrayExtensionCompoundAssignment() {
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

public int RunConcreteArrayExtensionIncrementDecrement() {
    int[] values = new int[1];
    values[0] = 7;
    int[] previous = values++;
    if (previous[0] != 7) return 1;
    if (values[0] != 42) return 2;
    int[] current = --values;
    if (current[0] != 21) return 3;
    if (values[0] != 21) return 4;
    return 42;
}

public int RunGenericArrayExtensionCompoundAssignment() {
    string[] values = new string[1];
    string[] replacement = new string[1];
    replacement[0] = "replacement";
    values += replacement;
    values -= replacement;
    values *= replacement;
    values /= replacement;
    values %= replacement;
    values++;
    --values;
    return values.Length == 1u ? 42 : 0;
}

public int RunGenericArrayExtension() {
    int[] values = new int[1];
    values[0] = 42;
    return values.GenericCount == 1u ? values.GetFirstElement() : 0;
}

public int RunGenericPropertyLocalCopy() {
    GenericPropertyLocalCopy<int> value = new GenericPropertyLocalCopy<int>();
    value.Value = 42;
    return value.Copied;
}

public int RunClosedGenericClassArrayMethods() {
    GenericArrayStorage<int> ints = new GenericArrayStorage<int>(1u);
    ints.Set(0u, 41);
    if (ints.Length != 1u || ints.Get(0u) != 41) return 1;
    GenericArrayStorage<long> longs = new GenericArrayStorage<long>(2u);
    longs.Set(1u, 40L);
    if (longs.Length != 2u || longs.Get(1u) != 40L) return 2;
    return ints.Get(0u);
}

public int RunGenericConstructorExpression() {
    ConstructorExpressionPair<int> pair = new ConstructorExpressionPair<int>(40, 1);
    return pair.Value + pair.Count;
}

public int RunGenericJaggedArrayExtension() {
    int[][] values = new int[1][];
    values[0] = new int[1];
    values[0][0] = 42;
    return values.GetFirstNestedElement();
}

public int RunGenericArrayExtensionOperator() {
    string[][] first = new string[1][];
    string[][] second = new string[2][];
    return first + second == 3u ? 42 : 0;
}

public int RunConstructedGenericArrayExtension() {
    ArrayBox<int> box = new ArrayBox<int>();
    box.Value = 42;
    ArrayBox<int>[] values = new ArrayBox<int>[1];
    values[0] = box;
    return values.GetFirstBox().Value;
}

public int RunMultipleGenericArrayExtension() {
    TwoPart<int, string> pair = new TwoPart<int, string>();
    pair.First = 40;
    pair.Second = "value";
    TwoPart<int, string>[] values = new TwoPart<int, string>[1];
    values[0] = pair;
    return values.GetFirstPair().First + 2;
}

public int RunBoxedGenericCounterMutation() {
    BoxedGenericCounter<int> source = new BoxedGenericCounter<int>(3);
    IBoxedGenericCounter<int> value = (IBoxedGenericCounter<int>)source;
    value.Add(4);
    value.Adjust(true, 2);
    value.Adjust(false, 3);
    value.AdjustIf(false, 100);
    value.AdjustIf(true, 1);
    return value.Read();
}

public int RunBoxedGenericCounterWhileMutation() {
    BoxedGenericCounter<int> direct = new BoxedGenericCounter<int>(1);
    direct.FillTo(9, 2);
    if (direct.ReadCount() != 9) return -1;
    direct.BumpThrough(14, 2);
    if (direct.ReadCount() != 15) return -3;
    direct.FillFor(10, 3);
    if (direct.Read() != 21) return -4;
    BoxedGenericCounter<int> source = new BoxedGenericCounter<int>(2);
    IBoxedGenericCounter<int> value = (IBoxedGenericCounter<int>)source;
    value.FillTo(10, 2);
    value.BumpThrough(15, 2);
    value.FillFor(10, 3);
    return value.Read();
}

public int RunGenericComputedScore() {
    GenericComputedScore<int> value = new GenericComputedScore<int>(0);
    value.Adjusted = 40;
    return value.Score;
}

public int RunBoxedGenericComputedSetter() {
    GenericAdjustedCounter<int> source = new GenericAdjustedCounter<int>(0);
    IAdjustedCounter<int> value = (IAdjustedCounter<int>)source;
    value.Adjusted = 41;
    return value.Read();
}

public int RunConstructedGenericClassConstraint() {
    ConstrainedBox<int> value = new ConstrainedBox<int>(41);
    return ReadConstrainedBox(value, true);
}

public int RunClassConceptMember() {
    ConceptClassWrapper<ConceptAddress> wrapper =
        new ConceptClassWrapper<ConceptAddress>();
    ConceptAddress value = new ConceptAddress(21);
    return wrapper.Read(value);
}

public int RunClassConceptOperator() {
    ConceptClassArithmetic<ConceptPoint> arithmetic =
        new ConceptClassArithmetic<ConceptPoint>();
    ConceptPoint first = new ConceptPoint(6);
    ConceptPoint second = new ConceptPoint(13);
    return arithmetic.Add(first, second).Value;
}

public int RunClassConceptVoidCalls() {
    ConceptClassVoidResetter<ConceptClassResettable> resetter =
        new ConceptClassVoidResetter<ConceptClassResettable>();
    ConceptClassResettable value = new ConceptClassResettable();
    resetter.SetAndAdd(value, 5);
    if (value.Value != 14) return 166;
    resetter.ApplyBuiltInCompounds(value);
    if (value.Value != 4) return 168;
    resetter.AddTwice(value, 2);
    if (value.Value != 8) return 167;
    resetter.Choose(value, true, 3);
    if (value.Value != 14) return 169;
    resetter.Choose(value, false, 2);
    if (value.Value != 14) return 170;
    resetter.Drain(value);
    if (value.Value != 0) return 171;
    resetter.DecrementOnce(value);
    if (value.Value != -1) return 172;
    resetter.ResetTwice(value);
    return value.Value;
}

public int RunClassConceptSwitch() {
    ConceptClassVoidResetter<ConceptClassResettable> resetter =
        new ConceptClassVoidResetter<ConceptClassResettable>();
    ConceptClassResettable value = new ConceptClassResettable();
    resetter.SwitchAdd(value, 5, true);
    if (value.Value != 5) return 178;
    value.Value = 0;
    resetter.SwitchAdd(value, 5, false);
    if (value.Value != -5) return 179;
    value.Value = 1;
    resetter.SwitchAdd(value, 5, false);
    if (value.Value != 11) return 180;
    value.Value = 3;
    resetter.SwitchAdd(value, 5, false);
    return value.Value == -2 ? 0 : 181;
}

public int RunClassConceptStringSwitch() {
    ConceptStringSwitcher<ConceptNamedResettable> switcher =
        new ConceptStringSwitcher<ConceptNamedResettable>();
    ConceptNamedResettable value = new ConceptNamedResettable();
    value.Name = "zero";
    switcher.Apply(value, 5, true);
    if (value.Value != 5) return 190;
    switcher.Apply(value, 5, false);
    if (value.Value != 0) return 191;
    value.Name = null;
    switcher.Apply(value, 5, false);
    if (value.Value != 7) return 192;
    value.Name = "other";
    switcher.Apply(value, 5, false);
    return value.Value == 2 ? 0 : 193;
}

public uint RunClassConceptUnsignedLocal() {
    ConceptUnsignedAccumulatorRunner<ConceptUnsignedAccumulator> runner =
        new ConceptUnsignedAccumulatorRunner<ConceptUnsignedAccumulator>();
    ConceptUnsignedAccumulator value = new ConceptUnsignedAccumulator();
    runner.AddNext(value, 2u);
    return value.Value;
}

public int RunClassConceptCountedLoop() {
    ConceptClassVoidResetter<ConceptClassResettable> resetter =
        new ConceptClassVoidResetter<ConceptClassResettable>();
    ConceptClassResettable value = new ConceptClassResettable();
    resetter.AddIndices(value, 4);
    return value.Value;
}

public int RunClassConceptDescendingLoop() {
    ConceptClassVoidResetter<ConceptClassResettable> resetter =
        new ConceptClassVoidResetter<ConceptClassResettable>();
    ConceptClassResettable value = new ConceptClassResettable();
    resetter.AddDescending(value, 4);
    return value.Value;
}

public int RunClassConceptExistingCounterLoop() {
    ConceptClassVoidResetter<ConceptClassResettable> resetter =
        new ConceptClassVoidResetter<ConceptClassResettable>();
    ConceptClassResettable value = new ConceptClassResettable();
    resetter.AddFromExistingCounter(value, 3);
    return value.Value;
}

public int RunClassConceptConcreteArrayForeach() {
    ConceptClassVoidResetter<ConceptClassResettable> resetter =
        new ConceptClassVoidResetter<ConceptClassResettable>();
    ConceptClassResettable value = new ConceptClassResettable();
    int[] amounts = new int[3];
    amounts[0] = 2;
    amounts[1] = 3;
    amounts[2] = 5;
    resetter.AddEach(value, amounts);
    return value.Value;
}

public int RunClassConceptVoidEarlyReturn() {
    ConceptClassVoidResetter<ConceptClassResettable> resetter =
        new ConceptClassVoidResetter<ConceptClassResettable>();
    ConceptClassResettable value = new ConceptClassResettable();
    resetter.AddUnless(value, true);
    if (value.Value != 0) return 183;
    resetter.AddUnless(value, false);
    return value.Value;
}

public int RunClassConceptForeach() {
    ConceptClassVoidResetter<ConceptClassResettable> resetter =
        new ConceptClassVoidResetter<ConceptClassResettable>();
    ConceptClassResettable[] values = new ConceptClassResettable[2];
    values[0] = new ConceptClassResettable();
    values[1] = new ConceptClassResettable();
    resetter.AddAll(values, 2);
    return values[0].Value + values[1].Value;
}

public int RunClassConceptLocals() {
    ConceptClassVoidResetter<ConceptClassResettable> resetter =
        new ConceptClassVoidResetter<ConceptClassResettable>();
    ConceptClassResettable value = new ConceptClassResettable();
    value.Value = 5;
    resetter.AddFromValue(value, 3);
    return value.Value;
}

public int RunClassConceptBooleanLocal() {
    ConceptClassVoidResetter<ConceptClassResettable> resetter =
        new ConceptClassVoidResetter<ConceptClassResettable>();
    ConceptClassResettable value = new ConceptClassResettable();
    resetter.AddWhenReady(value, false);
    if (value.Value != 0) return 187;
    resetter.AddWhenReady(value, true);
    return value.Value;
}

public int RunClassConceptGenericLocal() {
    ConceptClassVoidResetter<ConceptClassResettable> resetter =
        new ConceptClassVoidResetter<ConceptClassResettable>();
    ConceptClassResettable value = new ConceptClassResettable();
    value.Value = 5;
    resetter.AddThroughCopy(value, 4);
    return value.Value;
}

public int RunStructConceptMembers() {
    ConceptStructWrapper<ConceptStructResettable> valueWrapper = default;
    ConceptStructResettable value;
    value.Value = 29;
    if (valueWrapper.ReadValue(value) != 29) return 173;
    valueWrapper.AddTo(value, 5);
    if (value.Value != 29) return 174;
    valueWrapper.Drain(value);
    if (value.Value != 29) return 176;
    ConceptStructWrapper<ConceptClassResettable> classWrapper = default;
    ConceptClassResettable reference = new ConceptClassResettable();
    reference.Value = 31;
    classWrapper.AddTo(reference, 5);
    int beforeDrain = classWrapper.ReadValue(reference);
    classWrapper.Drain(reference);
    if (classWrapper.ReadValue(reference) != 0) return 177;
    return beforeDrain;
}

public int RunRepeatedGenericFieldConstructor() {
    RepeatedGenericField<int> value = new RepeatedGenericField<int>(21);
    return value.First + value.Second;
}

public int RunGenericConditionalProperty() {
    if (RunConditionalDefaultReset() != 0) return -100;
    if (RunConcreteReturnBranch() != 0) return -101;
    OptionalValue<int> present = new OptionalValue<int>(true, 23, 5);
    OptionalValue<int> absent = new OptionalValue<int>(false, 99, 7);
    IOptionalValue<int> presentInterface = (IOptionalValue<int>)present;
    IOptionalValue<int> absentInterface = (IOptionalValue<int>)absent;
    return present.Value + absent.Value + present.ValueOrFallback +
        absent.ValueOrFallback + presentInterface.Value + absentInterface.Value +
        present.ConditionalCopy + absent.ConditionalCopy +
        presentInterface.ConditionalCopy + absentInterface.ConditionalCopy +
        present.FieldSelectionCopy + absent.FieldSelectionCopy +
        presentInterface.FieldSelectionCopy + absentInterface.FieldSelectionCopy +
        present.BooleanExpressionCopy + absent.BooleanExpressionCopy +
        presentInterface.BooleanExpressionCopy + absentInterface.BooleanExpressionCopy +
        presentInterface.Read(true) + absentInterface.Read(false) +
        present.Read(true) + absent.Read(false) +
        ReadOptionalThroughConstraint(present, true) +
        ReadOptionalThroughConstraint(absent, false) +
        present.ReadBranch(true) + absent.ReadBranch(false) +
        ReadOptionalBranchThroughConstraint(present, true) +
        ReadOptionalBranchThroughConstraint(absent, false);
}

public int RunConcreteReturnBranch() {
    OptionalValue<int> direct = new OptionalValue<int>(true, 23, 5);
    if (direct.SelectConcrete(true, 2) != 12 ||
        direct.SelectConcrete(false, 3) != 6) return -1;
    IOptionalValue<int> boxed = (IOptionalValue<int>)direct;
    if (boxed.SelectConcrete(true, 4) != 14 ||
        boxed.SelectConcrete(false, 5) != 10) return -2;
    if (SelectOptionalConcreteThroughConstraint(direct, true, 6) != 16 ||
        SelectOptionalConcreteThroughConstraint(direct, false, 7) != 14) return -3;
    return 0;
}

public int RunBoxedGenericInterface() {
    GenericInterfaceStruct<int> direct = new GenericInterfaceStruct<int>(0);
    direct.SetValueAndCounters(21, 4);
    if (direct.Value != 21 || direct.Counter != 4 || direct.OtherCounter != 8)
        return -14;
    if (direct.ReadDefaultGenericValue(0) != 0) return -15;
    GenericInterfaceStruct<int> source = new GenericInterfaceStruct<int>(73);
    object boxed = source;
    IBoxedGenericValue<int> value = (IBoxedGenericValue<int>)boxed;
    if (value.ReadDefaultGenericValue(0) != 0) return -12;
    value.Current = 75;
    if (value.Current != 75) return -1;
    if (value.EchoGenericValue(0, 74, 0) != 74) return -4;
    value.WriteGenericValue(0, 74, 0);
    value.ClearGenericValue();
    if (value.ReadGenericValue() != 0) return -10;
    value.WriteGenericValue(0, 74, 0);
    value.WritePair(31, 47);
    if (value.ReadFirst() != 47 || value.ReadSecond() != 31) return -9;
    value.ClearPair();
    if (value.ReadFirst() != 0 || value.ReadSecond() != 0) return -11;
    value.SetValueAndCounters(19, 5);
    if (value.ReadGenericValue() != 19 || value.ReadCounter() != 5 ||
        value.ReadOtherCounter() != 10) return -13;
    value.WriteGenericValue(0, 74, 0);
    if (value.ReadConstant(1) != 76) return -2;
    value.WriteCounter(0, 88);
    if (value.ReadCounter() != 88) return -3;
    value.ResetCounter();
    if (value.ReadCounter() != 0) return -5;
    if (value.AddToCounter(6) != 6) return -6;
    value.IncreaseCounter(9);
    if (value.ReadCounter() != 9) return -7;
    value.InitializeCounters(11);
    if (value.ReadCounter() != 11 || value.ReadOtherCounter() != 22) return -8;
    return value.ReadGenericValue();
}

public int Main(string[] args) {
    if (args.Length != 2u) return 21;
    if (RunConcreteArrayExtension() != 42) return 212;
    if (RunConcreteArrayExtensionCompoundAssignment() != 42) return 221;
    if (RunConcreteArrayExtensionIncrementDecrement() != 42)
        return RunConcreteArrayExtensionIncrementDecrement();
    if (RunGenericArrayExtensionCompoundAssignment() != 42) return 222;
    if (RunGenericArrayExtension() != 42) return 213;
    if (RunGenericPropertyLocalCopy() != 42) return 219;
    if (RunClosedGenericClassArrayMethods() != 41) return 223;
    if (RunGenericPropertyLocalCopyThroughInterface() != 126) return 220;
    if (RunGenericConstructorExpression() != 42) return 214;
    if (RunGenericJaggedArrayExtension() != 42) return 215;
    if (RunGenericArrayExtensionOperator() != 42) return 218;
    if (RunConstructedGenericArrayExtension() != 42) return 216;
    if (RunMultipleGenericArrayExtension() != 42) return 217;
    if (RunGenericSpecialization() != 0) return 171;
    if (RunRecursiveSpecialization() != 42) return 174;
    if (RunSpecializationShapes() != 31) return 175;
    if (RunGenericMethodSpecialization() != 0) return 172;
    if (RunGenericContainingTypeSpecialization() != 0) return 183;
    if (RunGenericStaticContainingTypeSpecialization() != 0) return 186;
    if (RunGenericExtensionSpecialization() != 42) return 187;
    if (RunClosedGenericExtensionSpecialization() != 42) return 188;
    if (RunGenericStructExtensionSpecialization() != 42) return 200;
    if (RunGenericInterfaceExtensionSpecialization() != 42) return 202;
    if (RunGenericEnumExtensionSpecialization() != 42) return 204;
    if (RunGenericClosedInterfaceExtensionSpecialization() != 42) return 210;
    if (RunGenericLocalSpecialization() != 42) return 190;
    if (RunGenericOperatorSpecialization() != 42) return 192;
    if (RunMutatingGenericOperatorSpecialization() != 0) return 198;
    if (RunGenericPrimitiveExtensionSpecialization() != 42) return 195;
    GenericDefaultField<int> defaultIntField = new GenericDefaultField<int>();
    GenericDefaultReference<ReflectionTarget> defaultReferenceField =
        new GenericDefaultReference<ReflectionTarget>();
    if (defaultIntField.Value != 0 || defaultReferenceField.Value != null) return 130;
    GenericDefaultHolder<int> genericDefaultHolder =
        new GenericDefaultHolder<int>();
    GenericDefaultHolderValue<int> genericDefaultValueHolder =
        new GenericDefaultHolderValue<int>();
    if (genericDefaultHolder.Child != null ||
        genericDefaultValueHolder.Child.Value != 0) return 155;
    CompoundPropertyCounter propertyCounter = new CompoundPropertyCounter(10);
    propertyCounter.Value += 32;
    if (propertyCounter.Value != 42) return 143;
    ICompoundPropertyCounter interfacePropertyCounter = propertyCounter;
    interfacePropertyCounter.Value += 1;
    if (propertyCounter.Value != 43) return 144;
    int oldInterfaceValue = interfacePropertyCounter.Value++;
    if (oldInterfaceValue != 43 || propertyCounter.Value != 44) return 148;
    CompoundPropertyHolder compoundPropertyHolder = new CompoundPropertyHolder(5);
    compoundPropertyHolder.Current += 7;
    if (compoundPropertyHolder.Current.Value != 12) return 145;
    CompoundPropertyAmount oldAmount = compoundPropertyHolder.Current++;
    if (oldAmount.Value != 12 || compoundPropertyHolder.Current.Value != 13) return 149;
    ++compoundPropertyHolder.Current;
    if (compoundPropertyHolder.Current.Value != 14) return 150;
    IndexedCompoundProperty indexedCompoundProperty = new IndexedCompoundProperty();
    indexedCompoundProperty.Stored = 8;
    if (indexedCompoundProperty.Add(9) != 17 ||
        indexedCompoundProperty.IndexCalls != 1u) return 146;
    IIndexedCompoundProperty indexedCompoundInterface = indexedCompoundProperty;
    indexedCompoundInterface.Item[indexedCompoundProperty.NextIndex()] += 3;
    if (indexedCompoundProperty.Stored != 20 ||
        indexedCompoundProperty.IndexCalls != 2u) return 147;
    int oldIndexedValue = indexedCompoundProperty.PostIncrement();
    if (oldIndexedValue != 20 || indexedCompoundProperty.Stored != 21 ||
        indexedCompoundProperty.IndexCalls != 3u) return 151;
    OrderedPropertyAssignment orderedPropertyAssignment =
        new OrderedPropertyAssignment();
    int assignedPropertyValue = orderedPropertyAssignment.Assign();
    if (assignedPropertyValue != 99 || orderedPropertyAssignment.Stored != 99 ||
        orderedPropertyAssignment.Trace != 123u) return 152;
    DirectPropertyStruct directPropertyStruct = new DirectPropertyStruct(7);
    directPropertyStruct.Current = 29;
    if (directPropertyStruct.Stored != 29 || directPropertyStruct.Current != 29)
        return 153;
    directPropertyStruct.Current += 4;
    if (directPropertyStruct.Stored != 33) return 154;
    if (RunBoxedStructInterface() != 59) return 140;
    if (RunBoxedGenericStructInterface() != 61) return 141;
    if (RunBoxedComputedScore() != 1) return 156;
    if (RunBoxedGenericComputedScore() != 43) return 157;
    if (RunBoxedGenericCounterMutation() != 15) return 197;
    if (RunBoxedGenericCounterWhileMutation() != 21) return 198;
    if (RunGenericComputedScore() != 42) return 158;
    if (RunBoxedGenericComputedSetter() != 42) return 161;
    if (RunConstructedGenericClassConstraint() != 41) return 162;
    if (RunClassConceptMember() != 63) return 163;
    if (RunClassConceptOperator() != 19) return 164;
    if (RunClassConceptVoidCalls() != 8) return 165;
    if (RunClassConceptSwitch() != 0) return 181;
    if (RunClassConceptStringSwitch() != 0) return 194;
    if (RunClassConceptUnsignedLocal() != 3u) return 196;
    if (RunClassConceptCountedLoop() != 6) return 182;
    if (RunClassConceptDescendingLoop() != 10) return 195;
    if (RunClassConceptExistingCounterLoop() != 6) return 198;
    if (RunClassConceptConcreteArrayForeach() != 10) return 196;
    if (RunClassConceptVoidEarlyReturn() != 3) return 184;
    if (RunClassConceptForeach() != 4) return 185;
    if (RunClassConceptLocals() != 7) return 186;
    if (RunClassConceptBooleanLocal() != 2) return 188;
    if (RunClassConceptGenericLocal() != 9) return 189;
    if (RunStructConceptMembers() != 36) return 175;
    if (RunRepeatedGenericFieldConstructor() != 42) return 159;
    if (RunGenericConditionalProperty() != 516) return 160;
    if (RunBoxedGenericInterface() != 74) return 142;
    string combinedString = "A" + "€" + "😀";
    if (combinedString.Length != 3u || combinedString != "A€😀") return 98;
    string nullCombined = null + "tail";
    if (nullCombined != "tail") return 99;
    string nullSuffix = "head" + null;
    if (nullSuffix != "head") return 100;
    if (args[0].Length != 5u) return 22;
    if (args[1].Length != 4u) return 23;
    int[] copyInput = new int[3];
    copyInput[0] = 7;
    copyInput[1] = 13;
    copyInput[2] = 21;
    int[] copyResult = CopyWhile(copyInput);
    if (copyResult.Length != 3u || copyResult[0] != 7 ||
        copyResult[1] != 13 || copyResult[2] != 21) return 112;
    long[] longCopyInput = new long[2];
    longCopyInput[0] = 9000000001L;
    longCopyInput[1] = 17000000003L;
    long[] longCopyResult = CopyWhile(longCopyInput);
    if (longCopyResult.Length != 2u || longCopyResult[0] != 9000000001L ||
        longCopyResult[1] != 17000000003L) return 113;
    int[] foreachCopyResult = CopyForeach(copyInput);
    if (foreachCopyResult.Length != 3u || foreachCopyResult[0] != 7 ||
        foreachCopyResult[1] != 13 || foreachCopyResult[2] != 21) return 114;
    if (ElementAtLocal(copyInput, 1u) != 13) return 115;
    if (ElementAtLocal(longCopyInput, 1u) != 17000000003L) return 116;
    int[][] jagged = new int[1][];
    jagged[0] = new int[3];
    SetNested(jagged, 0, 0, 53);
    int[][] jaggedCopy = new int[1][];
    jaggedCopy[0] = new int[3];
    CopyNested(jagged, jaggedCopy, 0, 0);
    if (FirstNested(jaggedCopy) != 53) return 104;
    if (FirstRowLength(jaggedCopy) != 3u) return 105;
    if (!HasNonEmptyFirstRow(jaggedCopy)) return 106;
    ConceptAddress firstAddress = new ConceptAddress(4);
    ConceptAddress secondAddress = new ConceptAddress(9);
    if (!ConceptUtils.IsGreater(secondAddress, firstAddress)) return 94;
    ConceptAddress maximumAddress = ConceptUtils.Max(firstAddress, secondAddress);
    if (maximumAddress.Value != 9) return 90;
    ConceptPoint firstPoint = new ConceptPoint(6);
    ConceptPoint secondPoint = new ConceptPoint(13);
    ConceptAddress selectedAddress = ConceptUtils.First(secondAddress, firstPoint);
    if (selectedAddress.Value != 9) return 96;
    ConceptPoint selectedPoint = ConceptUtils.First(secondPoint, firstAddress);
    if (selectedPoint.Value != 13) return 97;
    if (!ConceptUtils.IsGreater(secondPoint, firstPoint)) return 95;
    ConceptPoint maximumPoint = ConceptUtils.Max(firstPoint, secondPoint);
    if (maximumPoint.Value != 13) return 91;
    ConceptPoint sumPoint = ConceptUtils.Add(firstPoint, secondPoint);
    if (sumPoint.Value != 19) return 92;
    ConceptPoint invertedPoint = ConceptUtils.Invert(firstPoint);
    if (invertedPoint.Value != ~6) return 88;
    ConceptPoint negatedPoint = ConceptUtils.Negate(firstPoint);
    if (negatedPoint.Value != -6) return 98;
    ConceptClassNumber classNumber = new ConceptClassNumber(7);
    ConceptClassNumber negatedClassNumber = ConceptUtils.Negate(classNumber);
    if (negatedClassNumber.Value != -7) return 99;
    ConceptTruth trueValue = new ConceptTruth(true);
    ConceptTruth falseValue = new ConceptTruth(false);
    if (ConceptUtils.IsFalse(trueValue) || !ConceptUtils.IsFalse(falseValue)) return 89;
    if (ConceptUtils.Read(firstAddress, 1) != 4 ||
        ConceptUtils.Read(firstPoint, 1) != 6 ||
        ConceptUtils.ReadWithLiteral(firstAddress) != 8 ||
        ConceptUtils.ReadWithLiteral(firstPoint) != 12) return 87;
    if (ConceptUtils.Echo(firstAddress, 17) != 17 ||
        ConceptUtils.Echo(firstPoint, 19L) != 19L) return 86;
    ConstrainedEchoProvider echoProvider = new ConstrainedEchoProvider();
    EchoMarker echoMarker = new EchoMarker(23);
    if (ConceptUtils.EchoConstrained(echoProvider, echoMarker).Value != 23) return 85;
    ConceptConstrainedEchoProvider conceptEchoProvider =
        new ConceptConstrainedEchoProvider();
    MarkedValue markedValue;
    MarkedValue echoedMarkedValue =
        ConceptUtils.EchoConceptConstrained(conceptEchoProvider, markedValue);
    if (echoedMarkedValue.Marker != 0) return 83;
    ConceptClassCounter classCounter = new ConceptClassCounter(3);
    ConceptStructCounter structCounter = new ConceptStructCounter(4);
    if (ConceptUtils.ApplyAdjustment(classCounter, 5) != 8 || classCounter.Value != 8 ||
        ConceptUtils.ApplyAdjustment(structCounter, 6) != 10 || structCounter.Value != 4)
        return 109;
    ConceptClassResettable compoundClass = new ConceptClassResettable();
    compoundClass.Value = 5;
    ConceptStructResettable compoundStruct;
    compoundStruct.Value = 7;
    ConceptUtils.UpdateValue(compoundClass, 3);
    ConceptUtils.UpdateValue(compoundStruct, 4);
    ConceptUtils.AddAdjustment(compoundClass, 1);
    ConceptUtils.AddAdjustment(compoundStruct, 1);
    if (compoundClass.Value != 5 || compoundStruct.Value != 7) return 157;
    ConceptClassResettable classResettable = new ConceptClassResettable();
    ConceptStructResettable structResettable;
    structResettable.Value = 0;
    int[] adjustments = new int[2];
    adjustments[0] = 1;
    adjustments[1] = 2;
    ConceptClassResettable[] classPeers = new ConceptClassResettable[1];
    classPeers[0] = new ConceptClassResettable();
    classPeers[0].Value = 3;
    ConceptStructResettable[] structPeers = new ConceptStructResettable[1];
    structPeers[0].Value = 5;
    ConceptClassResettable[][] classPeerRows = new ConceptClassResettable[1][];
    classPeerRows[0] = classPeers;
    ConceptStructResettable[][] structPeerRows = new ConceptStructResettable[1][];
    structPeerRows[0] = structPeers;
    ConceptUtils.ResetValue(classResettable, 11, true, adjustments, classPeers, classPeerRows);
    ConceptUtils.ResetValue(structResettable, 13, false, adjustments, structPeers, structPeerRows);
    if (classResettable.Value != 25 || structResettable.Value != 0) return 110;
    AutoPropertyProbe autoProperty = new AutoPropertyProbe();
    autoProperty.Value = 17;
    if (autoProperty.Value != 17) return 93;
    ConceptBounds bounds = new ConceptBounds(4, 11);
    ConceptBoundClass classBounds = new ConceptBoundClass(7, 19);
    ConceptUtils.SetHigh(bounds, 3);
    ConceptUtils.SetHigh(classBounds, 5);
    if (ConceptUtils.Width(bounds) != 7 || ConceptUtils.Width(classBounds) != 5)
        return 111;
    if (SelectStringSwitch(args[1], true) != 11 ||
        SelectStringSwitch(args[1], false) != 12 ||
        SelectStringSwitch(null, true) != 13 ||
        SelectStringSwitch(args[0], true) != 14 ||
        SelectStringSwitchStatement(args[1]) != 11 ||
        SelectStringSwitchStatement(null) != 13) return 84;
    if (Effects.Initialized != 42 || Effects.State != 7) return 35;
    if (StaticInitProbe.Value != 42 || StaticInitProbe.Runs != 1) return 59;
    StaticInitProbe probe = new StaticInitProbe();
    if (probe == null || StaticInitProbe.Runs != 1) return 60;
    if (CaptureParameter(42) != 42 || GenericLocalIdentity() != 42) return 34;
    ConstrainedDerived constrained = new ConstrainedDerived();
    constrained.Value = 151;
    if (constrained.ToString().Length != 18u) return 73;
    if (!constrained.Equals(constrained) ||
        constrained.GetHashCode() != constrained.GetHashCode()) return 74;
    ConstrainedDerived otherConstrained = new ConstrainedDerived();
    if (constrained.Equals(otherConstrained)) return 75;
    ObjectOverrideProbe overrideProbe = new ObjectOverrideProbe();
    object objectReference = overrideProbe;
    if (objectReference.ToString().Length != 8u ||
        !objectReference.Equals(null) || objectReference.GetHashCode() != 42) return 76;
    string firstString = new string(3u, 'x');
    string equalString = new string(3u, 'x');
    string differentString = new string(2u, 'x');
    if (firstString != equalString || !(firstString == equalString) ||
        firstString == differentString) return 77;
    string lowerString = "alpha";
    string higherString = "beta";
    string unicodeHigherString = "ä";
    if (!(lowerString < higherString) || !(lowerString <= lowerString) ||
        !(higherString > lowerString) || !(higherString >= higherString) ||
        !("z" < unicodeHigherString)) return 92;
    object stringObject = firstString;
    if (!stringObject.Equals(equalString)) return 78;
    if (stringObject.GetHashCode() != equalString.GetHashCode()) return 79;
    if (stringObject.ToString() != firstString) return 80;
    if (ReadConstrained(constrained, 2) != 309 ||
        ReadConstrainedField(constrained) != 151 ||
        ReadConstrainedProperty(constrained) != 151 ||
        ReadConstrainedInterface(constrained, 3) != 311 ||
        ReadConstrainedClassLocal(constrained, 2) != 309 ||
        ReadConstrainedInterfaceLocal(constrained, 3) != 309 ||
        ReadConstrainedSecondInterface(constrained) != 173 ||
        ReadConstrainedInterfaceProperty(constrained) != 151) return 66;
    SetConstrainedClassProperty(constrained, 155);
    if (ReadConstrainedClassProperty(constrained) != 155 ||
        constrained.StoredOther != 156) return 69;
    SetConstrainedInterfaceProperty(constrained, 160);
    SetConstrainedIndexer(constrained, 0u, 170);
    if (constrained.Stored != 170 || constrained.StoredOther != 161 ||
        ReadConstrainedIndexer(constrained, 0u) != 170)
        return 67;
    ConstrainedDerived memberWrites = new ConstrainedDerived();
    SetConstrainedMembers(memberWrites, 10);
    if (memberWrites.Stored != 10 || memberWrites.Value != 10) return 111;
    StructConstrainedValue structConstrained;
    structConstrained.Value = 180;
    structConstrained.StoredOtherStruct = 0;
    if (ReadStructConstrained(structConstrained, 3) != 369 ||
        ReadStructConstrainedProperty(structConstrained) != 180 ||
        ReadStructConstrainedIndexer(structConstrained, 0u) != 180 ||
        structConstrained.StoredOtherStruct != 0) return 68;
    SetStructConstrainedProperty(structConstrained, 190);
    SetStructConstrainedIndexer(structConstrained, 0u, 190);
    GenericStructValue<int> genericStructConstrained;
    genericStructConstrained.Value = 23;
    genericStructConstrained.StoredValue = 0;
    SetGenericStructStored(genericStructConstrained, 61);
    if (ReadGenericStructConstrained(genericStructConstrained) != 42) return 102;
    if (ReadGenericStructProperty(genericStructConstrained) != 24) return 103;
    if (ReadGenericStructStored(genericStructConstrained) != 0) return 104;
    GenericVoidMutation classVoidMutation = new GenericVoidMutation();
    classVoidMutation.Value = 10;
    ApplyGenericVoidMutation(classVoidMutation, 4);
    if (classVoidMutation.Value != 19) return 105;
    ApplyGenericVoidMutationConditional(classVoidMutation, 2);
    if (classVoidMutation.Value != 23) return 132;
    ApplyGenericVoidMutationConditional(classVoidMutation, 0);
    if (classVoidMutation.Value != 26) return 133;
    GenericVoidMutationValue structVoidMutation;
    structVoidMutation.Value = 10;
    ApplyGenericVoidMutation(structVoidMutation, 4);
    if (structVoidMutation.Value != 10) return 106;
    classVoidMutation.Value = 2;
    structVoidMutation.Value = 3;
    ApplyGenericVoidMutationPair(classVoidMutation, structVoidMutation, 5);
    if (classVoidMutation.Value != 7 || structVoidMutation.Value != 3) return 107;
    GenericValueReader classReader = new GenericValueReader();
    classReader.Value = 11;
    GenericValueReaderValue structReader;
    structReader.Value = 13;
    if (SumGenericValueReaders(classReader, structReader) != 24 ||
        SumGenericValueReadersLocal(classReader, structReader) != 24) return 108;
    MixedLayoutValue mixedValue;
    mixedValue.Number = 29;
    GenericMixedField<MixedLayoutValue> mixedLayout =
        new GenericMixedField<MixedLayoutValue>(23, mixedValue, mixedValue);
    if (mixedLayout.Id != 7 || mixedLayout.Computed != 21 || mixedLayout.Index != 23 ||
        mixedLayout.Extra.Number != 29) return 85;
    mixedLayout.Id = 31;
    if (mixedLayout.Id != 31 || mixedLayout.Value.Number != 29) return 83;
    int[] referenceValues = new int[1];
    referenceValues[0] = 31;
    GenericReferenceMixed<string> mixedReferenceLayout =
        new GenericReferenceMixed<string>(mixedValue, "reference", referenceValues);
    mixedReferenceLayout.CountProperty = 43;
    if (mixedReferenceLayout.Count != 43 || mixedReferenceLayout.CountProperty != 43 ||
        mixedReferenceLayout.Value != "reference" ||
        mixedReferenceLayout.Extra.Number != 29 ||
        mixedReferenceLayout.Values[0] != 31) return 88;
    if (CreateNested() != 37) return 86;
    GenericNullFields<string> nullFields = new GenericNullFields<string>();
    if (nullFields.Value != null || nullFields.Values != null) return 87;
    GenericReferenceDerived<string> genericDerived =
        new GenericReferenceDerived<string>();
    GenericReferenceBase<string> genericBase = genericDerived;
    GenericReferenceDerived<string> nullDerived = null;
    NullMarker marker = new NullMarker();
    NullMarker missingMarker = null;
    int[] nullCheckArray = new int[0];
    int[] nullCheckMissingArray = null;
    genericDerived.Value = "derived";
    if (genericBase.Number != 42 || genericBase.Value != "derived" ||
        genericDerived.Value != "derived" ||
        !(genericDerived is GenericReferenceBase<string>) ||
        IsNull(genericDerived) || !IsNull(nullDerived) ||
        IsNullWithInterfaceConstraint(marker) ||
        !IsNullWithInterfaceConstraint(missingMarker) ||
        IsNullArray(nullCheckArray) || !IsNullArray(nullCheckMissingArray)) return 89;
    if (DefaultShade() != Shade.Light) return 48;
    if (DefaultInteger() != 0 || DefaultPointValue().Value != 0) return 101;
    int[] genericValues = new int[1];
    genericValues[0] = 37;
    if (First(genericValues) != 37) return 40;
    if (Count(genericValues) != 1u) return 49;
    int[] emptyGenericValues = new int[0];
    int[] missingGenericValues = null;
    if (HasValues(emptyGenericValues) || !HasValues(genericValues) ||
        !IsAtMostTwo(genericValues) || !IsAtLeast(genericValues, 1u)) return 64;
    if (!HasLengthIfPresent(genericValues, 1u) ||
        HasLengthIfPresent(missingGenericValues, 0u) ||
        !IsNullOrLongEnough(missingGenericValues, 4u) ||
        IsNullOrLongEnough(genericValues, 4u)) return 155;
    SetAt(genericValues, 0, 53);
    if (genericValues[0] != 53 || AtThroughLocals(genericValues, 0u) != 53 ||
        AtInferred(genericValues, 0u) != 53)
        return 50;
    int[] copiedElement = new int[1];
    CopyAt(genericValues, copiedElement, 0, 0);
    if (copiedElement[0] != 53) return 81;
    int[] createdValues = Create(2u, 1u, 59);
    if (createdValues.Length != 2u || createdValues[1] != 59) return 52;
    int[] copiedValues = Copy(genericValues);
    if (copiedValues.Length != 1u || copiedValues[0] != 53) return 53;
    long[] longValues = new long[2];
    longValues[0] = 61L;
    longValues[1] = 67L;
    if (AtThroughLocals(longValues, 0u) != 61L ||
        AtInferred(longValues, 0u) != 61L) return 155;
    long[] copiedLongValues = Copy(longValues);
    if (copiedLongValues.Length != 2u || copiedLongValues[0] != 61L ||
        copiedLongValues[1] != 67L) return 54;
    if (!HasValues(copiedLongValues) || !IsAtMostTwo(copiedLongValues) ||
        !IsAtLeast(copiedLongValues, 2u)) return 65;
    if (Select(true, 71, 73) != 71 || Select(false, 71, 73) != 73) return 55;
    if (Select(true, 79L, 83L) != 79L || Select(false, 79L, 83L) != 83L) return 56;
    if (SelectLocal(true, 89, 97) != 89 || SelectLocal(false, 89, 97) != 97) return 57;
    if (SelectLocal(true, 101L, 103L) != 101L ||
        SelectLocal(false, 101L, 103L) != 103L) return 58;
    OperatorBox<int> firstBox = new OperatorBox<int>(107);
    OperatorBox<int> secondBox = new OperatorBox<int>(109);
    OperatorBox<int> sumBox = firstBox + secondBox;
    if (sumBox.value != 109) return 61;
    OperatorBox<long> firstLongBox = new OperatorBox<long>(113L);
    OperatorBox<long> secondLongBox = new OperatorBox<long>(127L);
    OperatorBox<long> sumLongBox = firstLongBox + secondLongBox;
    if (sumLongBox.value != 127L) return 62;
    GenericOperatorMethodBox genericOperatorMethod;
    if (genericOperatorMethod + 139 != 139 ||
        genericOperatorMethod + 149L != 149L) return 71;
    GenericOperatorMethodContainer<GenericOperatorMarker> genericClassOperator =
        new GenericOperatorMethodContainer<GenericOperatorMarker>();
    if (genericClassOperator + 157 != 157) return 72;
    OperatorClass<int> firstClass = new OperatorClass<int>(131);
    OperatorClass<int> secondClass = new OperatorClass<int>(137);
    OperatorClass<int> sumClass = firstClass + secondClass;
    if (sumClass.value != 137) return 63;
    int extensionValue = 41;
    if (extensionValue.AddOne() != 42 || extensionValue.Next != 42) return 51;

    Effects.State = 0;
    bool andResult = Effects.Mark(false, 1) && Effects.Mark(true, 2);
    if (andResult || Effects.State != 1) return 24;
    Effects.State = 0;
    bool orResult = Effects.Mark(true, 3) || Effects.Mark(false, 4);
    if (!orResult || Effects.State != 3) return 25;
    if (Choose(null) != "fallback") return 26;
    if (ReadLongMaximum() != 9223372036854775807L) return 27;
    if (ReadLongMinimum() != -9223372036854775807L - 1L) return 28;

    ReflectionTarget reflectionSeed = new ReflectionTarget();
    ReflectionTarget nullReflection = null;
    ReflectionTarget coalescedNull =
        FirstNonNull<ReflectionTarget>(nullReflection, nullReflection, reflectionSeed);
    ReflectionTarget coalescedFirst =
        FirstNonNull(reflectionSeed, nullReflection, nullReflection);
    if (coalescedNull != reflectionSeed || coalescedFirst != reflectionSeed)
        return 134;
    int[] coalescingArrayFallback = new int[1];
    int[] coalescedArray = FirstNonNullArray<int>(null, coalescingArrayFallback);
    int[] coalescingArrayFirst = new int[1];
    int[] coalescedArrayFirst =
        FirstNonNullArray(coalescingArrayFirst, coalescingArrayFallback);
    if (coalescedArray != coalescingArrayFallback ||
        coalescedArrayFirst != coalescingArrayFirst) return 135;
    TypeInfo reflectionType = reflectionSeed.Type;
    int[] reflectedArray = new int[3];
    object reflectedArrayObject = reflectedArray;
    TypeInfo reflectedArrayType = reflectedArrayObject.Type;
    object reflectedArrayLength = reflectedArrayType.Invoke(
        reflectedArray, "Length", null);
    if (reflectedArrayLength == null || (uint)reflectedArrayLength != 3u) return 159;
    object[] invalidArrayLengthArguments = new object[1];
    invalidArrayLengthArguments[0] = 0u;
    if (reflectedArrayType.Invoke(
        reflectedArray, "Length", invalidArrayLengthArguments) != null) return 160;
    if (RunBoxedStructReflection(reflectionSeed) != 0) return 116;
    object reflectionPointSeed = new ReflectionPoint(9);
    TypeInfo reflectionPointType = reflectionPointSeed.Type;
    object reflectedPoint = reflectionPointType.CreateInstance();
    if (reflectedPoint == null) return 117;
    ReflectionPoint defaultReflectionPoint = (ReflectionPoint)reflectedPoint;
    if (defaultReflectionPoint.X != 0) return 118;
    object[] reflectionPointArguments = new object[1];
    reflectionPointArguments[0] = 27;
    object constructedReflectionPoint = reflectionPointType.CreateInstance(
        reflectionPointArguments);
    if (constructedReflectionPoint == null) return 121;
    ReflectionPoint initializedReflectionPoint =
        (ReflectionPoint)constructedReflectionPoint;
    if (initializedReflectionPoint.X != 27) return 122;
    reflectionPointArguments[0] = 27L;
    if (reflectionPointType.CreateInstance(reflectionPointArguments) != null) return 123;
    object genericReflectionPointSeed = new GenericReflectionPoint<int>(9);
    TypeInfo genericReflectionPointType = genericReflectionPointSeed.Type;
    object reflectedGenericPoint = genericReflectionPointType.CreateInstance();
    if (reflectedGenericPoint == null) return 119;
    GenericReflectionPoint<int> defaultGenericReflectionPoint =
        (GenericReflectionPoint<int>)reflectedGenericPoint;
    if (defaultGenericReflectionPoint.Value != 0) return 120;
    object[] genericPointArguments = new object[1];
    genericPointArguments[0] = 31;
    object constructedGenericPoint = genericReflectionPointType.CreateInstance(
        genericPointArguments);
    if (constructedGenericPoint == null) return 124;
    GenericReflectionPoint<int> initializedGenericReflectionPoint =
        (GenericReflectionPoint<int>)constructedGenericPoint;
    if (initializedGenericReflectionPoint.Value != 31) return 125;
    genericPointArguments[0] = 44;
    genericReflectionPointType.Invoke(
        constructedGenericPoint, "SetValue", genericPointArguments);
    object reflectedGenericValue = genericReflectionPointType.Invoke(
        constructedGenericPoint, "GetValue", null);
    if (reflectedGenericValue == null || (int)reflectedGenericValue != 44)
        return 127;
    object[] genericCounterArguments = new object[1];
    genericCounterArguments[0] = 5;
    object adjustedGenericCounter = genericReflectionPointType.Invoke(
        constructedGenericPoint, "AdjustCounter", genericCounterArguments);
    if (adjustedGenericCounter == null || (int)adjustedGenericCounter != 22)
        return 130;
    genericCounterArguments[0] = 25;
    genericReflectionPointType.Invoke(
        constructedGenericPoint, "UpdateCounter", genericCounterArguments);
    GenericReflectionPoint<int> updatedGenericReflectionPoint =
        (GenericReflectionPoint<int>)constructedGenericPoint;
    if (updatedGenericReflectionPoint.Counter != 25 ||
        updatedGenericReflectionPoint.OtherCounter != 26) return 131;
    object[] genericPointAssignmentArguments = new object[2];
    genericPointAssignmentArguments[0] = 1;
    genericPointAssignmentArguments[1] = 52;
    genericReflectionPointType.Invoke(
        constructedGenericPoint, "SetValueFromSecond",
        genericPointAssignmentArguments);
    GenericReflectionPoint<int> changedGenericReflectionPoint =
        (GenericReflectionPoint<int>)constructedGenericPoint;
    if (changedGenericReflectionPoint.Value != 52) return 138;
    object[] genericPairArguments = new object[2];
    genericPairArguments[0] = 5;
    genericPairArguments[1] = 8;
    genericReflectionPointType.Invoke(
        constructedGenericPoint, "SetPair", genericPairArguments);
    GenericReflectionPoint<int> assignedGenericPair =
        (GenericReflectionPoint<int>)constructedGenericPoint;
    if (assignedGenericPair.Value != 8 || assignedGenericPair.OtherValue != 5)
        return 139;
    genericReflectionPointType.Invoke(constructedGenericPoint, "ResetValue", null);
    GenericReflectionPoint<int> resetGenericReflectionPoint =
        (GenericReflectionPoint<int>)constructedGenericPoint;
    if (resetGenericReflectionPoint.Value != 0) return 136;
    object genericReferenceReflectionSeed =
        new GenericReflectionPoint<ReflectionTarget>(reflectionSeed);
    TypeInfo genericReferenceReflectionType = genericReferenceReflectionSeed.Type;
    genericReferenceReflectionType.Invoke(
        genericReferenceReflectionSeed, "ResetValue", null);
    GenericReflectionPoint<ReflectionTarget> resetGenericReferenceReflection =
        (GenericReflectionPoint<ReflectionTarget>)genericReferenceReflectionSeed;
    if (resetGenericReferenceReflection.Value != null) return 137;
    genericPointArguments[0] = 31L;
    if (genericReflectionPointType.CreateInstance(genericPointArguments) != null)
        return 126;
    object reflected = reflectionType.CreateInstance();
    if (reflected == null) return 36;
    if (ReflectionTarget.Instances != 2) return 37;
    Reflectable interfaceResult = (Reflectable)reflectionType.Invoke(
        reflectionSeed, "GetInterface", null);
    if (interfaceResult == null || interfaceResult.Value != 74) return 111;
    object[] reflectionArguments = new object[1];
    reflectionArguments[0] = reflected;
    object[] primitiveReflectionArguments = new object[2];
    primitiveReflectionArguments[0] = 19;
    primitiveReflectionArguments[1] = 23;
    int primitiveSum = (int)reflectionType.Invoke(
        reflectionSeed, "AddValues", primitiveReflectionArguments);
    if (primitiveSum != 42) return 112;
    object[] primitiveOverloadArguments = new object[1];
    primitiveOverloadArguments[0] = 40;
    int primitiveOverloadResult = (int)reflectionType.Invoke(
        reflectionSeed, "EchoNumber", primitiveOverloadArguments);
    if (primitiveOverloadResult != 41) return 113;
    primitiveOverloadArguments[0] = 40L;
    long longOverloadResult = (long)reflectionType.Invoke(
        reflectionSeed, "EchoNumber", primitiveOverloadArguments);
    if (longOverloadResult != 42) return 114;
    if (reflectionType.Invoke(reflectionSeed, "IntOnly", primitiveOverloadArguments) != null)
        return 119;
    long staticPrimitiveResult = (long)reflectionType.Invoke(
        null, "DoubleValue", primitiveOverloadArguments);
    if (staticPrimitiveResult != 80) return 115;
    if (reflectionType.Invoke(reflectionSeed, "Echo", reflectionArguments) != reflected) return 41;
    if (reflectionType.Invoke(reflectionSeed, "EchoBase", reflectionArguments) != reflected) return 47;
    ReflectionDerived reflectionDerived = new ReflectionDerived();
    ReflectionBase virtualResult = (ReflectionBase)reflectionType.Invoke(
        reflectionDerived, "VirtualEcho", reflectionArguments);
    if ((object)virtualResult != (object)reflectionDerived) return 101;
    ParameterizedReflectionTarget parameterizedSeed =
        new ParameterizedReflectionTarget(reflectionSeed);
    TypeInfo parameterizedReflectionType = parameterizedSeed.Type;
    object[] constructorArguments = new object[1];
    constructorArguments[0] = reflected;
    object parameterizedObject =
        parameterizedReflectionType.CreateInstance(constructorArguments);
    if (parameterizedObject == null) return 102;
    ParameterizedReflectionTarget parameterized =
        (ParameterizedReflectionTarget)parameterizedObject;
    if (parameterized.Value != (ReflectionBase)reflected) return 103;
    OtherReflectionTarget constructorWrongType = new OtherReflectionTarget();
    constructorArguments[0] = constructorWrongType;
    if (parameterizedReflectionType.CreateInstance(constructorArguments) != null) return 104;
    if (reflectionType.Invoke(null, "StaticEcho", reflectionArguments) != reflected) return 45;
    ReflectionTarget replacement = new ReflectionTarget();
    replacement.Value = 77;
    reflectionArguments[0] = replacement;
    if (reflectionType.Invoke(reflectionSeed, "SetValue", reflectionArguments) != null ||
        reflectionSeed.Value != 77) return 42;
    OtherReflectionTarget wrongType = new OtherReflectionTarget();
    reflectionArguments[0] = wrongType;
    if (reflectionType.Invoke(reflectionSeed, "Echo", reflectionArguments) != null) return 43;
    reflectionArguments[0] = reflected;
    if (reflectionType.Invoke(wrongType, "Echo", reflectionArguments) != null) return 46;
    if (reflectionType.Invoke(reflectionSeed, "Missing", null) != null) return 44;
    GenericReflectionTarget<ReflectionTarget> genericReflectionSeed =
        new GenericReflectionTarget<ReflectionTarget>();
    TypeInfo genericReflectionType = genericReflectionSeed.Type;
    object[] genericReflectionArguments = new object[1];
    genericReflectionArguments[0] = reflectionSeed;
    genericReflectionType.Invoke(
        genericReflectionSeed, "SetValue", genericReflectionArguments);
    object genericClassReflectionValue = genericReflectionType.Invoke(
        genericReflectionSeed, "GetValue", null);
    if ((ReflectionTarget)genericClassReflectionValue != reflectionSeed ||
        genericReflectionSeed.Value != reflectionSeed) return 131;
    object[] genericPrimitiveArguments = new object[1];
    genericPrimitiveArguments[0] = 73;
    genericReflectionType.Invoke(
        genericReflectionSeed, "SetCounter", genericPrimitiveArguments);
    object genericClassPrimitiveValue = genericReflectionType.Invoke(
        genericReflectionSeed, "GetCounter", null);
    if (genericClassPrimitiveValue == null ||
        (int)genericClassPrimitiveValue != 73 || genericReflectionSeed.Counter != 73)
        return 132;
    genericPrimitiveArguments[0] = 73L;
    if (genericReflectionType.Invoke(
        genericReflectionSeed, "SetCounter", genericPrimitiveArguments) != null ||
        genericReflectionSeed.Counter != 73) return 133;
    object genericReflected = genericReflectionType.CreateInstance();
    if (genericReflected == null) return 39;
    TypeInfo privateReflectionType = PrivateReflectionTarget.GetTypeInfo();
    if (privateReflectionType.CreateInstance() != null) return 38;

    int delta = 2;
    Transform second = value => {
        Effects.State = Effects.State * 10 + 2;
        return value + delta;
    };
    delta = 3;
    Transform combined = FirstTransform;
    combined += second;
    Effects.State = 0;
    if (combined(40) != 43 || Effects.State != 12) return 31;
    combined -= second;
    Effects.State = 0;
    if (combined(40) != 41 || Effects.State != 1) return 32;
    Transform sameHandler = FirstTransform;
    if (combined != sameHandler || combined == null) return 33;

    Level4 deep = new Level4();
    if (deep.Number() != 41) return 29;
    IBaseValue diamond = new DiamondValue();
    if (diamond.Get() != 42) return 30;

    Console.WriteLine("native e2e pass");
    return 0;
}
'@, [Text.UTF8Encoding]::new($false))
    $env:PATH = "$(Split-Path -Parent $cmakePath);$env:PATH"
    if ($Generator -like '*Ninja*') {
        $ninjaDirectory = Join-Path (Split-Path -Parent $cmakePath) '..\..\Ninja'
        if (Test-Path -LiteralPath (Join-Path $ninjaDirectory 'ninja.exe')) {
            $env:PATH = "$(Resolve-Path $ninjaDirectory);$env:PATH"
        }
    }
    if ($Generator) { $env:CMAKE_GENERATOR = $Generator }
    if ($CCompiler) { $env:CC = $CCompiler }

    $compilerArguments = @(
        '--compile', '--cxcore-dir', $cxCorePath, '--verbosity', 'quiet',
        '--output-dir', $outputDirectory, $sourcePath
    )
    if ([IO.Path]::GetExtension($compilerPath) -ieq '.dll') {
        & dotnet $compilerPath @compilerArguments
    } else {
        & $compilerPath @compilerArguments
    }
    if ($LASTEXITCODE -ne 0) {
        throw "CX native compilation failed (exit $LASTEXITCODE). Build files are in '$buildRoot'."
    }

    $executable = Get-ChildItem -LiteralPath $binaryDirectory -Filter '*.exe' -File -Recurse |
        Select-Object -First 1
    if ($null -eq $executable) {
        throw "Native executable was not created. Build files are in '$buildRoot'."
    }

    $unicodeArgument = ([char]0x00C4).ToString() + 'lpha'
    $validArguments = @($unicodeArgument, 'beta')
    $actualOutput = (& $executable.FullName @validArguments | ForEach-Object { "$_" }) -join "`n"
    $validExitCode = $LASTEXITCODE
    if ($validExitCode -ne 0 -or $actualOutput.Trim() -ne 'native e2e pass') {
        throw "Valid argument run failed: exit=$validExitCode output='$($actualOutput.Trim())'."
    }

    $invalidArguments = @('alpha')
    $invalidOutput = (& $executable.FullName @invalidArguments | ForEach-Object { "$_" }) -join "`n"
    $invalidExitCode = $LASTEXITCODE
    if ($invalidExitCode -ne 21 -or $invalidOutput.Trim()) {
        throw "Invalid argument run failed: exit=$invalidExitCode output='$($invalidOutput.Trim())'."
    }

    $collectionsRoot = Join-Path $buildRoot 'collections-array'
    $collectionsSourceDirectory = Join-Path $collectionsRoot 'src'
    $collectionsOutput = Join-Path $collectionsRoot 'generated'
    New-Item -ItemType Directory -Path $collectionsSourceDirectory, $collectionsOutput -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $collectionsRoot 'collections-array.cxproj'), "name: collections_array_e2e`ntype: Executable`n", [Text.UTF8Encoding]::new($false))
    foreach ($collectionSource in @('Array.cx', 'IEnumerable.cx', 'IEnumerator.cx')) {
        Copy-Item -LiteralPath (Join-Path $cxCorePath "src\System\Collections\$collectionSource") -Destination (Join-Path $collectionsSourceDirectory $collectionSource)
    }
    [IO.File]::WriteAllText((Join-Path $collectionsSourceDirectory 'main.cx'), @'
import System.Collections;
public class IntegerCursor : IEnumerator<int> {
    private int _next;
    private int _current;
    public int Current { const get { return _current; } }
    public constructor() { _next = 1; _current = 0; }
    public bool MoveNext() {
        if (_next > 3) return false;
        _current = _next;
        _next += 1;
        return true;
    }
}
public class IntegerSequence : IEnumerable<int> {
    public IEnumerator<int> GetEnumerator() const { return new IntegerCursor(); }
}
public class ArrayHolder<T> {
    public Array<T> Value;
    public constructor(Array<T> value) { Value = value; }
}
public class ArrayFactory<T> {
    public Array<T> Value;
    public constructor(uint length) { Value = new Array<T>(length); }
}
public Array<int> WrapBuiltInArray(int[] values) { return values; }
public int[] UnwrapBuiltInArray(Array<int> values) { return values; }
public T[] EchoBuiltInArray<T>(T[] values) { return values; }
public Array<T> EchoPublicArray<T>(Array<T> values) { return values; }
public T FirstThroughArrayApi<T>(T[] values) { return values.Get(0u); }
public T FirstThroughPublicArrayApi<T>(Array<T> values) { return values.Get(0u); }
public int Main(string[] args) {
    int[] builtinInts = new int[2];
    Array<int> publicInts = builtinInts;
    publicInts.Set(0u, 42);
    if (builtinInts[0] != 42 || builtinInts.Get(0u) != 42) return 18;
    builtinInts.Set(0u, 45);
    if (publicInts.Get(0u) != 45) return 23;
    int[] sameBuiltinInts = UnwrapBuiltInArray(publicInts);
    sameBuiltinInts[1] = 73;
    if (publicInts.Get(1u) != 73) return 19;
    Array<int> samePublicInts = WrapBuiltInArray(sameBuiltinInts);
    samePublicInts.Set(0u, 84);
    if (builtinInts[0] != 84) return 20;
    Array<int> inferredPublicInts = EchoPublicArray(builtinInts);
    int[] inferredBuiltinInts = EchoBuiltInArray(publicInts);
    inferredPublicInts.Set(1u, 96);
    if (inferredBuiltinInts[1] != 96) return 22;
    if (builtinInts.GetOrDefault(3u) != 0) return 24;
    if (!builtinInts.TrySet(1u, 101) || publicInts.Get(1u) != 101) return 25;
    builtinInts.Fill(102);
    if (publicInts.Get(0u) != 102 || publicInts.Get(1u) != 102) return 26;
    if (FirstThroughArrayApi(builtinInts) != 102) return 29;
    if (FirstThroughPublicArrayApi(builtinInts) != 102 ||
        FirstThroughPublicArrayApi(publicInts) != 102) return 30;
    int[] builtInCopy = new int[3];
    if (!builtinInts.TryCopyTo(builtInCopy, 1u) ||
        builtInCopy[1] != 102 || builtInCopy[2] != 102) return 27;
    IEnumerator<int> interfaceEnumerator = builtinInts.GetEnumerator();
    if (!interfaceEnumerator.MoveNext() || interfaceEnumerator.Current != 102) return 33;

    Array<int> ints = new Array<int>(2u);
    ints.Set(0u, 17);
    ints.Set(1u, 24);
    if (ints.Length != 2u || ints.Get(0u) != 17 || ints.Get(1u) != 24) return 1;
    int arraySum = 0;
    foreach (int value in ints) { arraySum += value; }
    if (arraySum != 41) return 11;
    IEnumerable<int> interfaceSequence = new IntegerSequence();
    int interfaceSum = 0;
    foreach (int value in interfaceSequence) { interfaceSum += value; }
    if (interfaceSum != 6) return 17;
    if (ints.GetOrDefault(1u) != 24 || ints.GetOrDefault(2u) != 0) return 6;
    if (!ints.TrySet(1u, 25) || ints.Get(1u) != 25 || ints.TrySet(2u, 30)) return 7;
    if (ints.GetOrDefault(2u) != 0) return 8;
    Array<int> intCopy = new Array<int>(3u);
    if (!ints.TryCopyTo(intCopy, 1u) || intCopy.Get(0u) != 0 ||
        intCopy.Get(1u) != 17 || intCopy.Get(2u) != 25 ||
        ints.TryCopyTo(intCopy, 2u) || intCopy.Get(2u) != 25) return 13;
    ArrayHolder<int> holder = new ArrayHolder<int>(ints);
    if (holder.Value != ints || holder.Value.Get(1u) != 25) return 4;
    ArrayFactory<int> factory = new ArrayFactory<int>(1u);
    factory.Value.Set(0u, 31);
    if (factory.Value.Get(0u) != 31) return 5;
    ints.Fill(7);
    if (ints.Get(0u) != 7 || ints.Get(1u) != 7) return 14;

    Array<long> longs = new Array<long>(1u);
    longs.Set(0u, 9000000000L);
    if (longs.Length != 1u || longs.Get(0u) != 9000000000L) return 2;
    if (FirstThroughArrayApi(longs) != 9000000000L ||
        FirstThroughPublicArrayApi(longs) != 9000000000L) return 31;

    Array<string> strings = new Array<string>(1u);
    strings.Set(0u, "collection element");
    if (FirstThroughArrayApi(strings) != "collection element" ||
        FirstThroughPublicArrayApi(strings) != "collection element") return 32;
    int stringCount = 0;
    foreach (string value in strings) { if (value != null) stringCount += 1; }
    if (stringCount != 1) return 12;
    if (strings.Length != 1u || strings.Get(0u) != "collection element" ||
        strings.GetOrDefault(1u) != null) return 3;
    if (!strings.TrySet(0u, "updated") || strings.Get(0u) != "updated" ||
        strings.TrySet(1u, "outside")) return 9;
    Array<string> stringCopy = new Array<string>(2u);
    if (!strings.TryCopyTo(stringCopy, 1u) || stringCopy.Get(1u) != "updated" ||
        strings.TryCopyTo(stringCopy, 2u) || stringCopy.Get(1u) != "updated") return 15;
    strings.Fill("filled");
    if (strings.Get(0u) != "filled") return 16;
    return 0;
}
'@, [Text.UTF8Encoding]::new($false))
    $collectionsCompilerArguments = @(
        '--compile', '--cxcore-dir', $cxCorePath, '--verbosity', 'quiet',
        '--output-dir', $collectionsOutput, (Join-Path $collectionsRoot 'collections-array.cxproj')
    )
    if ([IO.Path]::GetExtension($compilerPath) -ieq '.dll') {
        & dotnet $compilerPath @collectionsCompilerArguments
    } else {
        & $compilerPath @collectionsCompilerArguments
    }
    if ($LASTEXITCODE -ne 0) {
        throw "System.Collections.Array<T> native compilation failed (exit $LASTEXITCODE). Build files are in '$collectionsRoot'."
    }
    $collectionsBinaryDirectory = Join-Path $collectionsRoot '.bin\Release'
    $collectionsExecutable = Get-ChildItem -LiteralPath $collectionsBinaryDirectory -Filter 'collections_array_e2e.exe' -File |
        Select-Object -First 1
    if ($null -eq $collectionsExecutable) {
        throw "System.Collections.Array<T> native executable was not created. Build files are in '$collectionsRoot'."
    }
    & $collectionsExecutable.FullName
    if ($LASTEXITCODE -ne 0) {
        throw "System.Collections.Array<T> native run failed (exit $LASTEXITCODE)."
    }
    Write-Output 'PASS System.Collections.Array<T>/T[]: shared API and storage, Fill, TryCopyTo, enumeration, and closed-owner construction'

    $listRoot = Join-Path $buildRoot 'collections-list'
    $listSourceDirectory = Join-Path $listRoot 'src'
    $listOutput = Join-Path $listRoot 'generated'
    New-Item -ItemType Directory -Path $listSourceDirectory, $listOutput -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $listRoot 'collections-list.cxproj'), "name: collections_list_e2e`ntype: Executable`n", [Text.UTF8Encoding]::new($false))
    foreach ($collectionSource in @('Array.cx', 'IEnumerable.cx', 'IEnumerator.cx', 'List.cx')) {
        Copy-Item -LiteralPath (Join-Path $cxCorePath "src\System\Collections\$collectionSource") -Destination (Join-Path $listSourceDirectory $collectionSource)
    }
    [IO.File]::WriteAllText((Join-Path $listSourceDirectory 'main.cx'), @'
import System.Collections;
public int Main(string[] args) {
    List<int> values = new List<int>();
    values.Add(1); values.Add(2); values.Add(3); values.Add(4); values.Add(5); values.Add(6);
    if (values.Count != 6u || values.Get(5u) != 6) return 1;
    int sum = 0;
    foreach (int value in values) { sum += value; }
    if (sum != 21) return 10;
    IEnumerable<int> listSequence = values;
    int interfaceListSum = 0;
    foreach (int value in listSequence) { interfaceListSum += value; }
    if (interfaceListSum != 21) return 21;
    int filteredSum = 0;
    foreach (int value in values) {
        if (value == 3) continue;
        if (value == 5) break;
        filteredSum += value;
    }
    if (filteredSum != 7) return 12;
    values.Insert(2u, 99);
    if (values.Count != 7u || values.Get(0u) != 1 || values.Get(1u) != 2 || values.Get(2u) != 99 || values.Get(3u) != 3 || values.Get(6u) != 6) return 2;
    values.Set(0u, 10);
    if (values.Get(0u) != 10) return 3;
    if (!values.TrySet(0u, 11) || values.TrySet(99u, 12) || values.Get(0u) != 11) return 15;
    if (!values.TryInsert(values.Count, 77) || values.TryInsert(values.Count + 1u, 88) || values.Get(values.Count - 1u) != 77) return 16;
    if (!values.TryRemoveAt(values.Count - 1u)) return 17;
    if (!values.TryRemoveAt(2u) || values.TryRemoveAt(99u) || values.Count != 6u || values.Get(2u) != 3) return 4;
    values.RemoveAt(0u);
    if (values.Count != 5u || values.Get(0u) != 2) return 5;
    values.Clear();
    if (values.Count != 0u || values.GetOrDefault(0u) != 0) return 6;
      values.Add(7); values.Add(8);
      values.AddRange(values);
      if (values.Count != 4u || values.Get(0u) != 7 || values.Get(1u) != 8 || values.Get(2u) != 7 || values.Get(3u) != 8) return 7;
      List<int> tail = new List<int>();
      tail.Add(9); tail.Add(10);
      values.AddRange(tail);
      Array<int> snapshot = values.ToArray();
      Array<int> copied = new Array<int>(8u);
      values.CopyTo(copied, 1u);
      if (snapshot.Length != 6u || snapshot.Get(5u) != 10 || copied.Get(0u) != 0 || copied.Get(1u) != 7 || copied.Get(6u) != 10) return 8;
      if (!values.TryCopyTo(copied, 2u) || values.TryCopyTo(copied, 3u) || values.TryCopyTo(copied, 0xFFFFFFFFu) || copied.Get(2u) != 7) return 18;

    List<string> names = new List<string>();
    names.Add("alpha"); names.Add("gamma");
    names.Insert(1u, "beta");
    if (names.Count != 3u || names.Get(0u) != "alpha" || names.Get(1u) != "beta" || names.Get(2u) != "gamma") return 7;
    List<string> extraNames = new List<string>();
    extraNames.Add("delta");
    names.AddRange(extraNames);
    Array<string> nameSnapshot = names.ToArray();
    Array<string> nameCopy = new Array<string>(5u);
    names.CopyTo(nameCopy, 1u);
    if (nameSnapshot.Length != 4u || nameSnapshot.Get(3u) != "delta" || nameCopy.Get(1u) != "alpha" || nameCopy.Get(4u) != "delta") return 13;
    if (!names.TrySet(0u, "first") || names.TrySet(names.Count, "outside") || names.Get(0u) != "first") return 19;
    if (!names.TryCopyTo(nameCopy, 1u) || names.TryCopyTo(nameCopy, 2u) || nameCopy.Get(1u) != "first") return 20;
    int nameCount = 0;
    foreach (string name in names) { if (name != null) nameCount += 1; }
    if (nameCount != 4) return 11;
    names.RemoveAt(1u);
    if (names.Count != 3u || names.Get(1u) != "gamma") return 14;
    names.Clear();
    if (names.Count != 0u || names.GetOrDefault(0u) != null) return 9;
    return 0;
}
'@, [Text.UTF8Encoding]::new($false))
    $listCompilerArguments = @(
        '--compile', '--cxcore-dir', $cxCorePath, '--verbosity', 'quiet',
        '--output-dir', $listOutput, (Join-Path $listRoot 'collections-list.cxproj')
    )
    if ([IO.Path]::GetExtension($compilerPath) -ieq '.dll') {
        & dotnet $compilerPath @listCompilerArguments
    } else {
        & $compilerPath @listCompilerArguments
    }
    if ($LASTEXITCODE -ne 0) {
        throw "System.Collections.List<T> native compilation failed (exit $LASTEXITCODE). Build files are in '$listRoot'."
    }
    $listExecutable = Get-ChildItem -LiteralPath (Join-Path $listRoot '.bin\Release') -Filter 'collections_list_e2e.exe' -File |
        Select-Object -First 1
    if ($null -eq $listExecutable) {
        throw "System.Collections.List<T> native executable was not created. Build files are in '$listRoot'."
    }
    & $listExecutable.FullName
    if ($LASTEXITCODE -ne 0) {
        throw "System.Collections.List<T> native run failed (exit $LASTEXITCODE)."
    }
    Write-Output 'PASS System.Collections.List<T>: growth, mutation, safe set/insert/copy, clear, and foreach for int/string'

    $adaptersRoot = Join-Path $buildRoot 'collections-adapters'
    $adaptersSourceDirectory = Join-Path $adaptersRoot 'src'
    $adaptersOutput = Join-Path $adaptersRoot 'generated'
    New-Item -ItemType Directory -Path $adaptersSourceDirectory, $adaptersOutput -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $adaptersRoot 'collections-adapters.cxproj'), "name: collections_adapters_e2e`ntype: Executable`n", [Text.UTF8Encoding]::new($false))
    foreach ($collectionSource in @('Array.cx', 'IEnumerable.cx', 'IEnumerator.cx', 'List.cx', 'Stack.cx', 'Queue.cx')) {
        Copy-Item -LiteralPath (Join-Path $cxCorePath "src\System\Collections\$collectionSource") -Destination (Join-Path $adaptersSourceDirectory $collectionSource)
    }
    [IO.File]::WriteAllText((Join-Path $adaptersSourceDirectory 'main.cx'), @'
import System.Collections;
public int Main(string[] args) {
    Stack<int> numbers = new Stack<int>();
    numbers.Push(10); numbers.Push(20); numbers.Push(30);
    int expectedNumber = 30;
    foreach (int number in numbers) {
        if (number != expectedNumber) return 5;
        expectedNumber -= 10;
    }
    if (expectedNumber != 0) return 6;
    IEnumerable<int> stackSequence = numbers;
    int stackInterfaceCount = 0;
    foreach (int number in stackSequence) { stackInterfaceCount += number; }
    if (stackInterfaceCount != 60) return 11;
    if (numbers.Count != 3u || numbers.Peek() != 30 || numbers.Pop() != 30 || numbers.Pop() != 20 || numbers.Pop() != 10 || numbers.Count != 0u) return 1;
    numbers.Push(40); numbers.Push(50); numbers.Clear();
    if (numbers.Count != 0u) return 9;

    Stack<string> words = new Stack<string>();
    words.Push("first"); words.Push("second");
    if (words.Peek() != "second" || words.Pop() != "second" || words.Pop() != "first" || words.Count != 0u) return 2;

    Queue<int> values = new Queue<int>();
    values.Enqueue(1); values.Enqueue(2); values.Enqueue(3);
    int expectedValue = 1;
    foreach (int value in values) {
        if (value != expectedValue) return 7;
        expectedValue += 1;
    }
    if (expectedValue != 4) return 8;
    IEnumerable<int> queueSequence = values;
    int queueInterfaceSum = 0;
    foreach (int value in queueSequence) { queueInterfaceSum += value; }
    if (queueInterfaceSum != 6) return 12;
    if (values.Count != 3u || values.Peek() != 1 || values.Dequeue() != 1 || values.Dequeue() != 2 || values.Dequeue() != 3 || values.Count != 0u) return 3;
    values.Enqueue(4); values.Enqueue(5); values.Clear();
    if (values.Count != 0u) return 10;

    Queue<string> names = new Queue<string>();
    names.Enqueue("alpha"); names.Enqueue("beta");
    if (names.Peek() != "alpha" || names.Dequeue() != "alpha" || names.Dequeue() != "beta" || names.Count != 0u) return 4;
    return 0;
}
'@, [Text.UTF8Encoding]::new($false))
    $adaptersCompilerArguments = @(
        '--compile', '--cxcore-dir', $cxCorePath, '--verbosity', 'quiet',
        '--output-dir', $adaptersOutput, (Join-Path $adaptersRoot 'collections-adapters.cxproj')
    )
    if ([IO.Path]::GetExtension($compilerPath) -ieq '.dll') {
        & dotnet $compilerPath @adaptersCompilerArguments
    } else {
        & $compilerPath @adaptersCompilerArguments
    }
    if ($LASTEXITCODE -ne 0) {
        throw "System.Collections.Stack<T>/Queue<T> native compilation failed (exit $LASTEXITCODE). Build files are in '$adaptersRoot'."
    }
    $adaptersExecutable = Get-ChildItem -LiteralPath (Join-Path $adaptersRoot '.bin\Release') -Filter 'collections_adapters_e2e.exe' -File |
        Select-Object -First 1
    if ($null -eq $adaptersExecutable) {
        throw "System.Collections.Stack<T>/Queue<T> native executable was not created. Build files are in '$adaptersRoot'."
    }
    & $adaptersExecutable.FullName
    if ($LASTEXITCODE -ne 0) {
        throw "System.Collections.Stack<T>/Queue<T> native run failed (exit $LASTEXITCODE)."
    }
    Write-Output 'PASS System.Collections.Stack<T>/Queue<T>: int/string LIFO/FIFO behavior and Clear'

    $linkedListRoot = Join-Path $buildRoot 'collections-linked-list'
    $linkedListSourceDirectory = Join-Path $linkedListRoot 'src'
    $linkedListOutput = Join-Path $linkedListRoot 'generated'
    New-Item -ItemType Directory -Path $linkedListSourceDirectory, $linkedListOutput -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $linkedListRoot 'collections-linked-list.cxproj'), "name: collections_linked_list_e2e`ntype: Executable`n", [Text.UTF8Encoding]::new($false))
    foreach ($collectionSource in @('IEnumerable.cx', 'IEnumerator.cx', 'LinkedList.cx')) {
        Copy-Item -LiteralPath (Join-Path $cxCorePath "src\System\Collections\$collectionSource") -Destination (Join-Path $linkedListSourceDirectory $collectionSource)
    }
    [IO.File]::WriteAllText((Join-Path $linkedListSourceDirectory 'main.cx'), @'
import System.Collections;
public int Main(string[] args) {
    LinkedList<int> numbers = new LinkedList<int>();
    numbers.AddFirst(2); numbers.AddFirst(1); numbers.AddLast(3);
    int expected = 1;
    foreach (int number in numbers) {
        if (number != expected) return 3;
        expected += 1;
    }
    if (expected != 4) return 4;
    IEnumerable<int> linkedSequence = numbers;
    int linkedInterfaceSum = 0;
    foreach (int number in linkedSequence) { linkedInterfaceSum += number; }
    if (linkedInterfaceSum != 6) return 8;
    if (numbers.Count != 3u || numbers.RemoveFirst() != 1 || numbers.RemoveLast() != 3 || numbers.RemoveFirst() != 2 || numbers.Count != 0u) return 1;
    numbers.AddLast(4); numbers.AddLast(5); numbers.Clear();
    if (numbers.Count != 0u) return 5;
    numbers.AddFirst(6);
    if (numbers.Count != 1u || numbers.RemoveFirst() != 6) return 6;

    LinkedList<string> words = new LinkedList<string>();
    words.AddLast("middle"); words.AddFirst("first"); words.AddLast("last");
    if (words.Count != 3u || words.RemoveLast() != "last" || words.RemoveFirst() != "first" || words.RemoveLast() != "middle" || words.Count != 0u) return 2;
    words.AddLast("retained"); words.Clear();
    if (words.Count != 0u) return 7;
    return 0;
}
'@, [Text.UTF8Encoding]::new($false))
    $linkedListCompilerArguments = @(
        '--compile', '--cxcore-dir', $cxCorePath, '--verbosity', 'quiet',
        '--output-dir', $linkedListOutput, (Join-Path $linkedListRoot 'collections-linked-list.cxproj')
    )
    if ([IO.Path]::GetExtension($compilerPath) -ieq '.dll') {
        & dotnet $compilerPath @linkedListCompilerArguments
    } else {
        & $compilerPath @linkedListCompilerArguments
    }
    if ($LASTEXITCODE -ne 0) {
        throw "System.Collections.LinkedList<T> native compilation failed (exit $LASTEXITCODE). Build files are in '$linkedListRoot'."
    }
    $linkedListExecutable = Get-ChildItem -LiteralPath (Join-Path $linkedListRoot '.bin\Release') -Filter 'collections_linked_list_e2e.exe' -File |
        Select-Object -First 1
    if ($null -eq $linkedListExecutable) {
        throw "System.Collections.LinkedList<T> native executable was not created. Build files are in '$linkedListRoot'."
    }
    & $linkedListExecutable.FullName
    if ($LASTEXITCODE -ne 0) {
        throw "System.Collections.LinkedList<T> native run failed (exit $LASTEXITCODE)."
    }
    Write-Output 'PASS System.Collections.LinkedList<T>: end operations, Clear, and reuse for int/string'

    $dictionaryRoot = Join-Path $buildRoot 'collections-dictionary'
    $dictionarySourceDirectory = Join-Path $dictionaryRoot 'src'
    $dictionaryOutput = Join-Path $dictionaryRoot 'generated'
    New-Item -ItemType Directory -Path $dictionarySourceDirectory, $dictionaryOutput -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $dictionaryRoot 'collections-dictionary.cxproj'), "name: collections_dictionary_e2e`ntype: Executable`n", [Text.UTF8Encoding]::new($false))
    foreach ($collectionSource in @('Array.cx', 'IEnumerable.cx', 'IEnumerator.cx', 'IEqualityComparer.cx', 'Dictionary.cx')) {
        Copy-Item -LiteralPath (Join-Path $cxCorePath "src\System\Collections\$collectionSource") -Destination (Join-Path $dictionarySourceDirectory $collectionSource)
    }
    [IO.File]::WriteAllText((Join-Path $dictionarySourceDirectory 'main.cx'), @'
import System.Collections;
public final class ConstantStringComparer : IEqualityComparer<string> {
    public int GetHashCode(string value) const { return 1; }
    public bool Equals(string first, string second) const {
        if (first == null) return second == null;
        return first.Equals(second);
    }
}

public final class CollisionKey {
    public override int GetHashCode() const { return 1; }
}

public int Main(string[] args) {
    Dictionary<string, int> values = new Dictionary<string, int>();
    values.Set("one", 1); values.Set("two", 2); values.Set("three", 3); values.Set("four", 4); values.Set("five", 5);
    if (values.Count != 5u || !values.ContainsKey("one") || values.ContainsKey("missing") || values.GetValueOrDefault("three") != 3 || values.GetValueOrDefault("missing") != 0) return 1;
    string prefix = "o";
    string equalOne = prefix + "ne";
    values.Set(equalOne, 1);
    if (values.Count != 5u || values.GetValueOrDefault("one") != 1 || !values.ContainsKey(equalOne)) return 8;
    if (values.TryAdd(equalOne, 99) || values.Count != 5u || values.GetValueOrDefault("one") != 1) return 11;
    if (!values.TryAdd(null, 9)) return 12;
    if (values.Count != 6u || !values.ContainsKey(null) || values.GetValueOrDefault(null) != 9) return 9;
    if (values.TryAdd(null, 10) || values.Count != 6u || values.GetValueOrDefault(null) != 9) return 13;
    values.Set(null, 10);
    if (values.Count != 6u || values.GetValueOrDefault(null) != 10 || !values.Remove(null)) return 10;
    int enumeratedSum = 0;
    foreach (var entry in values) {
        enumeratedSum += entry.Value;
        if (entry.Key == null) return 5;
    }
    if (enumeratedSum != 15) return 6;
    int typedEnumeratedSum = 0;
    foreach (KeyValuePair<string, int> entry in values) {
        typedEnumeratedSum += entry.Value;
    }
    if (typedEnumeratedSum != 15) return 7;
    IEnumerable<KeyValuePair<string, int>> entrySequence = values;
    int interfaceEntrySum = 0;
    foreach (KeyValuePair<string, int> entry in entrySequence) {
        interfaceEntrySum += entry.Value;
    }
    if (interfaceEntrySum != 15) return 20;
    values.Set("three", 33);
    if (values.GetValueOrDefault("three") != 33 || !values.Remove("two") || values.Remove("missing") || values.Count != 4u) return 2;
    if (values.GetValueOrDefault("four") != 4 || values.GetValueOrDefault("five") != 5) return 3;
    values.Clear();
    if (values.Count != 0u || values.ContainsKey("one") || values.GetValueOrDefault("one") != 0) return 4;

    Dictionary<CollisionKey, int> colliding = new Dictionary<CollisionKey, int>();
    CollisionKey first = new CollisionKey();
    CollisionKey second = new CollisionKey();
    CollisionKey third = new CollisionKey();
    CollisionKey fourth = new CollisionKey();
    CollisionKey fifth = new CollisionKey();
    CollisionKey sixth = new CollisionKey();
    if (!colliding.TryAdd(first, 11) || !colliding.TryAdd(second, 22) ||
        !colliding.TryAdd(third, 33) || !colliding.TryAdd(fourth, 44) ||
        !colliding.TryAdd(fifth, 55) || !colliding.TryAdd(sixth, 66) ||
        colliding.Count != 6u ||
        colliding.GetValueOrDefault(first) != 11 ||
        colliding.GetValueOrDefault(second) != 22 ||
        colliding.GetValueOrDefault(third) != 33 ||
        colliding.GetValueOrDefault(fourth) != 44 ||
        colliding.GetValueOrDefault(fifth) != 55 ||
        colliding.GetValueOrDefault(sixth) != 66) return 14;
    if (!colliding.Remove(second) || colliding.ContainsKey(second) ||
        colliding.GetValueOrDefault(first) != 11 ||
        colliding.GetValueOrDefault(third) != 33 ||
        colliding.GetValueOrDefault(fourth) != 44 ||
        colliding.GetValueOrDefault(fifth) != 55 ||
        colliding.GetValueOrDefault(sixth) != 66) return 15;

    Dictionary<string, int> custom = new Dictionary<string, int>(new ConstantStringComparer());
    custom.Set("one", 1);
    custom.Set("two", 2);
    custom.Set("three", 3);
    custom.Set("four", 4);
    custom.Set("five", 5);
    string customEqualOne = "o" + "ne";
    if (custom.Count != 5u || custom.GetValueOrDefault(customEqualOne) != 1 ||
        custom.TryAdd(customEqualOne, 99) || custom.GetValueOrDefault("five") != 5 ||
        !custom.Remove("three") || custom.ContainsKey("three") ||
        custom.GetValueOrDefault("four") != 4) return 16;

    Dictionary<string, long> longValues = new Dictionary<string, long>();
    if (!longValues.TryAdd("first", 4294967296L) ||
        !longValues.TryAdd("second", 8589934592L) ||
        longValues.Count != 2u ||
        longValues.GetValueOrDefault("first") != 4294967296L ||
        longValues.GetValueOrDefault("second") != 8589934592L) return 17;
    longValues.Set("first", 12884901888L);
    if (longValues.Count != 2u ||
        longValues.GetValueOrDefault("first") != 12884901888L) return 18;

    Dictionary<CollisionKey, long> objectValues =
        new Dictionary<CollisionKey, long>();
    CollisionKey seventh = new CollisionKey();
    CollisionKey eighth = new CollisionKey();
    if (!objectValues.TryAdd(seventh, 7L) ||
        !objectValues.TryAdd(eighth, 8L) ||
        objectValues.Count != 2u ||
        objectValues.GetValueOrDefault(seventh) != 7L ||
        objectValues.GetValueOrDefault(eighth) != 8L) return 19;

    Dictionary<string, string> referenceValues =
        new Dictionary<string, string>();
    referenceValues.Set("first", "alpha");
    if (!referenceValues.TryAdd("second", "beta") ||
        referenceValues.Count != 2u ||
        referenceValues.GetValueOrDefault("first") != "alpha" ||
        referenceValues.GetValueOrDefault("second") != "beta") return 21;
    referenceValues.Set("first", "updated");
    int referenceEntryCount = 0;
    foreach (KeyValuePair<string, string> entry in referenceValues) {
        if (entry.Value == null) return 22;
        referenceEntryCount += 1;
    }
    if (referenceEntryCount != 2 ||
        referenceValues.GetValueOrDefault("first") != "updated" ||
        !referenceValues.Remove("second")) return 23;
    referenceValues.Clear();
    if (referenceValues.Count != 0u ||
        referenceValues.GetValueOrDefault("first") != null) return 24;
    return 0;
}
'@, [Text.UTF8Encoding]::new($false))
    $dictionaryCompilerArguments = @(
        '--compile', '--cxcore-dir', $cxCorePath, '--verbosity', 'quiet',
        '--output-dir', $dictionaryOutput, (Join-Path $dictionaryRoot 'collections-dictionary.cxproj')
    )
    if ([IO.Path]::GetExtension($compilerPath) -ieq '.dll') {
        & dotnet $compilerPath @dictionaryCompilerArguments
    } else {
        & $compilerPath @dictionaryCompilerArguments
    }
    if ($LASTEXITCODE -ne 0) {
        throw "System.Collections.Dictionary<TKey, TValue> native compilation failed (exit $LASTEXITCODE). Build files are in '$dictionaryRoot'."
    }
    $dictionaryExecutable = Get-ChildItem -LiteralPath (Join-Path $dictionaryRoot '.bin\Release') -Filter 'collections_dictionary_e2e.exe' -File |
        Select-Object -First 1
    if ($null -eq $dictionaryExecutable) {
        throw "System.Collections.Dictionary<TKey, TValue> native executable was not created. Build files are in '$dictionaryRoot'."
    }
    & $dictionaryExecutable.FullName
    if ($LASTEXITCODE -ne 0) {
        throw "System.Collections.Dictionary<TKey, TValue> native run failed (exit $LASTEXITCODE)."
    }
    Write-Output 'PASS System.Collections.Dictionary<TKey, TValue>: string equality, reference and value elements, null keys, TryAdd, collisions, entry foreach, and clear'

    $projectRoot = Join-Path $buildRoot 'project-reference'
    $appDirectory = Join-Path $projectRoot 'app'
    $libraryDirectory = Join-Path $projectRoot 'math'
    $projectOutput = Join-Path $projectRoot 'generated'
    New-Item -ItemType Directory -Path $appDirectory, $libraryDirectory, $projectOutput -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $libraryDirectory 'math.cxproj'), "name: math`ntype: Library`n", [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText((Join-Path $libraryDirectory 'api.cx'), @'
namespace Math;
public class Box<T> { }
public int AddOne(int value) { return value + 1; }
public T Identity<T>(T value) { return value; }
public int Identity <int>(int value) { return value + 1; }
public class Counter {
    public int Value = 10;
    public T Adjust<T>(T value) { return value; }
    public int Adjust <int>(int value) { return Value + value; }
}
public class GenericRouter<TState> {
    public TState Last;
    public U Route<U>(TState state, U value) { return value; }
    public int Route<int>(TState state, int value) {
        Last = state;
        return value + 1;
    }
}
public class GenericStaticRouter<TState> {
    public static U Route<U>(TState state, U value) { return value; }
    public static int Route<int>(TState state, int value) { return value + 20; }
}
public concept CComparable {
    bool operator >(const CComparable other) const;
}
public concept CReadable {
    int GetValue(int multiplier) const;
    int GetValue(long multiplier) const;
    int Value { const get; set; }
}
public T First<T, U>(T first, U second)
    : T = CComparable : U = CComparable {
    return first;
}
public bool HasValues<T>(T[] values) { return values.Length > 0; }
public int Read<T>(T value, int multiplier) : T = CReadable {
    return value.GetValue(multiplier);
}
public int ReadWithLiteral<T>(T value) : T = CReadable {
    return value.GetValue(2);
}
public int ReadLong<T>(T value, long multiplier) : T = CReadable {
    return value.GetValue(multiplier);
}
public int ReadLongWithLiteral<T>(T value) : T = CReadable {
    return value.GetValue(2L);
}
public int ReadValue<T>(T value) : T = CReadable {
    return value.Value;
}
public void SetValue<T>(T value, int next) : T = CReadable {
    value.Value = next;
}
public class FactoryTarget {
    public static int Instances;
    public constructor() { Instances += 1; }
}
public int FactoryInstanceCount() { return FactoryTarget.Instances; }
'@, [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText((Join-Path $libraryDirectory 'numeric.cx'), @'
import Math;
namespace NumericExtensions;
public extension BoxExtensions<T> : Box<T> {
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
public enum Shade { Dark, Light }
public extension ShadeExtensions : Shade {
    public bool IsLight() const { return this == Shade.Light; }
    public int operator +(Shade other) const { return this == other ? 42 : 0; }
    public Shade operator ++() const { return Shade.Dark; }
    public Shade operator +=(Shade other) const { return other; }
}
public extension IntExtensions : int {
    public int AddOneExtension() const { return this - (-1); }
    public int Next { const get => this - (-1); }
    public int operator +(int other) const { return this * 10 - other; }
    public int operator ~() const { return -this; }
    public int operator ++() const { return this - (-1); }
    public int operator --() const { return this - 1; }
    public int operator +=(int other) const { return this - (0 - other); }
    public int operator -=(int other) const { return this - other; }
}
'@, [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText((Join-Path $appDirectory 'app.cxproj'), "name: app`ntype: Executable`nproject_references:`n- ../math/math.cxproj`n", [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText((Join-Path $appDirectory 'main.cx'), @'
import Math;
import NumericExtensions;
namespace App;
public delegate int MathTransform(int value);
public interface IReflectable { void Mark(); }
public class Reflectable : IReflectable {
    public int Value;
    public void Mark() { Value = 73; }
}
public class InterfaceConstructed {
    public constructor(IReflectable value) { value.Mark(); }
}
public class ReflectionTarget {
    public void UseInterface(IReflectable value) { value.Mark(); }
}
public class ComparableAddress {
    public int Value { const get; set; }
    public constructor(int value) { Value = value; }
    public int GetValue(int multiplier) const { return Value * multiplier; }
    public int GetValue(long multiplier) const { return Value * 3; }
    public bool operator >(const Math.CComparable other) const {
        return Value > other.Value;
    }
}
public struct ComparablePoint {
    public int Value { const get; set; }
    public constructor(int value) { Value = value; }
    public int GetValue(int multiplier) const { return Value * multiplier; }
    public int GetValue(long multiplier) const { return Value * 3; }
    public bool operator >(const Math.CComparable other) const {
        return Value > other.Value;
    }
}
public delegate int IntReader();
public delegate int Transform(int value);
public struct DefaultPoint { public int Value; }
public concept CAdjustable {
    int Value { const get; set; }
    int Adjustment() const;
}
public concept CRepeatedReadable { int Read(int ignored) const; }
public class ConceptReader {
    public int Value;
    public constructor(int value) { Value = value; }
    public int Read(int ignored) const { return Value; }
}
public struct ConceptReaderValue {
    public int Value;
    public constructor(int value) { Value = value; }
    public int Read(int ignored) const { return Value; }
}
public class ConceptAdjuster {
    public int Value { const get; set; }
    public int Adjustment() const { return 7; }
    public constructor(int value) { Value = value; }
}
public void AddAdjustment<T>(T item) : T = CAdjustable {
    item.Value += item.Adjustment();
}
public bool HasRepeatedValue<T>(T item, int minimum) : T = CRepeatedReadable {
    return item.Read(item.Read(0)) == item.Read(0) && item.Read(0) > minimum;
}
public T DefaultValue<T>() { return default; }
public T DefaultLocal<T>() {
    T value = default;
    return value;
}
public T[] DefaultArray<T>() { return default; }
public T[] DefaultArrayLocal<T>() {
    T[] values = default;
    return values;
}
public int Run() {
    Counter counter = new Counter();
    GenericRouter<string> router = new GenericRouter<string>();
    GenericRouter<string[]> arrayRouter = new GenericRouter<string[]>();
    MathTransform identity = Identity;
    MathTransform adjust = counter.Adjust;
    Reflectable reflectable = new Reflectable();
    ReflectionTarget reflectionTarget = new ReflectionTarget();
    TypeInfo reflectionType = reflectionTarget.Type;
    object[] reflectionArguments = new object[1];
    reflectionArguments[0] = reflectable;
    object reflectedValue = reflectionType.Invoke(
        reflectionTarget, "UseInterface", reflectionArguments);
    InterfaceConstructed constructorSeed = new InterfaceConstructed(reflectable);
    TypeInfo constructorType = constructorSeed.Type;
    InterfaceConstructed interfaceConstructed =
        (InterfaceConstructed)constructorType.CreateInstance(reflectionArguments);
    FactoryTarget seed = new FactoryTarget();
    TypeInfo type = seed.Type;
    object created = type.CreateInstance();
    ComparableAddress address = new ComparableAddress(9);
    ComparablePoint point = new ComparablePoint(3);
    ConceptAdjuster conceptAdjuster = new ConceptAdjuster(3);
    AddAdjustment(conceptAdjuster);
    if (conceptAdjuster.Value != 10) return 48;
    ConceptReader conceptReader = new ConceptReader(7);
    ConceptReaderValue conceptReaderValue = new ConceptReaderValue(7);
    if (!HasRepeatedValue(conceptReader, 5) ||
        !HasRepeatedValue(conceptReaderValue, 5)) return 49;
    ComparableAddress selected = First(address, point);
    SetValue(address, 11);
    SetValue(point, 5);
    int value = 41;
    IntReader reader = value.AddOneExtension;
    Transform transform = item => item * 10;
    Transform transformTail = item => item * 10 - 2;
    transform += transformTail;
    Transform[] invocationList = transform.GetInvocationList();
    Transform firstHandler = invocationList[0];
    Transform secondHandler = invocationList[1];
    DefaultPoint defaultPoint = DefaultLocal<DefaultPoint>();
    int[] defaultArray = DefaultArray<int>();
    int[] defaultArrayLocal = DefaultArrayLocal<int>();
    if (router.Route<int>("library", 4) != 5 || router.Last != "library" ||
        arrayRouter.Route<int>(null, 6) != 7 || arrayRouter.Last != null ||
        router.Route<long>("fallback", 8L) != 8L || router.Last != "library")
        return 83;
    if (GenericStaticRouter<string>.Route<int>("library", 3) != 23 ||
        GenericStaticRouter<int>.Route<int>(17, 3) != 23)
        return 84;
    if (invocationList.Length != 2u) return 51;
    if (firstHandler(10) != 100) return 52;
    if (secondHandler(10) != 98) return 53;
    if (invocationList[0](10) != 100) return 54;
    if (invocationList[1](10) != 98) return 55;
    if (DefaultValue<int>() != 0 || DefaultLocal<long>() != 0L ||
        DefaultValue<Box<int> >() != null || defaultPoint.Value != 0 ||
        defaultArray != null || defaultArrayLocal != null)
        return 56;
    Box<int> box = new Box<int>();
    int converted = box.Convert(7);
    int[] extensionValues = new int[2];
    extensionValues[0] = 12;
    extensionValues[1] = 18;
    int[] extensionCopy = box.Copy(extensionValues);
    Shade shade = Shade.Light;
    int mutationValue = 3;
    int oldMutationValue = mutationValue++;
    if (oldMutationValue != 3 || --mutationValue != 3 || ++mutationValue != 4)
        return 0;
    mutationValue += 5;
    mutationValue -= 2;
    Shade mutationShade = Shade.Light;
    Shade oldMutationShade = mutationShade++;
    mutationShade += Shade.Light;
    return reflectedValue == null && interfaceConstructed != null &&
        reflectable.Value == 73 && created != null &&
        HasValues(new int[2]) && !HasValues(new int[0]) &&
        FactoryInstanceCount() == 2 && selected.Value == 11 &&
        Read(address, 1) == 11 && Read(point, 1) == 3 &&
        ReadWithLiteral(address) == 22 && ReadWithLiteral(point) == 6 &&
        ReadLong(address, 1L) == 33 && ReadLong(point, 1L) == 9 &&
        ReadLongWithLiteral(address) == 33 && ReadLongWithLiteral(point) == 9 &&
        ReadValue(address) == 11 && ReadValue(point) == 3 &&
        value.AddOneExtension() == 42 && reader() == 42 && value.Next == 42 &&
        invocationList.Length == 2u && firstHandler(10) == 100 &&
        secondHandler(10) == 98 &&
        extensionCopy.Length == 2u && extensionCopy[0] == 12 && extensionCopy[1] == 18 &&
        shade.IsLight() && shade + Shade.Light == 42 &&
        mutationValue == 7 && oldMutationShade == Shade.Light &&
        mutationShade == Shade.Light && converted == 7 &&
        Identity(41) == 42 && identity(41) == 42 && adjust(2) == 12 &&
        40 + 2 == 398 && ~40 == -40 ? AddOne(41) : 0;
}
'@, [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText((Join-Path $appDirectory 'main.c'), @'
#include "app.h"
int main(void) { return CX_ID_3(app, App, Run)() == 42 ? 0 : 1; }
'@, [Text.UTF8Encoding]::new($false))

    $projectArguments = @(
        '--compile', '--cxcore-dir', $cxCorePath, '--verbosity', 'quiet',
        '--output-dir', $projectOutput, (Join-Path $appDirectory 'app.cxproj')
    )
    if ([IO.Path]::GetExtension($compilerPath) -ieq '.dll') {
        & dotnet $compilerPath @projectArguments
    } else {
        & $compilerPath @projectArguments
    }
    if ($LASTEXITCODE -ne 0) {
        throw "Cross-module native compilation failed (exit $LASTEXITCODE). Build files are in '$buildRoot'."
    }
    $projectExecutable = Get-ChildItem -LiteralPath (Join-Path $appDirectory '.bin') -Filter 'app.exe' -File -Recurse |
        Select-Object -First 1
    if ($null -eq $projectExecutable) {
        throw "Cross-module native executable was not created. Build files are in '$buildRoot'."
    }
    & $projectExecutable.FullName
    if ($LASTEXITCODE -ne 0) {
        throw "Cross-module generated call returned exit $LASTEXITCODE; expected 0. Build files are in '$buildRoot'."
    }

    $projectBuildDirectory = Join-Path $projectOutput 'build'
    $dynamicConfigureOutput = & $cmakePath `
        -S $projectOutput `
        -B $projectBuildDirectory `
        "-DCXCORE_SOURCE_DIR=$cxCorePath" `
        '-DCMAKE_BUILD_TYPE=Release' `
        '-DCX_STATIC_LINK=OFF' 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Dynamic cross-module CMake configuration failed: $($dynamicConfigureOutput -join [Environment]::NewLine)"
    }
    $dynamicBuildOutput = & $cmakePath --build $projectBuildDirectory --config Release 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Dynamic cross-module build failed: $($dynamicBuildOutput -join [Environment]::NewLine)"
    }
    & $projectExecutable.FullName
    if ($LASTEXITCODE -ne 0) {
        throw "Dynamic cross-module generated call returned exit $LASTEXITCODE; expected 0. Build files are in '$buildRoot'."
    }

    Write-Output 'PASS Main(string[] args): argument values, output, and exit codes'
    Write-Output 'PASS property assignment order, compound assignment, and increment/decrement: indexed, class, interface, and mutating struct operator'
    Write-Output 'PASS named delegates: multicast invocation, removal, invocation-list reflection, and closure capture'
    Write-Output 'PASS native cross-module generic specializations through a referenced CX library (static and DLL)'
    $completed = $true
}
finally {
    $env:PATH = $previousPath
    $env:CMAKE_GENERATOR = $previousGenerator
    $env:CC = $previousCCompiler
    if ($completed -and (Test-Path -LiteralPath $buildRoot)) {
        Remove-Item -LiteralPath $buildRoot -Recurse -Force
    }
}

param(
    [string] $Cxc = 'cxc',
    [string] $CxCoreDir = (Join-Path $PSScriptRoot '..\..\..\cxcore'),
    [string] $CMake = 'cmake',
    [ValidateSet('Windows', 'Linux')] [string] $Platform = 'Windows',
    [ValidateSet('x64', 'x86')] [string] $Architecture = 'x64',
    [ValidateSet('static', 'shared')] [string] $LinkMode = 'static',
    [ValidateSet('ValueTask', 'Task')] [string] $AsyncEnumeratorContract = 'ValueTask',
    [string] $Distribution = 'Ubuntu',
    [string] $Generator = '',
    [string] $VsInstallationPath = 'C:\Program Files\Microsoft Visual Studio\18\Professional',
    [ValidateRange(1, 46340)] [int] $Elements = 1000,
    [ValidateRange(1, 100000)] [int] $Iterations = 1000,
    [ValidateRange(1, 100000)] [int] $AsyncIteratorIterations = 100,
    [ValidateRange(1, 1000000)] [int] $AwaitIterations = 250,
    [ValidateRange(1, 1000000)] [int] $HandAwaitIterations = 100000,
    [ValidateRange(8, 100000)] [int] $GcPressureIterations = 128,
    [ValidateRange(3, 100)] [int] $Samples = 7,
    [string] $OutputCsv = '',
    [switch] $KeepBuild
)

$ErrorActionPreference = 'Stop'
$compilerPath = if ([IO.Path]::IsPathRooted($Cxc)) {
    (Resolve-Path -LiteralPath $Cxc).Path
} else {
    (Get-Command $Cxc -ErrorAction Stop).Source
}
$cxCorePath = (Resolve-Path -LiteralPath $CxCoreDir).Path
$cmakePath = if ($Platform -eq 'Linux') {
    $CMake
} elseif (Test-Path -LiteralPath $CMake -PathType Leaf) {
    (Resolve-Path -LiteralPath $CMake).Path
} else {
    (Get-Command $CMake -ErrorAction Stop).Source
}
$buildRoot = Join-Path ([IO.Path]::GetTempPath()) `
    "cx-coroutine-bench-$([guid]::NewGuid().ToString('N'))"
$generatedDirectory = Join-Path $buildRoot 'generated'
$sourcePath = Join-Path $buildRoot 'bench.cx'
$projectPath = Join-Path $buildRoot 'bench.cxproj'
$mainPath = Join-Path $buildRoot 'main.c'
$buildDirectory = Join-Path $generatedDirectory "build-$Platform-$Architecture-$LinkMode"
$runSucceeded = $false
$csvPath = if ([string]::IsNullOrWhiteSpace($OutputCsv)) {
    Join-Path $PSScriptRoot "results\coroutine-iterator-$(Get-Date -Format 'yyyyMMdd-HHmmss').csv"
} else {
    $OutputCsv
}
$reportPath = [IO.Path]::ChangeExtension($csvPath, '.txt')

function Convert-WindowsPathToWslPath([string] $Path) {
    if (-not [IO.Path]::IsPathRooted($Path) -or $Path.Length -lt 3 -or $Path[1] -ne ':') {
        throw "Expected a drive-qualified Windows path, found '$Path'."
    }
    return "/mnt/$($Path.Substring(0, 1).ToLowerInvariant())$($Path.Substring(2).Replace('\', '/'))"
}

if ($Platform -eq 'Linux' -and $Architecture -ne 'x64') {
    throw 'The Linux coroutine benchmark currently supports x64 only.'
}
if ($Platform -eq 'Windows') {
    $devShell = Join-Path $VsInstallationPath 'Common7\Tools\Launch-VsDevShell.ps1'
    if (-not (Test-Path -LiteralPath $devShell -PathType Leaf)) {
        throw "Visual Studio developer shell was not found at '$devShell'."
    }
    & $devShell -VsInstallationPath $VsInstallationPath -Arch $Architecture -HostArch x64 -SkipAutomaticLocation -NoLogo
    if ($LASTEXITCODE -and $LASTEXITCODE -ne 0) {
        throw "Visual Studio developer shell initialization failed with exit $LASTEXITCODE."
    }
} else {
    $cmakeVersionOutput = & wsl.exe --distribution $Distribution -- $CMake --version 2>&1
    $cmakeVersion = [regex]::Match(($cmakeVersionOutput -join ' '), 'cmake version ([0-9]+)\.([0-9]+)')
    if ($LASTEXITCODE -ne 0 -or -not $cmakeVersion.Success -or
        ([int]$cmakeVersion.Groups[1].Value -lt 3) -or
        ([int]$cmakeVersion.Groups[1].Value -eq 3 -and [int]$cmakeVersion.Groups[2].Value -lt 31)) {
        throw "Linux benchmark requires CMake 3.31 or newer; found '$($cmakeVersionOutput -join ' ')'."
    }
}

New-Item -ItemType Directory -Path $buildRoot -Force | Out-Null
$utf8NoBom = [Text.UTF8Encoding]::new($false)
[IO.File]::WriteAllText($projectPath, "name: bench`ntype: Executable`n", $utf8NoBom)
$sourceText = @'
import System.Threading.Tasks;
namespace System.Collections;
public interface IEnumerable<T> { IEnumerator<T> GetEnumerator() const; }
public interface IDisposable { void Dispose(); }
public interface IEnumerator<T> : IDisposable { T Current { const get; } bool MoveNext(); }
public interface IAsyncEnumerable<T> {
    IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken);
}
public interface IAsyncEnumerator<T> {
    T Current { const get; }
    ValueTask<bool> MoveNextAsync();
    ValueTask DisposeAsync();
}
public IEnumerable<int> Range(int count) {
    int index = 0;
    while (index < count) {
        yield return index;
        index++;
    }
}
public class GenericIteratorProvider<T> {
    public IEnumerable<T> GenericValues(T value, int count) {
        int index = 0;
        while (index < count) {
            yield return value;
            index++;
        }
    }
}
public int GenericIntIteratorSum(int count) {
    GenericIteratorProvider<int> provider = new GenericIteratorProvider<int>();
    int sum = 0;
    foreach (int value in provider.GenericValues(7, count)) sum += value;
    return sum;
}
public int GenericStringIteratorCount(int count) {
    GenericIteratorProvider<string> provider = new GenericIteratorProvider<string>();
    string expected = "generic-owner-reference";
    int visited = 0;
    foreach (string value in provider.GenericValues(expected, count)) {
        if (value == null || value != expected) return -1;
        visited++;
    }
    return visited;
}
public IEnumerable<int> BranchingRange(int count) {
    int index = 0;
    while (index < count) {
        if (index % 2 == 0) {
            yield return index + 10;
        } else {
            yield return index + 20;
        }
        index++;
    }
}
public int BranchingSum(int count) {
    int sum = 0;
    foreach (int value in BranchingRange(count)) sum += value;
    return sum;
}
public struct IteratorPayload {
    public int Number;
    public string Reference;
    public constructor(int number, string reference) {
        Number = number;
        Reference = reference;
    }
}
public struct ReusablePayload {
    public int Number;
    public string Label;
    public constructor(int number, string label) {
        Number = number;
        Label = label;
    }
}
public IEnumerable<IteratorPayload> PayloadRange(int count) {
    int index = 0;
    while (index < count) {
        yield return new IteratorPayload(index, "payload");
        index++;
    }
}
public int PayloadSum(int count) {
    int sum = 0;
    foreach (IteratorPayload value in PayloadRange(count)) {
        if (value.Reference == null) return -100;
        sum += value.Number;
    }
    return sum;
}
public int GenericPayloadIteratorSum(int count) {
    GenericIteratorProvider<IteratorPayload> provider = new GenericIteratorProvider<IteratorPayload>();
    IteratorPayload expected = new IteratorPayload(7, "generic-owner-payload");
    int sum = 0;
    foreach (IteratorPayload value in provider.GenericValues(expected, count)) {
        if (value.Number != expected.Number || value.Reference != expected.Reference) return -101;
        sum += value.Number;
    }
    return sum;
}
public IEnumerable<string> ReferenceRange(int count) {
    int index = 0;
    while (index < count) {
        yield return "reference";
        index++;
    }
}
public int ReferenceCount(int count) {
    int seen = 0;
    foreach (string value in ReferenceRange(count)) {
        if (value == null) return -100;
        seen++;
    }
    return seen;
}
public struct IteratorLargePayload {
    public int Number;
    public string Reference0;
    public string Reference1;
    public string Reference2;
    public string Reference3;
    public string Reference4;
    public string Reference5;
    public string Reference6;
    public string Reference7;
    public string Reference8;
    public string Reference9;
    public string Reference10;
    public string Reference11;
    public constructor(int number, string reference) {
        Number = number;
        Reference0 = reference;
        Reference1 = reference;
        Reference2 = reference;
        Reference3 = reference;
        Reference4 = reference;
        Reference5 = reference;
        Reference6 = reference;
        Reference7 = reference;
        Reference8 = reference;
        Reference9 = reference;
        Reference10 = reference;
        Reference11 = reference;
    }
}
public IEnumerable<IteratorLargePayload> LargePayloadRange(int count) {
    int index = 0;
    while (index < count) {
        yield return new IteratorLargePayload(index, "large-payload");
        index++;
    }
}
public int LargePayloadSum(int count) {
    int sum = 0;
    foreach (IteratorLargePayload value in LargePayloadRange(count)) {
        if (value.Reference0 == null || value.Reference1 == null ||
            value.Reference2 == null || value.Reference3 == null ||
            value.Reference4 == null || value.Reference5 == null ||
            value.Reference6 == null || value.Reference7 == null ||
            value.Reference8 == null || value.Reference9 == null ||
            value.Reference10 == null || value.Reference11 == null) return -100;
        sum += value.Number;
    }
    return sum;
}
public int Sum(int count) {
    int sum = 0;
    foreach (int value in Range(count)) sum += value;
    return sum;
}
public class DirectIteratorProbe {
    public static int Starts;
    public static int Disposals;
    private int _offset;
    public constructor(int offset) { _offset = offset; }
    public int OffsetValue() { return _offset; }
    public IEnumerable<int> Values(int count) {
        DirectIteratorProbe.Starts++;
        try {
            int index = 0;
            while (index < count) { yield return this._offset + index; index++; }
        } finally {
            DirectIteratorProbe.Disposals++;
        }
    }
    public IEnumerable<int> BranchingValues(int count) {
        DirectIteratorProbe.Starts++;
        try {
            int index = 0;
            while (index < count) {
                if (index % 2 == 0) {
                    yield return this._offset + index;
                } else {
                    yield return this._offset + index + 100;
                }
                index++;
            }
        } finally {
            DirectIteratorProbe.Disposals++;
        }
    }
    public int BranchingValuesSum(int count) {
        int sum = 0;
        foreach (int value in BranchingValues(count)) sum += value;
        return sum;
    }
}
public int DirectIteratorForeachChecks() {
    DirectIteratorProbe.Starts = 0;
    DirectIteratorProbe.Disposals = 0;
    DirectIteratorProbe probe = new DirectIteratorProbe(0);
    IEnumerable<int> lazy = probe.Values(3);
    if (DirectIteratorProbe.Starts != 0) return 1;

    int partialSum = 0;
    foreach (int value in lazy) {
        partialSum += value;
        break;
    }
    if (partialSum != 0) return 21;
    if (DirectIteratorProbe.Starts != 1) return 22;
    if (DirectIteratorProbe.Disposals != 1) return 23;

    int complete = 0;
    foreach (int value in lazy) complete += value;
    if (complete != 3 || DirectIteratorProbe.Starts != 2 ||
        DirectIteratorProbe.Disposals != 2) return 3;

    DirectIteratorProbe.Starts = 0;
    DirectIteratorProbe.Disposals = 0;
    partialSum = 0;
    foreach (int value in probe.Values(3)) {
        partialSum += value;
        break;
    }
    if (partialSum != 0 || DirectIteratorProbe.Starts != 1 ||
        DirectIteratorProbe.Disposals != 1) return 4;

    complete = 0;
    foreach (int value in probe.Values(3)) complete += value;
    if (complete != 3 || DirectIteratorProbe.Starts != 2 ||
        DirectIteratorProbe.Disposals != 2) return 5;
    DirectIteratorProbe.Starts = 0;
    DirectIteratorProbe.Disposals = 0;
    lazy = probe.BranchingValues(3);
    if (DirectIteratorProbe.Starts != 0) return 6;
    partialSum = 0;
    foreach (int value in lazy) {
        partialSum += value;
        break;
    }
    if (partialSum != 0 || DirectIteratorProbe.Starts != 1 ||
        DirectIteratorProbe.Disposals != 1) return 7;
    complete = 0;
    foreach (int value in lazy) complete += value;
    if (complete != 103 || DirectIteratorProbe.Starts != 2 ||
        DirectIteratorProbe.Disposals != 2) return 8;
    return 0;
}
public async IAsyncEnumerable<int> AsyncRange(int count) {
    int index = 0;
    while (index < count) {
        yield return index;
        index++;
    }
}
public async IAsyncEnumerable<int> AsyncAwaitOne(Task<int> source) {
    int value = await source;
    yield return value;
}
public class ReusableInlineAsyncSequence : IAsyncEnumerable<int>, IAsyncEnumerator<int> {
    private int _index;
    public int Current { const get => 43; }
    public IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken cancellationToken) {
        _index = 0;
        return this;
    }
    public ValueTask<bool> MoveNextAsync() {
        if (_index == 0) {
            _index = 1;
            return new ValueTask<bool>(true);
        }
        return new ValueTask<bool>(false);
    }
    public ValueTask DisposeAsync() { return new ValueTask(); }
}
async Task<int> ConsumeAsyncSequence(IAsyncEnumerable<int> source) {
    int total = 0;
    await foreach (int value in source) total += value;
    return total;
}
public static class AwaitForeachAllocationProbe {
    public static ReusableInlineAsyncSequence Sequence;
    public static void Initialize() {
        if (Sequence == null) {
            ReusableInlineAsyncSequence created = new ReusableInlineAsyncSequence();
            Sequence = created;
        }
    }
    public static int RunInline() {
        Task<int> result = ConsumeAsyncSequence(Sequence);
        if (!result.IsCompleted) return -1;
        return result.GetResult();
    }
}
public class ControlAsyncSequence : IAsyncEnumerable<int>, IAsyncEnumerator<int> {
    private int _index;
    private int _current;
    public static ValueTask<bool> HasValue;
    public static ValueTask<bool> Completed;
    public static bool FirstMovePending;
    public int Moves;
    public int Disposals;
    public int Current { const get => _current; }
    public IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken cancellationToken) {
        _index = 0;
        _current = -1;
        Moves = 0;
        Disposals = 0;
        return this;
    }
    public ValueTask<bool> MoveNextAsync() {
        Moves++;
        if (_index == 0 && FirstMovePending) {
            _current = 0;
            _index = 1;
            ValueTask<bool> pending = new ValueTask<bool>();
            pending.SetTask(AwaitForeachControlAllocationProbe.Source.Task);
            return pending;
        }
        if (_index < 4) {
            _current = _index;
            _index++;
            return HasValue;
        }
        return Completed;
    }
    public ValueTask DisposeAsync() {
        Disposals++;
        return new ValueTask();
    }
}
async Task<int> ConsumeAsyncSequenceWithControl(IAsyncEnumerable<int> source) {
    int total = 0;
    await foreach (int value in source) {
        if (value == 1) continue;
        if (value == 3) break;
        total += value;
    }
    return total;
}
public static class AwaitForeachControlAllocationProbe {
    public static ControlAsyncSequence Sequence;
    public static TaskCompletionSource<bool> Source;
    public static Task<int> ResultTask;
    public static void Initialize() {
        if (Sequence == null) {
            ControlAsyncSequence.HasValue = new ValueTask<bool>(true);
            ControlAsyncSequence.Completed = new ValueTask<bool>(false);
            Sequence = new ControlAsyncSequence();
        }
        ControlAsyncSequence.FirstMovePending = false;
        ResultTask = null;
    }
    public static int RunInline() {
        Task<int> result = ConsumeAsyncSequenceWithControl(Sequence);
        if (!result.IsCompleted || result.GetResult() != 2 ||
            Sequence.Moves != 4 || Sequence.Disposals != 1) return -1;
        return 2;
    }
    public static int BeginPending() {
        TaskCompletionSource<bool> source = new TaskCompletionSource<bool>();
        Source = source;
        ControlAsyncSequence.FirstMovePending = true;
        ResultTask = ConsumeAsyncSequenceWithControl(Sequence);
        return ResultTask.IsCompleted ? -1 : 0;
    }
    public static void CompleteMove() {
        Source.SetResult(true);
    }
    public static int Finish() {
        ControlAsyncSequence.FirstMovePending = false;
        TaskScheduler.Run();
        if (ResultTask == null || !ResultTask.IsCompleted ||
            ResultTask.GetResult() != 2 || Sequence.Moves != 4 ||
            Sequence.Disposals != 1) return -1;
        return 0;
    }
}
public class PendingFirstMoveAsyncSequence : IAsyncEnumerable<int>, IAsyncEnumerator<int> {
    private int _index;
    public int Current { const get => 43; }
    public IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken cancellationToken) {
        _index = 0;
        return this;
    }
    public ValueTask<bool> MoveNextAsync() {
        if (_index == 0) {
            _index = 1;
            ValueTask<bool> pending = new ValueTask<bool>();
            pending.SetTask(AwaitForeachPendingProbe.Source.Task);
            return pending;
        }
        return new ValueTask<bool>(false);
    }
    public ValueTask DisposeAsync() {
        AwaitForeachPendingProbe.Disposals++;
        return new ValueTask();
    }
}
public static class AwaitForeachPendingProbe {
    public static TaskCompletionSource<bool> Source;
    public static Task<int> ResultTask;
    public static PendingFirstMoveAsyncSequence Sequence;
    public static int Disposals;
    public static void Initialize() {
        TaskCompletionSource<bool> source = new TaskCompletionSource<bool>();
        PendingFirstMoveAsyncSequence sequence = new PendingFirstMoveAsyncSequence();
        Source = source;
        Sequence = sequence;
        ResultTask = null;
        Disposals = 0;
    }
    public static int Begin() {
        ResultTask = ConsumeAsyncSequence(Sequence);
        return ResultTask.IsCompleted ? -1 : 0;
    }
    public static void CompleteMove() { Source.SetResult(true); }
    public static int Finish() {
        TaskScheduler.Run();
        if (!ResultTask.IsCompleted || ResultTask.GetResult() != 43 || Disposals != 1)
            return -1;
        return 0;
    }
}
public int AsyncIteratorSum(int count) {
    IAsyncEnumerator<int> enumerator =
        AsyncRange(count).GetAsyncEnumerator(CancellationToken.None);
    int sum = 0;
    while (enumerator.MoveNextAsync().GetResult()) sum += enumerator.Current;
    enumerator.DisposeAsync().GetResult();
    return sum;
}
public int PendingAsyncIteratorResume(int value) {
    TaskCompletionSource<int> source = new TaskCompletionSource<int>();
    IAsyncEnumerator<int> enumerator =
        AsyncAwaitOne(source.Task).GetAsyncEnumerator(CancellationToken.None);
    ValueTask<bool> move = enumerator.MoveNextAsync();
    if (move.IsCompleted) return -1;
    source.SetResult(value);
    TaskScheduler.Run();
    if (!move.GetResult()) return -1;
    int result = enumerator.Current;
    enumerator.DisposeAsync().GetResult();
    return result;
}
public class CapturedAsyncBranchSession {
    public static TaskCompletionSource<int> Source;
    public static IAsyncEnumerator<int> Enumerator;
    public static ValueTask<bool> Move;
    public static int Count;
    private DirectIteratorProbe _provider;
    public async IAsyncEnumerable<int> AsyncBranchValues(Task<int> source, int count) {
        DirectIteratorProbe.Starts++;
        int seed = await source;
        try {
            int index = 0;
            while (index < count) {
                if (index % 2 == 0) {
                    yield return this.ProviderValue(seed, index);
                } else {
                    yield return this.ProviderValue(seed, index + 100);
                }
                index++;
            }
        } finally {
            DirectIteratorProbe.Disposals++;
        }
    }
    private int ProviderValue(int seed, int index) {
        return this._provider.OffsetValue() + seed + index;
    }
    public constructor() { _provider = new DirectIteratorProbe(5); }
    public static int Begin(int count) {
        DirectIteratorProbe.Starts = 0;
        DirectIteratorProbe.Disposals = 0;
        CapturedAsyncBranchSession.Count = count;
        CapturedAsyncBranchSession session = new CapturedAsyncBranchSession();
        CapturedAsyncBranchSession.Source = new TaskCompletionSource<int>();
        CapturedAsyncBranchSession.Enumerator = session.AsyncBranchValues(
            CapturedAsyncBranchSession.Source.Task, count)
            .GetAsyncEnumerator(CancellationToken.None);
        IAsyncEnumerator<int> enumerator = CapturedAsyncBranchSession.Enumerator;
        CapturedAsyncBranchSession.Move = enumerator.MoveNextAsync();
        if (CapturedAsyncBranchSession.Move.IsCompleted ||
            DirectIteratorProbe.Starts != 1 || DirectIteratorProbe.Disposals != 0) return 1;
        return 0;
    }
    public static int CompleteSource() {
        TaskCompletionSource<int> source = CapturedAsyncBranchSession.Source;
        source.SetResult(7);
        TaskScheduler.Run();
        return CapturedAsyncBranchSession.Move.IsCompleted ? 0 : 2;
    }
    public static int ReadAll() {
        IAsyncEnumerator<int> enumerator = CapturedAsyncBranchSession.Enumerator;
        ValueTask<bool> move = CapturedAsyncBranchSession.Move;
        int index = 0;
        int sum = 0;
        while (index < CapturedAsyncBranchSession.Count) {
            if (!move.GetResult()) return 3;
            int expected = 12 + index + ((index % 2) == 0 ? 0 : 100);
            if (enumerator.Current != expected) return 4;
            sum += enumerator.Current;
            index++;
            CapturedAsyncBranchSession.Move = enumerator.MoveNextAsync();
            move = CapturedAsyncBranchSession.Move;
        }
        if (move.GetResult() || DirectIteratorProbe.Disposals != 1) return 5;
        enumerator.DisposeAsync().GetResult();
        return sum;
    }
    public static int ReadFirstAndDispose() {
        IAsyncEnumerator<int> enumerator = CapturedAsyncBranchSession.Enumerator;
        ValueTask<bool> move = CapturedAsyncBranchSession.Move;
        if (!move.GetResult() || enumerator.Current != 12) return 6;
        enumerator.DisposeAsync().GetResult();
        if (DirectIteratorProbe.Disposals != 1) return 7;
        return 0;
    }
}
public async Task<int> AwaitOne(Task<int> source) {
    return await source;
}
public async Task<int> AwaitReusableValueTask(ValueTask<int> source) {
    return await source;
}
public async Task<ReusablePayload> AwaitReusablePayloadValueTask(
    ValueTask<ReusablePayload> source) {
    return await source;
}
public async Task<int> AwaitNested(Task<int> source) {
    return await AwaitOne(source);
}
public async Task<int> AwaitDeep(Task<int> source) {
    return await AwaitNested(source);
}
public async Task<IteratorPayload> AwaitPayloadOne(Task<IteratorPayload> source) {
    return await source;
}
public async Task<IteratorPayload> AwaitPayloadNested(Task<IteratorPayload> source) {
    return await AwaitPayloadOne(source);
}
public async Task<IteratorPayload> AwaitPayloadDeep(Task<IteratorPayload> source) {
    return await AwaitPayloadNested(source);
}
public async Task<IteratorPayload> AwaitPayloadVeryDeep(Task<IteratorPayload> source) {
    return await AwaitPayloadDeep(source);
}
public class DeepPayloadAwaitSession {
    public static TaskCompletionSource<IteratorPayload> Source;
    public static Task<IteratorPayload> Result;
    public static IteratorPayload Expected;
    public static int Begin(int number) {
        string label = new string(128u, 'q');
        IteratorPayload expected = new IteratorPayload(number, label);
        TaskCompletionSource<IteratorPayload> source =
            new TaskCompletionSource<IteratorPayload>();
        Task<IteratorPayload> result = AwaitPayloadVeryDeep(source.Task);
        if (result.IsCompleted) return 1;
        DeepPayloadAwaitSession.Source = source;
        DeepPayloadAwaitSession.Result = result;
        DeepPayloadAwaitSession.Expected = expected;
        return 0;
    }
    public static int Complete() {
        TaskCompletionSource<IteratorPayload> source = DeepPayloadAwaitSession.Source;
        IteratorPayload expected = DeepPayloadAwaitSession.Expected;
        source.SetResult(expected);
        TaskScheduler.Run();
        return 0;
    }
    public static int Read() {
        Task<IteratorPayload> result = DeepPayloadAwaitSession.Result;
        IteratorPayload expected = DeepPayloadAwaitSession.Expected;
        IteratorPayload actual = result.GetResult();
        if (actual.Number != expected.Number || actual.Reference != expected.Reference) return -1;
        return actual.Number;
    }
}
public class SharedPendingAwaitSession {
    public static TaskCompletionSource<int> Source;
    public static Task<int> First;
    public static Task<int> Second;
    public static int Value;
    public static int Begin(int value) {
        TaskCompletionSource<int> source = new TaskCompletionSource<int>();
        Task<int> first = AwaitOne(source.Task);
        Task<int> second = AwaitOne(source.Task);
        SharedPendingAwaitSession.Source = source;
        SharedPendingAwaitSession.First = first;
        SharedPendingAwaitSession.Second = second;
        SharedPendingAwaitSession.Value = value;
        if (first.IsCompleted || second.IsCompleted) return 1;
        return 0;
    }
    public static int Complete() {
        TaskCompletionSource<int> source = SharedPendingAwaitSession.Source;
        int value = SharedPendingAwaitSession.Value;
        source.SetResult(value);
        TaskScheduler.Run();
        Task<int> first = SharedPendingAwaitSession.First;
        Task<int> second = SharedPendingAwaitSession.Second;
        if (!first.IsCompleted || !second.IsCompleted) return 2;
        return 0;
    }
    public static int ReadBoth() {
        Task<int> first = SharedPendingAwaitSession.First;
        Task<int> second = SharedPendingAwaitSession.Second;
        return first.GetResult() + second.GetResult();
    }
}
public class ReusableSourceAwaitBench {
    public static ReusableValueTaskSource<int> WarmSource;
    public static ReusableValueTaskSource<ReusablePayload> WarmPayloadSource;
    private static int CompletePending(
        ReusableValueTaskSource<int> source, int value) {
        uint version = source.GetVersion();
        ValueTask<int> pending = new ValueTask<int>();
        pending.SetSource(source, version);
        Task<int> result = AwaitReusableValueTask(pending);
        if (result.IsCompleted) return -1;
        source.SetResult(value);
        TaskScheduler.Run();
        int actual = result.GetResult();
        source.Reset();
        return actual;
    }
    public static int ColdPending(int value) {
        ReusableValueTaskSource<int> source =
            new ReusableValueTaskSource<int>();
        return CompletePending(source, value);
    }
    public static int WarmPending(int value) {
        if (ReusableSourceAwaitBench.WarmSource == null) {
            ReusableSourceAwaitBench.WarmSource =
                new ReusableValueTaskSource<int>();
        }
        return CompletePending(ReusableSourceAwaitBench.WarmSource, value);
    }
    public static int WarmPayloadPending(int value) {
        if (ReusableSourceAwaitBench.WarmPayloadSource == null) {
            ReusableSourceAwaitBench.WarmPayloadSource =
                new ReusableValueTaskSource<ReusablePayload>();
        }
        ReusableValueTaskSource<ReusablePayload> source =
            ReusableSourceAwaitBench.WarmPayloadSource;
        uint version = source.GetVersion();
        ReusablePayload expected = new ReusablePayload(
            value, new string(128u, (char)('a' + value % 26)));
        ValueTask<ReusablePayload> pending = new ValueTask<ReusablePayload>();
        pending.SetSource(source, version);
        Task<ReusablePayload> result = AwaitReusablePayloadValueTask(pending);
        if (result.IsCompleted) return -1;
        source.SetResult(expected);
        TaskScheduler.Run();
        ReusablePayload actual = result.GetResult();
        if (actual.Number != expected.Number || actual.Label != expected.Label) return -2;
        source.Reset();
        return actual.Number;
    }
}
public int CompletedAwaitOperation(int value) {
    Task<int> source = new Task<int>(value);
    return AwaitOne(source).GetResult();
}
public int PendingAwaitOperation(int value) {
    TaskCompletionSource<int> source = new TaskCompletionSource<int>();
    Task<int> pending = AwaitOne(source.Task);
    source.SetResult(value);
    TaskScheduler.Run();
    return pending.GetResult();
}
public int NestedCompletedAwaitOperation(int value) {
    Task<int> source = new Task<int>(value);
    return AwaitNested(source).GetResult();
}
public int NestedPendingAwaitOperation(int value) {
    TaskCompletionSource<int> source = new TaskCompletionSource<int>();
    Task<int> pending = AwaitNested(source.Task);
    source.SetResult(value);
    TaskScheduler.Run();
    return pending.GetResult();
}
public int DeepCompletedAwaitOperation(int value) {
    Task<int> source = new Task<int>(value);
    return AwaitDeep(source).GetResult();
}
public int DeepPendingAwaitOperation(int value) {
    TaskCompletionSource<int> source = new TaskCompletionSource<int>();
    Task<int> pending = AwaitDeep(source.Task);
    source.SetResult(value);
    TaskScheduler.Run();
    return pending.GetResult();
}
public int DeepPayloadCompletedAwaitOperation(int number) {
    string label = "deep-payload";
    IteratorPayload expected = new IteratorPayload(number, label);
    Task<IteratorPayload> source = new Task<IteratorPayload>(expected);
    IteratorPayload actual = AwaitPayloadVeryDeep(source).GetResult();
    if (actual.Number != number || actual.Reference != label) return -1;
    return actual.Number;
}
public int InlineValueTaskAsTaskOperation(int value) {
    ValueTask<int> valueTask = new ValueTask<int>(value);
    Task<int> task = valueTask.AsTask();
    return task.GetResult();
}
public int TaskBackedValueTaskAsTaskOperation(int value) {
    Task<int> original = new Task<int>(value);
    ValueTask<int> valueTask = new ValueTask<int>();
    valueTask.SetTask(original);
    Task<int> task = valueTask.AsTask();
    return task.GetResult();
}
'@
if ($AsyncEnumeratorContract -eq 'Task') {
    $sourceText = $sourceText.Replace('ValueTask<bool>', 'Task<bool>')
    $sourceText = $sourceText.Replace(
        'ValueTask DisposeAsync()', 'TaskHandle DisposeAsync()')
    $sourceText = $sourceText.Replace(
        'return new ValueTask();', 'return new TaskHandle();')
    $sourceText = [regex]::Replace(
        $sourceText,
        '(public class ReusableInlineAsyncSequence : IAsyncEnumerable<int>, IAsyncEnumerator<int> \{\r?\n)',
        ('$1' + [Environment]::NewLine +
            '    public static Task<bool> HasValue;' + [Environment]::NewLine +
            '    public static Task<bool> Completed;' + [Environment]::NewLine +
            '    public static TaskHandle Disposed;' + [Environment]::NewLine),
        [System.Text.RegularExpressions.RegexOptions]::None,
        [TimeSpan]::FromSeconds(2))
    $sourceText = [regex]::Replace(
        $sourceText,
        '(public class ControlAsyncSequence : IAsyncEnumerable<int>, IAsyncEnumerator<int> \{\r?\n)',
        ('$1' + [Environment]::NewLine +
            '    public static TaskHandle Disposed;' + [Environment]::NewLine),
        [System.Text.RegularExpressions.RegexOptions]::None,
        [TimeSpan]::FromSeconds(2))
    $sourceText = $sourceText.Replace(
        'if (Sequence == null) {',
        'if (Sequence == null) {' + "`r`n" +
        '            ReusableInlineAsyncSequence.HasValue = new Task<bool>(true);' + "`r`n" +
        '            ReusableInlineAsyncSequence.Completed = new Task<bool>(false);' + "`r`n" +
        '            ReusableInlineAsyncSequence.Disposed = new TaskHandle();')
    $sourceText = $sourceText.Replace(
        'ControlAsyncSequence.HasValue = new Task<bool>(true);',
        'ControlAsyncSequence.HasValue = new Task<bool>(true);' + "`r`n" +
        '            ControlAsyncSequence.Disposed = new TaskHandle();')
    $sourceText = $sourceText.Replace(
        'return new Task<bool>(true);',
        'return ReusableInlineAsyncSequence.HasValue;')
    $sourceText = $sourceText.Replace(
        'return new Task<bool>(false);',
        'return ReusableInlineAsyncSequence.Completed;')
    $sourceText = ([regex]'return new TaskHandle\(\);').Replace(
        $sourceText, 'return ReusableInlineAsyncSequence.Disposed;', 1)
    $sourceText = [regex]::Replace(
        $sourceText,
        '(public TaskHandle DisposeAsync\(\) \{\s*Disposals\+\+;\s*)return new TaskHandle\(\);',
        '$1return ControlAsyncSequence.Disposed;',
        [System.Text.RegularExpressions.RegexOptions]::None,
        [TimeSpan]::FromSeconds(2))
    $sourceText = [regex]::Replace(
        $sourceText,
        'Task<bool> pending = new Task<bool>\(\);\s*pending\.SetTask\(AwaitForeachPendingProbe\.Source\.Task\);\s*return pending;',
        'return AwaitForeachPendingProbe.Source.Task;',
        [System.Text.RegularExpressions.RegexOptions]::None,
        [TimeSpan]::FromSeconds(2))
    $sourceText = [regex]::Replace(
        $sourceText,
        'Task<bool> pending = new Task<bool>\(\);\s*pending\.SetTask\(AwaitForeachControlAllocationProbe\.Source\.Task\);\s*return pending;',
        'return AwaitForeachControlAllocationProbe.Source.Task;',
        [System.Text.RegularExpressions.RegexOptions]::None,
        [TimeSpan]::FromSeconds(2))
    if ($sourceText.Contains('ValueTask<bool> MoveNextAsync()') -or
        $sourceText.Contains('ValueTask DisposeAsync()') -or
        $sourceText.Contains('pending.SetTask(AwaitForeachPendingProbe.Source.Task)') -or
        $sourceText.Contains('return new Task<bool>(true);') -or
        $sourceText.Contains('return new Task<bool>(false);')) {
        throw 'Task enumerator contract rewrite did not cover the benchmark source.'
    }
}
[IO.File]::WriteAllText($sourcePath, $sourceText, $utf8NoBom)
$mainText = @'
#include "bench.h"
int main(void) {
    return bench_System_Collections_DirectIteratorForeachChecks();
}
'@
[IO.File]::WriteAllText($mainPath, $mainText, $utf8NoBom)

try {
    $arguments = @(
        '--compile', '--cxcore-dir', $cxCorePath, '--verbosity', 'quiet',
        '--output-dir', $generatedDirectory, $projectPath
    )
    if ([IO.Path]::GetExtension($compilerPath) -ieq '.dll') {
        & dotnet $compilerPath @arguments
    } else {
        & $compilerPath @arguments
    }
    if ($LASTEXITCODE -ne 0) {
        throw "Coroutine benchmark fixture generation failed (exit $LASTEXITCODE). Build files are in '$buildRoot'."
    }

    $internalHeader = Get-Content -LiteralPath (Join-Path $generatedDirectory 'bench.internal.h') -Raw
    $iteratorMatch = [regex]::Match(
        $internalHeader,
        'CX_TYPE_DEF\(bench_System_Collections_(__CxIterator_Range_[A-Fa-f0-9]+)\)')
    if (-not $iteratorMatch.Success) {
        throw "Could not find the generated Range iterator frame in '$generatedDirectory\bench.internal.h'."
    }
    $iteratorName = $iteratorMatch.Groups[1].Value
    $branchingIteratorMatch = [regex]::Match(
        $internalHeader,
        'CX_TYPE_DEF\(bench_System_Collections_(__CxIterator_BranchingRange_[A-Fa-f0-9]+)\)')
    if (-not $branchingIteratorMatch.Success) {
        throw "Could not find the generated BranchingRange iterator frame in '$generatedDirectory\bench.internal.h'."
    }
    $branchingIteratorName = $branchingIteratorMatch.Groups[1].Value
    $capturedBranchIteratorMatch = [regex]::Match(
        $internalHeader,
        'CX_TYPE_DEF\(bench_System_Collections_(__CxIterator_BranchingValues_[A-Fa-f0-9]+)\)')
    if (-not $capturedBranchIteratorMatch.Success) {
        throw "Could not find the generated BranchingValues iterator frame in '$generatedDirectory\bench.internal.h'."
    }
    $capturedBranchIteratorName = $capturedBranchIteratorMatch.Groups[1].Value
    $capturedAsyncBranchIteratorMatch = [regex]::Match(
        $internalHeader,
        'CX_TYPE_DEF\(bench_System_Collections_(__CxIterator_AsyncBranchValues_[A-Fa-f0-9]+)\)')
    if (-not $capturedAsyncBranchIteratorMatch.Success) {
        throw "Could not find the generated AsyncBranchValues iterator frame in '$generatedDirectory\bench.internal.h'."
    }
    $capturedAsyncBranchIteratorName = $capturedAsyncBranchIteratorMatch.Groups[1].Value
    $referenceIteratorMatch = [regex]::Match(
        $internalHeader,
        'CX_TYPE_DEF\(bench_System_Collections_(__CxIterator_ReferenceRange_[A-Fa-f0-9]+)\)')
    if (-not $referenceIteratorMatch.Success) {
        throw "Could not find the generated ReferenceRange iterator frame in '$generatedDirectory\bench.internal.h'."
    }
    $referenceIteratorName = $referenceIteratorMatch.Groups[1].Value
    $payloadIteratorMatch = [regex]::Match(
        $internalHeader,
        'CX_TYPE_DEF\(bench_System_Collections_(__CxIterator_PayloadRange_[A-Fa-f0-9]+)\)')
    if (-not $payloadIteratorMatch.Success) {
        throw "Could not find the generated PayloadRange iterator frame in '$generatedDirectory\bench.internal.h'."
    }
    $payloadIteratorName = $payloadIteratorMatch.Groups[1].Value
    $largePayloadIteratorMatch = [regex]::Match(
        $internalHeader,
        'CX_TYPE_DEF\(bench_System_Collections_(__CxIterator_LargePayloadRange_[A-Fa-f0-9]+)\)')
    if (-not $largePayloadIteratorMatch.Success) {
        throw "Could not find the generated LargePayloadRange iterator frame in '$generatedDirectory\bench.internal.h'."
    }
    $largePayloadIteratorName = $largePayloadIteratorMatch.Groups[1].Value
    $closedGenericIteratorMatch = [regex]::Matches(
        $internalHeader,
        'struct (?<name>cx_generic_[A-Fa-f0-9]+)\s*\{(?<body>[^}]+)\}') |
        Where-Object {
            $_.Groups['body'].Value -match 'cx_int __cx_current;' -and
            $_.Groups['body'].Value -match '__cx_receiver;'
        } | Select-Object -First 1
    if ($null -eq $closedGenericIteratorMatch) {
        throw "Could not find the closed int GenericValues frame in '$generatedDirectory\bench.internal.h'."
    }
    $closedGenericIteratorName = $closedGenericIteratorMatch.Groups['name'].Value
    $closedGenericPayloadIteratorMatch = [regex]::Matches(
        $internalHeader,
        'struct (?<name>cx_generic_[A-Fa-f0-9]+)\s*\{(?<body>[^}]+)\}') |
        Where-Object {
            $_.Groups['body'].Value -match 'struct bench_System_Collections_IteratorPayload __cx_current;' -and
            $_.Groups['body'].Value -match '__cx_receiver;'
        } | Select-Object -First 1
    if ($null -eq $closedGenericPayloadIteratorMatch) {
        throw "Could not find the closed IteratorPayload GenericValues frame in '$generatedDirectory\bench.internal.h'."
    }
    $closedGenericPayloadIteratorName = $closedGenericPayloadIteratorMatch.Groups['name'].Value
    # Reference-type generic arguments share the open iterator frame because both
    # the open element slot and a closed reference slot are pointer-sized.
    $genericReferenceFrameMatch = [regex]::Match(
        $internalHeader,
        'CX_TYPE_DEF\(bench_System_Collections_(?<name>__CxIterator_GenericValues_[A-Fa-f0-9]+)\)\s*\{(?<body>[^}]+)\}')
    if (-not $genericReferenceFrameMatch.Success -or
        $genericReferenceFrameMatch.Groups['body'].Value -notmatch 'cx_ptr __cx_current;' -or
        $genericReferenceFrameMatch.Groups['body'].Value -notmatch '__cx_receiver;') {
        throw "Could not find the pointer-sized string GenericValues frame in '$generatedDirectory\bench.internal.h'."
    }
    $genericReferenceFrameName = $genericReferenceFrameMatch.Groups['name'].Value
    $generatedC = Get-Content -LiteralPath (Join-Path $generatedDirectory 'bench.c') -Raw
    $typedCurrentThunk = 'static cx_int ' +
        [regex]::Escape($closedGenericIteratorName) +
        '___iface_thunk_bench_System_Collections_IEnumerator_slot_2\(cx_ptr __root\)'
    if ($generatedC -notmatch $typedCurrentThunk) {
        throw "The closed generic int iterator does not emit a cx_int IEnumerator.Current thunk in '$generatedDirectory\bench.c'."
    }
    $typedPayloadCurrentThunk = 'static struct bench_System_Collections_IteratorPayload ' +
        [regex]::Escape($closedGenericPayloadIteratorName) +
        '___iface_thunk_bench_System_Collections_IEnumerator_slot_2\(cx_ptr __root\)'
    if ($generatedC -notmatch $typedPayloadCurrentThunk) {
        throw "The closed generic IteratorPayload iterator does not emit a concrete IteratorPayload IEnumerator.Current thunk in '$generatedDirectory\bench.c'."
    }
    $typedReferenceCurrentThunk = '(?s)static struct cxcore_System_String\* cx_generic_[A-Fa-f0-9]+___iface_thunk_bench_System_Collections_IEnumerator_slot_2\(cx_ptr __root\)\s*\{(?<body>.*?)\n\}'
    $typedReferenceCurrentThunkMatch = [regex]::Match($generatedC, $typedReferenceCurrentThunk)
    if (-not $typedReferenceCurrentThunkMatch.Success -or
        $typedReferenceCurrentThunkMatch.Groups['body'].Value -notmatch '__CxIterator_GenericValues_' -or
        $typedReferenceCurrentThunkMatch.Groups['body'].Value -notmatch '__cx_current') {
        throw "The closed generic string iterator does not emit a concrete string IEnumerator.Current thunk in '$generatedDirectory\bench.c'."
    }
    $asyncIteratorMatch = [regex]::Match(
        $internalHeader,
        'CX_TYPE_DEF\(bench_System_Collections_([A-Za-z0-9_]*AsyncRange_[A-Fa-f0-9]+)\)')
    if (-not $asyncIteratorMatch.Success) {
        throw "Could not find the generated AsyncRange iterator frame in '$generatedDirectory\bench.internal.h'."
    }
    $asyncIteratorName = $asyncIteratorMatch.Groups[1].Value
    $pendingAsyncIteratorMatch = [regex]::Match(
        $internalHeader,
        'CX_TYPE_DEF\(bench_System_Collections_([A-Za-z0-9_]*AsyncAwaitOne_[A-Fa-f0-9]+)\)')
    if (-not $pendingAsyncIteratorMatch.Success) {
        throw "Could not find the generated AsyncAwaitOne iterator frame in '$generatedDirectory\bench.internal.h'."
    }
    $pendingAsyncIteratorName = $pendingAsyncIteratorMatch.Groups[1].Value
    $cSource = @'
#if !defined(_WIN32)
#define _POSIX_C_SOURCE 200809L
#include <stdint.h>
#include <time.h>
typedef struct { int64_t QuadPart; } LARGE_INTEGER;
static int QueryPerformanceFrequency(LARGE_INTEGER *frequency) {
    frequency->QuadPart = INT64_C(1000000000);
    return 1;
}
static int QueryPerformanceCounter(LARGE_INTEGER *counter) {
    struct timespec now;
    if (clock_gettime(CLOCK_MONOTONIC, &now) != 0) return 0;
    counter->QuadPart = (int64_t)now.tv_sec * INT64_C(1000000000) + now.tv_nsec;
    return 1;
}
#define CX_NOINLINE __attribute__((noinline))
#else
#include <windows.h>
#define CX_NOINLINE __declspec(noinline)
#endif
#include <inttypes.h>
#include <stddef.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "bench.internal.h"

#define CX_BENCH_ELEMENTS __ELEMENTS__
#define CX_BENCH_ITERATIONS __ITERATIONS__
#define CX_BENCH_ASYNC_ITERATOR_ITERATIONS __ASYNC_ITERATOR_ITERATIONS__
#define CX_BENCH_AWAIT_ITERATIONS __AWAIT_ITERATIONS__
#define CX_BENCH_RATIO_ITERATIONS (((CX_BENCH_AWAIT_ITERATIONS + 1) / 2) * 2)
#define CX_BENCH_HAND_AWAIT_ITERATIONS __HAND_AWAIT_ITERATIONS__
#define CX_BENCH_GC_ITERATIONS __GC_ITERATIONS__
#define CX_BENCH_SAMPLES __SAMPLES__
#define CX_BENCH_GC_BATCH 8
#define CX_BENCH_GC_BLOCKS 16
#define CX_BENCH_GC_BLOCK_BYTES 4096
#if UINTPTR_MAX == UINT64_MAX
#define CX_BENCH_TASK_ALLOC_BYTES 48u
#define CX_BENCH_PENDING_AWAIT_ALLOC_BYTES 328u
#define CX_BENCH_COMPLETED_AWAIT_ALLOC_BYTES 96u
#define CX_BENCH_SHARED_PENDING_AWAIT_ALLOC_BYTES 600u
#define CX_BENCH_DEEP_COMPLETED_AWAIT_ALLOC_BYTES 192u
#define CX_BENCH_DEEP_PENDING_AWAIT_ALLOC_BYTES 888u
#define CX_BENCH_COLD_REUSABLE_SOURCE_AWAIT_ALLOC_BYTES 652u
#define CX_BENCH_WARM_REUSABLE_SOURCE_AWAIT_ALLOC_BYTES 404u
#else
#define CX_BENCH_TASK_ALLOC_BYTES 24u
#define CX_BENCH_PENDING_AWAIT_ALLOC_BYTES 172u
#define CX_BENCH_COMPLETED_AWAIT_ALLOC_BYTES 48u
#define CX_BENCH_SHARED_PENDING_AWAIT_ALLOC_BYTES 316u
#define CX_BENCH_DEEP_COMPLETED_AWAIT_ALLOC_BYTES 96u
#define CX_BENCH_DEEP_PENDING_AWAIT_ALLOC_BYTES 468u
#define CX_BENCH_COLD_REUSABLE_SOURCE_AWAIT_ALLOC_BYTES 352u
#define CX_BENCH_WARM_REUSABLE_SOURCE_AWAIT_ALLOC_BYTES 212u
#endif

extern size_t cx_memory_test_allocation_count_get(void);
extern size_t cx_memory_test_allocation_bytes_get(void);
extern void cx_memory_test_allocation_tracking_set(cx_bool enabled);
extern size_t cx_memory_test_collection_count_get(void);
extern size_t cx_memory_test_heap_bytes_get(void);

typedef struct {
    struct cxcore_System_Object __base;
    cx_uint state;
    cx_bool has_current;
    cx_int current;
    cx_int parameter;
    cx_int local;
} HandIteratorFrame;

typedef struct {
    cx_uint state;
    cx_int result;
} HandAwaitFrame;

typedef struct {
    cx_uint state;
    cx_int current;
    cx_int count;
} HandAsyncIteratorFrame;

static volatile uint64_t hand_await_invocations;
static int completion_ratio_pending_per_two;
static int completion_ratio_call_index;

CX_NOINLINE static int hand_move_next(HandIteratorFrame *frame, cx_int count) {
    switch (frame->state) {
        case 0:
            frame->local = 0;
            frame->state = 1;
            /* fall through */
        case 1:
            if (frame->local < count) {
                frame->current = frame->local;
                frame->local += 1;
                frame->has_current = CX_TRUE;
                return 1;
            }
            frame->state = 2;
            /* fall through */
        default:
            frame->has_current = CX_FALSE;
            return 0;
    }
}

CX_NOINLINE static cx_int hand_sum(cx_int count) {
    HandIteratorFrame frame = { 0 };
    cx_int sum = 0;
    while (hand_move_next(&frame, count)) {
        sum += frame.current;
    }
    return sum;
}

CX_NOINLINE static cx_int hand_async_iterator_sum(cx_int count) {
    HandAsyncIteratorFrame frame = { 0u, 0, count };
    cx_int sum = 0;
    while (frame.state == 0u && frame.current < frame.count) {
        sum += frame.current;
        ++frame.current;
    }
    frame.state = 1u;
    return sum;
}

static double elapsed_ns(LARGE_INTEGER start, LARGE_INTEGER end, LARGE_INTEGER frequency) {
    return ((double)(end.QuadPart - start.QuadPart) * 1000000000.0) /
        (double)frequency.QuadPart;
}

typedef cx_int (*AwaitOperation)(cx_int value);
typedef cx_int (*IteratorOperation)(cx_int count);

static cx_int generated_completed_await(cx_int value) {
    return bench_System_Collections_CompletedAwaitOperation(value);
}

static cx_int generated_pending_await(cx_int value) {
    return bench_System_Collections_PendingAwaitOperation(value);
}

static cx_int generated_cold_reusable_source_await(cx_int value) {
    return bench_System_Collections_ReusableSourceAwaitBench_ColdPending(value);
}

static cx_int generated_warm_reusable_source_await(cx_int value) {
    return bench_System_Collections_ReusableSourceAwaitBench_WarmPending(value);
}

static cx_int generated_warm_reusable_payload_await(cx_int value) {
    return bench_System_Collections_ReusableSourceAwaitBench_WarmPayloadPending(value);
}

static cx_int generated_shared_pending_await(cx_int value) {
    if (bench_System_Collections_SharedPendingAwaitSession_Begin(value / 2) != 0) return -1;
    if (bench_System_Collections_SharedPendingAwaitSession_Complete() != 0) return -2;
    return bench_System_Collections_SharedPendingAwaitSession_ReadBoth();
}

static cx_int generated_completion_ratio_await(cx_int value) {
    int pending = (completion_ratio_call_index++ & 1) < completion_ratio_pending_per_two;
    return pending ? generated_pending_await(value) : generated_completed_await(value);
}

static cx_int generated_nested_completed_await(cx_int value) {
    return bench_System_Collections_NestedCompletedAwaitOperation(value);
}

static cx_int generated_nested_pending_await(cx_int value) {
    return bench_System_Collections_NestedPendingAwaitOperation(value);
}

static cx_int generated_deep_completed_await(cx_int value) {
    return bench_System_Collections_DeepCompletedAwaitOperation(value);
}

static cx_int generated_deep_payload_completed_await(cx_int value) {
    return bench_System_Collections_DeepPayloadCompletedAwaitOperation(value);
}

static cx_int generated_deep_pending_await(cx_int value) {
    return bench_System_Collections_DeepPendingAwaitOperation(value);
}

static cx_int generated_inline_value_task_as_task(cx_int value) {
    return bench_System_Collections_InlineValueTaskAsTaskOperation(value);
}

static cx_int generated_task_backed_value_task_as_task(cx_int value) {
    return bench_System_Collections_TaskBackedValueTaskAsTaskOperation(value);
}

static cx_int generated_async_iterator_sum(cx_int count) {
    return bench_System_Collections_AsyncIteratorSum(count);
}

static cx_int generated_pending_async_iterator_resume(cx_int value) {
    return bench_System_Collections_PendingAsyncIteratorResume(value);
}

CX_NOINLINE static cx_int hand_completed_await(cx_int value) {
    HandAwaitFrame frame = { 0 };
    frame.state = 2u;
    frame.result = value;
    ++hand_await_invocations;
    return frame.result;
}

CX_NOINLINE static void hand_await_resume(HandAwaitFrame *frame, cx_int value) {
    if (frame->state == 1u) {
        frame->result = value;
        frame->state = 2u;
    }
    ++hand_await_invocations;
}

CX_NOINLINE static cx_int hand_pending_await(cx_int value) {
    HandAwaitFrame frame = { 0 };
    frame.state = 1u;
    hand_await_resume(&frame, value);
    return frame.result;
}

CX_NOINLINE static cx_int hand_pending_async_iterator_resume(cx_int value) {
    HandAsyncIteratorFrame frame = { 1u, 0, 0 };
    frame.current = value;
    frame.state = 2u;
    return frame.current;
}

CX_NOINLINE static cx_int hand_nested_pending_await(cx_int value) {
    HandAwaitFrame inner = { 0 };
    HandAwaitFrame outer = { 0 };
    inner.state = 1u;
    outer.state = 1u;
    hand_await_resume(&inner, value);
    hand_await_resume(&outer, inner.result);
    return outer.result;
}

CX_NOINLINE static cx_int hand_nested_completed_await(cx_int value) {
    return hand_completed_await(hand_completed_await(value));
}

static void run_await_scenario(
    const char *name,
    AwaitOperation operation,
    size_t frame_bytes,
    int sample,
    int operation_count,
    cx_int expected) {
    LARGE_INTEGER frequency;
    LARGE_INTEGER start;
    LARGE_INTEGER end;
    size_t allocations_before;
    size_t allocations_after;
    size_t bytes_before;
    size_t bytes_after;
    int iteration;
    if (!QueryPerformanceFrequency(&frequency) || frequency.QuadPart <= 0) abort();
    cx_gc_collect();
    QueryPerformanceCounter(&start);
    for (iteration = 0; iteration < operation_count; ++iteration) {
        cx_int actual = operation(expected);
        if (actual != expected) {
            fprintf(stderr, "%s warmup operation %d returned %d, expected %d\n",
                name, iteration, actual, expected);
            abort();
        }
    }
    QueryPerformanceCounter(&end);
    cx_gc_collect();
    cx_memory_test_allocation_tracking_set(CX_TRUE);
    allocations_before = cx_memory_test_allocation_count_get();
    bytes_before = cx_memory_test_allocation_bytes_get();
    for (iteration = 0; iteration < operation_count; ++iteration) {
        cx_int actual = operation(expected);
        if (actual != expected) {
            fprintf(stderr, "%s allocation operation %d returned %d, expected %d\n",
                name, iteration, actual, expected);
            abort();
        }
    }
    allocations_after = cx_memory_test_allocation_count_get();
    bytes_after = cx_memory_test_allocation_bytes_get();
    cx_memory_test_allocation_tracking_set(CX_FALSE);
    if (strcmp(name, "shared_pending_await_generated") == 0 &&
        (allocations_after - allocations_before != (size_t)operation_count * 13u ||
         bytes_after - bytes_before != (size_t)operation_count * CX_BENCH_SHARED_PENDING_AWAIT_ALLOC_BYTES)) {
        fprintf(stderr,
            "shared pending await allocations: %zu/%zu objects, %zu/%zu bytes\n",
            allocations_after - allocations_before, (size_t)operation_count * 13u,
            bytes_after - bytes_before,
            (size_t)operation_count * CX_BENCH_SHARED_PENDING_AWAIT_ALLOC_BYTES);
        abort();
    }
    if ((strcmp(name, "as_task_inline_valuetask_generated") == 0 ||
         strcmp(name, "as_task_task_backed_valuetask_generated") == 0) &&
        (allocations_after - allocations_before != (size_t)operation_count ||
         bytes_after - bytes_before != (size_t)operation_count * CX_BENCH_TASK_ALLOC_BYTES)) {
        fprintf(stderr,
            "%s allocation mismatch: %zu/%d objects, %zu/%d bytes\n",
            name, allocations_after - allocations_before, operation_count,
            bytes_after - bytes_before, operation_count * CX_BENCH_TASK_ALLOC_BYTES);
        abort();
    }
    if (strcmp(name, "deep_completed_await_generated") == 0 &&
        (allocations_after - allocations_before != (size_t)operation_count * 4u ||
         bytes_after - bytes_before != (size_t)operation_count * CX_BENCH_DEEP_COMPLETED_AWAIT_ALLOC_BYTES)) {
        fprintf(stderr,
            "%s allocation mismatch: %zu/%d objects, %zu/%d bytes\n",
            name, allocations_after - allocations_before, operation_count * 4,
            bytes_after - bytes_before,
            operation_count * CX_BENCH_DEEP_COMPLETED_AWAIT_ALLOC_BYTES);
        abort();
    }
    if (strcmp(name, "deep_pending_await_generated") == 0 &&
        (allocations_after - allocations_before != (size_t)operation_count * 19u ||
         bytes_after - bytes_before != (size_t)operation_count * CX_BENCH_DEEP_PENDING_AWAIT_ALLOC_BYTES)) {
        fprintf(stderr,
            "%s allocation mismatch: %zu/%d objects, %zu/%d bytes\n",
            name, allocations_after - allocations_before, operation_count * 19,
            bytes_after - bytes_before,
            operation_count * CX_BENCH_DEEP_PENDING_AWAIT_ALLOC_BYTES);
        abort();
    }
    if (strcmp(name, "cold_reusable_source_await_generated") == 0 &&
        (allocations_after - allocations_before != (size_t)operation_count * 14u ||
         bytes_after - bytes_before != (size_t)operation_count * CX_BENCH_COLD_REUSABLE_SOURCE_AWAIT_ALLOC_BYTES)) {
        fprintf(stderr,
            "%s allocation mismatch: %zu/%d objects, %zu/%d bytes\n",
            name, allocations_after - allocations_before, operation_count * 14,
            bytes_after - bytes_before,
            operation_count * CX_BENCH_COLD_REUSABLE_SOURCE_AWAIT_ALLOC_BYTES);
        abort();
    }
    if (strcmp(name, "warm_reusable_source_await_generated") == 0 &&
        (allocations_after - allocations_before != (size_t)operation_count * 11u ||
         bytes_after - bytes_before != (size_t)operation_count * CX_BENCH_WARM_REUSABLE_SOURCE_AWAIT_ALLOC_BYTES)) {
        fprintf(stderr,
            "%s allocation mismatch: %zu/%d objects, %zu/%d bytes\n",
            name, allocations_after - allocations_before, operation_count * 11,
            bytes_after - bytes_before,
            operation_count * CX_BENCH_WARM_REUSABLE_SOURCE_AWAIT_ALLOC_BYTES);
        abort();
    }
    cx_gc_collect();
    printf("%s,%d,%d,1,%zu,%zu,%zu,%.6f,%.2f,0,%.2f\n",
        name, sample, operation_count, frame_bytes,
        allocations_after - allocations_before,
        bytes_after - bytes_before,
        (double)(allocations_after - allocations_before) / operation_count,
        (double)(bytes_after - bytes_before) / operation_count,
        elapsed_ns(start, end, frequency) / operation_count);
    fflush(stdout);
}

static void run_completion_ratio_scenario(
    const char *name,
    int pending_per_two,
    int sample,
    int operation_count,
    cx_int expected) {
    LARGE_INTEGER frequency;
    LARGE_INTEGER start;
    LARGE_INTEGER end;
    size_t allocations_before;
    size_t allocations_after;
    size_t bytes_before;
    size_t bytes_after;
    size_t expected_allocations;
    size_t expected_bytes;
    size_t pending_operations;
    size_t completed_operations;
    size_t collections_before;
    size_t collections_after;
    int iteration;
    if ((operation_count & 1) != 0 || pending_per_two < 0 || pending_per_two > 2) abort();
    if (!QueryPerformanceFrequency(&frequency) || frequency.QuadPart <= 0) abort();
    completion_ratio_pending_per_two = pending_per_two;
    completion_ratio_call_index = 0;
    cx_gc_collect();
    collections_before = cx_memory_test_collection_count_get();
    QueryPerformanceCounter(&start);
    for (iteration = 0; iteration < operation_count; ++iteration) {
        if (generated_completion_ratio_await(expected) != expected) abort();
    }
    QueryPerformanceCounter(&end);
    collections_after = cx_memory_test_collection_count_get();
    cx_gc_collect();
    completion_ratio_call_index = 0;
    cx_memory_test_allocation_tracking_set(CX_TRUE);
    allocations_before = cx_memory_test_allocation_count_get();
    bytes_before = cx_memory_test_allocation_bytes_get();
    for (iteration = 0; iteration < operation_count; ++iteration) {
        if (generated_completion_ratio_await(expected) != expected) abort();
    }
    allocations_after = cx_memory_test_allocation_count_get();
    bytes_after = cx_memory_test_allocation_bytes_get();
    cx_memory_test_allocation_tracking_set(CX_FALSE);
    cx_gc_collect();

    pending_operations = (size_t)(operation_count / 2) * (size_t)pending_per_two;
    completed_operations = (size_t)operation_count - pending_operations;
    expected_allocations = pending_operations * 7u + completed_operations * 2u;
    expected_bytes = pending_operations * CX_BENCH_PENDING_AWAIT_ALLOC_BYTES +
        completed_operations * CX_BENCH_COMPLETED_AWAIT_ALLOC_BYTES;
    if (allocations_after - allocations_before != expected_allocations ||
        bytes_after - bytes_before != expected_bytes) {
        fprintf(stderr,
            "%s allocation mismatch: %zu/%zu objects, %zu/%zu bytes\n",
            name, allocations_after - allocations_before, expected_allocations,
            bytes_after - bytes_before, expected_bytes);
        abort();
    }
    printf("%s,%d,%d,1,0,%zu,%zu,%.6f,%.2f,%zu,%.2f\n",
        name, sample, operation_count,
        allocations_after - allocations_before,
        bytes_after - bytes_before,
        (double)(allocations_after - allocations_before) / operation_count,
        (double)(bytes_after - bytes_before) / operation_count,
        collections_after - collections_before,
        elapsed_ns(start, end, frequency) / operation_count);
    fflush(stdout);
}

static void run_iterator_scenario(
    const char *name,
    IteratorOperation operation,
    size_t frame_bytes,
    int sample,
    int operation_count,
    cx_int element_count,
    cx_int expected) {
    LARGE_INTEGER frequency;
    LARGE_INTEGER start;
    LARGE_INTEGER end;
    size_t allocations_before;
    size_t allocations_after;
    size_t bytes_before;
    size_t bytes_after;
    int iteration;
    if (!QueryPerformanceFrequency(&frequency) || frequency.QuadPart <= 0) abort();
    cx_gc_collect();
    QueryPerformanceCounter(&start);
    for (iteration = 0; iteration < operation_count; ++iteration) {
        if (operation(element_count) != expected) abort();
    }
    QueryPerformanceCounter(&end);
    cx_gc_collect();
    cx_memory_test_allocation_tracking_set(CX_TRUE);
    allocations_before = cx_memory_test_allocation_count_get();
    bytes_before = cx_memory_test_allocation_bytes_get();
    for (iteration = 0; iteration < operation_count; ++iteration) {
        if (operation(element_count) != expected) abort();
    }
    allocations_after = cx_memory_test_allocation_count_get();
    bytes_after = cx_memory_test_allocation_bytes_get();
    cx_memory_test_allocation_tracking_set(CX_FALSE);
    cx_gc_collect();
    printf("%s,%d,%d,%d,%zu,%zu,%zu,%.6f,%.2f,0,%.2f\n",
        name, sample, operation_count, element_count, frame_bytes,
        allocations_after - allocations_before,
        bytes_after - bytes_before,
        (double)(allocations_after - allocations_before) / operation_count,
        (double)(bytes_after - bytes_before) / operation_count,
        elapsed_ns(start, end, frequency) / operation_count);
    fflush(stdout);
}

static void run_gc_pressure_scenario(
    const char *name,
    AwaitOperation operation,
    size_t frame_bytes,
    int sample,
    int operation_count,
    cx_int expected) {
    LARGE_INTEGER frequency;
    LARGE_INTEGER start;
    LARGE_INTEGER end;
    size_t allocations_before;
    size_t allocations_after;
    size_t bytes_before;
    size_t bytes_after;
    size_t collections_before;
    size_t collections_after;
    int iteration;
    int block;
    if (!QueryPerformanceFrequency(&frequency) || frequency.QuadPart <= 0) abort();

    collections_before = cx_memory_test_collection_count_get();
    QueryPerformanceCounter(&start);
    for (iteration = 0; iteration < operation_count; ++iteration) {
        if (operation(expected) != expected) abort();
        if ((iteration + 1) % CX_BENCH_GC_BATCH == 0) {
            for (block = 0; block < CX_BENCH_GC_BLOCKS; ++block) {
                (void)cxcore_System_Memory_Alloc(CX_BENCH_GC_BLOCK_BYTES);
            }
            cx_gc_collect();
        }
    }
    QueryPerformanceCounter(&end);
    collections_after = cx_memory_test_collection_count_get();

    cx_memory_test_allocation_tracking_set(CX_TRUE);
    allocations_before = cx_memory_test_allocation_count_get();
    bytes_before = cx_memory_test_allocation_bytes_get();
    for (iteration = 0; iteration < operation_count; ++iteration) {
        if (operation(expected) != expected) abort();
        if ((iteration + 1) % CX_BENCH_GC_BATCH == 0) {
            for (block = 0; block < CX_BENCH_GC_BLOCKS; ++block) {
                (void)cxcore_System_Memory_Alloc(CX_BENCH_GC_BLOCK_BYTES);
            }
            cx_gc_collect();
        }
    }
    allocations_after = cx_memory_test_allocation_count_get();
    bytes_after = cx_memory_test_allocation_bytes_get();
    cx_memory_test_allocation_tracking_set(CX_FALSE);

    printf("%s,%d,%d,1,%zu,%zu,%zu,%.6f,%.2f,%zu,%.2f\n",
        name, sample, operation_count, frame_bytes,
        allocations_after - allocations_before,
        bytes_after - bytes_before,
        (double)(allocations_after - allocations_before) / operation_count,
        (double)(bytes_after - bytes_before) / operation_count,
        collections_after - collections_before,
        elapsed_ns(start, end, frequency) / operation_count);
    fflush(stdout);
}

int main(void) {
    LARGE_INTEGER frequency;
    LARGE_INTEGER start;
    LARGE_INTEGER end;
    cx_int expected = (cx_int)(((int64_t)CX_BENCH_ELEMENTS *
        (CX_BENCH_ELEMENTS - 1)) / 2);
    volatile int64_t checksum = 0;
    volatile cx_int element_count = CX_BENCH_ELEMENTS;
    size_t allocations_before;
    size_t allocations_after;
    size_t bytes_before;
    size_t bytes_after;
    size_t single_element_allocations;
    size_t single_element_bytes;
    size_t full_enumeration_allocations;
    size_t full_enumeration_bytes;
    size_t captured_one_allocations;
    size_t captured_one_bytes;
    size_t captured_async_one_allocations;
    size_t captured_async_one_bytes;
    size_t captured_async_full_allocations;
    size_t captured_async_full_bytes;
    size_t shared_pending_allocations;
    size_t shared_pending_bytes;
    size_t deep_payload_completed_allocations;
    size_t deep_payload_completed_bytes;
    size_t deep_payload_allocations;
    size_t deep_payload_bytes;
    size_t warm_source_heap_baseline;
    size_t warm_source_heap_after[3];
    size_t warm_payload_heap_baseline;
    size_t warm_payload_heap_after[3];
    cx_int captured_branch_expected = 0;
    cx_int captured_async_expected = 0;
    int sample;
    int iteration;
    int captured_index;
    int result;
    if (!QueryPerformanceFrequency(&frequency) || frequency.QuadPart <= 0) return 2;
    result = bench_System_Collections_DirectIteratorForeachChecks();
    if (result != 0) return result;
    bench_System_Collections_AwaitForeachAllocationProbe_Initialize();
    if (bench_System_Collections_AwaitForeachAllocationProbe_RunInline() != 43) return 9;
    cx_memory_test_allocation_tracking_set(CX_TRUE);
    allocations_before = cx_memory_test_allocation_count_get();
    bytes_before = cx_memory_test_allocation_bytes_get();
    result = bench_System_Collections_AwaitForeachAllocationProbe_RunInline();
    allocations_after = cx_memory_test_allocation_count_get();
    bytes_after = cx_memory_test_allocation_bytes_get();
    cx_memory_test_allocation_tracking_set(CX_FALSE);
    if (result != 43 || allocations_after - allocations_before != 1u ||
        bytes_after - bytes_before != CX_BENCH_TASK_ALLOC_BYTES) {
        fprintf(stderr,
            "all-inline await-foreach allocated %zu objects/%zu bytes; expected one Task (%u bytes)\n",
            allocations_after - allocations_before,
            bytes_after - bytes_before, CX_BENCH_TASK_ALLOC_BYTES);
        return 10;
    }
    fprintf(stderr,
        "PASS all-inline await-foreach: one returned Task, %zu bytes\n",
        bytes_after - bytes_before);
    bench_System_Collections_AwaitForeachControlAllocationProbe_Initialize();
    if (bench_System_Collections_AwaitForeachControlAllocationProbe_RunInline() != 2) return 13;
    cx_memory_test_allocation_tracking_set(CX_TRUE);
    allocations_before = cx_memory_test_allocation_count_get();
    bytes_before = cx_memory_test_allocation_bytes_get();
    result = bench_System_Collections_AwaitForeachControlAllocationProbe_RunInline();
    allocations_after = cx_memory_test_allocation_count_get();
    bytes_after = cx_memory_test_allocation_bytes_get();
    cx_memory_test_allocation_tracking_set(CX_FALSE);
    if (result != 2 || allocations_after - allocations_before != 1u ||
        bytes_after - bytes_before != CX_BENCH_TASK_ALLOC_BYTES) {
        fprintf(stderr,
            "all-inline await-foreach control body allocated %zu objects/%zu bytes; expected one Task (%u bytes)\n",
            allocations_after - allocations_before,
            bytes_after - bytes_before, CX_BENCH_TASK_ALLOC_BYTES);
        return 14;
    }
    fprintf(stderr,
        "PASS all-inline await-foreach break/continue: one returned Task, %zu bytes\n",
        bytes_after - bytes_before);
    if (bench_System_Collections_AwaitForeachControlAllocationProbe_BeginPending() != 0) return 15;
    cx_gc_collect();
    bench_System_Collections_AwaitForeachControlAllocationProbe_CompleteMove();
    if (bench_System_Collections_AwaitForeachControlAllocationProbe_Finish() != 0) return 16;
    fprintf(stderr,
        "PASS pending await-foreach control body survives forced GC and transfers break/continue\n");
    bench_System_Collections_AwaitForeachPendingProbe_Initialize();
    if (bench_System_Collections_AwaitForeachPendingProbe_Begin() != 0) return 11;
    cx_gc_collect();
    bench_System_Collections_AwaitForeachPendingProbe_CompleteMove();
    if (bench_System_Collections_AwaitForeachPendingProbe_Finish() != 0) return 12;
    fprintf(stderr,
        "PASS pending await-foreach move survives forced GC and disposes once\n");
    cx_memory_test_allocation_tracking_set(CX_TRUE);
    allocations_before = cx_memory_test_allocation_count_get();
    bytes_before = cx_memory_test_allocation_bytes_get();
    if (bench_System_Collections_GenericIntIteratorSum(1) != 7) return 70;
    allocations_after = cx_memory_test_allocation_count_get();
    bytes_after = cx_memory_test_allocation_bytes_get();
    single_element_allocations = allocations_after - allocations_before;
    single_element_bytes = bytes_after - bytes_before;
    allocations_before = allocations_after;
    bytes_before = bytes_after;
    if (bench_System_Collections_GenericIntIteratorSum(16) != 112) return 71;
    allocations_after = cx_memory_test_allocation_count_get();
    bytes_after = cx_memory_test_allocation_bytes_get();
    cx_memory_test_allocation_tracking_set(CX_FALSE);
    full_enumeration_allocations = allocations_after - allocations_before;
    full_enumeration_bytes = bytes_after - bytes_before;
    if (single_element_allocations != full_enumeration_allocations ||
        single_element_bytes != full_enumeration_bytes) {
        fprintf(stderr,
            "generic-owner int iterator allocations scaled with element count "
            "(%zu/%zu objects, %zu/%zu bytes)\n",
            single_element_allocations, full_enumeration_allocations,
            single_element_bytes, full_enumeration_bytes);
        return 72;
    }
    fprintf(stderr,
        "generic-owner int iterator allocations,1/16 elements: %zu/%zu objects, %zu/%zu bytes; generated frame=%zu bytes\n",
        single_element_allocations, full_enumeration_allocations,
        single_element_bytes, full_enumeration_bytes,
        sizeof(struct __GENERIC_ITERATOR_NAME__));
    fprintf(stderr,
        "PASS generic-owner int iterator values and no per-yield allocations\n");
    cx_memory_test_allocation_tracking_set(CX_TRUE);
    allocations_before = cx_memory_test_allocation_count_get();
    bytes_before = cx_memory_test_allocation_bytes_get();
    if (bench_System_Collections_GenericPayloadIteratorSum(1) != 7) return 73;
    allocations_after = cx_memory_test_allocation_count_get();
    bytes_after = cx_memory_test_allocation_bytes_get();
    single_element_allocations = allocations_after - allocations_before;
    single_element_bytes = bytes_after - bytes_before;
    allocations_before = allocations_after;
    bytes_before = bytes_after;
    if (bench_System_Collections_GenericPayloadIteratorSum(16) != 112) return 74;
    allocations_after = cx_memory_test_allocation_count_get();
    bytes_after = cx_memory_test_allocation_bytes_get();
    cx_memory_test_allocation_tracking_set(CX_FALSE);
    full_enumeration_allocations = allocations_after - allocations_before;
    full_enumeration_bytes = bytes_after - bytes_before;
    if (single_element_allocations != full_enumeration_allocations ||
        single_element_bytes != full_enumeration_bytes) {
        fprintf(stderr,
            "generic-owner IteratorPayload iterator allocations scaled with element count "
            "(%zu/%zu objects, %zu/%zu bytes)\n",
            single_element_allocations, full_enumeration_allocations,
            single_element_bytes, full_enumeration_bytes);
        return 75;
    }
    fprintf(stderr,
        "generic-owner IteratorPayload iterator allocations,1/16 elements: %zu/%zu objects, %zu/%zu bytes; generated frame=%zu bytes\n",
        single_element_allocations, full_enumeration_allocations,
        single_element_bytes, full_enumeration_bytes,
        sizeof(struct __GENERIC_PAYLOAD_ITERATOR_NAME__));
    fprintf(stderr,
        "PASS generic-owner IteratorPayload values, references and no per-yield allocations\n");
    cx_memory_test_allocation_tracking_set(CX_TRUE);
    allocations_before = cx_memory_test_allocation_count_get();
    bytes_before = cx_memory_test_allocation_bytes_get();
    if (bench_System_Collections_GenericStringIteratorCount(1) != 1) return 76;
    allocations_after = cx_memory_test_allocation_count_get();
    bytes_after = cx_memory_test_allocation_bytes_get();
    single_element_allocations = allocations_after - allocations_before;
    single_element_bytes = bytes_after - bytes_before;
    allocations_before = allocations_after;
    bytes_before = bytes_after;
    if (bench_System_Collections_GenericStringIteratorCount(16) != 16) return 77;
    allocations_after = cx_memory_test_allocation_count_get();
    bytes_after = cx_memory_test_allocation_bytes_get();
    cx_memory_test_allocation_tracking_set(CX_FALSE);
    full_enumeration_allocations = allocations_after - allocations_before;
    full_enumeration_bytes = bytes_after - bytes_before;
    if (single_element_allocations != full_enumeration_allocations ||
        single_element_bytes != full_enumeration_bytes) {
        fprintf(stderr,
            "generic-owner string iterator allocations scaled with element count "
            "(%zu/%zu objects, %zu/%zu bytes)\n",
            single_element_allocations, full_enumeration_allocations,
            single_element_bytes, full_enumeration_bytes);
        return 78;
    }
    fprintf(stderr,
        "generic-owner string iterator allocations,1/16 elements: %zu/%zu objects, %zu/%zu bytes; generated frame=%zu bytes\n",
        single_element_allocations, full_enumeration_allocations,
        single_element_bytes, full_enumeration_bytes,
        sizeof(struct __GENERIC_REFERENCE_ITERATOR_NAME__));
    fprintf(stderr,
        "PASS generic-owner string values and no per-yield allocations\n");
    cx_memory_test_allocation_tracking_set(CX_TRUE);
    allocations_before = cx_memory_test_allocation_count_get();
    bytes_before = cx_memory_test_allocation_bytes_get();
    if (bench_System_Collections_CapturedAsyncBranchSession_Begin(1) != 0) return 40;
    cx_gc_collect();
    if (bench_System_Collections_CapturedAsyncBranchSession_CompleteSource() != 0) return 41;
    cx_gc_collect();
    result = bench_System_Collections_CapturedAsyncBranchSession_ReadAll();
    if (result != 12) return 42 + result;
    allocations_after = cx_memory_test_allocation_count_get();
    bytes_after = cx_memory_test_allocation_bytes_get();
    captured_async_one_allocations = allocations_after - allocations_before;
    captured_async_one_bytes = bytes_after - bytes_before;
    for (captured_index = 0; captured_index < CX_BENCH_ELEMENTS; ++captured_index) {
        captured_async_expected += 12 + captured_index +
            ((captured_index % 2) == 0 ? 0 : 100);
    }
    allocations_before = allocations_after;
    bytes_before = bytes_after;
    if (bench_System_Collections_CapturedAsyncBranchSession_Begin(CX_BENCH_ELEMENTS) != 0) return 43;
    cx_gc_collect();
    if (bench_System_Collections_CapturedAsyncBranchSession_CompleteSource() != 0) return 44;
    cx_gc_collect();
    result = bench_System_Collections_CapturedAsyncBranchSession_ReadAll();
    if (result != captured_async_expected) return 45 + result;
    allocations_after = cx_memory_test_allocation_count_get();
    bytes_after = cx_memory_test_allocation_bytes_get();
    cx_memory_test_allocation_tracking_set(CX_FALSE);
    captured_async_full_allocations = allocations_after - allocations_before;
    captured_async_full_bytes = bytes_after - bytes_before;
    if (captured_async_one_allocations != captured_async_full_allocations ||
        captured_async_one_bytes != captured_async_full_bytes) {
        fprintf(stderr,
            "captured async branch allocation mismatch: %zu/%zu objects, %zu/%zu bytes\n",
            captured_async_one_allocations, captured_async_full_allocations,
            captured_async_one_bytes, captured_async_full_bytes);
        return 46;
    }
    fprintf(stderr,
        "captured async branch allocations,1/%d elements: %zu/%zu objects, %zu/%zu bytes\n",
        CX_BENCH_ELEMENTS, captured_async_one_allocations,
        captured_async_full_allocations, captured_async_one_bytes,
        captured_async_full_bytes);
    if (bench_System_Collections_CapturedAsyncBranchSession_Begin(3) != 0) return 47;
    cx_gc_collect();
    if (bench_System_Collections_CapturedAsyncBranchSession_CompleteSource() != 0) return 48;
    cx_gc_collect();
    result = bench_System_Collections_CapturedAsyncBranchSession_ReadFirstAndDispose();
    if (result != 0) return 49 + result;
    if (bench_System_Collections_Sum(CX_BENCH_ELEMENTS) != expected ||
        hand_sum(CX_BENCH_ELEMENTS) != expected ||
        generated_async_iterator_sum(CX_BENCH_ELEMENTS) != expected ||
        hand_async_iterator_sum(CX_BENCH_ELEMENTS) != expected ||
        generated_completed_await(73) != 73 || generated_pending_await(73) != 73 ||
        generated_cold_reusable_source_await(73) != 73 ||
        generated_warm_reusable_source_await(73) != 73 ||
        generated_nested_completed_await(73) != 73 ||
        generated_nested_pending_await(73) != 73 ||
        generated_deep_completed_await(73) != 73 ||
        generated_deep_pending_await(73) != 73 ||
        hand_completed_await(73) != 73 || hand_pending_await(73) != 73 ||
        hand_nested_completed_await(73) != 73 || hand_nested_pending_await(73) != 73) return 3;
    if (generated_deep_payload_completed_await(83) != 83) return 66;
    cx_gc_collect();
    cx_memory_test_allocation_tracking_set(CX_TRUE);
    allocations_before = cx_memory_test_allocation_count_get();
    bytes_before = cx_memory_test_allocation_bytes_get();
    if (generated_deep_payload_completed_await(84) != 84) return 63;
    allocations_after = cx_memory_test_allocation_count_get();
    bytes_after = cx_memory_test_allocation_bytes_get();
    cx_memory_test_allocation_tracking_set(CX_FALSE);
    deep_payload_completed_allocations = allocations_after - allocations_before;
    deep_payload_completed_bytes = bytes_after - bytes_before;
    fprintf(stderr,
        "deep mixed-reference completed await through four layers: %zu allocations, %zu requested bytes\n",
        deep_payload_completed_allocations, deep_payload_completed_bytes);
    if (deep_payload_completed_allocations != 5u ||
        deep_payload_completed_bytes != (sizeof(void*) == 8u ? 280u : 140u)) return 64;
    cx_memory_test_allocation_tracking_set(CX_TRUE);
    allocations_before = cx_memory_test_allocation_count_get();
    bytes_before = cx_memory_test_allocation_bytes_get();
    if (bench_System_Collections_DeepPayloadAwaitSession_Begin(85) != 0) return 60;
    cx_gc_collect();
    if (bench_System_Collections_DeepPayloadAwaitSession_Complete() != 0) return 61;
    cx_gc_collect();
    if (bench_System_Collections_DeepPayloadAwaitSession_Read() != 85) return 62;
    allocations_after = cx_memory_test_allocation_count_get();
    bytes_after = cx_memory_test_allocation_bytes_get();
    cx_memory_test_allocation_tracking_set(CX_FALSE);
    deep_payload_allocations = allocations_after - allocations_before;
    deep_payload_bytes = bytes_after - bytes_before;
    fprintf(stderr,
    "deep mixed-reference pending await through four layers: %zu allocations, %zu requested bytes; forced-GC result pass\n",
        deep_payload_allocations, deep_payload_bytes);
    if (deep_payload_allocations != 27u ||
        deep_payload_bytes != (sizeof(void*) == 8u ? 1368u : 780u)) return 65;
    for (iteration = 0; iteration < 64; ++iteration) {
        cx_int value = (cx_int)(73 + iteration);
        if (generated_warm_reusable_source_await(value) != value) return 54;
    }
    cx_gc_collect();
    warm_source_heap_baseline = cx_memory_test_heap_bytes_get();
    for (sample = 0; sample < 3; ++sample) {
        for (iteration = 0; iteration < 64; ++iteration) {
            cx_int value = (cx_int)(sample * 64 + iteration + 1);
            if (generated_warm_reusable_source_await(value) != value) return 55;
        }
        cx_gc_collect();
        warm_source_heap_after[sample] = cx_memory_test_heap_bytes_get();
    }
    fprintf(stderr,
        "warm reusable source heap after drain: baseline %zu, after 64/128/192 cycles %zu/%zu/%zu bytes\n",
        warm_source_heap_baseline, warm_source_heap_after[0],
        warm_source_heap_after[1], warm_source_heap_after[2]);
    if (warm_source_heap_after[2] > warm_source_heap_baseline) {
        fprintf(stderr, "warm reusable source retained heap grew after 192 drained cycles\n");
        return 56;
    }
    for (iteration = 0; iteration < 64; ++iteration) {
        cx_int value = (cx_int)(73 + iteration);
        if (generated_warm_reusable_payload_await(value) != value) return 57;
    }
    cx_gc_collect();
    warm_payload_heap_baseline = cx_memory_test_heap_bytes_get();
    for (sample = 0; sample < 3; ++sample) {
        for (iteration = 0; iteration < 64; ++iteration) {
            cx_int value = (cx_int)(sample * 64 + iteration + 1);
            if (generated_warm_reusable_payload_await(value) != value) return 58;
        }
        cx_gc_collect();
        warm_payload_heap_after[sample] = cx_memory_test_heap_bytes_get();
    }
    fprintf(stderr,
        "warm reusable reference-containing source heap after drain: baseline %zu, after 64/128/192 cycles %zu/%zu/%zu bytes\n",
        warm_payload_heap_baseline, warm_payload_heap_after[0],
        warm_payload_heap_after[1], warm_payload_heap_after[2]);
    if (warm_payload_heap_after[2] > warm_payload_heap_baseline) {
        fprintf(stderr, "warm reusable reference-containing source retained heap grew after 192 drained cycles\n");
        return 59;
    }
    cx_memory_test_allocation_tracking_set(CX_TRUE);
    allocations_before = cx_memory_test_allocation_count_get();
    bytes_before = cx_memory_test_allocation_bytes_get();
    if (bench_System_Collections_SharedPendingAwaitSession_Begin(73) != 0) return 50;
    cx_gc_collect();
    if (bench_System_Collections_SharedPendingAwaitSession_Complete() != 0) return 51;
    cx_gc_collect();
    result = bench_System_Collections_SharedPendingAwaitSession_ReadBoth();
    if (result != 146) return 52;
    allocations_after = cx_memory_test_allocation_count_get();
    bytes_after = cx_memory_test_allocation_bytes_get();
    cx_memory_test_allocation_tracking_set(CX_FALSE);
    shared_pending_allocations = allocations_after - allocations_before;
    shared_pending_bytes = bytes_after - bytes_before;
    fprintf(stderr, "shared pending await lifecycle: %zu objects, %zu bytes\n",
        shared_pending_allocations, shared_pending_bytes);
    if (shared_pending_allocations != 13u ||
        shared_pending_bytes != CX_BENCH_SHARED_PENDING_AWAIT_ALLOC_BYTES) {
        fprintf(stderr, "shared pending await single-operation allocation budget mismatch\n");
        return 53;
    }
    struct bench_System_Collections_DirectIteratorProbe *captured_probe =
        (struct bench_System_Collections_DirectIteratorProbe *)
        cxcore_System_Memory_Alloc((cx_uint)sizeof(struct bench_System_Collections_DirectIteratorProbe));
    bench_System_Collections_DirectIteratorProbe___constructor(captured_probe, 0);
    for (captured_index = 0; captured_index < CX_BENCH_ELEMENTS; ++captured_index) {
        captured_branch_expected += captured_index + ((captured_index % 2) == 0 ? 0 : 100);
    }
    cx_memory_test_allocation_tracking_set(CX_TRUE);
    allocations_before = cx_memory_test_allocation_count_get();
    bytes_before = cx_memory_test_allocation_bytes_get();
    if (bench_System_Collections_DirectIteratorProbe_BranchingValuesSum(captured_probe, 1) != 0) return 30;
    allocations_after = cx_memory_test_allocation_count_get();
    bytes_after = cx_memory_test_allocation_bytes_get();
    captured_one_allocations = allocations_after - allocations_before;
    captured_one_bytes = bytes_after - bytes_before;
    allocations_before = allocations_after;
    bytes_before = bytes_after;
    if (bench_System_Collections_DirectIteratorProbe_BranchingValuesSum(captured_probe, CX_BENCH_ELEMENTS) != captured_branch_expected) return 31;
    allocations_after = cx_memory_test_allocation_count_get();
    bytes_after = cx_memory_test_allocation_bytes_get();
    cx_memory_test_allocation_tracking_set(CX_FALSE);
    full_enumeration_allocations = allocations_after - allocations_before;
    full_enumeration_bytes = bytes_after - bytes_before;
    if (captured_one_allocations != full_enumeration_allocations ||
        captured_one_bytes != full_enumeration_bytes) {
        fprintf(stderr, "captured branch allocation mismatch: %zu/%zu objects, %zu/%zu bytes\n",
            captured_one_allocations, full_enumeration_allocations,
            captured_one_bytes, full_enumeration_bytes);
        return 32;
    }
    fprintf(stderr, "captured branching iterator allocations,1/%d elements: %zu/%zu objects, %zu/%zu bytes\n",
        CX_BENCH_ELEMENTS, captured_one_allocations, full_enumeration_allocations,
        captured_one_bytes, full_enumeration_bytes);
    cx_memory_test_allocation_tracking_set(CX_TRUE);
    allocations_before = cx_memory_test_allocation_count_get();
    bytes_before = cx_memory_test_allocation_bytes_get();
    if (bench_System_Collections_Sum(1) != 0) return 4;
    allocations_after = cx_memory_test_allocation_count_get();
    bytes_after = cx_memory_test_allocation_bytes_get();
    single_element_allocations = allocations_after - allocations_before;
    single_element_bytes = bytes_after - bytes_before;
    allocations_before = allocations_after;
    bytes_before = bytes_after;
    if (bench_System_Collections_Sum(CX_BENCH_ELEMENTS) != expected) return 4;
    allocations_after = cx_memory_test_allocation_count_get();
    bytes_after = cx_memory_test_allocation_bytes_get();
    full_enumeration_allocations = allocations_after - allocations_before;
    full_enumeration_bytes = bytes_after - bytes_before;
    cx_memory_test_allocation_tracking_set(CX_FALSE);
    if (single_element_allocations != full_enumeration_allocations ||
        single_element_bytes != full_enumeration_bytes) {
        fprintf(stderr,
            "synchronous iterator allocations scaled with element count "
            "(%zu/%zu objects, %zu/%zu bytes)\n",
            single_element_allocations, full_enumeration_allocations,
            single_element_bytes, full_enumeration_bytes);
        return 4;
    }
    cx_int branching_expected = 0;
    for (iteration = 0; iteration < CX_BENCH_ELEMENTS; ++iteration) {
        branching_expected += iteration + ((iteration % 2) == 0 ? 10 : 20);
    }
    cx_memory_test_allocation_tracking_set(CX_TRUE);
    allocations_before = cx_memory_test_allocation_count_get();
    bytes_before = cx_memory_test_allocation_bytes_get();
    if (bench_System_Collections_BranchingSum(1) != 10) return 8;
    allocations_after = cx_memory_test_allocation_count_get();
    bytes_after = cx_memory_test_allocation_bytes_get();
    single_element_allocations = allocations_after - allocations_before;
    single_element_bytes = bytes_after - bytes_before;
    allocations_before = allocations_after;
    bytes_before = bytes_after;
    if (bench_System_Collections_BranchingSum(CX_BENCH_ELEMENTS) != branching_expected) return 8;
    allocations_after = cx_memory_test_allocation_count_get();
    bytes_after = cx_memory_test_allocation_bytes_get();
    full_enumeration_allocations = allocations_after - allocations_before;
    full_enumeration_bytes = bytes_after - bytes_before;
    cx_memory_test_allocation_tracking_set(CX_FALSE);
    if (single_element_allocations != full_enumeration_allocations ||
        single_element_bytes != full_enumeration_bytes) {
        fprintf(stderr,
            "branching iterator allocations scaled with element count "
            "(%zu/%zu objects, %zu/%zu bytes)\n",
            single_element_allocations, full_enumeration_allocations,
            single_element_bytes, full_enumeration_bytes);
        return 8;
    }
    fprintf(stderr, "branching iterator allocations,1/%d elements: %zu/%zu objects, %zu/%zu bytes\n",
        CX_BENCH_ELEMENTS, single_element_allocations, full_enumeration_allocations,
        single_element_bytes, full_enumeration_bytes);
    cx_memory_test_allocation_tracking_set(CX_TRUE);
    allocations_before = cx_memory_test_allocation_count_get();
    bytes_before = cx_memory_test_allocation_bytes_get();
    if (bench_System_Collections_PayloadSum(1) != 0) return 5;
    allocations_after = cx_memory_test_allocation_count_get();
    bytes_after = cx_memory_test_allocation_bytes_get();
    single_element_allocations = allocations_after - allocations_before;
    single_element_bytes = bytes_after - bytes_before;
    allocations_before = allocations_after;
    bytes_before = bytes_after;
    if (bench_System_Collections_PayloadSum(CX_BENCH_ELEMENTS) != expected) return 5;
    allocations_after = cx_memory_test_allocation_count_get();
    bytes_after = cx_memory_test_allocation_bytes_get();
    full_enumeration_allocations = allocations_after - allocations_before;
    full_enumeration_bytes = bytes_after - bytes_before;
    cx_memory_test_allocation_tracking_set(CX_FALSE);
    if (single_element_allocations != full_enumeration_allocations ||
        single_element_bytes != full_enumeration_bytes) {
        fprintf(stderr,
            "reference-containing struct iterator allocations scaled with element count "
            "(%zu/%zu objects, %zu/%zu bytes)\n",
            single_element_allocations, full_enumeration_allocations,
            single_element_bytes, full_enumeration_bytes);
        return 5;
    }
    fprintf(stderr, "reference-containing struct iterator allocations,1/%d elements: %zu/%zu objects, %zu/%zu bytes\n",
        CX_BENCH_ELEMENTS, single_element_allocations, full_enumeration_allocations,
        single_element_bytes, full_enumeration_bytes);
    cx_memory_test_allocation_tracking_set(CX_TRUE);
    allocations_before = cx_memory_test_allocation_count_get();
    bytes_before = cx_memory_test_allocation_bytes_get();
    if (bench_System_Collections_ReferenceCount(1) != 1) return 6;
    allocations_after = cx_memory_test_allocation_count_get();
    bytes_after = cx_memory_test_allocation_bytes_get();
    single_element_allocations = allocations_after - allocations_before;
    single_element_bytes = bytes_after - bytes_before;
    allocations_before = allocations_after;
    bytes_before = bytes_after;
    if (bench_System_Collections_ReferenceCount(CX_BENCH_ELEMENTS) != CX_BENCH_ELEMENTS) return 6;
    allocations_after = cx_memory_test_allocation_count_get();
    bytes_after = cx_memory_test_allocation_bytes_get();
    full_enumeration_allocations = allocations_after - allocations_before;
    full_enumeration_bytes = bytes_after - bytes_before;
    cx_memory_test_allocation_tracking_set(CX_FALSE);
    if (single_element_allocations != full_enumeration_allocations ||
        single_element_bytes != full_enumeration_bytes) {
        fprintf(stderr,
            "reference iterator allocations scaled with element count "
            "(%zu/%zu objects, %zu/%zu bytes)\n",
            single_element_allocations, full_enumeration_allocations,
            single_element_bytes, full_enumeration_bytes);
        return 6;
    }
    fprintf(stderr, "reference iterator allocations,1/%d elements: %zu/%zu objects, %zu/%zu bytes\n",
        CX_BENCH_ELEMENTS, single_element_allocations, full_enumeration_allocations,
        single_element_bytes, full_enumeration_bytes);
    cx_memory_test_allocation_tracking_set(CX_TRUE);
    allocations_before = cx_memory_test_allocation_count_get();
    bytes_before = cx_memory_test_allocation_bytes_get();
    if (bench_System_Collections_LargePayloadSum(1) != 0) return 7;
    allocations_after = cx_memory_test_allocation_count_get();
    bytes_after = cx_memory_test_allocation_bytes_get();
    single_element_allocations = allocations_after - allocations_before;
    single_element_bytes = bytes_after - bytes_before;
    allocations_before = allocations_after;
    bytes_before = bytes_after;
    if (bench_System_Collections_LargePayloadSum(CX_BENCH_ELEMENTS) != expected) return 7;
    allocations_after = cx_memory_test_allocation_count_get();
    bytes_after = cx_memory_test_allocation_bytes_get();
    full_enumeration_allocations = allocations_after - allocations_before;
    full_enumeration_bytes = bytes_after - bytes_before;
    cx_memory_test_allocation_tracking_set(CX_FALSE);
    if (single_element_allocations != full_enumeration_allocations ||
        single_element_bytes != full_enumeration_bytes) {
        fprintf(stderr,
            "large-value iterator allocations scaled with element count "
            "(%zu/%zu objects, %zu/%zu bytes)\n",
            single_element_allocations, full_enumeration_allocations,
            single_element_bytes, full_enumeration_bytes);
        return 7;
    }
    fprintf(stderr, "large-value iterator allocations,1/%d elements: %zu/%zu objects, %zu/%zu bytes\n",
        CX_BENCH_ELEMENTS, single_element_allocations, full_enumeration_allocations,
        single_element_bytes, full_enumeration_bytes);
    fprintf(stderr,
        "generated iterator frames: int=%zu, branching=%zu, captured_branch=%zu, captured_async_branch=%zu, reference=%zu, struct=%zu, large_struct=%zu bytes; element sizes: string=%zu, struct=%zu, large_struct=%zu bytes\n",
        sizeof(struct bench_System_Collections___ITERATOR_NAME__),
        sizeof(struct bench_System_Collections___BRANCHING_ITERATOR_NAME__),
        sizeof(struct bench_System_Collections___CAPTURED_BRANCH_ITERATOR_NAME__),
        sizeof(struct bench_System_Collections___CAPTURED_ASYNC_BRANCH_ITERATOR_NAME__),
        sizeof(struct bench_System_Collections___REFERENCE_ITERATOR_NAME__),
        sizeof(struct bench_System_Collections___PAYLOAD_ITERATOR_NAME__),
        sizeof(struct bench_System_Collections___LARGE_PAYLOAD_ITERATOR_NAME__),
        sizeof(struct cxcore_System_String),
        sizeof(struct bench_System_Collections_IteratorPayload),
        sizeof(struct bench_System_Collections_IteratorLargePayload));
    for (iteration = 0; iteration < 2; ++iteration) {
        (void)bench_System_Collections_Sum(CX_BENCH_ELEMENTS);
        (void)hand_sum(CX_BENCH_ELEMENTS);
    }

    puts("scenario,sample,operations,elements_per_operation,frame_bytes,managed_allocations,managed_allocated_bytes,allocations_per_operation,allocated_bytes_per_operation,gc_collections,ns_per_operation");
    fflush(stdout);
    for (sample = 1; sample <= CX_BENCH_SAMPLES; ++sample) {
        run_iterator_scenario("async_iterator_generated",
            generated_async_iterator_sum,
            sizeof(struct bench_System_Collections___ASYNC_ITERATOR_NAME__),
            sample, CX_BENCH_ASYNC_ITERATOR_ITERATIONS, CX_BENCH_ELEMENTS, expected);
        run_iterator_scenario("async_iterator_handwritten_c",
            hand_async_iterator_sum, sizeof(HandAsyncIteratorFrame),
            sample, CX_BENCH_ASYNC_ITERATOR_ITERATIONS, CX_BENCH_ELEMENTS, expected);
        run_await_scenario("async_iterator_pending_resume_generated",
            generated_pending_async_iterator_resume,
            sizeof(struct bench_System_Collections___PENDING_ASYNC_ITERATOR_NAME__), sample,
            CX_BENCH_ASYNC_ITERATOR_ITERATIONS, 73);
        run_await_scenario("async_iterator_pending_resume_handwritten_c",
            hand_pending_async_iterator_resume, sizeof(HandAsyncIteratorFrame), sample,
            CX_BENCH_HAND_AWAIT_ITERATIONS, 73);
        QueryPerformanceCounter(&start);
        for (iteration = 0; iteration < CX_BENCH_ITERATIONS; ++iteration) {
            checksum += bench_System_Collections_Sum(element_count);
        }
        QueryPerformanceCounter(&end);

        cx_memory_test_allocation_tracking_set(CX_TRUE);
        allocations_before = cx_memory_test_allocation_count_get();
        bytes_before = cx_memory_test_allocation_bytes_get();
        for (iteration = 0; iteration < CX_BENCH_ITERATIONS; ++iteration) {
            if (bench_System_Collections_Sum(element_count) != expected) return 3;
        }
        allocations_after = cx_memory_test_allocation_count_get();
        bytes_after = cx_memory_test_allocation_bytes_get();
        cx_memory_test_allocation_tracking_set(CX_FALSE);
        printf("generated_iterator,%d,%d,%d,%zu,%zu,%zu,%.6f,%.2f,0,%.2f\n",
            sample, CX_BENCH_ITERATIONS, CX_BENCH_ELEMENTS,
            sizeof(struct bench_System_Collections___ITERATOR_NAME__),
            allocations_after - allocations_before,
            bytes_after - bytes_before,
            (double)(allocations_after - allocations_before) / CX_BENCH_ITERATIONS,
            (double)(bytes_after - bytes_before) / CX_BENCH_ITERATIONS,
            elapsed_ns(start, end, frequency) / CX_BENCH_ITERATIONS);

        QueryPerformanceCounter(&start);
        for (iteration = 0; iteration < CX_BENCH_ITERATIONS; ++iteration) {
            checksum += hand_sum(element_count);
        }
        QueryPerformanceCounter(&end);

        cx_memory_test_allocation_tracking_set(CX_TRUE);
        allocations_before = cx_memory_test_allocation_count_get();
        bytes_before = cx_memory_test_allocation_bytes_get();
        for (iteration = 0; iteration < CX_BENCH_ITERATIONS; ++iteration) {
            if (hand_sum(element_count) != expected) return 3;
        }
        allocations_after = cx_memory_test_allocation_count_get();
        bytes_after = cx_memory_test_allocation_bytes_get();
        cx_memory_test_allocation_tracking_set(CX_FALSE);
        printf("handwritten_c,%d,%d,%d,%zu,%zu,%zu,%.6f,%.2f,0,%.2f\n",
            sample, CX_BENCH_ITERATIONS, CX_BENCH_ELEMENTS,
            sizeof(HandIteratorFrame), allocations_after - allocations_before,
            bytes_after - bytes_before,
            (double)(allocations_after - allocations_before) / CX_BENCH_ITERATIONS,
            (double)(bytes_after - bytes_before) / CX_BENCH_ITERATIONS,
            elapsed_ns(start, end, frequency) / CX_BENCH_ITERATIONS);

        run_await_scenario("completed_await_generated",
            generated_completed_await, 0u, sample, CX_BENCH_AWAIT_ITERATIONS, 73);
        run_await_scenario("completed_await_handwritten",
            hand_completed_await, sizeof(HandAwaitFrame), sample, CX_BENCH_HAND_AWAIT_ITERATIONS, 73);
        run_await_scenario("pending_await_generated",
            generated_pending_await, 0u, sample, CX_BENCH_AWAIT_ITERATIONS, 73);
        run_await_scenario("cold_reusable_source_await_generated",
            generated_cold_reusable_source_await, 0u, sample, CX_BENCH_AWAIT_ITERATIONS, 73);
        run_await_scenario("warm_reusable_source_await_generated",
            generated_warm_reusable_source_await, 0u, sample, CX_BENCH_AWAIT_ITERATIONS, 73);
        run_await_scenario("shared_pending_await_generated",
            generated_shared_pending_await, 0u, sample, CX_BENCH_AWAIT_ITERATIONS, 146);
        run_await_scenario("pending_await_handwritten",
            hand_pending_await, sizeof(HandAwaitFrame), sample, CX_BENCH_HAND_AWAIT_ITERATIONS, 73);
        run_await_scenario("nested_completed_await_generated",
            generated_nested_completed_await, 0u, sample, CX_BENCH_AWAIT_ITERATIONS, 73);
        run_await_scenario("nested_completed_await_handwritten",
            hand_nested_completed_await, sizeof(HandAwaitFrame), sample, CX_BENCH_HAND_AWAIT_ITERATIONS, 73);
        run_await_scenario("nested_pending_await_generated",
            generated_nested_pending_await, 0u, sample, CX_BENCH_AWAIT_ITERATIONS, 73);
        run_await_scenario("nested_pending_await_handwritten",
            hand_nested_pending_await, 2u * sizeof(HandAwaitFrame), sample, CX_BENCH_HAND_AWAIT_ITERATIONS, 73);
        run_await_scenario("deep_completed_await_generated",
            generated_deep_completed_await, 0u, sample, CX_BENCH_AWAIT_ITERATIONS, 73);
        run_await_scenario("deep_pending_await_generated",
            generated_deep_pending_await, 0u, sample, CX_BENCH_AWAIT_ITERATIONS, 73);
        run_await_scenario("as_task_inline_valuetask_generated",
            generated_inline_value_task_as_task, 0u, sample, CX_BENCH_AWAIT_ITERATIONS, 73);
        run_await_scenario("as_task_task_backed_valuetask_generated",
            generated_task_backed_value_task_as_task, 0u, sample, CX_BENCH_AWAIT_ITERATIONS, 73);
        run_completion_ratio_scenario("completion_ratio_0_pending_generated",
            0, sample, CX_BENCH_RATIO_ITERATIONS, 73);
        run_completion_ratio_scenario("completion_ratio_50_pending_generated",
            1, sample, CX_BENCH_RATIO_ITERATIONS, 73);
        run_completion_ratio_scenario("completion_ratio_100_pending_generated",
            2, sample, CX_BENCH_RATIO_ITERATIONS, 73);
        run_gc_pressure_scenario("pending_await_generated_gc_pressure",
            generated_pending_await, 0u, sample, CX_BENCH_GC_ITERATIONS, 73);
        run_gc_pressure_scenario("pending_await_handwritten_gc_pressure",
            hand_pending_await, sizeof(HandAwaitFrame), sample, CX_BENCH_GC_ITERATIONS, 73);
    }
    return checksum == 0 ? 4 : 0;
}
'@
    $cSource = $cSource.Replace('__ELEMENTS__', $Elements.ToString())
    $cSource = $cSource.Replace('__ITERATIONS__', $Iterations.ToString())
    $cSource = $cSource.Replace('__ASYNC_ITERATOR_ITERATIONS__', $AsyncIteratorIterations.ToString())
    $cSource = $cSource.Replace('__AWAIT_ITERATIONS__', $AwaitIterations.ToString())
    $cSource = $cSource.Replace('__HAND_AWAIT_ITERATIONS__', $HandAwaitIterations.ToString())
    $cSource = $cSource.Replace('__GC_ITERATIONS__', $GcPressureIterations.ToString())
    $cSource = $cSource.Replace('__SAMPLES__', $Samples.ToString())
    $cSource = $cSource.Replace('__ITERATOR_NAME__', $iteratorName)
    $cSource = $cSource.Replace('__BRANCHING_ITERATOR_NAME__', $branchingIteratorName)
    $cSource = $cSource.Replace('__CAPTURED_BRANCH_ITERATOR_NAME__', $capturedBranchIteratorName)
    $cSource = $cSource.Replace('__CAPTURED_ASYNC_BRANCH_ITERATOR_NAME__', $capturedAsyncBranchIteratorName)
    $cSource = $cSource.Replace('__REFERENCE_ITERATOR_NAME__', $referenceIteratorName)
    $cSource = $cSource.Replace('__PAYLOAD_ITERATOR_NAME__', $payloadIteratorName)
    $cSource = $cSource.Replace('__LARGE_PAYLOAD_ITERATOR_NAME__', $largePayloadIteratorName)
    $cSource = $cSource.Replace('__GENERIC_ITERATOR_NAME__', $closedGenericIteratorName)
    $cSource = $cSource.Replace('__GENERIC_PAYLOAD_ITERATOR_NAME__', $closedGenericPayloadIteratorName)
    $cSource = $cSource.Replace('__GENERIC_REFERENCE_ITERATOR_NAME__', "bench_System_Collections_$genericReferenceFrameName")
    $cSource = $cSource.Replace('__ASYNC_ITERATOR_NAME__', $asyncIteratorName)
    $cSource = $cSource.Replace('__PENDING_ASYNC_ITERATOR_NAME__', $pendingAsyncIteratorName)
    if ($AsyncEnumeratorContract -eq 'Task') {
        $cSource = [regex]::Replace(
            $cSource,
            '(fprintf\(stderr,\r?\n\s*"PASS pending await-foreach move survives forced GC and disposes once\\n"\);)',
            ('$1' + [Environment]::NewLine + '    return 0;'),
            [System.Text.RegularExpressions.RegexOptions]::None,
            [TimeSpan]::FromSeconds(2))
        if (-not [regex]::IsMatch($cSource, 'PASS pending await-foreach move survives forced GC and disposes once\\n"\);\r?\n\s*return 0;')) {
            throw 'Task enumerator benchmark could not isolate the await-foreach checks.'
        }
    }
    [IO.File]::WriteAllText($mainPath, $cSource, $utf8NoBom)

    $cmakeListsPath = Join-Path $generatedDirectory 'CMakeLists.txt'
    $cmakeLists = Get-Content -LiteralPath $cmakeListsPath -Raw
    $testingOff = 'set(BUILD_TESTING OFF CACHE BOOL "" FORCE)'
    if (-not $cmakeLists.Contains($testingOff)) {
        throw "Could not enable allocation counters in '$cmakeListsPath'."
    }
    [IO.File]::WriteAllText(
        $cmakeListsPath,
        $cmakeLists.Replace($testingOff, 'set(BUILD_TESTING ON CACHE BOOL "" FORCE)'),
        $utf8NoBom)

    $staticLink = if ($LinkMode -eq 'static') { 'ON' } else { 'OFF' }
    if ($Platform -eq 'Windows') {
        $configureArgs = @(
            '-S', $generatedDirectory, '-B', $buildDirectory,
            "-DCXCORE_SOURCE_DIR=$cxCorePath",
            "-DCX_STATIC_LINK=$staticLink", '-DBUILD_TESTING=ON'
        )
        if ($Generator) {
            $configureArgs += @('-G', $Generator)
            if ($Generator -notmatch 'Visual Studio|Xcode') {
                $configureArgs += '-DCMAKE_BUILD_TYPE=Release'
            }
        }
        if ($Architecture -eq 'x86' -and (-not $Generator -or $Generator -match 'Visual Studio')) {
            $configureArgs += @('-A', 'Win32')
        } elseif ($Architecture -eq 'x86') {
            throw 'Windows x86 benchmarks require a Visual Studio generator.'
        }
        $configureOutput = & $cmakePath @configureArgs 2>&1
        if ($LASTEXITCODE -ne 0) {
            throw "Coroutine benchmark CMake configuration failed: $($configureOutput -join [Environment]::NewLine). Build files are in '$buildRoot'."
        }
        $buildOutput = & $cmakePath --build $buildDirectory --config Release --target bench 2>&1
        if ($LASTEXITCODE -ne 0) {
            throw "Coroutine benchmark Release build failed: $($buildOutput -join [Environment]::NewLine). Build files are in '$buildRoot'."
        }
        $executable = Get-ChildItem -LiteralPath (Join-Path $buildRoot '.bin') `
            -Filter 'bench.exe' -File -Recurse | Select-Object -First 1
        if ($null -eq $executable) {
            throw "Coroutine benchmark executable was not created. Build files are in '$buildRoot'."
        }
    } else {
        $linuxGenerated = Convert-WindowsPathToWslPath $generatedDirectory
        $linuxBuild = Convert-WindowsPathToWslPath $buildDirectory
        $linuxCore = Convert-WindowsPathToWslPath $cxCorePath
        $linuxCmake = @(
            '-S', $linuxGenerated, '-B', $linuxBuild,
            "-DCXCORE_SOURCE_DIR=$linuxCore", '-DCMAKE_BUILD_TYPE=Release',
            "-DCX_STATIC_LINK=$staticLink", '-DBUILD_TESTING=ON'
        )
        if ($Generator) { $linuxCmake += @('-G', $Generator) }
        $savedErrorActionPreference = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        try {
            $configureOutput = & wsl.exe --distribution $Distribution -- $cmakePath @linuxCmake 2>&1
            $configureExitCode = $LASTEXITCODE
        } finally {
            $ErrorActionPreference = $savedErrorActionPreference
        }
        if ($configureExitCode -ne 0) {
            throw "Linux coroutine benchmark CMake configuration failed: $($configureOutput -join [Environment]::NewLine). Build files are in '$buildRoot'."
        }
        $savedErrorActionPreference = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        try {
            $buildOutput = & wsl.exe --distribution $Distribution -- $cmakePath --build $linuxBuild --target bench --parallel 2>&1
            $buildExitCode = $LASTEXITCODE
        } finally {
            $ErrorActionPreference = $savedErrorActionPreference
        }
        if ($buildExitCode -ne 0) {
            throw "Linux coroutine benchmark Release build failed: $($buildOutput -join [Environment]::NewLine). Build files are in '$buildRoot'."
        }
        # This fixture sets CMAKE_RUNTIME_OUTPUT_DIRECTORY to the temporary
        # root's .bin folder, independently of the CMake build tree.
        $linuxExecutable = "$(Convert-WindowsPathToWslPath $buildRoot)/.bin/bench"
        if ($LinkMode -eq 'shared') {
            $relocations = & wsl.exe --distribution $Distribution -- ldd -r $linuxExecutable 2>&1
            if ($LASTEXITCODE -ne 0) {
                throw "Linux shared benchmark dependency check failed: $($relocations -join [Environment]::NewLine)."
            }
        }
    }
    if ($AsyncEnumeratorContract -eq 'Task') {
        if ($Platform -eq 'Windows') {
            $nativeOutput = @(& $executable.FullName 2>&1 | ForEach-Object { "$_" })
            $benchmarkExitCode = $LASTEXITCODE
        } else {
            $savedErrorActionPreference = $ErrorActionPreference
            $ErrorActionPreference = 'Continue'
            try {
                $nativeOutput = @(& wsl.exe --distribution $Distribution -- $linuxExecutable 2>&1)
                $benchmarkExitCode = $LASTEXITCODE
            } finally {
                $ErrorActionPreference = $savedErrorActionPreference
            }
        }
        if ($benchmarkExitCode -ne 0) {
            throw "Task-contract await-foreach checks returned exit ${benchmarkExitCode}: $($nativeOutput -join [Environment]::NewLine). Build files are in '$buildRoot'."
        }
        $csvDirectory = Split-Path -Parent $csvPath
        if (-not [string]::IsNullOrWhiteSpace($csvDirectory)) {
            New-Item -ItemType Directory -Path $csvDirectory -Force | Out-Null
        }
        [IO.File]::WriteAllText($csvPath, "check,result`nall-inline-one-task,pass`npending-first-move-forced-gc,pass`n", $utf8NoBom)
        $taskReport = @(
            "Date: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss zzz')",
            "Configuration: Release, $Platform $Architecture, $LinkMode cxcore",
            'Await-foreach enumerator contract: Task<bool>/TaskHandle',
            'All-inline reusable sequence: exactly one returned Task allocation (48 bytes x64, 24 bytes x86).',
            'Pending first MoveNextAsync: forced GC while suspended; source completion and scheduler drain preserve result and exactly-once disposal.',
            "Compiler repository revision: $(& git -C (Join-Path (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path 'cxc') rev-parse --short HEAD 2>`$null)",
            'Native output:',
            ($nativeOutput -join [Environment]::NewLine),
            "Build root: $buildRoot"
        )
        [IO.File]::WriteAllLines($reportPath, $taskReport, $utf8NoBom)
        $runSucceeded = $true
        Write-Output "PASS Task-contract await-foreach $Platform $Architecture $LinkMode"
        Write-Output "Build root: $buildRoot"
        return
    }

    $csvDirectory = Split-Path -Parent $csvPath
    if (-not [string]::IsNullOrWhiteSpace($csvDirectory)) {
        New-Item -ItemType Directory -Path $csvDirectory -Force | Out-Null
    }
    if ($Platform -eq 'Windows') {
        & $executable.FullName | Set-Content -LiteralPath $csvPath -Encoding utf8
        $benchmarkExitCode = $LASTEXITCODE
    } else {
        $savedErrorActionPreference = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        try {
            $benchmarkOutput = & wsl.exe --distribution $Distribution -- $linuxExecutable 2>$null
            $benchmarkExitCode = $LASTEXITCODE
        } finally {
            $ErrorActionPreference = $savedErrorActionPreference
        }
        $benchmarkOutput | Set-Content -LiteralPath $csvPath -Encoding utf8
    }
    if ($benchmarkExitCode -ne 0) {
        throw "Coroutine benchmark returned exit $benchmarkExitCode. Build files are in '$buildRoot'."
    }
    $rows = @(Import-Csv -LiteralPath $csvPath)
    $repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
    $compilerRevision = (& git -C (Join-Path $repositoryRoot 'cxc') rev-parse --short HEAD 2>$null)
    $compilerStatus = @(& git -C (Join-Path $repositoryRoot 'cxc') status --short 2>$null)
    $compilerDirty = $compilerStatus.Count -gt 0
    $processor = Get-CimInstance Win32_Processor | Select-Object -First 1 -ExpandProperty Name
    $hostOperatingSystem = Get-CimInstance Win32_OperatingSystem | Select-Object -First 1 -ExpandProperty Caption
    if ($Platform -eq 'Windows') {
        $operatingSystem = $hostOperatingSystem
        $cmakeVersion = (& $cmakePath --version | Select-Object -First 1)
    } else {
        $operatingSystem = "WSL $Distribution on $hostOperatingSystem"
        $cmakeVersion = (& wsl.exe --distribution $Distribution -- $cmakePath --version | Select-Object -First 1)
    }
    $report = [System.Collections.Generic.List[string]]::new()
    $report.Add("Date: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss zzz')")
    $report.Add("CPU: $processor")
    $report.Add("OS: $operatingSystem")
    $report.Add("CMake: $cmakeVersion")
    $report.Add("Compiler: $compilerPath")
    $report.Add("Compiler repository revision: $compilerRevision (working tree modified: $compilerDirty)")
    $report.Add("Configuration: Release, $Platform $Architecture, $LinkMode cxcore")
    $report.Add("Await-foreach enumerator contract: $AsyncEnumeratorContract")
    $report.Add("Workload: $Elements elements per enumeration; $Iterations operations per sample; $Samples samples")
    $report.Add("Async iterator workload: $AsyncIteratorIterations enumerations per sample")
    $report.Add("Pending async iterator resume: $AsyncIteratorIterations generated operations and $HandAwaitIterations C reference operations per sample")
    $report.Add("Await workload: $AwaitIterations operations per sample")
    $ratioIterations = [int]([Math]::Ceiling($AwaitIterations / 2.0) * 2)
    $report.Add("Completion-ratio workload: $ratioIterations operations/sample (rounded up to an even count), with 0%, 50%, and 100% pending Task awaits")
    $report.Add('Completion-ratio allocation counts and bytes are asserted independently of timing against the completed and pending Task await baselines for this ABI.')
    $report.Add("Handwritten await reference: $HandAwaitIterations operations per sample to resolve short-path timer granularity")
    $report.Add("GC-pressure workload: $GcPressureIterations pending-await operations per sample; collect every 8 operations after 64 KiB managed garbage")
    $report.Add('Timings are medians and min-max ranges in ns per operation.')
    $report.Add('Managed allocated bytes count requested CX Memory.Alloc payload bytes; native malloc bookkeeping and allocator metadata are excluded.')
    $report.Add('Generated scenarios combine compiler-generated temporaries and runtime task/continuation allocations; the handwritten C scenarios are a user-work lower-bound control, not an equivalent implementation.')
    $pressureBatches = [Math]::Floor($GcPressureIterations / 8)
    $pressureAllocationsPerOperation = ($pressureBatches * 16) / [double]$GcPressureIterations
    $pressureBytesPerOperation = ($pressureBatches * 16 * 4096) / [double]$GcPressureIterations
    $report.Add("GC-pressure scenarios include $pressureAllocationsPerOperation known pressure allocations and $pressureBytesPerOperation pressure bytes per operation; report both gross and pressure-adjusted managed allocation totals.")
    $report.Add('A generated await frame size of 0 means return-await relay lowering uses no separate CX state frame; Task and continuation allocations are counted separately.')
    $report.Add('The handwritten async-iterator case is a stack-only lower bound; it omits Task allocation, interface dispatch, and async disposal.')
    foreach ($scenario in @(
        'async_iterator_generated', 'async_iterator_handwritten_c',
        'async_iterator_pending_resume_generated', 'async_iterator_pending_resume_handwritten_c',
        'generated_iterator', 'handwritten_c',
        'completed_await_generated', 'completed_await_handwritten',
        'pending_await_generated', 'pending_await_handwritten',
        'cold_reusable_source_await_generated', 'warm_reusable_source_await_generated',
        'shared_pending_await_generated',
        'nested_completed_await_generated', 'nested_completed_await_handwritten',
        'nested_pending_await_generated', 'nested_pending_await_handwritten',
        'deep_completed_await_generated', 'deep_pending_await_generated',
        'completion_ratio_0_pending_generated', 'completion_ratio_50_pending_generated',
        'completion_ratio_100_pending_generated',
        'as_task_inline_valuetask_generated', 'as_task_task_backed_valuetask_generated',
        'pending_await_generated_gc_pressure', 'pending_await_handwritten_gc_pressure')) {
        $scenarioRows = @($rows | Where-Object scenario -eq $scenario)
        $times = @($scenarioRows | ForEach-Object { [double]$_.ns_per_operation } | Sort-Object)
        if ($times.Count -eq 0) {
            throw "No measurements were recorded for scenario '$scenario'."
        }
        $middle = [int][Math]::Floor($times.Count / 2)
        $median = if ($times.Count % 2) {
            $times[$middle]
        } else {
            ($times[$middle - 1] + $times[$middle]) / 2
        }
        $first = $scenarioRows[0]
        $grossAllocations = [double]$first.allocations_per_operation
        $grossBytes = [double]$first.allocated_bytes_per_operation
        if ($scenario -like '*_gc_pressure') {
            $reportedAllocations = $grossAllocations - $pressureAllocationsPerOperation
            $reportedBytes = $grossBytes - $pressureBytesPerOperation
            $allocationDetail = "gross $grossAllocations alloc/$grossBytes bytes; pressure-adjusted $reportedAllocations alloc/$reportedBytes bytes"
        } else {
            $allocationDetail = "$grossAllocations managed allocations/$grossBytes managed bytes"
        }
        $report.Add(("{0}: median {1:N2} ns/op, range {2:N2}-{3:N2}, frame {4} bytes, {5}, {6} GC collections/sample" -f `
            $scenario, $median, $times[0], $times[-1], $first.frame_bytes, $allocationDetail, $first.gc_collections))
    }
    [IO.File]::WriteAllLines($reportPath, $report, $utf8NoBom)
    $runSucceeded = $true
    Write-Output "CSV: $((Resolve-Path -LiteralPath $csvPath).Path)"
    Write-Output "Report: $((Resolve-Path -LiteralPath $reportPath).Path)"
    Get-Content -LiteralPath $csvPath
    Get-Content -LiteralPath $reportPath
} finally {
    if (-not $KeepBuild -and $runSucceeded -and (Test-Path -LiteralPath $buildRoot)) {
        $resolvedBuildRoot = (Resolve-Path -LiteralPath $buildRoot).Path
        $resolvedTempRoot = (Resolve-Path -LiteralPath ([IO.Path]::GetTempPath())).Path
        if (-not $resolvedBuildRoot.StartsWith($resolvedTempRoot, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to remove benchmark build outside the temporary directory: '$resolvedBuildRoot'."
        }
        Remove-Item -LiteralPath $resolvedBuildRoot -Recurse -Force
    } else {
        Write-Output "Build: $buildRoot"
    }
}

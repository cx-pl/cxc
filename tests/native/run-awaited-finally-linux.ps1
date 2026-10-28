param(
    [string] $Cxc = 'cxc',
    [string] $CxCoreDir = (Join-Path $PSScriptRoot '..\..\..\cxcore'),
    [string] $Distribution = 'Ubuntu',
    [string] $CMake = 'cmake',
    [ValidateSet('Linux', 'Windows')]
    [string] $Platform = 'Linux',
    [ValidateSet('x64', 'x86')]
    [string] $Architecture = 'x64',
    [ValidateSet('static', 'shared', 'both')]
    [string] $LinkMode = 'both',
    [string] $Generator = ''
)

$ErrorActionPreference = 'Stop'
$compilerPath = if ([IO.Path]::IsPathRooted($Cxc)) {
    (Resolve-Path -LiteralPath $Cxc).Path
} else {
    (Get-Command $Cxc -ErrorAction Stop).Source
}
$cxCorePath = (Resolve-Path -LiteralPath $CxCoreDir).Path
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$buildRoot = Join-Path ([IO.Path]::GetTempPath()) `
    "cx-awaited-finally-linux-$([guid]::NewGuid().ToString('N'))"
$projectOutput = Join-Path $buildRoot 'generated'
$projectPath = Join-Path $buildRoot 'bench.cxproj'
$collectionsPath = Join-Path $buildRoot 'collections.cx'
$asyncCollectionsPath = Join-Path $buildRoot 'async-enumerable.cx'
$sourcePath = Join-Path $buildRoot 'main.cx'
$fixturePath = Join-Path $PSScriptRoot 'fixtures\coroutine-acceptance'
$utf8NoBom = [Text.UTF8Encoding]::new($false)
$runSucceeded = $false

function Convert-WindowsPathToWslPath([string] $Path) {
    if (-not [IO.Path]::IsPathRooted($Path) -or $Path.Length -lt 3 -or $Path[1] -ne ':') {
        throw "Expected a drive-qualified Windows path, found '$Path'."
    }
    $drive = $Path.Substring(0, 1).ToLowerInvariant()
    $suffix = $Path.Substring(2).Replace('\', '/')
    return "/mnt/$drive$suffix"
}

if ($Platform -eq 'Linux' -and $Architecture -ne 'x64') {
    throw 'The Linux fixture currently supports x64 only.'
}
if ($Platform -eq 'Linux') {
    $cmakeVersionOutput = & wsl.exe --distribution $Distribution -- $CMake --version 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Could not run WSL CMake '$CMake' in distribution '$Distribution': $($cmakeVersionOutput -join [Environment]::NewLine)"
    }
    $cmakeVersionMatch = [regex]::Match(($cmakeVersionOutput -join ' '), 'cmake version ([0-9]+)\.([0-9]+)')
    if (-not $cmakeVersionMatch.Success -or
        ([int]$cmakeVersionMatch.Groups[1].Value -lt 3) -or
        ([int]$cmakeVersionMatch.Groups[1].Value -eq 3 -and
            [int]$cmakeVersionMatch.Groups[2].Value -lt 31)) {
        throw "WSL CMake 3.31 or newer is required; found '$($cmakeVersionOutput -join ' ')'. Pass its WSL executable path with -CMake."
    }
}

New-Item -ItemType Directory -Path $buildRoot -Force | Out-Null
[IO.File]::WriteAllText($projectPath, "name: bench`ntype: Executable`n", $utf8NoBom)
[IO.File]::WriteAllText($collectionsPath, @'
namespace System.Collections;
public interface IEnumerable<T> { IEnumerator<T> GetEnumerator() const; }
public interface IEnumerator<T> : System.IDisposable {
    T Current { const get; }
    bool MoveNext();
}
'@, $utf8NoBom)
[IO.File]::WriteAllText($asyncCollectionsPath, @'
namespace System.Collections;
public interface IAsyncEnumerable<T> {
    IAsyncEnumerator<T> GetAsyncEnumerator(
        System.Threading.Tasks.CancellationToken cancellationToken);
}
public interface IAsyncEnumerator<T> {
    T Current { const get; }
    System.Threading.Tasks.Task<bool> MoveNextAsync();
    System.Threading.Tasks.TaskHandle DisposeAsync();
}
'@, $utf8NoBom)
[IO.File]::WriteAllText($sourcePath, @'
import System;
import System.Threading.Tasks;
import System.Collections;

namespace App;

public extern void ForceCollection();
public delegate int CapturedIteratorValue();
public static class IteratorCleanupProbe { public static int FinalizerRuns; }
public static class AsyncIteratorCleanupProbe { public static int FinalizerRuns; }
public static class ReentrantIteratorProbe {
    public static IEnumerator<int> Enumerator;
    public static bool ReentryRejected;
}
public static class IteratorCaptureProbe { public static CapturedIteratorValue Value; }

public IEnumerable<int> ProtectedValues() {
    try {
        yield return 1;
        yield return 2;
    } finally {
        IteratorCleanupProbe.FinalizerRuns += 1;
    }
}

public IEnumerable<int> ReenterDuringMove() {
    ReentrantIteratorProbe.ReentryRejected = false;
    IEnumerator<int> activeEnumerator = ReentrantIteratorProbe.Enumerator;
    try { activeEnumerator.MoveNext(); }
    catch (Exception) { ReentrantIteratorProbe.ReentryRejected = true; }
    yield return ReentrantIteratorProbe.ReentryRejected ? 1 : 0;
}

async IAsyncEnumerable<int> InnerValues() {
    try {
        yield return 41;
        yield return 42;
    } finally {
        AsyncIteratorCleanupProbe.FinalizerRuns += 1;
    }
}

async IAsyncEnumerable<int> BreakAfterOneAsyncValue() {
    await foreach (int value in InnerValues()) {
        yield return value;
        break;
    }
}

async IAsyncEnumerable<int> CaptureAcrossAwait(Task pending) {
    int capturedValue = 41;
    delegate() => int readCapturedValue = () => capturedValue;
    IteratorCaptureProbe.Value = readCapturedValue;
    await pending;
    capturedValue = 52;
    yield return readCapturedValue();
}

async IAsyncEnumerable<int> ThrowAfterOneAsyncValue() {
    await foreach (int value in InnerValues()) {
        yield return value;
        throw new Exception("consumer body failed");
    }
}

async Task AwaitCleanupAfterTry(Task cleanup, bool failBeforeCleanup) {
    try {
        if (failBeforeCleanup) throw new Exception("try failed before cleanup");
    } finally {
        await cleanup;
    }
}

public int Run() {
    IteratorCleanupProbe.FinalizerRuns = 0;
    foreach (int value in ProtectedValues()) {
        if (value == 1) break;
    }
    if (IteratorCleanupProbe.FinalizerRuns != 1) return 11;

    IEnumerator<int> reentrantEnumerator = ReenterDuringMove().GetEnumerator();
    ReentrantIteratorProbe.Enumerator = reentrantEnumerator;
    if (!reentrantEnumerator.MoveNext() || !ReentrantIteratorProbe.ReentryRejected ||
        reentrantEnumerator.Current != 1 || reentrantEnumerator.MoveNext()) return 18;
    ReentrantIteratorProbe.Enumerator = null;

    TaskCompletionSource captureSource = new TaskCompletionSource();
    IAsyncEnumerator<int> captureEnumerator = CaptureAcrossAwait(captureSource.Task)
        .GetAsyncEnumerator(CancellationToken.None);
    Task<bool> captureMove = captureEnumerator.MoveNextAsync();
    if (captureMove.IsCompleted) return 19;
    ForceCollection();
    captureSource.SetResult();
    TaskScheduler.Run();
    if (!captureMove.IsCompleted || !captureMove.GetResult() ||
        captureEnumerator.Current != 52) return 20;
    captureEnumerator.DisposeAsync().GetResult();
    captureEnumerator = null;
    ForceCollection();
    CapturedIteratorValue escapedCapture = IteratorCaptureProbe.Value;
    if (escapedCapture == null || escapedCapture() != 52) return 21;

    AsyncIteratorCleanupProbe.FinalizerRuns = 0;
    IAsyncEnumerator<int> asyncValues = BreakAfterOneAsyncValue()
        .GetAsyncEnumerator(CancellationToken.None);
    Task<bool> firstAsyncMove = asyncValues.MoveNextAsync();
    TaskScheduler.Run();
    if (!firstAsyncMove.IsCompleted || !firstAsyncMove.GetResult() ||
        asyncValues.Current != 41 || AsyncIteratorCleanupProbe.FinalizerRuns != 0) return 12;
    ForceCollection();
    Task<bool> secondAsyncMove = asyncValues.MoveNextAsync();
    TaskScheduler.Run();
    if (!secondAsyncMove.IsCompleted || secondAsyncMove.GetResult() ||
        AsyncIteratorCleanupProbe.FinalizerRuns != 1) return 13;
    asyncValues.DisposeAsync().GetResult();

    AsyncIteratorCleanupProbe.FinalizerRuns = 0;
    IAsyncEnumerator<int> throwingAsyncValues = ThrowAfterOneAsyncValue()
        .GetAsyncEnumerator(CancellationToken.None);
    Task<bool> firstThrowingMove = throwingAsyncValues.MoveNextAsync();
    TaskScheduler.Run();
    if (!firstThrowingMove.IsCompleted || !firstThrowingMove.GetResult() ||
        throwingAsyncValues.Current != 41 || AsyncIteratorCleanupProbe.FinalizerRuns != 0) return 14;
    ForceCollection();
    Task<bool> consumerFaultMove = throwingAsyncValues.MoveNextAsync();
    TaskScheduler.Run();
    if (!consumerFaultMove.IsCompleted || AsyncIteratorCleanupProbe.FinalizerRuns != 1) return 15;
    try {
        consumerFaultMove.GetResult();
        return 16;
    } catch (Exception exception) {
        if (exception.Message != "consumer body failed") return 17;
    }
    throwingAsyncValues.DisposeAsync().GetResult();

    TaskCompletionSource successSource = new TaskCompletionSource();
    Task success = AwaitCleanupAfterTry(successSource.Task, false);
    if (success.IsCompleted) return 1;
    ForceCollection();
    successSource.SetResult();
    TaskScheduler.Run();
    if (!success.IsCompleted) return 2;
    success.GetResult();

    TaskCompletionSource tryFaultSource = new TaskCompletionSource();
    Task tryFault = AwaitCleanupAfterTry(tryFaultSource.Task, true);
    ForceCollection();
    tryFaultSource.SetResult();
    TaskScheduler.Run();
    if (!tryFault.IsCompleted) return 3;
    try {
        tryFault.GetResult();
        return 4;
    } catch (Exception exception) {
        if (exception.Message != "try failed before cleanup") return 5;
    }

    TaskCompletionSource cleanupFaultSource = new TaskCompletionSource();
    Task cleanupFault = AwaitCleanupAfterTry(cleanupFaultSource.Task, true);
    cleanupFaultSource.SetException(new Exception("cleanup failed"));
    TaskScheduler.Run();
    if (!cleanupFault.IsCompleted) return 6;
    try {
        cleanupFault.GetResult();
        return 7;
    } catch (Exception exception) {
        if (exception.Message != "cleanup failed") return 8;
    }

    TaskCompletionSource cleanupCanceledSource = new TaskCompletionSource();
    Task cleanupCanceled = AwaitCleanupAfterTry(cleanupCanceledSource.Task, true);
    ForceCollection();
    cleanupCanceledSource.SetCanceled();
    TaskScheduler.Run();
    if (!cleanupCanceled.IsCanceled) return 9;
    try {
        cleanupCanceled.GetResult();
        return 10;
    } catch (OperationCanceledException) {
    }
    return 0;
}
'@, $utf8NoBom)
try {
    $arguments = @(
        '--emit-only', '--cxcore-dir', $cxCorePath, '--verbosity', 'quiet',
        '--output-dir', $projectOutput, $projectPath
    )
    if ([IO.Path]::GetExtension($compilerPath) -ieq '.dll') {
        & dotnet $compilerPath @arguments
    } else {
        & $compilerPath @arguments
    }
    if ($LASTEXITCODE -ne 0) {
        throw "Awaited-finally probe generation failed (exit $LASTEXITCODE). Files are in '$buildRoot'."
    }

    $linkModes = if ($LinkMode -eq 'both') { @('static', 'shared') } else { @($LinkMode) }
    foreach ($mode in $linkModes) {
        $staticLink = if ($mode -eq 'static') { 'ON' } else { 'OFF' }
        $buildName = "$Platform-$Architecture-$mode"
        $cmakeBuildDirectory = Join-Path $buildRoot "build-$buildName"
        if ($Platform -eq 'Linux') {
            $linuxRepositoryRoot = Convert-WindowsPathToWslPath $repositoryRoot
            $linuxBuildRoot = Convert-WindowsPathToWslPath $buildRoot
            $linuxFixturePath = Convert-WindowsPathToWslPath $fixturePath
            $linuxGeneratedPath = Convert-WindowsPathToWslPath $projectOutput
            $linuxBuildDirectory = "$linuxBuildRoot/build-$buildName"
            $linuxCxCorePath = "$linuxRepositoryRoot/cxcore"
            $configureOutput = & wsl.exe --distribution $Distribution -- $CMake `
                -S $linuxFixturePath -B $linuxBuildDirectory `
                "-DCX_GENERATED_DIR=$linuxGeneratedPath" `
                "-DCXCORE_SOURCE_DIR=$linuxCxCorePath" `
                "-DCX_STATIC_LINK=$staticLink" '-DCMAKE_BUILD_TYPE=Release' 2>&1
            if ($LASTEXITCODE -ne 0) {
                throw "WSL $buildName CMake configure failed: $($configureOutput -join [Environment]::NewLine). Files are in '$buildRoot'."
            }
            Write-Output $configureOutput
            $buildOutput = & wsl.exe --distribution $Distribution -- $CMake `
                --build $linuxBuildDirectory --target coroutine_acceptance --parallel 2 2>&1
            if ($LASTEXITCODE -ne 0) {
                throw "WSL $buildName build failed: $($buildOutput -join [Environment]::NewLine). Files are in '$buildRoot'."
            }
            Write-Output $buildOutput
            & wsl.exe --distribution $Distribution -- ctest --test-dir $linuxBuildDirectory --output-on-failure
            if ($LASTEXITCODE -ne 0) {
                throw "WSL $buildName CTest failed. Files are in '$buildRoot'."
            }
            if ($mode -eq 'shared') {
                $linuxExecutable = "$linuxBuildDirectory/coroutine_acceptance"
                $relocations = & wsl.exe --distribution $Distribution -- ldd -r $linuxExecutable 2>&1
                if ($LASTEXITCODE -ne 0) {
                    throw "WSL shared dependency check failed: $($relocations -join [Environment]::NewLine). Files are in '$buildRoot'."
                }
            }
        } else {
            $configureArguments = @('-S', $fixturePath, '-B', $cmakeBuildDirectory,
                "-DCX_GENERATED_DIR=$projectOutput", "-DCXCORE_SOURCE_DIR=$cxCorePath",
                "-DCX_STATIC_LINK=$staticLink", '-DCMAKE_BUILD_TYPE=Release')
            if ($Generator) { $configureArguments += @('-G', $Generator) }
            if ($Architecture -eq 'x86') { $configureArguments += @('-A', 'Win32') }
            elseif ($Architecture -eq 'x64' -and $Generator -match 'Visual Studio') {
                $configureArguments += @('-A', 'x64')
            }
            $configureOutput = & $CMake @configureArguments 2>&1
            if ($LASTEXITCODE -ne 0) {
                throw "Windows $buildName CMake configure failed: $($configureOutput -join [Environment]::NewLine). Files are in '$buildRoot'."
            }
            Write-Output $configureOutput
            $buildOutput = & $CMake --build $cmakeBuildDirectory --config Release --target coroutine_acceptance --parallel 2 2>&1
            if ($LASTEXITCODE -ne 0) {
                throw "Windows $buildName build failed: $($buildOutput -join [Environment]::NewLine). Files are in '$buildRoot'."
            }
            Write-Output $buildOutput
            & ctest --test-dir $cmakeBuildDirectory -C Release --output-on-failure
            if ($LASTEXITCODE -ne 0) {
                throw "Windows $buildName CTest failed. Files are in '$buildRoot'."
            }
        }
        Write-Output "PASS $Platform $Architecture $mode iterator cleanup, Task pending/fault/cancel, async enumeration/disposal, forced collection"
    }
    $runSucceeded = $true
} finally {
    if ($runSucceeded -and (Test-Path -LiteralPath $buildRoot)) {
        $resolvedBuildRoot = (Resolve-Path -LiteralPath $buildRoot).Path
        $resolvedTempRoot = (Resolve-Path -LiteralPath ([IO.Path]::GetTempPath())).Path
        if (-not $resolvedBuildRoot.StartsWith($resolvedTempRoot, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to remove fixture build outside the temporary directory: '$resolvedBuildRoot'."
        }
        Remove-Item -LiteralPath $resolvedBuildRoot -Recurse -Force
    } else {
        Write-Output "Build: $buildRoot"
    }
}

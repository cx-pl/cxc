param(
    [string]$Cxc = "$PSScriptRoot/../../src/CxCompiler/bin/Release/net10.0/CxCompiler.dll",
    [string]$CxCoreDir = "$PSScriptRoot/../../../cxcore",
    [string]$Configuration = 'Release',
    [string]$Generator = '',
    [string]$Architecture = '',
    [ValidateSet('ON','OFF')][string]$StaticLink = 'ON',
    [switch]$LinuxWsl,
    [string]$WslCmake = '/home/k.klein/.local/bin/cmake'
)

$ErrorActionPreference = 'Stop'
$fixture = Join-Path $PSScriptRoot 'fixtures/collection-queries'
$output = Join-Path $fixture '.obj'
$buildArchitecture = if ($LinuxWsl) {
    'linux-x64'
} elseif ($IsWindows -and $Architecture -eq 'x86') {
    'win32'
} elseif ($Architecture) {
    $Architecture.ToLowerInvariant()
} else {
    'native'
}
$buildGenerator = if ($Generator) {
    ($Generator -replace '[^A-Za-z0-9]+', '-').Trim('-').ToLowerInvariant()
} else {
    'default'
}
$build = Join-Path $fixture ".build-$buildArchitecture-$buildGenerator-$StaticLink"

& dotnet $Cxc --emit-only --cxcore-dir $CxCoreDir `
    (Join-Path $fixture 'collection_queries.cxproj')
if ($LASTEXITCODE -ne 0) { throw 'Collection query project code generation failed.' }

# Keep runtime outputs inside the target-specific build directory. CMake's
# generated default points every architecture/link-mode build at one shared
# .bin directory, allowing an x86 DLL to be picked up by an x64 executable.
$generatedCMake = Join-Path $output 'CMakeLists.txt'
$cmakeText = [IO.File]::ReadAllText($generatedCMake)
$cmakeText = $cmakeText.Replace(
    'set(CX_BIN_DIRECTORY "${CMAKE_CURRENT_LIST_DIR}/../.bin")',
    'set(CX_BIN_DIRECTORY "${CMAKE_CURRENT_BINARY_DIR}/bin")')
[IO.File]::WriteAllText($generatedCMake, $cmakeText, [Text.UTF8Encoding]::new($false))

$cmakeArgs = @(
    '-S', $output,
    '-B', $build,
    "-DCXCORE_SOURCE_DIR=$([IO.Path]::GetFullPath($CxCoreDir))",
    "-DCX_STATIC_LINK=$StaticLink",
    "-DCMAKE_BUILD_TYPE=$Configuration"
)
if ($Generator) { $cmakeArgs += @('-G', $Generator) }
if ($Architecture -and -not $LinuxWsl) {
    $cmakeArchitecture = if ($IsWindows -and $Architecture -eq 'x86') {
        'Win32'
    } else {
        $Architecture
    }
    $cmakeArgs += @('-A', $cmakeArchitecture)
}
if ($LinuxWsl) {
    if (-not $IsWindows -or $Architecture -ne 'x64' -or $Generator) {
        throw '-LinuxWsl requires Windows, -Architecture x64, and no CMake generator override.'
    }
    function ConvertTo-WslPath([string]$Path) {
        $fullPath = [IO.Path]::GetFullPath($Path)
        if ($fullPath -notmatch '^([A-Za-z]):\\(.*)$') {
            throw "Linux WSL paths must be on a drive mounted under /mnt: $fullPath"
        }
        $driveLetter = $Matches[1].ToLowerInvariant()
        $relativePath = $Matches[2].Replace('\','/')
        return "/mnt/$driveLetter/$relativePath"
    }
    $cmakeOutput = ConvertTo-WslPath $output
    $cmakeBuild = ConvertTo-WslPath $build
    $cmakeCore = ConvertTo-WslPath $CxCoreDir
    $cmakeArgs[1] = $cmakeOutput
    $cmakeArgs[3] = $cmakeBuild
    $cmakeArgs[4] = "-DCXCORE_SOURCE_DIR=$cmakeCore"
    $cmakeArgs += @('-DCMAKE_SYSTEM_NAME=Linux')
}

if ($LinuxWsl) {
    & wsl $WslCmake @cmakeArgs
} else {
    & cmake @cmakeArgs
}
if ($LASTEXITCODE -ne 0) { throw 'Collection query CMake configuration failed.' }
if ($LinuxWsl) {
    & wsl $WslCmake --build $cmakeBuild --config $Configuration --parallel 4
} else {
    & cmake --build $build --config $Configuration --parallel 4
}
if ($LASTEXITCODE -ne 0) { throw 'Collection query native build failed.' }

$targetBin = Join-Path $build 'bin'
$executable = if ($LinuxWsl) {
    ConvertTo-WslPath (Join-Path $targetBin 'collectionQueries')
} elseif ($IsWindows) {
    Join-Path $targetBin "$Configuration/collectionQueries.exe"
} else {
    Join-Path $targetBin 'collectionQueries'
}
if ($LinuxWsl) {
    & wsl $executable
} else {
    & $executable
}
if ($LASTEXITCODE -ne 0) {
    throw "Collection query native fixture failed at check $LASTEXITCODE."
}
Write-Output 'PASS generic interface dispatch, sync and async generic source and Skip/Take/Page provider execution, generic string equality/inequality stream capture snapshots and GC retention, async provider source-window and integer Filter/Select streaming with cancellation, provider-stream mid-move cancellation and awaited cleanup, operator-order and capture-snapshot semantics, partial-consumer disposal with pending cleanup success and fault propagation, async source-fault cleanup precedence, unsupported stream predicates rejected, local sync query effects, source-open/source-move/Current faults, projection failures and cleanup precedence, synchronous predicate position terminals and cleanup precedence, indexed Array/List Skip/Take range and snapshot semantics, indexed Array/List Filter/Skip/Take, Map/Skip/Take, Filter/Take, and Map/Take prefix, snapshot, callback-effect, and disposal semantics, Filter/Map/Skip/Take fallback callback order, zero-Take short-circuit, captured callback liveness, and List snapshot semantics, captured Array/List Map/Skip/Take and Filter/Skip/Take callback/storage liveness after forced GC, local async Filter/FilterAsync/Skip/Map/MapAsync/Take/Page and ToListAsync/ToArrayAsync behavior, local async Any/Count/First/Last/Single/ElementAt terminals with synchronous and awaited task predicates, awaited callback cleanup coordination, projection caching, cancellation forwarding and cleanup, fused filters, reference/struct projections and GC retention, closure retention across GC including captured Array/List Filter/Take predicates, a captured MapAsync selector, and async scalar predicates, disposal after pending MapAsync advancement/selector and FilterAsync predicate completion, provider-plan validation and matching operator names with the eight-key limit, captured filter/projection/order/window execution, async provider operator order, multi-cell capture snapshots and capture re-execution, cleanup-before-task completion, early termination, sorting, sync/async provider scalar terminals, cleanup precedence, materialization, and re-enumeration'

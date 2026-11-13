param(
    [string]$Cxc = "$PSScriptRoot/../../src/CxCompiler/bin/Release/net10.0/CxCompiler.dll",
    [string]$CxCoreDir = "$PSScriptRoot/../../../cxcore",
    [string]$Configuration = 'Release',
    [string]$Generator = '',
    [string]$Architecture = '',
    [ValidateSet('ON','OFF')][string]$StaticLink = 'ON',
    [switch]$BudgetWorkloadsOnly,
    [switch]$EnforcePerformanceBudgets,
    [switch]$CallbackWorkloadsOnly,
    [switch]$EnforceCallbackBudgets,
    [switch]$SortGrowthOnly,
    [switch]$LowSortGrowthOnly,
    [switch]$RetainedProbeOnly,
    [switch]$FilterTakeWorkloadsOnly,
    [switch]$FilterSkipTakeWorkloadsOnly,
    [switch]$SkipTakeWorkloadsOnly,
    [switch]$MapSkipTakeWorkloadsOnly,
    [switch]$MapFilterTakeWorkloadsOnly,
    [switch]$MapTakeWorkloadsOnly,
    [switch]$LinuxWsl,
    [string]$WslCmake = '/home/k.klein/.local/bin/cmake'
)

$ErrorActionPreference = 'Stop'
if ($RetainedProbeOnly -and ($BudgetWorkloadsOnly -or $EnforcePerformanceBudgets)) {
    throw '-RetainedProbeOnly cannot be combined with workload or performance-budget sampling.'
}
if ($MapTakeWorkloadsOnly -and
    ($BudgetWorkloadsOnly -or $CallbackWorkloadsOnly -or
        $EnforceCallbackBudgets -or $RetainedProbeOnly -or
        $MapFilterTakeWorkloadsOnly -or
        $SkipTakeWorkloadsOnly -or $FilterTakeWorkloadsOnly -or
        $FilterSkipTakeWorkloadsOnly -or
        $SortGrowthOnly -or $LowSortGrowthOnly)) {
    throw '-MapTakeWorkloadsOnly selects its own workload set and cannot be combined with other workload filters.'
}
if ($MapSkipTakeWorkloadsOnly -and
    ($BudgetWorkloadsOnly -or $CallbackWorkloadsOnly -or
        $EnforceCallbackBudgets -or $RetainedProbeOnly -or
        $MapFilterTakeWorkloadsOnly -or $MapTakeWorkloadsOnly -or
        $FilterTakeWorkloadsOnly -or $FilterSkipTakeWorkloadsOnly -or
        $SkipTakeWorkloadsOnly -or $SortGrowthOnly -or $LowSortGrowthOnly)) {
    throw '-MapSkipTakeWorkloadsOnly selects its own workload set and cannot be combined with other workload filters.'
}
if ($MapFilterTakeWorkloadsOnly -and
    ($BudgetWorkloadsOnly -or $CallbackWorkloadsOnly -or
        $EnforceCallbackBudgets -or $RetainedProbeOnly -or
        $FilterTakeWorkloadsOnly -or $FilterSkipTakeWorkloadsOnly -or
        $SkipTakeWorkloadsOnly -or $MapTakeWorkloadsOnly -or
        $SortGrowthOnly -or $LowSortGrowthOnly)) {
    throw '-MapFilterTakeWorkloadsOnly selects its own workload set and cannot be combined with other workload filters.'
}
if ($FilterTakeWorkloadsOnly -and
    ($BudgetWorkloadsOnly -or $CallbackWorkloadsOnly -or
        $EnforceCallbackBudgets -or $RetainedProbeOnly -or
        $MapFilterTakeWorkloadsOnly -or $MapTakeWorkloadsOnly -or
        $SkipTakeWorkloadsOnly -or $FilterSkipTakeWorkloadsOnly -or
        $SortGrowthOnly -or $LowSortGrowthOnly)) {
    throw '-FilterTakeWorkloadsOnly selects its own workload set and cannot be combined with other workload filters.'
}
if ($FilterSkipTakeWorkloadsOnly -and
    ($BudgetWorkloadsOnly -or $CallbackWorkloadsOnly -or
        $EnforceCallbackBudgets -or $RetainedProbeOnly -or
        $MapFilterTakeWorkloadsOnly -or $MapTakeWorkloadsOnly -or
        $FilterTakeWorkloadsOnly -or $SkipTakeWorkloadsOnly -or
        $SortGrowthOnly -or $LowSortGrowthOnly)) {
    throw '-FilterSkipTakeWorkloadsOnly selects its own workload set and cannot be combined with other workload filters.'
}
if ($SkipTakeWorkloadsOnly -and
    ($BudgetWorkloadsOnly -or $CallbackWorkloadsOnly -or
        $EnforceCallbackBudgets -or $RetainedProbeOnly -or
        $FilterTakeWorkloadsOnly -or $MapFilterTakeWorkloadsOnly -or
        $FilterSkipTakeWorkloadsOnly -or $MapTakeWorkloadsOnly -or
        $SortGrowthOnly -or $LowSortGrowthOnly)) {
    throw '-SkipTakeWorkloadsOnly selects its own workload set and cannot be combined with other workload filters.'
}
if ($SortGrowthOnly -and
    ($CallbackWorkloadsOnly -or $EnforceCallbackBudgets -or $RetainedProbeOnly)) {
    throw '-SortGrowthOnly cannot be combined with callback or retained-only workloads.'
}
if ($LowSortGrowthOnly -and
    ($SortGrowthOnly -or $CallbackWorkloadsOnly -or $EnforceCallbackBudgets -or $RetainedProbeOnly)) {
    throw '-LowSortGrowthOnly cannot be combined with other workload filters.'
}
if ($CallbackWorkloadsOnly -and
    ($BudgetWorkloadsOnly -or $EnforcePerformanceBudgets -or $RetainedProbeOnly)) {
    throw '-CallbackWorkloadsOnly selects its own workload set and cannot be combined with other workload filters.'
}
if ($CallbackWorkloadsOnly -and -not $EnforceCallbackBudgets) {
    throw '-CallbackWorkloadsOnly requires -EnforceCallbackBudgets.'
}
$fixture = Join-Path $PSScriptRoot 'fixtures/collection-query-benchmarks'
$output = Join-Path $fixture '.obj'
$emitTimer = [Diagnostics.Stopwatch]::StartNew()
$archKey = if ($LinuxWsl) {
    'linux-x64'
} elseif ($Architecture) {
    $Architecture.ToLowerInvariant()
} else {
    'native'
}
$generatorKey = if ($Generator) {
    ($Generator -replace '[^A-Za-z0-9]+', '-').Trim('-').ToLowerInvariant()
} else { 'default' }
$build = Join-Path $fixture ".build-$archKey-$generatorKey-$StaticLink"

& dotnet $Cxc --emit-only --cxcore-dir $CxCoreDir `
    (Join-Path $fixture 'CollectionQueryBenchmarks.cxproj')
if ($LASTEXITCODE -ne 0) { throw 'Query benchmark project code generation failed.' }
$emitTimer.Stop()
$queryGeneratedBytes = (Get-ChildItem -LiteralPath $output -Recurse -Filter '*.c' |
    Measure-Object -Property Length -Sum).Sum
$coreGenerated = Join-Path $CxCoreDir 'src/.obj'
$coreGeneratedBytes = if (Test-Path -LiteralPath $coreGenerated) {
    (Get-ChildItem -LiteralPath $coreGenerated -Recurse -Filter '*.c' |
        Measure-Object -Property Length -Sum).Sum
} else { 0 }

$cmakeLists = Join-Path $output 'CMakeLists.txt'
$cmakeContent = [IO.File]::ReadAllText($cmakeLists)
$coreAdd = '    add_subdirectory("${CXCORE_SOURCE_DIR}" "${CMAKE_CURRENT_BINARY_DIR}/cxcore")'
if (-not $cmakeContent.Contains($coreAdd)) {
    throw 'Generated CMake file has no cxcore dependency block.'
}
$cmakeContent = $cmakeContent.Replace(
    $coreAdd,
    $coreAdd + "`r`n    target_compile_definitions(cxcore PRIVATE CXCORE_TESTING CXCORE_ARENA_BENCHMARK)")
$cmakeContent = $cmakeContent.Replace(
    'set(CX_BIN_DIRECTORY "${CMAKE_CURRENT_LIST_DIR}/../.bin")',
    'set(CX_BIN_DIRECTORY "${CMAKE_CURRENT_BINARY_DIR}/bin")')
[IO.File]::WriteAllText(
    $cmakeLists,
    $cmakeContent,
    [Text.UTF8Encoding]::new($false))

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
    } else { $Architecture }
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
$cmakeCommand = if ($LinuxWsl) {
    $WslCmake
} else {
    'cmake'
}
$configureTimer = [Diagnostics.Stopwatch]::StartNew()
if ($LinuxWsl) {
    & wsl $cmakeCommand @cmakeArgs
} else {
    & cmake @cmakeArgs
}
if ($LASTEXITCODE -ne 0) { throw 'Query benchmark CMake configuration failed.' }
$configureTimer.Stop()
$buildTimer = [Diagnostics.Stopwatch]::StartNew()
if ($LinuxWsl) {
    & wsl $cmakeCommand --build $cmakeBuild --config $Configuration --parallel 4
} else {
    & cmake --build $build --config $Configuration --parallel 4
}
if ($LASTEXITCODE -ne 0) { throw 'Query benchmark native build failed.' }
$buildTimer.Stop()

$targetBin = Join-Path $build 'bin'
$executable = if ($LinuxWsl) {
    "$cmakeBuild/bin/CollectionQueryBenchmarks"
} elseif ($IsWindows) {
    Join-Path $targetBin "$Configuration/CollectionQueryBenchmarks.exe"
} else {
    Join-Path $targetBin 'CollectionQueryBenchmarks'
}
if ($LinuxWsl) {
    & wsl test -x $executable
    if ($LASTEXITCODE -ne 0) {
        throw "Query benchmark executable was not created: $executable"
    }
} elseif (-not (Test-Path -LiteralPath $executable)) {
    throw "Query benchmark executable was not created: $executable"
}
$previousBudgetMode = [Environment]::GetEnvironmentVariable(
    'CX_QUERY_BUDGETS_ONLY', 'Process')
$previousCallbackMode = [Environment]::GetEnvironmentVariable(
    'CX_QUERY_CALLBACKS_ONLY', 'Process')
$previousRetainedMode = [Environment]::GetEnvironmentVariable(
    'CX_QUERY_RETAINED_ONLY', 'Process')
$previousSortGrowthMode = [Environment]::GetEnvironmentVariable(
    'CX_QUERY_SORT_GROWTH_ONLY', 'Process')
$previousLowSortGrowthMode = [Environment]::GetEnvironmentVariable(
    'CX_QUERY_LOW_SORT_GROWTH_ONLY', 'Process')
$previousMapTakeMode = [Environment]::GetEnvironmentVariable(
    'CX_QUERY_MAP_TAKE_ONLY', 'Process')
$previousMapFilterTakeMode = [Environment]::GetEnvironmentVariable(
    'CX_QUERY_MAP_FILTER_TAKE_ONLY', 'Process')
$previousFilterTakeMode = [Environment]::GetEnvironmentVariable(
    'CX_QUERY_FILTER_TAKE_ONLY', 'Process')
$previousFilterSkipTakeMode = [Environment]::GetEnvironmentVariable(
    'CX_QUERY_FILTER_SKIP_TAKE_ONLY', 'Process')
$previousSkipTakeMode = [Environment]::GetEnvironmentVariable(
    'CX_QUERY_SKIP_TAKE_ONLY', 'Process')
$previousMapSkipTakeMode = [Environment]::GetEnvironmentVariable(
    'CX_QUERY_MAP_SKIP_TAKE_ONLY', 'Process')
try {
    if ($BudgetWorkloadsOnly) {
        [Environment]::SetEnvironmentVariable(
            'CX_QUERY_BUDGETS_ONLY', '1', 'Process')
    }
    if ($CallbackWorkloadsOnly) {
        [Environment]::SetEnvironmentVariable(
            'CX_QUERY_CALLBACKS_ONLY', '1', 'Process')
    }
    if ($RetainedProbeOnly) {
        [Environment]::SetEnvironmentVariable(
            'CX_QUERY_RETAINED_ONLY', '1', 'Process')
    }
    if ($SortGrowthOnly) {
        [Environment]::SetEnvironmentVariable(
            'CX_QUERY_SORT_GROWTH_ONLY', '1', 'Process')
    }
    if ($LowSortGrowthOnly) {
        [Environment]::SetEnvironmentVariable(
            'CX_QUERY_LOW_SORT_GROWTH_ONLY', '1', 'Process')
    }
    if ($MapTakeWorkloadsOnly) {
        [Environment]::SetEnvironmentVariable(
            'CX_QUERY_MAP_TAKE_ONLY', '1', 'Process')
    }
    if ($MapFilterTakeWorkloadsOnly) {
        [Environment]::SetEnvironmentVariable(
            'CX_QUERY_MAP_FILTER_TAKE_ONLY', '1', 'Process')
    }
    if ($FilterTakeWorkloadsOnly) {
        [Environment]::SetEnvironmentVariable(
            'CX_QUERY_FILTER_TAKE_ONLY', '1', 'Process')
    }
    if ($FilterSkipTakeWorkloadsOnly) {
        [Environment]::SetEnvironmentVariable(
            'CX_QUERY_FILTER_SKIP_TAKE_ONLY', '1', 'Process')
    }
    if ($SkipTakeWorkloadsOnly) {
        [Environment]::SetEnvironmentVariable(
            'CX_QUERY_SKIP_TAKE_ONLY', '1', 'Process')
    }
    if ($MapSkipTakeWorkloadsOnly) {
        [Environment]::SetEnvironmentVariable(
            'CX_QUERY_MAP_SKIP_TAKE_ONLY', '1', 'Process')
    }
    if ($LinuxWsl) {
        $linuxRunArguments = @('env')
        if ($BudgetWorkloadsOnly) {
            $linuxRunArguments += 'CX_QUERY_BUDGETS_ONLY=1'
        }
        if ($CallbackWorkloadsOnly) {
            $linuxRunArguments += 'CX_QUERY_CALLBACKS_ONLY=1'
        }
        if ($RetainedProbeOnly) {
            $linuxRunArguments += 'CX_QUERY_RETAINED_ONLY=1'
        }
        if ($SortGrowthOnly) {
            $linuxRunArguments += 'CX_QUERY_SORT_GROWTH_ONLY=1'
        }
        if ($LowSortGrowthOnly) {
            $linuxRunArguments += 'CX_QUERY_LOW_SORT_GROWTH_ONLY=1'
        }
        if ($MapTakeWorkloadsOnly) {
            $linuxRunArguments += 'CX_QUERY_MAP_TAKE_ONLY=1'
        }
        if ($MapFilterTakeWorkloadsOnly) {
            $linuxRunArguments += 'CX_QUERY_MAP_FILTER_TAKE_ONLY=1'
        }
        if ($FilterTakeWorkloadsOnly) {
            $linuxRunArguments += 'CX_QUERY_FILTER_TAKE_ONLY=1'
        }
        if ($FilterSkipTakeWorkloadsOnly) {
            $linuxRunArguments += 'CX_QUERY_FILTER_SKIP_TAKE_ONLY=1'
        }
        if ($SkipTakeWorkloadsOnly) {
            $linuxRunArguments += 'CX_QUERY_SKIP_TAKE_ONLY=1'
        }
        if ($MapSkipTakeWorkloadsOnly) {
            $linuxRunArguments += 'CX_QUERY_MAP_SKIP_TAKE_ONLY=1'
        }
        $linuxRunArguments += $executable
        $benchmarkOutput = @(& wsl @linuxRunArguments)
    } else {
        $benchmarkOutput = @(& $executable)
    }
    $benchmarkExitCode = $LASTEXITCODE
} finally {
    [Environment]::SetEnvironmentVariable(
        'CX_QUERY_BUDGETS_ONLY', $previousBudgetMode, 'Process')
    [Environment]::SetEnvironmentVariable(
        'CX_QUERY_CALLBACKS_ONLY', $previousCallbackMode, 'Process')
    [Environment]::SetEnvironmentVariable(
        'CX_QUERY_RETAINED_ONLY', $previousRetainedMode, 'Process')
    [Environment]::SetEnvironmentVariable(
        'CX_QUERY_SORT_GROWTH_ONLY', $previousSortGrowthMode, 'Process')
    [Environment]::SetEnvironmentVariable(
        'CX_QUERY_LOW_SORT_GROWTH_ONLY', $previousLowSortGrowthMode, 'Process')
    [Environment]::SetEnvironmentVariable(
        'CX_QUERY_MAP_TAKE_ONLY', $previousMapTakeMode, 'Process')
    [Environment]::SetEnvironmentVariable(
        'CX_QUERY_MAP_FILTER_TAKE_ONLY', $previousMapFilterTakeMode, 'Process')
    [Environment]::SetEnvironmentVariable(
        'CX_QUERY_FILTER_TAKE_ONLY', $previousFilterTakeMode, 'Process')
    [Environment]::SetEnvironmentVariable(
        'CX_QUERY_FILTER_SKIP_TAKE_ONLY', $previousFilterSkipTakeMode, 'Process')
    [Environment]::SetEnvironmentVariable(
        'CX_QUERY_SKIP_TAKE_ONLY', $previousSkipTakeMode, 'Process')
    [Environment]::SetEnvironmentVariable(
        'CX_QUERY_MAP_SKIP_TAKE_ONLY', $previousMapSkipTakeMode, 'Process')
}
$benchmarkOutput | Write-Output
if ($benchmarkExitCode -ne 0) {
    throw "Query benchmark failed at check $benchmarkExitCode."
}

if ($EnforcePerformanceBudgets) {
    $supportedBudgetTarget =
        ($IsWindows -and $Architecture -in @('x64','x86')) -or
        ($LinuxWsl -and $Architecture -eq 'x64')
    if (-not $supportedBudgetTarget -or $Configuration -ne 'Release') {
        throw 'Measured budgets require Windows x86/x64 or Linux WSL x64 Release builds.'
    }

    function Get-QueryBenchmarkCategory([string]$Name) {
        $rows = @($benchmarkOutput | Where-Object { $_.StartsWith("$Name,") } |
            ForEach-Object {
                $columns = $_ -split ','
                [pscustomobject]@{
                    Iterations = [int64]$columns[2]
                    Elapsed = [int64]$columns[3]
                    Allocations = [int64]$columns[5]
                    Bytes = [int64]$columns[6]
                }
            })
        if ($rows.Count -lt 7) {
            throw "Performance budget requires at least seven samples for '$Name'; found $($rows.Count)."
        }
        $ordered = @($rows.Elapsed | Sort-Object)
        $median = $ordered[[math]::Floor($ordered.Count / 2)]
        $iterations = $rows[0].Iterations
        if ($rows | Where-Object {
            $_.Iterations -ne $iterations -or
            $_.Allocations -ne $rows[0].Allocations -or
            $_.Bytes -ne $rows[0].Bytes
        }) {
            throw "Sample work or per-sample allocations vary for '$Name'."
        }
        return [pscustomobject]@{
            MedianNs = [int64]$median
            AllocationsPerEnumeration = [double]$rows[0].Allocations / $iterations
            BytesPerEnumeration = [double]$rows[0].Bytes / $iterations
        }
    }

    $budgets = @(
        @{ Direct = 'query-indexed-array-filter-map-take'; Fallback = 'query-single-filter-map-take-layered'; Ratio = 0.60; Allocations = 1; Bytes = 128 },
        @{ Direct = 'query-indexed-list-filter-map-take'; Fallback = 'query-interface-list-filter-map-take'; Ratio = 0.75; Allocations = 1; Bytes = 128 },
        @{ Direct = 'query-indexed-array-map-filter-take'; Fallback = 'query-interface-array-map-filter-take'; Ratio = 1.10; Allocations = 0; Bytes = 0 },
        @{ Direct = 'query-indexed-list-map-filter-take'; Fallback = 'query-interface-list-map-filter-take'; Ratio = 0.70; Allocations = 1; Bytes = 128 },
        @{ Direct = 'query-indexed-array-map-take'; Fallback = 'query-interface-array-map-take'; Ratio = 0.70; Allocations = 1; Bytes = 88 },
        @{ Direct = 'query-indexed-list-map-take'; Fallback = 'query-interface-list-map-take'; Ratio = 0.70; Allocations = 1; Bytes = 88 },
        @{ Direct = 'query-indexed-array-filter-take'; Fallback = 'query-interface-array-filter-take'; Ratio = 0.70; Allocations = 1; Bytes = 88 },
        @{ Direct = 'query-indexed-list-filter-take'; Fallback = 'query-interface-list-filter-take'; Ratio = 0.70; Allocations = 1; Bytes = 88 },
        @{ Direct = 'query-indexed-array-skip-take'; Fallback = 'query-interface-array-skip-take'; Ratio = 0.70; Allocations = 1; Bytes = 88 },
        @{ Direct = 'query-indexed-list-skip-take'; Fallback = 'query-interface-list-skip-take'; Ratio = 0.70; Allocations = 1; Bytes = 88 },
        @{ Direct = 'query-indexed-array-filter-skip-take'; Fallback = 'query-interface-array-filter-skip-take'; Ratio = 0.70; Allocations = 1; Bytes = 88 },
        @{ Direct = 'query-indexed-list-filter-skip-take'; Fallback = 'query-interface-list-filter-skip-take'; Ratio = 0.70; Allocations = 1; Bytes = 88 },
        @{ Direct = 'query-indexed-array-map-skip-take'; Fallback = 'query-interface-array-map-skip-take'; Ratio = 0.70; Allocations = 1; Bytes = 88 },
        @{ Direct = 'query-indexed-list-map-skip-take'; Fallback = 'query-interface-list-map-skip-take'; Ratio = 0.70; Allocations = 1; Bytes = 88 },
        @{ Direct = 'query-indexed-array-page-map'; Fallback = 'query-page-map'; Ratio = 0.60; Allocations = 3; Bytes = 208 },
        @{ Direct = 'query-indexed-list-page-map'; Fallback = 'query-interface-list-page-map'; Ratio = 0.60; Allocations = 3; Bytes = 208 }
    )
    if ($MapTakeWorkloadsOnly) {
        $budgets = @($budgets | Where-Object {
            $_.Direct -eq 'query-indexed-array-map-take' -or
            $_.Direct -eq 'query-indexed-list-map-take'
        })
    }
    if ($MapFilterTakeWorkloadsOnly) {
        $budgets = @($budgets | Where-Object {
            $_.Direct -eq 'query-indexed-array-map-filter-take' -or
            $_.Direct -eq 'query-indexed-list-map-filter-take'
        })
    }
    if ($FilterTakeWorkloadsOnly) {
        $budgets = @($budgets | Where-Object {
            $_.Direct -eq 'query-indexed-array-filter-take' -or
            $_.Direct -eq 'query-indexed-list-filter-take'
        })
    }
    if ($FilterSkipTakeWorkloadsOnly) {
        $budgets = @($budgets | Where-Object {
            $_.Direct -eq 'query-indexed-array-filter-skip-take' -or
            $_.Direct -eq 'query-indexed-list-filter-skip-take'
        })
    }
    if ($SkipTakeWorkloadsOnly) {
        $budgets = @($budgets | Where-Object {
            $_.Direct -eq 'query-indexed-array-skip-take' -or
            $_.Direct -eq 'query-indexed-list-skip-take'
        })
    }
    if ($MapSkipTakeWorkloadsOnly) {
        $budgets = @($budgets | Where-Object {
            $_.Direct -eq 'query-indexed-array-map-skip-take' -or
            $_.Direct -eq 'query-indexed-list-map-skip-take'
        })
    }
    if (-not ($SortGrowthOnly -or $LowSortGrowthOnly)) {
    foreach ($budget in $budgets) {
        $direct = Get-QueryBenchmarkCategory $budget.Direct
        $fallback = Get-QueryBenchmarkCategory $budget.Fallback
        $ratio = [double]$direct.MedianNs / $fallback.MedianNs
        if ($ratio -gt $budget.Ratio -or
            $direct.AllocationsPerEnumeration -gt $budget.Allocations -or
            $direct.BytesPerEnumeration -gt $budget.Bytes) {
            throw ("Performance budget failed for '{0}': median ratio {1:P1} (max {2:P0}), " +
                "{3} allocations/{4} bytes per enumeration (max {5}/{6}).") -f
                $budget.Direct, $ratio, $budget.Ratio,
                $direct.AllocationsPerEnumeration, $direct.BytesPerEnumeration,
                $budget.Allocations, $budget.Bytes
        }
        Write-Output ("# budget,{0},median_ratio={1:P1},allocations_per_enumeration={2},bytes_per_enumeration={3},PASS" -f
            $budget.Direct, $ratio,
            $direct.AllocationsPerEnumeration, $direct.BytesPerEnumeration)
    }
    }

    if (-not ($MapTakeWorkloadsOnly -or $MapFilterTakeWorkloadsOnly -or
        $FilterTakeWorkloadsOnly -or $FilterSkipTakeWorkloadsOnly -or
        $SkipTakeWorkloadsOnly -or $MapSkipTakeWorkloadsOnly)) {
    $sortBudgets = @(
        @{ Name = 'array'; One = 'query-sort-one-key-bucket-array'; Two = 'query-sort-multi-key-array-backed'; Three = 'query-sort-three-key-array-backed'; Four = 'query-sort-four-key-array-backed'; Five = 'query-sort-five-key-array-backed'; Six = 'query-sort-six-key-array-backed'; Seven = 'query-sort-seven-key-array-backed'; Eight = 'query-sort-eight-key-array-backed' },
        @{ Name = 'list'; One = 'query-sort-one-key-bucket-list'; Two = 'query-sort-multi-key-list-backed'; Three = 'query-sort-three-key-list-backed'; Four = 'query-sort-four-key-list-backed'; Five = 'query-sort-five-key-list-backed'; Six = 'query-sort-six-key-list-backed'; Seven = 'query-sort-seven-key-list-backed'; Eight = 'query-sort-eight-key-list-backed' }
    )
    $sortByteGrowthLimit = 40000
    foreach ($budget in $sortBudgets) {
        $oneKey = Get-QueryBenchmarkCategory $budget.One
        $twoKey = Get-QueryBenchmarkCategory $budget.Two
        $twoKeyRatio = [double]$twoKey.MedianNs / $oneKey.MedianNs
        $twoKeyAllocationGrowth = $twoKey.AllocationsPerEnumeration -
            $oneKey.AllocationsPerEnumeration
        $twoKeyByteGrowth = $twoKey.BytesPerEnumeration -
            $oneKey.BytesPerEnumeration
        if ($twoKeyRatio -gt 3.50 -or $twoKeyAllocationGrowth -gt 50 -or
            $twoKeyByteGrowth -gt $sortByteGrowthLimit) {
            throw ("Two-key {0} sort budget failed: time ratio {1:P1} (max 350%), " +
                "allocation growth {2} (max 50), byte growth {3} (max {4}).") -f
                $budget.Name, $twoKeyRatio, $twoKeyAllocationGrowth,
                $twoKeyByteGrowth, $sortByteGrowthLimit
        }
        Write-Output ("# budget,query-sort-two-key-{0},median_ratio={1:P1},allocation_growth={2},byte_growth={3},PASS" -f
            $budget.Name, $twoKeyRatio, $twoKeyAllocationGrowth,
            $twoKeyByteGrowth)

        $threeKey = Get-QueryBenchmarkCategory $budget.Three
        $threeKeyRatio = [double]$threeKey.MedianNs / $twoKey.MedianNs
        $threeKeyAllocationGrowth = $threeKey.AllocationsPerEnumeration -
            $twoKey.AllocationsPerEnumeration
        $threeKeyByteGrowth = $threeKey.BytesPerEnumeration -
            $twoKey.BytesPerEnumeration
        if ($threeKeyRatio -gt 1.75 -or $threeKeyAllocationGrowth -gt 50 -or
            $threeKeyByteGrowth -gt $sortByteGrowthLimit) {
            throw ("Three-key {0} sort budget failed: time ratio {1:P1} (max 175%), " +
                "allocation growth {2} (max 50), byte growth {3} (max {4}).") -f
                $budget.Name, $threeKeyRatio, $threeKeyAllocationGrowth,
                $threeKeyByteGrowth, $sortByteGrowthLimit
        }
        Write-Output ("# budget,query-sort-three-key-{0},median_ratio={1:P1},allocation_growth={2},byte_growth={3},PASS" -f
            $budget.Name, $threeKeyRatio, $threeKeyAllocationGrowth,
            $threeKeyByteGrowth)

        if ($LowSortGrowthOnly) { continue }

        $fourKey = Get-QueryBenchmarkCategory $budget.Four
        $ratio = [double]$fourKey.MedianNs / $threeKey.MedianNs
        $allocationGrowth = $fourKey.AllocationsPerEnumeration -
            $threeKey.AllocationsPerEnumeration
        $byteGrowth = $fourKey.BytesPerEnumeration -
            $threeKey.BytesPerEnumeration
        if ($ratio -gt 1.75 -or $allocationGrowth -gt 50 -or
            $byteGrowth -gt $sortByteGrowthLimit) {
            throw ("Four-key {0} sort budget failed: time ratio {1:P1} (max 175%), " +
                "allocation growth {2} (max 50), byte growth {3} (max {4}).") -f
                $budget.Name, $ratio, $allocationGrowth, $byteGrowth,
                $sortByteGrowthLimit
        }
        Write-Output ("# budget,query-sort-four-key-{0},median_ratio={1:P1},allocation_growth={2},byte_growth={3},PASS" -f
            $budget.Name, $ratio, $allocationGrowth, $byteGrowth)

        $fiveKey = Get-QueryBenchmarkCategory $budget.Five
        $fiveKeyRatio = [double]$fiveKey.MedianNs / $fourKey.MedianNs
        $fiveKeyAllocationGrowth = $fiveKey.AllocationsPerEnumeration -
            $fourKey.AllocationsPerEnumeration
        $fiveKeyByteGrowth = $fiveKey.BytesPerEnumeration -
            $fourKey.BytesPerEnumeration
        if ($fiveKeyRatio -gt 1.75 -or $fiveKeyAllocationGrowth -gt 50 -or
            $fiveKeyByteGrowth -gt $sortByteGrowthLimit) {
            throw ("Five-key {0} sort budget failed: time ratio {1:P1} (max 175%), " +
                "allocation growth {2} (max 50), byte growth {3} (max {4}).") -f
                $budget.Name, $fiveKeyRatio, $fiveKeyAllocationGrowth,
                $fiveKeyByteGrowth, $sortByteGrowthLimit
        }
        Write-Output ("# budget,query-sort-five-key-{0},median_ratio={1:P1},allocation_growth={2},byte_growth={3},PASS" -f
            $budget.Name, $fiveKeyRatio, $fiveKeyAllocationGrowth,
            $fiveKeyByteGrowth)

        $sixKey = Get-QueryBenchmarkCategory $budget.Six
        $sixKeyRatio = [double]$sixKey.MedianNs / $fiveKey.MedianNs
        $sixKeyAllocationGrowth = $sixKey.AllocationsPerEnumeration -
            $fiveKey.AllocationsPerEnumeration
        $sixKeyByteGrowth = $sixKey.BytesPerEnumeration -
            $fiveKey.BytesPerEnumeration
        if ($sixKeyRatio -gt 1.75 -or $sixKeyAllocationGrowth -gt 50 -or
            $sixKeyByteGrowth -gt $sortByteGrowthLimit) {
            throw ("Six-key {0} sort budget failed: time ratio {1:P1} (max 175%), " +
                "allocation growth {2} (max 50), byte growth {3} (max {4}).") -f
                $budget.Name, $sixKeyRatio, $sixKeyAllocationGrowth,
                $sixKeyByteGrowth, $sortByteGrowthLimit
        }
        Write-Output ("# budget,query-sort-six-key-{0},median_ratio={1:P1},allocation_growth={2},byte_growth={3},PASS" -f
            $budget.Name, $sixKeyRatio, $sixKeyAllocationGrowth,
            $sixKeyByteGrowth)

        $sevenKey = Get-QueryBenchmarkCategory $budget.Seven
        $sevenKeyRatio = [double]$sevenKey.MedianNs / $sixKey.MedianNs
        $sevenKeyAllocationGrowth = $sevenKey.AllocationsPerEnumeration -
            $sixKey.AllocationsPerEnumeration
        $sevenKeyByteGrowth = $sevenKey.BytesPerEnumeration -
            $sixKey.BytesPerEnumeration
        if ($sevenKeyRatio -gt 1.75 -or $sevenKeyAllocationGrowth -gt 50 -or
            $sevenKeyByteGrowth -gt $sortByteGrowthLimit) {
            throw ("Seven-key {0} sort budget failed: time ratio {1:P1} (max 175%), " +
                "allocation growth {2} (max 50), byte growth {3} (max {4}).") -f
                $budget.Name, $sevenKeyRatio, $sevenKeyAllocationGrowth,
                $sevenKeyByteGrowth, $sortByteGrowthLimit
        }
        Write-Output ("# budget,query-sort-seven-key-{0},median_ratio={1:P1},allocation_growth={2},byte_growth={3},PASS" -f
            $budget.Name, $sevenKeyRatio, $sevenKeyAllocationGrowth,
            $sevenKeyByteGrowth)

        $eightKey = Get-QueryBenchmarkCategory $budget.Eight
        $eightKeyRatio = [double]$eightKey.MedianNs / $sevenKey.MedianNs
        $eightKeyAllocationGrowth = $eightKey.AllocationsPerEnumeration -
            $sevenKey.AllocationsPerEnumeration
        $eightKeyByteGrowth = $eightKey.BytesPerEnumeration -
            $sevenKey.BytesPerEnumeration
        if ($eightKeyRatio -gt 1.75 -or $eightKeyAllocationGrowth -gt 50 -or
            $eightKeyByteGrowth -gt $sortByteGrowthLimit) {
            throw ("Eight-key {0} sort budget failed: time ratio {1:P1} (max 175%), " +
                "allocation growth {2} (max 50), byte growth {3} (max {4}).") -f
                $budget.Name, $eightKeyRatio, $eightKeyAllocationGrowth,
                $eightKeyByteGrowth, $sortByteGrowthLimit
        }
        Write-Output ("# budget,query-sort-eight-key-{0},median_ratio={1:P1},allocation_growth={2},byte_growth={3},PASS" -f
            $budget.Name, $eightKeyRatio, $eightKeyAllocationGrowth,
            $eightKeyByteGrowth)

    }
    }
}

if (($EnforceCallbackBudgets -or $EnforcePerformanceBudgets) -and
    -not ($SortGrowthOnly -or $LowSortGrowthOnly -or $MapTakeWorkloadsOnly -or
        $MapFilterTakeWorkloadsOnly -or $FilterTakeWorkloadsOnly -or
        $FilterSkipTakeWorkloadsOnly -or
        $SkipTakeWorkloadsOnly -or $MapSkipTakeWorkloadsOnly)) {
    $supportedCallbackTarget =
        ($IsWindows -and $Architecture -in @('x64','x86')) -or
        ($LinuxWsl -and $Architecture -eq 'x64')
    if (-not $supportedCallbackTarget -or $Configuration -ne 'Release') {
        throw 'Callback budgets require Windows x86/x64 or Linux WSL x64 Release builds.'
    }

    function Get-CallbackBenchmarkCategory([string]$Name) {
        $rows = @($benchmarkOutput | Where-Object { $_.StartsWith("$Name,") } |
            ForEach-Object {
                $columns = $_ -split ','
                [pscustomobject]@{
                    Iterations = [int64]$columns[2]
                    Elapsed = [int64]$columns[3]
                    Checksum = [int64]$columns[4]
                    Allocations = [int64]$columns[5]
                    Bytes = [int64]$columns[6]
                }
            })
        if ($rows.Count -lt 7) {
            throw "Callback budget requires at least seven samples for '$Name'; found $($rows.Count)."
        }
        $iterations = $rows[0].Iterations
        $allocations = $rows[0].Allocations
        $bytes = $rows[0].Bytes
        $checksum = $rows[0].Checksum
        if ($rows | Where-Object {
            $_.Iterations -ne $iterations -or
            $_.Allocations -ne $allocations -or
            $_.Bytes -ne $bytes -or
            $_.Checksum -ne $checksum
        }) {
            throw "Callback benchmark work, checksum, or allocations vary for '$Name'."
        }
        $ordered = @($rows.Elapsed | Sort-Object)
        return [pscustomobject]@{
            MedianNs = [int64]$ordered[[math]::Floor($ordered.Count / 2)]
            AllocationsPerEnumeration = [double]$allocations / $iterations
            BytesPerEnumeration = [double]$bytes / $iterations
            Checksum = $checksum
        }
    }

    $callbackBudgets = @(
        @{ Query = 'query-captured-filter-map'; Baseline = 'handwritten-captured-filter-map'; Ratio = 3.25; Allocations = 3; Bytes = 216 },
        @{ Query = 'query-noncaptured-filter-map'; Baseline = 'handwritten-noncaptured-filter-map'; Ratio = 3.25; Allocations = 2; Bytes = 192 }
    )
    foreach ($budget in $callbackBudgets) {
        $query = Get-CallbackBenchmarkCategory $budget.Query
        $baseline = Get-CallbackBenchmarkCategory $budget.Baseline
        if ($query.Checksum -ne $baseline.Checksum) {
            throw "Callback benchmark checksum mismatch for '$($budget.Query)'."
        }
        $ratio = [double]$query.MedianNs / $baseline.MedianNs
        if ($ratio -gt $budget.Ratio -or
            $query.AllocationsPerEnumeration -gt $budget.Allocations -or
            $query.BytesPerEnumeration -gt $budget.Bytes) {
            throw ("Callback performance budget failed for '{0}': median ratio {1:N2}x (max {2:N2}x), " +
                "{3} allocations/{4} bytes per enumeration (max {5}/{6}).") -f
                $budget.Query, $ratio, $budget.Ratio,
                $query.AllocationsPerEnumeration, $query.BytesPerEnumeration,
                $budget.Allocations, $budget.Bytes
        }
        Write-Output ("# budget,{0},baseline={1},median_ratio={2:N2}x,allocations_per_enumeration={3},bytes_per_enumeration={4},PASS" -f
            $budget.Query, $budget.Baseline, $ratio,
            $query.AllocationsPerEnumeration, $query.BytesPerEnumeration)
    }
}

Write-Output "# cost,emit_ms,$([Math]::Round($emitTimer.Elapsed.TotalMilliseconds, 2))"
Write-Output "# cost,cmake_configure_ms,$([Math]::Round($configureTimer.Elapsed.TotalMilliseconds, 2))"
Write-Output "# cost,native_build_ms,$([Math]::Round($buildTimer.Elapsed.TotalMilliseconds, 2))"
Write-Output "# code_size,query_generated_c_bytes,$queryGeneratedBytes"
Write-Output "# code_size,cxcore_generated_c_bytes,$coreGeneratedBytes"

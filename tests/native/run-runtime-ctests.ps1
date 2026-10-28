param(
    [ValidateSet('Linux', 'Windows')]
    [string] $Platform = 'Windows',
    [ValidateSet('x64', 'x86')]
    [string] $Architecture = 'x64',
    [ValidateSet('static', 'shared', 'both')]
    [string] $LinkMode = 'both',
    [string] $Distribution = 'Ubuntu',
    [string] $CMake = 'cmake',
    [string] $Generator = '',
    [string] $VsInstallationPath = 'C:\Program Files\Microsoft Visual Studio\18\Professional'
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$cxCore = (Resolve-Path (Join-Path $repoRoot 'cxcore')).Path
$cxNet = (Resolve-Path (Join-Path $repoRoot 'cxnet')).Path
$results = Join-Path $PSScriptRoot 'results'
New-Item -ItemType Directory -Path $results -Force | Out-Null
$runId = [guid]::NewGuid().ToString('N')
$runTag = $runId.Substring(0, 8)
$runRoot = Join-Path $repoRoot ".m18rt-$runTag"
$sourceDir = Join-Path $runRoot 'source'
New-Item -ItemType Directory -Path $sourceDir -Force | Out-Null

function Convert-WindowsPathToWslPath([string] $Path) {
    if (-not [IO.Path]::IsPathRooted($Path) -or $Path.Length -lt 3 -or $Path[1] -ne ':') {
        throw "Expected a drive-qualified Windows path, found '$Path'."
    }
    return "/mnt/$($Path.Substring(0, 1).ToLowerInvariant())$($Path.Substring(2).Replace('\', '/'))"
}

if ($Platform -eq 'Linux' -and $Architecture -ne 'x64') {
    throw 'The Linux runtime fixture currently supports x64 only.'
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
    if (-not $Generator) {
        $Generator = 'Ninja'
        $ninjaDir = Join-Path $VsInstallationPath 'Common7\IDE\CommonExtensions\Microsoft\CMake\Ninja'
        $env:PATH = "$ninjaDir;$env:PATH"
    }
}
if ($Platform -eq 'Linux') {
    $coreForCmake = Convert-WindowsPathToWslPath $cxCore
    $netForCmake = Convert-WindowsPathToWslPath $cxNet
} else {
    $coreForCmake = $cxCore.Replace('\', '/')
    $netForCmake = $cxNet.Replace('\', '/')
}
$cmakeLists = @'
cmake_minimum_required(VERSION 3.31)
project(cx_runtime_matrix C)
include(CTest)
set(CX_BUILD_STATIC ${CX_MATRIX_STATIC})
set(BUILD_TESTING ON)
add_subdirectory("@CORE@" cxcore)
add_subdirectory("@NET@" cxnet)
'@
$cmakeLists = $cmakeLists.Replace('@CORE@', $coreForCmake).Replace('@NET@', $netForCmake)
[IO.File]::WriteAllText((Join-Path $sourceDir 'CMakeLists.txt'), $cmakeLists, [Text.UTF8Encoding]::new($false))

$modes = if ($LinkMode -eq 'both') { @('static', 'shared') } else { @($LinkMode) }
foreach ($mode in $modes) {
    $cell = "milestone18-f4-runtime-$($Platform.ToLowerInvariant())-$($Architecture.ToLowerInvariant())-$mode-$runTag"
    $buildDir = Join-Path $runRoot "$($Platform.ToLowerInvariant())-$($Architecture.ToLowerInvariant())-$mode"
    $logPath = Join-Path $results "$cell.log"
    $configureArgs = @('-S', $sourceDir, '-B', $buildDir, "-DCX_MATRIX_STATIC=$([int]($mode -eq 'static'))")
    if ($Platform -eq 'Windows' -and $Generator -match 'Visual Studio') {
        $configureArgs += @('-A', $(if ($Architecture -eq 'x86') { 'Win32' } else { 'x64' }))
    }
    if ($Generator) { $configureArgs += @('-G', $Generator) }
    if ($Platform -eq 'Linux') {
        $wslSource = Convert-WindowsPathToWslPath $sourceDir
        $wslBuild = Convert-WindowsPathToWslPath $buildDir
        $configureArgs = @('-S', $wslSource, '-B', $wslBuild, "-DCX_MATRIX_STATIC=$([int]($mode -eq 'static'))")
        if ($Generator) { $configureArgs += @('-G', $Generator) }
        $configure = & wsl.exe --distribution $Distribution -- $CMake @configureArgs 2>&1
    } else {
        $configure = & $CMake @configureArgs 2>&1
    }
    $configureCode = $LASTEXITCODE
    $configure | Tee-Object -FilePath $logPath
    if ($configureCode -ne 0) { throw "$cell configure failed with exit $configureCode; see $logPath" }

    if ($Platform -eq 'Linux') {
        $wslBuild = Convert-WindowsPathToWslPath $buildDir
        $build = & wsl.exe --distribution $Distribution -- $CMake --build $wslBuild --parallel 2>&1
    } else {
        $build = & $CMake --build $buildDir --config Debug --parallel 2>&1
    }
    $buildCode = $LASTEXITCODE
    $build | Tee-Object -FilePath $logPath -Append
    if ($buildCode -ne 0) { throw "$cell build failed with exit $buildCode; see $logPath" }

    if ($Platform -eq 'Linux') {
        $test = & wsl.exe --distribution $Distribution -- ctest --test-dir (Convert-WindowsPathToWslPath $buildDir) --output-on-failure 2>&1
    } else {
        $testArgs = @('--test-dir', $buildDir, '--output-on-failure', '-C', 'Debug')
        $test = & ctest @testArgs 2>&1
    }
    $testCode = $LASTEXITCODE
    $test | Tee-Object -FilePath $logPath -Append
    if ($testCode -ne 0) { throw "$cell CTest failed with exit $testCode; see $logPath" }
    Write-Output "PASS $cell"
}

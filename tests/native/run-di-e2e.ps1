param(
    [string]$Cxc = "$PSScriptRoot/../../src/CxCompiler/bin/Release/net10.0/CxCompiler.dll",
    [string]$CxCoreDir = "$PSScriptRoot/../../../cxcore",
    [string]$Configuration = 'Release',
    [string]$Generator = '',
    [string]$Architecture = '',
    [ValidateSet('ON','OFF','BOTH')][string]$StaticLink = 'BOTH',
    [switch]$ArenaActivationOnly
)

$ErrorActionPreference = 'Stop'
$fixture = Join-Path $PSScriptRoot 'fixtures/di'
$output = Join-Path $fixture '.obj'
$project = Join-Path $fixture 'di.cxproj'

& dotnet $Cxc --emit-only --output-dir $output $project
if ($LASTEXITCODE -ne 0) { throw 'DI project C generation failed.' }

$linkModes = if ($StaticLink -eq 'BOTH') { @('ON', 'OFF') } else { @($StaticLink) }
foreach ($linkMode in $linkModes) {
    $modeName = if ($linkMode -eq 'ON') { 'static' } else { 'shared' }
    $buildName = if ($Architecture) {
        ".build-$($Architecture.ToLowerInvariant())-$modeName"
    } else {
        ".build-$modeName"
    }
    $build = Join-Path $fixture $buildName
    $cmakeArgs = @(
        '-S', $output,
        '-B', $build,
        "-DCXCORE_SOURCE_DIR=$([IO.Path]::GetFullPath($CxCoreDir))",
        "-DCX_STATIC_LINK=$linkMode",
        "-DCMAKE_BUILD_TYPE=$Configuration"
    )
    if ($Generator) { $cmakeArgs += @('-G', $Generator) }
    if ($Architecture) { $cmakeArgs += @('-A', $Architecture) }

    & cmake @cmakeArgs
    if ($LASTEXITCODE -ne 0) { throw "DI $modeName CMake configuration failed." }
    & cmake --build $build --target cxcore --clean-first --config $Configuration --parallel 4
    if ($LASTEXITCODE -ne 0) { throw "DI $modeName cxcore clean build failed." }
    & cmake --build $build --config $Configuration --parallel 4
    if ($LASTEXITCODE -ne 0) { throw "DI $modeName native compilation failed." }

    $executable = if ($IsWindows) {
        Join-Path $fixture ".bin/$Configuration/di.exe"
    } else {
        Join-Path $fixture '.bin/di'
    }
    if (!(Test-Path -LiteralPath $executable)) {
        $executable = Join-Path $fixture '.bin/di.exe'
    }
    if (!(Test-Path -LiteralPath $executable)) {
        throw "DI $modeName executable was not created."
    }
    if ($ArenaActivationOnly) {
        & $executable '--arena-activation-only'
    } else {
        & $executable
    }
    if ($LASTEXITCODE -ne 0) { throw "DI $modeName fixture failed at check $LASTEXITCODE." }
    Write-Output "PASS DI native fixture ($modeName)"
}

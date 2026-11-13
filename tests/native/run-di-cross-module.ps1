param(
    [string]$Cxc = "$PSScriptRoot/../../src/CxCompiler/bin/Release/net10.0/CxCompiler.dll",
    [string]$CxCoreDir = "$PSScriptRoot/../../../cxcore",
    [string]$Configuration = 'Release',
    [string]$Generator = '',
    [string]$Architecture = '',
    [ValidateSet('ON','OFF','BOTH')][string]$StaticLink = 'BOTH'
)

$ErrorActionPreference = 'Stop'
$fixture = Join-Path $PSScriptRoot 'fixtures/di-cross-module'
$output = Join-Path $fixture '.obj'
$project = Join-Path $fixture 'app/app.cxproj'

& dotnet $Cxc --emit-only --cxcore-dir $CxCoreDir --output-dir $output $project
if ($LASTEXITCODE -ne 0) { throw 'Cross-module DI project C generation failed.' }

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
        '-S', $fixture,
        '-B', $build,
        "-DCX_GENERATED_DIR=$([IO.Path]::GetFullPath($output))",
        "-DCXCORE_SOURCE_DIR=$([IO.Path]::GetFullPath($CxCoreDir))",
        "-DCX_STATIC_LINK=$linkMode",
        "-DCMAKE_BUILD_TYPE=$Configuration"
    )
    if ($Generator) { $cmakeArgs += @('-G', $Generator) }
    if ($Architecture) { $cmakeArgs += @('-A', $Architecture) }

    & cmake @cmakeArgs
    if ($LASTEXITCODE -ne 0) { throw "Cross-module DI $modeName CMake configuration failed." }
    & cmake --build $build --clean-first --config $Configuration --parallel 4
    if ($LASTEXITCODE -ne 0) { throw "Cross-module DI $modeName native build failed." }
    & ctest --test-dir $build -C $Configuration --output-on-failure
    if ($LASTEXITCODE -ne 0) { throw "Cross-module DI $modeName fixture failed." }
    Write-Output "PASS cross-module DI native fixture ($modeName)"
}

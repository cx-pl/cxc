param(
    [string]$Cxc = "$PSScriptRoot/../../src/bin/Release/net10.0/CxCompiler.dll",
    [string]$CxCoreDir = "$PSScriptRoot/../../../cxcore",
    [string]$Configuration = 'Debug',
    [string]$Generator = '',
    [string]$Architecture = '',
    [ValidateSet('ON','OFF')][string]$StaticLink = 'ON'
)
$ErrorActionPreference = 'Stop'
$fixture = Join-Path $PSScriptRoot 'fixtures/stdlib-additions'
$output = Join-Path $fixture '.obj'
$build = Join-Path $fixture ".build-$StaticLink"
& dotnet $Cxc (Join-Path $fixture 'main.cx') --module-name stdlibAdditions -o $output
if ($LASTEXITCODE -ne 0) { throw 'Standard-library source generation failed.' }
$cmakeArgs = @('-S',$output,'-B',$build,"-DCXCORE_SOURCE_DIR=$([IO.Path]::GetFullPath($CxCoreDir))",
    "-DCX_STATIC_LINK=$StaticLink","-DCMAKE_BUILD_TYPE=$Configuration")
if ($Generator) { $cmakeArgs += @('-G',$Generator) }
if ($Architecture) { $cmakeArgs += @('-A',$Architecture) }
& cmake @cmakeArgs
if ($LASTEXITCODE -ne 0) { throw 'Standard-library CMake configuration failed.' }
& cmake --build $build --config $Configuration --parallel 4
if ($LASTEXITCODE -ne 0) { throw 'Standard-library native compilation failed.' }
$executable = if ($IsWindows) { Join-Path $fixture ".bin/$Configuration/stdlibAdditions.exe" }
    else { Join-Path $fixture '.bin/stdlibAdditions' }
if (!(Test-Path -LiteralPath $executable)) { $executable = Join-Path $fixture '.bin/stdlibAdditions.exe' }
& $executable
if ($LASTEXITCODE -ne 0) { throw "Standard-library fixture failed at check $LASTEXITCODE." }
Write-Output 'PASS UUID versions 1-8, numeric TryParse, and StringBuilder'

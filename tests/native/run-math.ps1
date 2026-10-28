param(
    [string]$Cxc = "$PSScriptRoot/../../src/bin/Release/net10.0/CxCompiler.dll",
    [string]$CxCoreDir = "$PSScriptRoot/../../../cxcore",
    [string]$Configuration = 'Debug',
    [string]$Generator = '',
    [string]$Architecture = '',
    [ValidateSet('ON','OFF')][string]$StaticLink = 'ON'
)
$ErrorActionPreference = 'Stop'
$fixture = Join-Path $PSScriptRoot 'fixtures/math'
$output = Join-Path $fixture '.obj'
$build = Join-Path $fixture ".build-$StaticLink"
& dotnet $Cxc (Join-Path $fixture 'main.cx') --module-name mathTest -o $output
if ($LASTEXITCODE -ne 0) { throw 'Math source generation failed.' }
$cmakeArgs = @('-S',$output,'-B',$build,"-DCXCORE_SOURCE_DIR=$([IO.Path]::GetFullPath($CxCoreDir))",
    "-DCX_STATIC_LINK=$StaticLink","-DCMAKE_BUILD_TYPE=$Configuration")
if ($Generator) { $cmakeArgs += @('-G',$Generator) }
if ($Architecture) { $cmakeArgs += @('-A',$Architecture) }
& cmake @cmakeArgs
if ($LASTEXITCODE -ne 0) { throw 'Math CMake configuration failed.' }
& cmake --build $build --config $Configuration --parallel 4
if ($LASTEXITCODE -ne 0) { throw 'Math native compilation failed.' }
$executable = if ($IsWindows) { Join-Path $fixture ".bin/$Configuration/mathTest.exe" }
    else { Join-Path $fixture '.bin/mathTest' }
if (!(Test-Path -LiteralPath $executable)) { $executable = Join-Path $fixture '.bin/mathTest.exe' }
& $executable
if ($LASTEXITCODE -ne 0) { throw "Math fixture failed at check $LASTEXITCODE." }
Write-Output 'PASS Math constants, all overloads, and managed errors'

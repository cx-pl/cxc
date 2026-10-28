param(
    [string]$Cxc = "$PSScriptRoot/../../src/bin/Release/net10.0/CxCompiler.dll",
    [string]$CxCoreDir = "$PSScriptRoot/../../../cxcore",
    [string]$Configuration = 'Debug',
    [string]$Generator = '',
    [string]$Architecture = '',
    [ValidateSet('ON', 'OFF')][string]$StaticLink = 'ON'
)
$ErrorActionPreference = 'Stop'
$keywordFixture = Join-Path $PSScriptRoot 'fixtures/keyword-expressions'
$keywordOutput = Join-Path $keywordFixture '.obj'
$keywordBuild = Join-Path $keywordFixture ".build-$StaticLink"
& dotnet $Cxc (Join-Path $keywordFixture 'main.cx') --module-name keywordExpressions -o $keywordOutput
if ($LASTEXITCODE -ne 0) { throw 'Keyword expression source generation failed.' }
$keywordCMakeArgs = @('-S', $keywordOutput, '-B', $keywordBuild,
    "-DCXCORE_SOURCE_DIR=$([IO.Path]::GetFullPath($CxCoreDir))", "-DCX_STATIC_LINK=$StaticLink",
    "-DCMAKE_BUILD_TYPE=$Configuration")
if ($Generator) { $keywordCMakeArgs += @('-G', $Generator) }
if ($Architecture) { $keywordCMakeArgs += @('-A', $Architecture) }
& cmake @keywordCMakeArgs
if ($LASTEXITCODE -ne 0) { throw 'Keyword expression CMake configuration failed.' }
& cmake --build $keywordBuild --config $Configuration --parallel 4
if ($LASTEXITCODE -ne 0) { throw 'Keyword expression native compilation failed.' }
$keywordExecutable = if ($IsWindows) {
    Join-Path $keywordFixture ".bin/$Configuration/keywordExpressions.exe"
} else { Join-Path $keywordFixture '.bin/keywordExpressions' }
if (!(Test-Path -LiteralPath $keywordExecutable)) {
    $keywordExecutable = Join-Path $keywordFixture '.bin/keywordExpressions.exe'
}
& $keywordExecutable
if ($LASTEXITCODE -ne 0) { throw "Keyword expression native fixture failed at check $LASTEXITCODE." }
Write-Output 'PASS safe casts, decimal arithmetic and storage, nameof, and typeof'

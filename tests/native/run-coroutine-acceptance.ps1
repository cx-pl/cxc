param(
    [string] $Cxc = 'cxc',
    [ValidateSet('Linux', 'Windows')]
    [string] $Platform = 'Windows',
    [ValidateSet('x64', 'x86')]
    [string] $Architecture = 'x64',
    [ValidateSet('static', 'shared', 'both')]
    [string] $LinkMode = 'both',
    [string] $CxCoreDir = (Join-Path $PSScriptRoot '..\..\..\cxcore'),
    [string] $Distribution = 'Ubuntu',
    [string] $CMake = 'cmake',
    [string] $Generator = ''
)

$ErrorActionPreference = 'Stop'
$probeRunner = Join-Path $PSScriptRoot 'run-awaited-finally-linux.ps1'
$runnerArguments = @{
    Cxc = $Cxc
    CxCoreDir = $CxCoreDir
    Platform = $Platform
    Architecture = $Architecture
    LinkMode = $LinkMode
    CMake = $CMake
    Generator = $Generator
}
if ($Platform -eq 'Linux') {
    $runnerArguments.Distribution = $Distribution
}
& $probeRunner @runnerArguments
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

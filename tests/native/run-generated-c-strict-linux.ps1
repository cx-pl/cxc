param(
    [ValidateSet('x64')]
    [string] $Architecture = 'x64',
    [string] $Distribution = 'Ubuntu'
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$results = Join-Path $PSScriptRoot 'results'
New-Item -ItemType Directory -Path $results -Force | Out-Null
$runTag = [guid]::NewGuid().ToString('N').Substring(0, 8)
$logPath = Join-Path $results "milestone18-f4-generated-c-strict-linux-$Architecture-$runTag.log"
$repoLinux = "/mnt/$($repoRoot.Substring(0, 1).ToLowerInvariant())$($repoRoot.Substring(2).Replace('\', '/'))"
$objectDir = "/tmp/m18-generated-c-strict-$runTag"
$createOutput = & wsl.exe --distribution $Distribution -- mkdir -p $objectDir 2>&1
if ($LASTEXITCODE -ne 0) { throw "Could not create Linux object directory: $($createOutput -join ' ')" }

# Keep cxcore/platform off the include path: its stdlib.h shadows the C standard header.
$coreIncludes = @(
    "$repoLinux/cxcore/src/.obj", "$repoLinux/cxcore/src", "$repoLinux/cxcore/inc",
    "$repoLinux/cxcore/platform/common",
    "$repoLinux/cxcore/platform/linux"
)
$cxnetIncludes = @(
    "$repoLinux/cxnet/.obj", "$repoLinux/cxnet/include", "$repoLinux/cxnet/platform/common",
    "$repoLinux/cxnet/platform/linux", "$repoLinux/cxcore/src/.obj", "$repoLinux/cxcore/src",
    "$repoLinux/cxcore/inc", "$repoLinux/cxcore/platform/common",
    "$repoLinux/cxcore/platform/linux"
)
$consumerIncludes = @(
    "$repoLinux/cxnet/examples/streaming-read/.obj", "$repoLinux/cxnet/.obj",
    "$repoLinux/cxnet/include", "$repoLinux/cxnet/platform/common", "$repoLinux/cxnet/platform/linux",
    "$repoLinux/cxcore/src/.obj", "$repoLinux/cxcore/src", "$repoLinux/cxcore/inc",
    "$repoLinux/cxcore/platform/common", "$repoLinux/cxcore/platform/linux"
)
$cases = @(
    @{ Name = 'core-static'; Source = 'cxcore/src/.obj/cxcore.generated.c'; Includes = $coreIncludes; Defines = @('CX_CXCORE_BUILD', 'CX_STATIC_LINK') }
    @{ Name = 'core-shared'; Source = 'cxcore/src/.obj/cxcore.generated.c'; Includes = $coreIncludes; Defines = @('CX_CXCORE_BUILD', 'CX_DYNAMIC_MODULE') }
    @{ Name = 'cxnet-static'; Source = 'cxnet/.obj/cxnet.c'; Includes = $cxnetIncludes; Defines = @('CX_CXNET_BUILD', 'CX_STATIC_LINK') }
    @{ Name = 'cxnet-shared'; Source = 'cxnet/.obj/cxnet.c'; Includes = $cxnetIncludes; Defines = @('CX_CXNET_BUILD', 'CX_DYNAMIC_MODULE') }
    @{ Name = 'consumer-static'; Source = 'cxnet/examples/streaming-read/.obj/streamingRead.c'; Includes = $consumerIncludes; Defines = @('CX_STREAMINGREAD_BUILD', 'CX_STATIC_LINK') }
    @{ Name = 'consumer-shared'; Source = 'cxnet/examples/streaming-read/.obj/streamingRead.c'; Includes = $consumerIncludes; Defines = @('CX_STREAMINGREAD_BUILD', 'CX_DYNAMIC_MODULE') }
)

foreach ($case in $cases) {
    $sourcePath = "$repoLinux/$($case.Source.Replace('\', '/'))"
    $arguments = @('-std=c11', '-Wall', '-Wextra', '-Werror', '-Wno-unused-parameter', '-Wno-unused-variable', '-c')
    foreach ($include in $case.Includes) { $arguments += @('-I', $include) }
    foreach ($define in $case.Defines) { $arguments += "-D$define" }
    $arguments += @($sourcePath, '-o', "$objectDir/$($case.Name).o")
    $output = & wsl.exe --distribution $Distribution -- gcc @arguments 2>&1
    $exitCode = $LASTEXITCODE
    $output | Tee-Object -FilePath $logPath -Append
    if ($exitCode -ne 0) {
        throw "$Architecture $($case.Name) GCC strict compilation failed with exit $exitCode; see $logPath"
    }
    "PASS $Architecture $($case.Name)" | Tee-Object -FilePath $logPath -Append
}

Write-Output "PASS Linux $Architecture strict generated C; log: $logPath"

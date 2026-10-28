param(
    [ValidateSet('x86', 'x64')]
    [string] $Architecture = 'x64',
    [string] $VsInstallationPath = 'C:\Program Files\Microsoft Visual Studio\18\Professional'
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$devShell = Join-Path $VsInstallationPath 'Common7\Tools\Launch-VsDevShell.ps1'
if (-not (Test-Path -LiteralPath $devShell -PathType Leaf)) {
    throw "Visual Studio developer shell was not found at '$devShell'."
}

& $devShell -VsInstallationPath $VsInstallationPath `
    -Arch $Architecture -HostArch x64 -SkipAutomaticLocation -NoLogo
if ($LASTEXITCODE -and $LASTEXITCODE -ne 0) {
    throw "Visual Studio developer shell initialization failed with exit $LASTEXITCODE."
}

$strictOutput = Join-Path ([IO.Path]::GetTempPath()) `
    "cx-generated-c-strict-$Architecture-$([guid]::NewGuid().ToString('N'))"
$results = Join-Path $PSScriptRoot 'results'
New-Item -ItemType Directory -Path $results -Force | Out-Null
$strictLog = Join-Path $results "milestone18-f4-generated-c-strict-windows-$Architecture-$([guid]::NewGuid().ToString('N').Substring(0, 8)).log"
New-Item -ItemType Directory -Path $strictOutput | Out-Null
"MSVC strict generated-C run: architecture=$Architecture compiler=MSVC C11 warnings-as-errors (4100,4130,4702 suppressed)" |
    Tee-Object -FilePath $strictLog

# Keep cxcore/platform off the include path: its stdlib.h shadows the C standard header.
$includeSets = @{
    core = @(
        'cxcore/src/.obj', 'cxcore/src', 'cxcore/inc',
        'cxcore/platform/common', 'cxcore/platform/win'
    )
    cxnet = @(
        'cxnet/.obj', 'cxnet/include', 'cxnet/platform/common',
        'cxnet/platform/win', 'cxcore/src/.obj', 'cxcore/src', 'cxcore/inc',
        'cxcore/platform/common', 'cxcore/platform/win'
    )
    consumer = @(
        'cxnet/examples/streaming-read/.obj', 'cxnet/.obj', 'cxnet/include',
        'cxnet/platform/common', 'cxnet/platform/win', 'cxcore/src/.obj',
        'cxcore/src', 'cxcore/inc',
        'cxcore/platform/common', 'cxcore/platform/win'
    )
}
$cases = @(
    @{ Name = 'core-static'; Source = 'cxcore/src/.obj/cxcore.generated.c'; Includes = $includeSets.core; Defines = @('CX_CXCORE_BUILD', 'CX_STATIC_LINK') }
    @{ Name = 'core-shared'; Source = 'cxcore/src/.obj/cxcore.generated.c'; Includes = $includeSets.core; Defines = @('CX_CXCORE_BUILD', 'CX_DYNAMIC_MODULE') }
    @{ Name = 'cxnet-static'; Source = 'cxnet/.obj/cxnet.c'; Includes = $includeSets.cxnet; Defines = @('CX_CXNET_BUILD', 'CX_STATIC_LINK') }
    @{ Name = 'cxnet-shared'; Source = 'cxnet/.obj/cxnet.c'; Includes = $includeSets.cxnet; Defines = @('CX_CXNET_BUILD', 'CX_DYNAMIC_MODULE') }
    @{ Name = 'consumer-static'; Source = 'cxnet/examples/streaming-read/.obj/streamingRead.c'; Includes = $includeSets.consumer; Defines = @('CX_STREAMINGREAD_BUILD', 'CX_STATIC_LINK') }
    @{ Name = 'consumer-shared'; Source = 'cxnet/examples/streaming-read/.obj/streamingRead.c'; Includes = $includeSets.consumer; Defines = @('CX_STREAMINGREAD_BUILD', 'CX_DYNAMIC_MODULE') }
)

try {
    foreach ($case in $cases) {
        $sourcePath = Join-Path $repoRoot $case.Source
        if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
            throw "Generated C source is missing: '$sourcePath'. Generate the cxcore/cxnet/streaming-read projects first."
        }

        $arguments = @(
            '/nologo', '/c', '/std:c11', '/W4', '/WX',
            '/wd4100', '/wd4130', '/wd4702',
            '/utf-8', '/experimental:c11atomics'
        )
        foreach ($include in $case.Includes) {
            $includePath = (Resolve-Path (Join-Path $repoRoot $include)).Path
            $arguments += "/I$includePath"
        }
        foreach ($define in $case.Defines) {
            $arguments += "/D$define"
        }
        $arguments += "/Fo$(Join-Path $strictOutput ($case.Name + '.obj'))"
        $arguments += $sourcePath

        & cl.exe @arguments 2>&1 | Tee-Object -FilePath $strictLog -Append
        if ($LASTEXITCODE -ne 0) {
            throw "$Architecture $($case.Name) strict compilation failed with exit $LASTEXITCODE. Objects are in '$strictOutput'."
        }
        "PASS $Architecture $($case.Name)" | Tee-Object -FilePath $strictLog -Append
    }

    Remove-Item -LiteralPath $strictOutput -Recurse -Force
    Write-Output "Strict generated-C log: $strictLog"
}
catch {
    Write-Output "Strict compile objects retained in '$strictOutput'."
    throw
}

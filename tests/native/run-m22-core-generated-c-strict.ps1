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

$sourcePath = Join-Path $repoRoot 'cxcore/src/.obj/cxcore.c'
if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
    throw "Current generated core C is missing: '$sourcePath'. Generate cxcore first."
}

$includeDirectories = @(
    'cxcore/src/.obj', 'cxcore/src', 'cxcore/include',
    'cxcore/platform/common', 'cxcore/platform/win'
)
$strictOutput = Join-Path ([IO.Path]::GetTempPath()) `
    "cx-m22-core-generated-c-strict-$Architecture-$([guid]::NewGuid().ToString('N'))"
$results = Join-Path $PSScriptRoot 'results'
New-Item -ItemType Directory -Path $results -Force | Out-Null
$strictLog = Join-Path $results `
    "milestone22-core-generated-c-strict-windows-$Architecture-$([guid]::NewGuid().ToString('N').Substring(0, 8)).log"
New-Item -ItemType Directory -Path $strictOutput | Out-Null
"Milestone 22 strict current generated-core C: architecture=$Architecture compiler=MSVC C11 /W4 /WX (4130,4702 suppressed)" |
    Tee-Object -FilePath $strictLog

try {
    foreach ($linkage in @('static', 'shared')) {
        $objectPath = Join-Path $strictOutput "core-current-$linkage.obj"
        $arguments = @(
            '/nologo', '/c', '/std:c11', '/W4', '/WX',
            '/wd4130', '/wd4702',
            '/utf-8', '/experimental:c11atomics'
        )
        foreach ($includeDirectory in $includeDirectories) {
            $includePath = (Resolve-Path (Join-Path $repoRoot $includeDirectory)).Path
            $arguments += "/I$includePath"
        }
        $arguments += '/DCX_CXCORE_BUILD'
        if ($linkage -eq 'static') {
            $arguments += '/DCX_STATIC_LINK'
        }
        else {
            $arguments += '/DCX_DYNAMIC_MODULE'
        }
        $arguments += "/Fo$objectPath"
        $arguments += $sourcePath

        & cl.exe @arguments 2>&1 | Tee-Object -FilePath $strictLog -Append
        if ($LASTEXITCODE -ne 0) {
            throw "$Architecture $linkage current generated-core strict compilation failed with exit $LASTEXITCODE. Objects are in '$strictOutput'."
        }
        "PASS $Architecture current-core $linkage" | Tee-Object -FilePath $strictLog -Append
    }

    $resolvedTempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    $resolvedOutput = [IO.Path]::GetFullPath($strictOutput)
    if (-not $resolvedOutput.StartsWith($resolvedTempRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove compiler objects outside the temp root: '$resolvedOutput'."
    }
    Remove-Item -LiteralPath $resolvedOutput -Recurse -Force
    Write-Output "Strict compile log: $strictLog"
}
catch {
    Write-Output "Strict compile objects retained in '$strictOutput'."
    throw
}

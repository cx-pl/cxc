param(
    [string] $Cxc = 'cxc',
    [string] $CxCoreDir = (Join-Path $PSScriptRoot '..\..\..\cxcore'),
    [ValidateSet('Windows', 'Linux')]
    [string] $Platform = 'Windows',
    [ValidateSet('x64', 'x86')]
    [string] $Architecture = 'x64',
    [ValidateSet('static', 'shared', 'both')]
    [string] $LinkMode = 'both',
    [string] $CMake = 'cmake',
    [string] $Generator = '',
    [string] $Distribution = 'Ubuntu',
    [string] $FixturePath = (Join-Path $PSScriptRoot 'fixtures\cross-module-abi'),
    [switch] $KeepBuild
)

$ErrorActionPreference = 'Stop'
$compilerPath = if ([IO.Path]::IsPathRooted($Cxc)) {
    (Resolve-Path -LiteralPath $Cxc).Path
} else {
    (Get-Command $Cxc -ErrorAction Stop).Source
}
$cxCorePath = (Resolve-Path -LiteralPath $CxCoreDir).Path
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$fixturePath = (Resolve-Path -LiteralPath $FixturePath).Path
$buildRoot = Join-Path ([IO.Path]::GetTempPath()) `
    "cx-cross-module-abi-$([guid]::NewGuid().ToString('N'))"
$projectRoot = Join-Path $buildRoot 'project'
$generatedPath = Join-Path $buildRoot 'generated'
$utf8NoBom = [Text.UTF8Encoding]::new($false)
$completed = $false

function Convert-WindowsPathToWslPath([string] $Path) {
    if (-not [IO.Path]::IsPathRooted($Path) -or $Path.Length -lt 3 -or $Path[1] -ne ':') {
        throw "Expected a drive-qualified Windows path, found '$Path'."
    }
    $drive = $Path.Substring(0, 1).ToLowerInvariant()
    $suffix = $Path.Substring(2).Replace('\', '/')
    return "/mnt/$drive$suffix"
}

if ($Platform -eq 'Linux' -and $Architecture -ne 'x64') {
    throw 'The Linux cross-module ABI fixture supports x64 only.'
}

if ($Platform -eq 'Linux') {
    $cmakeVersionOutput = & wsl.exe --distribution $Distribution -- $CMake --version 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Could not run WSL CMake '$CMake': $($cmakeVersionOutput -join [Environment]::NewLine)"
    }
    $version = [regex]::Match(($cmakeVersionOutput -join ' '), 'cmake version ([0-9]+)\.([0-9]+)')
    if (-not $version.Success -or [int]$version.Groups[1].Value -lt 3 -or
        ([int]$version.Groups[1].Value -eq 3 -and [int]$version.Groups[2].Value -lt 31)) {
        throw "CMake 3.31 or newer is required; found '$($cmakeVersionOutput -join ' ')'."
    }
}

New-Item -ItemType Directory -Path $buildRoot -Force | Out-Null
Copy-Item -LiteralPath $fixturePath -Destination $projectRoot -Recurse
New-Item -ItemType Directory -Path $generatedPath -Force | Out-Null

try {
    $emitArguments = @(
        '--emit-only', '--cxcore-dir', $cxCorePath, '--verbosity', 'quiet',
        '--output-dir', $generatedPath, (Join-Path $projectRoot 'app\app.cxproj')
    )
    if ([IO.Path]::GetExtension($compilerPath) -ieq '.dll') {
        & dotnet $compilerPath @emitArguments
    } else {
        & $compilerPath @emitArguments
    }
    if ($LASTEXITCODE -ne 0) {
        throw "Cross-module ABI fixture generation failed (exit $LASTEXITCODE). Files are in '$buildRoot'."
    }

    $modes = if ($LinkMode -eq 'both') { @('static', 'shared') } else { @($LinkMode) }
    foreach ($mode in $modes) {
        $staticLink = if ($mode -eq 'static') { 'ON' } else { 'OFF' }
        $buildName = "$Platform-$Architecture-$mode"
        if ($Platform -eq 'Linux') {
            $linuxFixture = Convert-WindowsPathToWslPath $projectRoot
            $linuxGenerated = Convert-WindowsPathToWslPath $generatedPath
            $linuxBuild = (Convert-WindowsPathToWslPath $buildRoot) + "/build-$buildName"
            $linuxCore = (Convert-WindowsPathToWslPath $cxCorePath)
            $configureOutput = & wsl.exe --distribution $Distribution -- $CMake `
                -S $linuxFixture -B $linuxBuild `
                "-DCX_GENERATED_DIR=$linuxGenerated" `
                "-DCXCORE_SOURCE_DIR=$linuxCore" `
                "-DCX_STATIC_LINK=$staticLink" '-DCMAKE_BUILD_TYPE=Release' 2>&1
            if ($LASTEXITCODE -ne 0) {
                throw "WSL $buildName configure failed: $($configureOutput -join [Environment]::NewLine). Files are in '$buildRoot'."
            }
            Write-Output $configureOutput
            $buildOutput = & wsl.exe --distribution $Distribution -- $CMake `
                --build $linuxBuild --parallel 2 2>&1
            if ($LASTEXITCODE -ne 0) {
                throw "WSL $buildName build failed: $($buildOutput -join [Environment]::NewLine). Files are in '$buildRoot'."
            }
            Write-Output $buildOutput
            & wsl.exe --distribution $Distribution -- ctest `
                --test-dir $linuxBuild --output-on-failure
            if ($LASTEXITCODE -ne 0) {
                throw "WSL $buildName CTest failed. Files are in '$buildRoot'."
            }
            if ($mode -eq 'shared') {
                $executable = (Convert-WindowsPathToWslPath $projectRoot) + '/app/.bin/app'
                $relocations = & wsl.exe --distribution $Distribution -- ldd -r $executable 2>&1
                if ($LASTEXITCODE -ne 0) {
                    throw "WSL shared ldd -r failed: $($relocations -join [Environment]::NewLine). Files are in '$buildRoot'."
                }
                Write-Output $relocations
            }
        } else {
            $cmakePath = if (Test-Path -LiteralPath $CMake -PathType Leaf) {
                (Resolve-Path -LiteralPath $CMake).Path
            } else {
                (Get-Command $CMake -ErrorAction Stop).Source
            }
            $cmakeBuild = Join-Path $buildRoot "build-$buildName"
            $configureArguments = @(
                '-S', $projectRoot, '-B', $cmakeBuild,
                "-DCX_GENERATED_DIR=$generatedPath",
                "-DCXCORE_SOURCE_DIR=$cxCorePath",
                "-DCX_STATIC_LINK=$staticLink"
            )
            if ($Generator) {
                $configureArguments += @('-G', $Generator)
            }
            if (-not $Generator -or $Generator -match 'Visual Studio') {
                $configureArguments += @('-A', $(if ($Architecture -eq 'x86') { 'Win32' } else { 'x64' }))
            } elseif ($Architecture -eq 'x86') {
                throw 'Windows x86 requires a Visual Studio generator with -A Win32.'
            }
            Write-Output "CONFIGURE cross-module ABI platform=Windows architecture=$Architecture link=$mode"
            & $cmakePath @configureArguments
            if ($LASTEXITCODE -ne 0) {
                throw "Windows $buildName configure failed (exit $LASTEXITCODE). Files are in '$buildRoot'."
            }
            & $cmakePath --build $cmakeBuild --config Release
            if ($LASTEXITCODE -ne 0) {
                throw "Windows $buildName build failed (exit $LASTEXITCODE). Files are in '$buildRoot'."
            }
            $ctestPath = Join-Path (Split-Path -Parent $cmakePath) 'ctest.exe'
            if (-not (Test-Path -LiteralPath $ctestPath -PathType Leaf)) {
                $ctestPath = (Get-Command ctest -ErrorAction Stop).Source
            }
            & $ctestPath --test-dir $cmakeBuild -C Release --output-on-failure
            if ($LASTEXITCODE -ne 0) {
                throw "Windows $buildName CTest failed (exit $LASTEXITCODE). Files are in '$buildRoot'."
            }
        }
        Write-Output "PASS native ABI fixture $Platform $Architecture $mode"
    }
    $completed = $true
}
finally {
    if ($completed -and $KeepBuild) {
        Write-Output "BUILD PRESERVED: $buildRoot"
    } elseif ($completed -and (Test-Path -LiteralPath $buildRoot)) {
        Remove-Item -LiteralPath $buildRoot -Recurse -Force
    } elseif (Test-Path -LiteralPath $buildRoot) {
        Write-Output "FAILED BUILD PRESERVED: $buildRoot"
    }
}

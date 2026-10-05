param(
    [string] $Cxc = 'cxc',
    [string] $CxCoreDir = (Join-Path $PSScriptRoot '..\..\..\cxcore'),
    [string] $CMake = 'cmake',
    [string] $Generator = '',
    [string] $CCompiler = ''
)

$ErrorActionPreference = 'Stop'
$compilerPath = if ([IO.Path]::IsPathRooted($Cxc)) {
    $Cxc
} else {
    (Get-Command $Cxc -ErrorAction Stop).Source
}
$cxCorePath = (Resolve-Path $CxCoreDir).Path
$cmakePath = if (Test-Path -LiteralPath $CMake -PathType Leaf) {
    (Resolve-Path $CMake).Path
} else {
    (Get-Command $CMake -ErrorAction Stop).Source
}
$buildRoot = Join-Path ([IO.Path]::GetTempPath()) "cx-entry-point-e2e-$([guid]::NewGuid().ToString('N'))"
$outputDirectory = Join-Path $buildRoot 'generated'
$binaryDirectory = Join-Path $buildRoot '.bin'
$sourcePath = Join-Path $buildRoot 'entry_point_e2e.cx'
$previousPath = $env:PATH
$previousGenerator = $env:CMAKE_GENERATOR
$previousCCompiler = $env:CC
$completed = $false

try {
    New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
    [IO.File]::WriteAllText($sourcePath, @'
import System;

public static class Effects {
    public static int State;

    public static bool Mark(bool result, int digit) {
        State = State * 10 + digit;
        return result;
    }
}

public class Level0 {
    public constructor() {}
    public int Number() { return 41; }
}
public class Level1 : Level0 { public constructor() : base() {} }
public class Level2 : Level1 { public constructor() : base() {} }
public class Level3 : Level2 { public constructor() : base() {} }
public class Level4 : Level3 { public constructor() : base() {} }

public interface IBaseValue { int Get(); }
public interface ILeftValue : IBaseValue {}
public interface IRightValue : IBaseValue {}
public interface IDiamondValue : ILeftValue, IRightValue {}
public class DiamondValue : IDiamondValue {
    public constructor() {}
    public int Get() { return 42; }
}

public string Choose(string value) {
    return value ?? "fallback";
}

public long ReadLongMaximum() {
    return 9223372036854775807L;
}

public long ReadLongMinimum() {
    return -9223372036854775807L - 1L;
}

public int Main(string[] args) {
    if (args.Length != 2u) return 21;
    if (args[0].Length != 5u) return 22;
    if (args[1].Length != 4u) return 23;

    Effects.State = 0;
    bool andResult = Effects.Mark(false, 1) && Effects.Mark(true, 2);
    if (andResult || Effects.State != 1) return 24;
    Effects.State = 0;
    bool orResult = Effects.Mark(true, 3) || Effects.Mark(false, 4);
    if (!orResult || Effects.State != 3) return 25;
    if (Choose(null) != "fallback") return 26;
    if (ReadLongMaximum() != 9223372036854775807L) return 27;
    if (ReadLongMinimum() != -9223372036854775807L - 1L) return 28;

    Level4 deep = new Level4();
    if (deep.Number() != 41) return 29;
    IBaseValue diamond = new DiamondValue();
    if (diamond.Get() != 42) return 30;

    Console.WriteLine("native e2e pass");
    return 0;
}
'@, [Text.UTF8Encoding]::new($false))
    $env:PATH = "$(Split-Path -Parent $cmakePath);$env:PATH"
    if ($Generator -like '*Ninja*') {
        $ninjaDirectory = Join-Path (Split-Path -Parent $cmakePath) '..\..\Ninja'
        if (Test-Path -LiteralPath (Join-Path $ninjaDirectory 'ninja.exe')) {
            $env:PATH = "$(Resolve-Path $ninjaDirectory);$env:PATH"
        }
    }
    if ($Generator) { $env:CMAKE_GENERATOR = $Generator }
    if ($CCompiler) { $env:CC = $CCompiler }

    $compilerArguments = @(
        '--compile', '--cxcore-dir', $cxCorePath, '--verbosity', 'quiet',
        '--output-dir', $outputDirectory, $sourcePath
    )
    if ([IO.Path]::GetExtension($compilerPath) -ieq '.dll') {
        & dotnet $compilerPath @compilerArguments
    } else {
        & $compilerPath @compilerArguments
    }
    if ($LASTEXITCODE -ne 0) {
        throw "CX native compilation failed (exit $LASTEXITCODE). Build files are in '$buildRoot'."
    }

    $executable = Get-ChildItem -LiteralPath $binaryDirectory -Filter '*.exe' -File -Recurse |
        Select-Object -First 1
    if ($null -eq $executable) {
        throw "Native executable was not created. Build files are in '$buildRoot'."
    }

    $validArguments = @('Älpha', 'beta')
    $actualOutput = (& $executable.FullName @validArguments | ForEach-Object { "$_" }) -join "`n"
    $validExitCode = $LASTEXITCODE
    if ($validExitCode -ne 0 -or $actualOutput.Trim() -ne 'native e2e pass') {
        throw "Valid argument run failed: exit=$validExitCode output='$($actualOutput.Trim())'."
    }

    $invalidArguments = @('alpha')
    $invalidOutput = (& $executable.FullName @invalidArguments | ForEach-Object { "$_" }) -join "`n"
    $invalidExitCode = $LASTEXITCODE
    if ($invalidExitCode -ne 21 -or $invalidOutput.Trim()) {
        throw "Invalid argument run failed: exit=$invalidExitCode output='$($invalidOutput.Trim())'."
    }

    $projectRoot = Join-Path $buildRoot 'project-reference'
    $appDirectory = Join-Path $projectRoot 'app'
    $libraryDirectory = Join-Path $projectRoot 'math'
    $projectOutput = Join-Path $projectRoot 'generated'
    New-Item -ItemType Directory -Path $appDirectory, $libraryDirectory, $projectOutput -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $libraryDirectory 'math.cxproj'), "name: math`ntype: Library`n", [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText((Join-Path $libraryDirectory 'api.cx'), @'
namespace Math;
public int AddOne(int value) { return value + 1; }
'@, [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText((Join-Path $appDirectory 'app.cxproj'), "name: app`ntype: Executable`nproject_references:`n- ../math/math.cxproj`n", [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText((Join-Path $appDirectory 'main.cx'), @'
import Math;
namespace App;
public int Run() { return AddOne(41); }
'@, [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText((Join-Path $appDirectory 'main.c'), @'
#include "app.h"
int main(void) { return CX_ID_3(app, App, Run)() == 42 ? 0 : 1; }
'@, [Text.UTF8Encoding]::new($false))

    $projectArguments = @(
        '--compile', '--cxcore-dir', $cxCorePath, '--verbosity', 'quiet',
        '--output-dir', $projectOutput, (Join-Path $appDirectory 'app.cxproj')
    )
    if ([IO.Path]::GetExtension($compilerPath) -ieq '.dll') {
        & dotnet $compilerPath @projectArguments
    } else {
        & $compilerPath @projectArguments
    }
    if ($LASTEXITCODE -ne 0) {
        throw "Cross-module native compilation failed (exit $LASTEXITCODE). Build files are in '$buildRoot'."
    }
    $projectExecutable = Get-ChildItem -LiteralPath (Join-Path $appDirectory '.bin') -Filter 'app.exe' -File -Recurse |
        Select-Object -First 1
    if ($null -eq $projectExecutable) {
        throw "Cross-module native executable was not created. Build files are in '$buildRoot'."
    }
    & $projectExecutable.FullName
    if ($LASTEXITCODE -ne 0) {
        throw "Cross-module generated call returned exit $LASTEXITCODE; expected 0. Build files are in '$buildRoot'."
    }

    Write-Output 'PASS Main(string[] args): argument values, output, and exit codes'
    Write-Output 'PASS native cross-module call through a referenced CX library'
    $completed = $true
}
finally {
    $env:PATH = $previousPath
    $env:CMAKE_GENERATOR = $previousGenerator
    $env:CC = $previousCCompiler
    if ($completed -and (Test-Path -LiteralPath $buildRoot)) {
        Remove-Item -LiteralPath $buildRoot -Recurse -Force
    }
}

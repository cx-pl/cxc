param(
    [string] $CompilerDll = (Join-Path $PSScriptRoot '../../src/bin/Debug/net10.0/CxCompiler.dll'),
    [string] $CxCoreDir = (Join-Path $PSScriptRoot '../../../cxcore'),
    [string] $BuildRoot = (Join-Path $PSScriptRoot '../../../.build/native-interop'),
    [ValidateSet('x64','Win32')] [string] $Architecture = 'x64',
    [ValidateSet('ON','OFF')] [string] $StaticLink = 'ON',
    [switch] $GenerateOnly,
    [switch] $NegativeOnly
)
$ErrorActionPreference = 'Stop'
$CompilerDll = (Resolve-Path -LiteralPath $CompilerDll).Path
$CxCoreDir = (Resolve-Path -LiteralPath $CxCoreDir).Path.Replace('\','/')
$BuildRoot = [IO.Path]::GetFullPath($BuildRoot)
$fixtures = if ($NegativeOnly) { @() } else { @('native-abi','native-text','native-scalar') }
foreach ($fixture in $fixtures) {
    $source = Join-Path $PSScriptRoot "fixtures/$fixture"
    $destination = Join-Path $BuildRoot $fixture
    foreach ($file in Get-ChildItem -LiteralPath $source -Recurse -File) {
        if ($file.Extension -notin @('.cx','.cxproj','.c','.h') -or $file.FullName -match '[\\/](\.obj|\.bin|build)[\\/]') { continue }
        $relative = [IO.Path]::GetRelativePath($source, $file.FullName)
        $copy = Join-Path $destination $relative
        New-Item -ItemType Directory -Path (Split-Path $copy) -Force | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $copy
    }
    $project = if ($fixture -eq 'native-scalar') { Join-Path $destination 'app/native_scalar_app.cxproj' }
        else { (Get-ChildItem -LiteralPath $destination -Filter '*.cxproj' | Select-Object -First 1).FullName }
    $defines = if ($GenerateOnly) { @() } else { @('-D','WINDOWS') }
    & dotnet $CompilerDll $project @defines
    if ($LASTEXITCODE -ne 0) { throw "$fixture CX compilation failed" }
    if ($GenerateOnly) { continue }
    $projectDirectory = Split-Path $project
    $native = Join-Path $destination "build-$Architecture-$StaticLink"
    & cmake -S (Join-Path $projectDirectory '.obj') -B $native -A $Architecture "-DCXCORE_SOURCE_DIR=$CxCoreDir" "-DCX_STATIC_LINK=$StaticLink"
    if ($LASTEXITCODE -ne 0) { throw "$fixture native configuration failed" }
    & cmake --build $native --config Debug
    if ($LASTEXITCODE -ne 0) { throw "$fixture native build failed" }
    $executable = Get-ChildItem (Join-Path $projectDirectory '.bin/Debug') -Filter '*.exe' | Select-Object -First 1
    & $executable.FullName
    if ($LASTEXITCODE -ne 0) { throw "$fixture execution failed ($LASTEXITCODE)" }
}
foreach ($negative in @('signature','layout','field')) {
    $destination = Join-Path $BuildRoot "bad-$negative"
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'fixtures/native-abi/native_fixture.h') -Destination $destination
    [IO.File]::WriteAllText((Join-Path $destination 'bad.cxproj'), "name: bad_native`ntype: Executable`nnative_libraries:`n  fixture:`n    windows:`n      headers: [native_fixture.h]`n    linux:`n      headers: [native_fixture.h]`n")
    $declaration = switch ($negative) {
        'signature' { '@NativeImport("fixture", entryPoint = "fixture_sum") public extern float Wrong(float value);' }
        'layout' { '@NativeType("fixture_point", header = "native_fixture.h", size = 12) public struct Point { public int x; public int y; }' }
        'field' { '@NativeType("fixture_point", header = "native_fixture.h") public struct Point { public float x; public int y; }' }
    }
    [IO.File]::WriteAllText((Join-Path $destination 'main.cx'), "import System.Interop;`n$declaration`npublic int Main() { return 0; }`n")
    & dotnet $CompilerDll (Join-Path $destination 'bad.cxproj')
    if ($LASTEXITCODE -ne 0) { throw "Negative SDK fixture must reach C SDK validation" }
    if ($GenerateOnly) { continue }
    $checks = Join-Path $destination 'sdk-checks'
    New-Item -ItemType Directory -Path $checks -Force | Out-Null
    $source = (Join-Path $destination '.obj/bad_native.c').Replace('\','/')
    $includes = ($destination.Replace('\','/') + '" "' + $CxCoreDir + '/src" "' + $CxCoreDir + '/inc" "' + $CxCoreDir + '/platform')
    [IO.File]::WriteAllText((Join-Path $checks 'CMakeLists.txt'), "cmake_minimum_required(VERSION 3.31)`nproject(sdk_check C)`nadd_library(bad_native OBJECT `"$source`")`ntarget_include_directories(bad_native PRIVATE `"$includes`")`ntarget_compile_features(bad_native PRIVATE c_std_11)`ntarget_compile_definitions(bad_native PRIVATE CX_STATIC_LINK CX_BAD_NATIVE_BUILD)`n")
    $build = Join-Path $destination "build-$Architecture-$StaticLink"
    & cmake -S $checks -B $build -A $Architecture
    if ($LASTEXITCODE -ne 0) { throw 'Negative SDK configuration failed unexpectedly' }
    $log = Join-Path $destination 'build.log'
    & cmake --build $build --config Debug *> $log
    if ($LASTEXITCODE -eq 0 -or (Get-Content $log -Raw) -notmatch 'SDK .*mismatch|SDK signature/ABI mismatch') {
        throw "Bad SDK $negative must fail with the generated SDK diagnostic; inspect $log"
    }
    Write-Output "Rejected incompatible SDK $negative"
}
$global:LASTEXITCODE = 0

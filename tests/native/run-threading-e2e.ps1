param(
    [string] $CompilerDll = (Join-Path $PSScriptRoot '../../src/bin/Debug/net10.0/CxCompiler.dll'),
    [string] $CxCoreDir = (Join-Path $PSScriptRoot '../../../cxcore'),
    [string] $BuildRoot = (Join-Path $PSScriptRoot '../../../.build/cxcore-m16-managed-fixture'),
    [string] $Generator = 'Visual Studio 18 2026',
    [ValidateSet('x64', 'Win32')] [string] $Architecture = 'x64',
    [ValidateSet('Debug', 'Release')] [string] $Configuration = 'Debug',
    [ValidateSet('ON', 'OFF')] [string] $StaticLink = 'ON'
)
$ErrorActionPreference = 'Stop'
$CompilerDll = (Resolve-Path -LiteralPath $CompilerDll).Path
$CxCoreDir = (Resolve-Path -LiteralPath $CxCoreDir).Path.Replace('\','/')
$BuildRoot = [IO.Path]::GetFullPath($BuildRoot)
$generated = Join-Path $BuildRoot 'generated'
$native = Join-Path $BuildRoot "native-$Architecture-$StaticLink"
& dotnet $CompilerDll --module-name threadingFixture -o $generated (Join-Path $PSScriptRoot 'fixtures/threading.cx')
if ($LASTEXITCODE -ne 0) { throw 'Threading fixture compilation failed' }
$configure = @('-S', (Join-Path $PSScriptRoot 'fixtures/threading'), '-B', $native, '-G', $Generator,
    "-DCX_GENERATED_DIR=$generated", "-DCXCORE_SOURCE_DIR=$CxCoreDir",
    "-DCX_STATIC_LINK=$StaticLink", '-DBUILD_TESTING=OFF')
if ($Generator -like 'Visual Studio*') {
    # C4232 is the existing generated-vtable DLL import-address warning.
    $configure += @('-A', $Architecture, '-DCMAKE_C_FLAGS=/std:c11 /W4 /WX /wd4130 /wd4090 /wd4100 /wd4102 /wd4189 /wd4232')
} else {
    $configure += @("-DCMAKE_BUILD_TYPE=$Configuration", '-DCMAKE_C_FLAGS=-std=c11 -Wall -Wextra')
}
& cmake @configure
if ($LASTEXITCODE -ne 0) { throw 'Threading fixture configuration failed' }
& cmake --build $native --config $Configuration
if ($LASTEXITCODE -ne 0) { throw 'Threading fixture native build failed' }
& ctest --test-dir $native -C $Configuration --output-on-failure
if ($LASTEXITCODE -ne 0) { throw 'Threading fixture execution failed' }

param(
    [string]$Cxc = "$PSScriptRoot/../../src/CxCompiler/bin/Release/net10.0/CxCompiler.dll",
    [string]$CxCoreDir = "$PSScriptRoot/../../../cxcore",
    [string]$Configuration = 'Release',
    [string]$Generator = '',
    [string]$Architecture = '',
    [ValidateSet('ON','OFF')][string]$StaticLink = 'ON'
)
$ErrorActionPreference = 'Stop'
$fixture = Join-Path $PSScriptRoot 'fixtures/memory-arena-dynamic-static-init'
$output = Join-Path $fixture '.obj'
$buildArchitecture = if ($IsWindows -and $Architecture -eq 'x86') {
    'win32'
} elseif ($Architecture) {
    $Architecture.ToLowerInvariant()
} else {
    'native'
}
$buildGenerator = if ($Generator) {
    ($Generator -replace '[^A-Za-z0-9]+', '-').Trim('-').ToLowerInvariant()
} else {
    'default'
}
$build = Join-Path $fixture ".build-$buildArchitecture-$buildGenerator-$StaticLink"

& dotnet $Cxc --compile --cxcore-dir $CxCoreDir `
    (Join-Path $fixture 'memory_arena_dynamic_static_init.cxproj')
if ($LASTEXITCODE -ne 0) { throw 'Dynamic static initializer fixture compilation failed.' }

$cmakeArgs = @('-S',$fixture,'-B',$build,
    "-DCX_GENERATED_DIR=$([IO.Path]::GetFullPath($output))",
    "-DCXCORE_SOURCE_DIR=$([IO.Path]::GetFullPath($CxCoreDir))",
    "-DCX_STATIC_LINK=$StaticLink","-DCMAKE_BUILD_TYPE=$Configuration")
if ($Generator) { $cmakeArgs += @('-G',$Generator) }
if ($Architecture) {
    $cmakeArchitecture = if ($IsWindows -and $Architecture -eq 'x86') { 'Win32' } else { $Architecture }
    $cmakeArgs += @('-A',$cmakeArchitecture)
}
& cmake @cmakeArgs
if ($LASTEXITCODE -ne 0) { throw 'Dynamic static initializer CMake configuration failed.' }
& cmake --build $build --config $Configuration --target clean
if ($LASTEXITCODE -ne 0) { throw 'Dynamic static initializer fixture clean failed.' }
& cmake --build $build --config $Configuration --parallel 4
if ($LASTEXITCODE -ne 0) { throw 'Dynamic static initializer native build failed.' }

$executable = if ($IsWindows) {
    Join-Path $build "$Configuration/memoryArenaDynamicStaticInit.exe"
} else {
    Join-Path $build 'memoryArenaDynamicStaticInit'
}
& $executable
if ($LASTEXITCODE -ne 0) { throw "Dynamic static initializer fixture failed at check $LASTEXITCODE." }
Write-Output 'PASS dynamic initialization and reflected reference invocation inside WithArena survive forced GC'

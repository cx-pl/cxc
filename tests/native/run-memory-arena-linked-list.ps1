param(
    [string]$Cxc = "$PSScriptRoot/../../src/CxCompiler/bin/Release/net10.0/CxCompiler.dll",
    [string]$CxCoreDir = "$PSScriptRoot/../../../cxcore",
    [string]$Configuration = 'Release',
    [string]$Generator = '',
    [string]$Architecture = '',
    [ValidateSet('ON','OFF')][string]$StaticLink = 'ON'
)
$ErrorActionPreference = 'Stop'
$fixture = Join-Path $PSScriptRoot 'fixtures/memory-arena-linked-list'
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
& dotnet $Cxc --emit-only --cxcore-dir $CxCoreDir `
    (Join-Path $fixture 'memory_arena_linked_list.cxproj')
if ($LASTEXITCODE -ne 0) { throw 'LinkedList arena fixture code generation failed.' }
$cmakeArgs = @('-S',$output,'-B',$build,"-DCXCORE_SOURCE_DIR=$([IO.Path]::GetFullPath($CxCoreDir))",
    "-DCX_STATIC_LINK=$StaticLink","-DCMAKE_BUILD_TYPE=$Configuration")
if ($Generator) { $cmakeArgs += @('-G',$Generator) }
if ($Architecture) {
    $cmakeArchitecture = if ($IsWindows -and $Architecture -eq 'x86') { 'Win32' } else { $Architecture }
    $cmakeArgs += @('-A',$cmakeArchitecture)
}
& cmake @cmakeArgs
if ($LASTEXITCODE -ne 0) { throw 'LinkedList arena fixture CMake configuration failed.' }
& cmake --build $build --config $Configuration --target clean
if ($LASTEXITCODE -ne 0) { throw 'LinkedList arena fixture clean failed.' }
& cmake --build $build --config $Configuration --parallel 4
if ($LASTEXITCODE -ne 0) { throw 'LinkedList arena fixture native build failed.' }
$executable = if ($IsWindows) {
    Join-Path $fixture ".bin/$Configuration/memoryArenaLinkedList.exe"
} else {
    Join-Path $fixture '.bin/memoryArenaLinkedList'
}
if (!(Test-Path -LiteralPath $executable)) {
    $executable = Join-Path $fixture '.bin/memoryArenaLinkedList.exe'
}
& $executable
if ($LASTEXITCODE -ne 0) { throw "LinkedList arena fixture failed at check $LASTEXITCODE." }
$escapeFlagVariable = 'CX_M22_GENERIC_ARRAY_ESCAPE'
$previousEscapeFlag = [Environment]::GetEnvironmentVariable($escapeFlagVariable, 'Process')
try {
    foreach ($escapeMode in @('1', '2', '3')) {
        [Environment]::SetEnvironmentVariable($escapeFlagVariable, $escapeMode, 'Process')
        $escapeOutput = & $executable 2>&1
        $escapeExitCode = $LASTEXITCODE
        $escapeText = $escapeOutput -join [Environment]::NewLine
        if ($escapeExitCode -eq 0 -or
            $escapeText -notmatch [regex]::Escape('CX scoped reference cannot escape its arena')) {
            $storeKind = switch ($escapeMode) {
                '1' { 'array' }
                '2' { 'generic field' }
                default { 'reference-bearing generic struct field' }
            }
            throw "Generic $storeKind store did not reject durable publication at runtime: $escapeText"
        }
    }
}
finally {
    [Environment]::SetEnvironmentVariable($escapeFlagVariable, $previousEscapeFlag, 'Process')
}
Write-Output 'PASS collection owner routing, generic array/reference/aggregate field forced-GC retention, and runtime durable-store rejection'

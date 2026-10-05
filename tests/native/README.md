# Native conformance checks

`run-entry-point-e2e.ps1` compiles a temporary CX program, builds the generated
C with CMake and `cxcore`, then checks argument count and contents,
short-circuit evaluation, null coalescing, 64-bit integer limits, deep
inheritance, interface diamond dispatch, stdout, and successful and nonzero
process exit codes. It also builds a two-project app and runs a call into its
referenced CX library. Build products are removed on success and retained in
the reported temporary directory on failure. Cross-module binding and generated
calls also have compiler assertions in the `ProjectReferenceTests` xUnit group.

From the `cxc` directory, run it with the compiler DLL and CMake executable:

```powershell
dotnet build src/CxCompiler.csproj --no-restore
.\tests\native\run-entry-point-e2e.ps1 `
  -Cxc .\src\bin\Debug\net10.0\CxCompiler.dll `
  -CxCoreDir ..\cxcore `
  -CMake 'C:\Program Files\Microsoft Visual Studio\18\Professional\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe' `
  -Generator 'Visual Studio 18 2026'
```

The harness also accepts a CMake generator and C compiler. To run it with
Clang/Ninja on Windows, use a Visual Studio developer environment so the Windows
SDK resource compiler and C runtime libraries are available, then pass
`-Generator Ninja -CCompiler <path-to-clang.exe>`.

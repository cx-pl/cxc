# Compiler test organization

The xUnit files are grouped by language feature. Focused tests also carry an
`Area` trait when they exercise a distinct compiler phase, so those checks can
be run independently:

```powershell
dotnet test tests/CxCompiler.Tests/CxCompiler.Tests.csproj --filter "Area=Syntax"
dotnet test tests/CxCompiler.Tests/CxCompiler.Tests.csproj --filter "Area=Binding"
dotnet test tests/CxCompiler.Tests/CxCompiler.Tests.csproj --filter "Area=Lowering"
dotnet test tests/CxCompiler.Tests/CxCompiler.Tests.csproj --filter "Area=NegativeDiagnostics"
dotnet test tests/CxCompiler.Tests/CxCompiler.Tests.csproj --filter "Area=CrossModule"
```

Native runtime behavior is tested by the [native harness](native/run-entry-point-e2e.ps1).
It compiles CX programs, builds the generated C with `cxcore`, and runs both a
single-module conformance program and a cross-module executable. See the
[native test instructions](native/README.md) for compiler and CMake options.

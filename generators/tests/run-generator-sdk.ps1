$ErrorActionPreference = "Stop"
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "../../..")).Path
$project = Join-Path $PSScriptRoot "generator-sdk/GeneratorSdkFixture.cxproj"

dotnet run --no-launch-profile --project (Join-Path $repositoryRoot "cxc/src/CxCompiler/CxCompiler.csproj") `
    -- --compile --cxcore-dir (Join-Path $repositoryRoot "cxcore") --verbosity quiet $project
exit $LASTEXITCODE

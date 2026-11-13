using System.Diagnostics;

namespace CxCompiler.Tests;

[Collection("Shared generated project builds")]
public sealed class CxDataJsonGeneratorIntegrationTests
{
    [Fact]
    public async Task CXAuthoredJsonGeneratorEmitsBindsAndRunsTypedMappings()
    {
        var workspaceRoot = FindWorkspaceRoot();
        var compilerRoot = Path.Combine(workspaceRoot, "cxc");
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var compilerDirectory = Path.Combine(compilerRoot, "src", "CxCompiler", "bin", configuration, "net10.0");
        var compilerAssembly = Path.Combine(compilerDirectory, "CxCompiler.dll");
        var cxCoreDirectory = Path.Combine(workspaceRoot, "cxcore");
        var generatorDirectory = Path.Combine(workspaceRoot, "cxdata-json-generator", "src");
        var generatorProject = Path.Combine(generatorDirectory, "JsonGenerator.cxproj");
        var consumerDirectory = Path.Combine(workspaceRoot, "cxdata/tests/fixtures/json-generator/consumer");
        var consumerProject = Path.Combine(consumerDirectory, "JsonGenConsumer.cxproj");
        var generatedWorker = Path.Combine(generatorDirectory, ".bin", "Release",
            OperatingSystem.IsWindows() ? "CxDataJsonGenerator.exe" : "CxDataJsonGenerator");

        Assert.True(File.Exists(compilerAssembly));
        Assert.True(File.Exists(Path.Combine(cxCoreDirectory, "CMakeLists.txt")));
        Assert.True(File.Exists(generatorProject));
        Assert.True(File.Exists(consumerProject));

        var buildWorker = CreateCompilerStartInfo(compilerDirectory, compilerAssembly,
            "--compile", "--cxcore-dir", cxCoreDirectory, "--verbosity", "quiet",
            "-o", Path.Combine(workspaceRoot, ".build", "json-generator-integration"), generatorProject);
        var workerBuild = await RunAsync(buildWorker, TimeSpan.FromMinutes(3));
        Assert.True(workerBuild.ExitCode == 0,
            $"CXDATA JSON generator build failed. stdout:\n{workerBuild.Stdout}\nstderr:\n{workerBuild.Stderr}");

        var compileConsumer = CreateCompilerStartInfo(compilerDirectory, compilerAssembly,
            "--compile", "--cxcore-dir", cxCoreDirectory, "--verbosity", "quiet", consumerProject);
        var consumerBuild = await RunAsync(compileConsumer, TimeSpan.FromMinutes(5));
        Assert.True(consumerBuild.ExitCode == 0,
            $"CXDATA generated consumer build failed. stdout:\n{consumerBuild.Stdout}\nstderr:\n{consumerBuild.Stderr}");

        var executable = Path.Combine(consumerDirectory, ".bin", "Release",
            OperatingSystem.IsWindows() ? "JsonGenConsumer.exe" : "JsonGenConsumer");
        Assert.True(File.Exists(executable), "The generated CXDATA consumer executable was not produced.");
        var run = await RunAsync(new ProcessStartInfo(executable)
        {
            WorkingDirectory = consumerDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        }, TimeSpan.FromSeconds(30));
        Assert.True(run.ExitCode == 0,
            $"CXDATA generated consumer exited with {run.ExitCode}. stdout:\n{run.Stdout}\nstderr:\n{run.Stderr}");

        var cloneProject = Path.Combine(workspaceRoot, "cxdata", "tests", "clone-generic",
            "CloneGenericFixture.cxproj");
        var compileCloneFixture = CreateCompilerStartInfo(compilerDirectory, compilerAssembly,
            "--compile", "--cxcore-dir", cxCoreDirectory, "--verbosity", "quiet", cloneProject);
        var cloneBuild = await RunAsync(compileCloneFixture, TimeSpan.FromMinutes(3));
        Assert.True(cloneBuild.ExitCode == 0,
            $"CXDATA clone fixture build failed. stdout:\n{cloneBuild.Stdout}\nstderr:\n{cloneBuild.Stderr}");

        var cloneDirectory = Path.GetDirectoryName(cloneProject)!;
        var cloneExecutable = Path.Combine(cloneDirectory, ".bin", "Release",
            OperatingSystem.IsWindows() ? "CloneGenericFixture.exe" : "CloneGenericFixture");
        Assert.True(File.Exists(cloneExecutable),
            "The generated CXDATA clone fixture executable was not produced.");
        var cloneRun = await RunAsync(new ProcessStartInfo(cloneExecutable)
        {
            WorkingDirectory = cloneDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        }, TimeSpan.FromSeconds(30));
        Assert.True(cloneRun.ExitCode == 0,
            $"CXDATA clone fixture exited with {cloneRun.ExitCode}. " +
            $"stdout:\n{cloneRun.Stdout}\nstderr:\n{cloneRun.Stderr}");

        var cloneCrossProject = Path.Combine(workspaceRoot, "cxdata", "tests",
            "clone-cross-project", "CloneCrossProjectFixture.cxproj");
        var cloneCrossProjectBuild = await RunAsync(CreateCompilerStartInfo(compilerDirectory,
            compilerAssembly, "--compile", "--cxcore-dir", cxCoreDirectory,
            "--verbosity", "quiet", cloneCrossProject), TimeSpan.FromMinutes(3));
        Assert.True(cloneCrossProjectBuild.ExitCode == 0,
            $"CXDATA cross-project clone fixture build failed. " +
            $"stdout:\n{cloneCrossProjectBuild.Stdout}\nstderr:\n{cloneCrossProjectBuild.Stderr}");

        var cloneCrossProjectDirectory = Path.GetDirectoryName(cloneCrossProject)!;
        var cloneCrossProjectExecutable = Path.Combine(cloneCrossProjectDirectory, ".bin", "Release",
            OperatingSystem.IsWindows() ? "CloneCrossProjectFixture.exe" : "CloneCrossProjectFixture");
        Assert.True(File.Exists(cloneCrossProjectExecutable),
            "The generated cross-project clone fixture executable was not produced.");
        var cloneCrossProjectRun = await RunAsync(new ProcessStartInfo(cloneCrossProjectExecutable)
        {
            WorkingDirectory = cloneCrossProjectDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        }, TimeSpan.FromSeconds(30));
        Assert.True(cloneCrossProjectRun.ExitCode == 0,
            $"CXDATA cross-project clone fixture exited with {cloneCrossProjectRun.ExitCode}. " +
            $"stdout:\n{cloneCrossProjectRun.Stdout}\nstderr:\n{cloneCrossProjectRun.Stderr}");

        var cloneDiagnosticsProject = Path.Combine(workspaceRoot, "cxdata", "tests",
            "clone-diagnostics", "CloneDiagnosticsFixture.cxproj");
        var cloneDiagnosticsBuild = await RunAsync(CreateCompilerStartInfo(compilerDirectory,
            compilerAssembly, "--compile", "--cxcore-dir", cxCoreDirectory,
            "--verbosity", "quiet", cloneDiagnosticsProject), TimeSpan.FromMinutes(3));
        var cloneDiagnostics = cloneDiagnosticsBuild.Stdout + cloneDiagnosticsBuild.Stderr;
        Assert.True(cloneDiagnosticsBuild.ExitCode != 0,
            $"The invalid clone fixture unexpectedly compiled. stdout:\n{cloneDiagnosticsBuild.Stdout}\n" +
            $"stderr:\n{cloneDiagnosticsBuild.Stderr}");
        Assert.Contains("CXDATA_CLONE003", cloneDiagnostics);
        Assert.Contains("CXDATA_CLONE002", cloneDiagnostics);
        Assert.Contains("CXDATA_CLONE001", cloneDiagnostics);
        Assert.Contains("CXDATA_CLONE005", cloneDiagnostics);
        Assert.Contains("CXDATA_CLONE006", cloneDiagnostics);
        Assert.Contains("CXDATA_CLONE008", cloneDiagnostics);
        Assert.Contains("CXDATA_CLONE010", cloneDiagnostics);

        var cloneIdentityEscapeProject = Path.Combine(workspaceRoot, "cxdata", "tests",
            "clone-identity-escape", "CloneIdentityEscapeFixture.cxproj");
        var cloneIdentityEscapeBuild = await RunAsync(CreateCompilerStartInfo(compilerDirectory,
            compilerAssembly, "--compile", "--cxcore-dir", cxCoreDirectory,
            "--verbosity", "quiet", cloneIdentityEscapeProject), TimeSpan.FromMinutes(3));
        var cloneIdentityEscape = cloneIdentityEscapeBuild.Stdout + cloneIdentityEscapeBuild.Stderr;
        Assert.True(cloneIdentityEscapeBuild.ExitCode != 0,
            $"The identity clone escape fixture unexpectedly compiled. stdout:\n{cloneIdentityEscapeBuild.Stdout}\n" +
            $"stderr:\n{cloneIdentityEscapeBuild.Stderr}");
        Assert.Contains("possibly arena-scoped reference cannot be stored", cloneIdentityEscape);

        var cloneExtensionConflictProject = Path.Combine(workspaceRoot, "cxdata", "tests",
            "clone-extension-conflict", "CloneExtensionConflictFixture.cxproj");
        var cloneExtensionConflictBuild = await RunAsync(CreateCompilerStartInfo(compilerDirectory,
            compilerAssembly, "--compile", "--cxcore-dir", cxCoreDirectory,
            "--verbosity", "quiet", cloneExtensionConflictProject), TimeSpan.FromMinutes(3));
        var cloneExtensionConflict = cloneExtensionConflictBuild.Stdout + cloneExtensionConflictBuild.Stderr;
        Assert.True(cloneExtensionConflictBuild.ExitCode != 0,
            "The conflicting Clone extension fixture unexpectedly compiled.");
        Assert.Contains("Function call 'Clone' with 0 argument(s) is ambiguous", cloneExtensionConflict);
        Assert.Contains("ExistingCloneExtension.Clone()", cloneExtensionConflict);
        Assert.Contains("CloneExtensionConflict_Models_TargetCloningExtensionsCloningExtensions.Clone()",
            cloneExtensionConflict);
    }

    private static ProcessStartInfo CreateCompilerStartInfo(string compilerDirectory,
        string compilerAssembly, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = compilerDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add("--runtimeconfig");
        startInfo.ArgumentList.Add(Path.Combine(compilerDirectory, "CxCompiler.runtimeconfig.json"));
        startInfo.ArgumentList.Add("--depsfile");
        startInfo.ArgumentList.Add(Path.Combine(compilerDirectory, "CxCompiler.deps.json"));
        startInfo.ArgumentList.Add(compilerAssembly);
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        return startInfo;
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(
        ProcessStartInfo startInfo, TimeSpan timeout)
    {
        using var process = Process.Start(startInfo)!;
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(timeout);
        return (process.ExitCode, await stdoutTask, await stderrTask);
    }

    private static string FindWorkspaceRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CX_ROADMAP.md")) &&
                Directory.Exists(Path.Combine(directory.FullName, "cxdata-json-generator")))
                return directory.FullName;
        }
        throw new DirectoryNotFoundException("Could not find the CX workspace root.");
    }
}

using System.Diagnostics;
using CxCompiler.Generators;

namespace CxCompiler.Tests;

public sealed class GeneratorWorkerClientTests
{
    [Fact]
    public async Task RunsWorkerAndReturnsSourceDiagnosticsAndStderr()
    {
        var result = await RunWorkerAsync("success", TimeSpan.FromSeconds(10));

        Assert.Equal(17UL, result.RequestId);
        Assert.Contains("fixture log", result.StandardError);
        Assert.Collection(result.Messages,
            frame => Assert.Equal(GeneratorMessageKind.Handshake, frame.Kind),
            frame =>
            {
                Assert.Equal(GeneratorMessageKind.SourceOutput, frame.Kind);
                Assert.Equal(new GeneratorSourceOutput("Generated.g.cx",
                    "public final class Generated { public static int GetValue() { return 42; } }"),
                    GeneratorWorkerMessages.DecodeSourceOutput(frame.Payload));
            },
            frame =>
            {
                Assert.Equal(GeneratorMessageKind.Diagnostic, frame.Kind);
                Assert.Equal("CXG001", GeneratorWorkerMessages.DecodeDiagnostic(frame.Payload).Id);
            });
    }

    [Fact]
    public async Task ReportsWorkerFailureAndPreservesStandardError()
    {
        var exception = await Assert.ThrowsAsync<GeneratorWorkerException>(
            () => RunWorkerAsync("failure", TimeSpan.FromSeconds(10)));

        Assert.Contains("fixture failed", exception.Message);
        Assert.Contains("fixture failure detail", exception.StandardError);
    }

    [Fact]
    public async Task KillsTimedOutWorker()
    {
        var exception = await Assert.ThrowsAsync<GeneratorWorkerException>(
            () => RunWorkerAsync("hang", TimeSpan.FromMilliseconds(250)));

        Assert.Contains("time limit", exception.Message);
    }

    [Fact]
    public async Task ReportsMalformedProtocolAndWorkerCrashes()
    {
        var protocolError = await Assert.ThrowsAsync<GeneratorWorkerException>(
            () => RunWorkerAsync("invalid", TimeSpan.FromSeconds(10)));
        Assert.Contains("protocol failed", protocolError.Message);

        var crash = await Assert.ThrowsAsync<GeneratorWorkerException>(
            () => RunWorkerAsync("crash", TimeSpan.FromSeconds(10)));
        Assert.Contains("code 23", crash.Message);
        Assert.Contains("fixture crashed", crash.StandardError);
    }

    private static Task<GeneratorWorkerResult> RunWorkerAsync(string mode, TimeSpan timeout)
    {
        var repositoryRoot = FindRepositoryRoot();
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var workerDirectory = Path.Combine(repositoryRoot, "tests", "GeneratorWorkerFixture", "bin",
            configuration, "net10.0");
        var workerAssembly = Path.Combine(workerDirectory, "GeneratorWorkerFixture.dll");
        var runtimeConfig = Path.Combine(workerDirectory, "GeneratorWorkerFixture.runtimeconfig.json");
        var dependencyFile = Path.Combine(workerDirectory, "GeneratorWorkerFixture.deps.json");
        Assert.True(File.Exists(workerAssembly), $"Generator fixture was not built: {workerAssembly}");

        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = repositoryRoot,
        };
        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add("--runtimeconfig");
        startInfo.ArgumentList.Add(runtimeConfig);
        startInfo.ArgumentList.Add("--depsfile");
        startInfo.ArgumentList.Add(dependencyFile);
        startInfo.ArgumentList.Add(workerAssembly);
        startInfo.ArgumentList.Add(mode);

        var request = new GeneratorProtocolFrame(GeneratorProtocol.CurrentVersion,
            GeneratorMessageKind.Generate, 17, [4, 2]);
        return GeneratorWorkerClient.RunAsync(startInfo, [request], timeout);
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "src", "CxCompiler", "CxCompiler.csproj")) &&
                Directory.Exists(Path.Combine(directory.FullName, "tests", "CxCompiler.Tests")))
                return directory.FullName;
        }
        throw new DirectoryNotFoundException("Unable to locate the cxc repository root from the test output directory.");
    }
}

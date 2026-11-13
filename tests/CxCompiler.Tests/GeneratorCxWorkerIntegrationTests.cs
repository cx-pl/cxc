using System.Diagnostics;
using CxCompiler.Generators;

namespace CxCompiler.Tests;

[Collection("Shared generated project builds")]
public sealed class GeneratorCxWorkerIntegrationTests
{
    [Fact]
    public async Task CXAuthoredWorkerReadsSnapshotAndAdditionalFileAndReturnsGeneratedSource()
    {
        var repositoryRoot = FindRepositoryRoot();
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var compilerDirectory = Path.Combine(repositoryRoot, "src", "CxCompiler", "bin", configuration, "net10.0");
        var compilerAssembly = Path.Combine(compilerDirectory, "CxCompiler.dll");
        var cxCoreDirectory = Path.GetFullPath(Path.Combine(repositoryRoot, "..", "cxcore"));
        var workerProject = Path.Combine(repositoryRoot, "generators", "tests", "worker-fixture",
            "WorkerFixture.cxproj");
        var outputDirectory = Path.Combine(repositoryRoot, "generators", "tests",
            "worker-fixture", ".obj");
        Directory.CreateDirectory(outputDirectory);
            Assert.True(File.Exists(compilerAssembly));
            Assert.True(File.Exists(Path.Combine(cxCoreDirectory, "CMakeLists.txt")));
            var build = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = repositoryRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            build.ArgumentList.Add("exec");
            build.ArgumentList.Add("--runtimeconfig");
            build.ArgumentList.Add(Path.Combine(compilerDirectory, "CxCompiler.runtimeconfig.json"));
            build.ArgumentList.Add("--depsfile");
            build.ArgumentList.Add(Path.Combine(compilerDirectory, "CxCompiler.deps.json"));
            build.ArgumentList.Add(compilerAssembly);
            build.ArgumentList.Add("--compile");
            build.ArgumentList.Add("--cxcore-dir");
            build.ArgumentList.Add(cxCoreDirectory);
            build.ArgumentList.Add("--verbosity");
            build.ArgumentList.Add("quiet");
            build.ArgumentList.Add("-o");
            build.ArgumentList.Add(outputDirectory);
            build.ArgumentList.Add(workerProject);

            using (var process = Process.Start(build)!)
            {
                var stdoutTask = process.StandardOutput.ReadToEndAsync();
                var stderrTask = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(3));
                var stdout = await stdoutTask;
                var stderr = await stderrTask;
                Assert.True(process.ExitCode == 0,
                    $"CX worker build failed. stdout:\n{stdout}\nstderr:\n{stderr}");
            }

            var executableName = OperatingSystem.IsWindows()
                ? "CxGeneratorWorkerFixture.exe"
                : "CxGeneratorWorkerFixture";
            var workerBinDirectory = Path.Combine(repositoryRoot, "generators", "tests",
                "worker-fixture", ".bin");
            var executable = Directory.GetFiles(workerBinDirectory, executableName, SearchOption.AllDirectories)
                .FirstOrDefault();
            Assert.NotNull(executable);

            var startInfo = new ProcessStartInfo(executable!)
            {
                WorkingDirectory = Path.GetDirectoryName(executable)!,
            };
            var snapshot = new GeneratorInputSnapshot("WorkerTarget", [], [], [], []);
            var requestId = 73UL;
            var requests = new[]
            {
                new GeneratorProtocolFrame(GeneratorProtocol.CurrentVersion,
                    GeneratorMessageKind.Handshake, requestId, []),
                new GeneratorProtocolFrame(GeneratorProtocol.CurrentVersion,
                    GeneratorMessageKind.Snapshot, requestId, GeneratorSnapshotCodec.Encode(snapshot)),
                new GeneratorProtocolFrame(GeneratorProtocol.CurrentVersion,
                    GeneratorMessageKind.AdditionalFile, requestId,
                    GeneratorWorkerMessages.EncodeAdditionalFile(new GeneratorAdditionalFile(
                        "worker-input.txt", "test-hash", "UTF-8 payload"))),
                new GeneratorProtocolFrame(GeneratorProtocol.CurrentVersion,
                    GeneratorMessageKind.Generate, requestId, []),
            };

            var result = await GeneratorWorkerClient.RunAsync(startInfo, requests,
                TimeSpan.FromSeconds(30));

            Assert.Equal(requestId, result.RequestId);
            Assert.Collection(result.Messages,
                frame => Assert.Equal(GeneratorMessageKind.Handshake, frame.Kind),
                frame =>
                {
                    Assert.Equal(GeneratorMessageKind.SourceOutput, frame.Kind);
                    Assert.Equal(new GeneratorSourceOutput("WorkerGenerated.g.cx",
                        "public final class WorkerGenerated { public static int GetValue() { return 42; } }"),
                        GeneratorWorkerMessages.DecodeSourceOutput(frame.Payload));
                });

            var consumerDirectory = Path.Combine(outputDirectory, "consumer");
            Directory.CreateDirectory(consumerDirectory);
            File.WriteAllText(Path.Combine(consumerDirectory, "worker-input.txt"), "UTF-8 payload");
            File.WriteAllText(Path.Combine(consumerDirectory, "Program.cx"), """
                public int Main() { return WorkerGenerated.GetValue() == 42 ? 0 : 1; }
                """);
            var consumerProject = Path.Combine(consumerDirectory, "WorkerConsumer.cxproj");
            File.WriteAllText(consumerProject, $$"""
                name: WorkerConsumer
                type: Executable
                generators:
                - id: cxworker
                  executable: {{Yaml(executable!)}}
                  additional_files:
                  - worker-input.txt
                  timeout_seconds: 30
                """);

            var compile = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = repositoryRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            compile.ArgumentList.Add("exec");
            compile.ArgumentList.Add("--runtimeconfig");
            compile.ArgumentList.Add(Path.Combine(compilerDirectory, "CxCompiler.runtimeconfig.json"));
            compile.ArgumentList.Add("--depsfile");
            compile.ArgumentList.Add(Path.Combine(compilerDirectory, "CxCompiler.deps.json"));
            compile.ArgumentList.Add(compilerAssembly);
            compile.ArgumentList.Add("--emit-only");
            compile.ArgumentList.Add("--verbosity");
            compile.ArgumentList.Add("quiet");
            compile.ArgumentList.Add(consumerProject);
            using (var process = Process.Start(compile)!)
            {
                var stdoutTask = process.StandardOutput.ReadToEndAsync();
                var stderrTask = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
                var stdout = await stdoutTask;
                var stderr = await stderrTask;
                Assert.True(process.ExitCode == 0,
                    $"Compiler failed to bind CX worker output. stdout:\n{stdout}\nstderr:\n{stderr}");
            }
            var generatedPath = Path.Combine(consumerDirectory, ".obj", "generators", "cxworker",
                "WorkerGenerated.g.cx");
            Assert.Contains("GetValue", File.ReadAllText(generatedPath));
            Assert.Contains("WorkerGenerated", File.ReadAllText(
                Path.Combine(consumerDirectory, ".obj", "WorkerConsumer.c")));
    }

    private static string Yaml(string value) => "'" + value.Replace("'", "''") + "'";

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "src", "CxCompiler", "CxCompiler.csproj")) &&
                Directory.Exists(Path.Combine(directory.FullName, "generators")))
                return directory.FullName;
        }
        throw new DirectoryNotFoundException("Could not find the cxc repository root.");
    }
}

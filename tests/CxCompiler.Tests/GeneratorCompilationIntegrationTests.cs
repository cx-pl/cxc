using System.Diagnostics;

namespace CxCompiler.Tests;

public sealed class GeneratorCompilationIntegrationTests
{
    [Fact]
    public async Task GeneratedLibrarySourceIsBoundAndConsumedAcrossProjectReferences()
    {
        var repositoryRoot = FindRepositoryRoot();
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var compilerDirectory = Path.Combine(repositoryRoot, "src", "CxCompiler", "bin", configuration, "net10.0");
        var compilerAssembly = Path.Combine(compilerDirectory, "CxCompiler.dll");
        var workerDirectory = Path.Combine(repositoryRoot, "tests", "GeneratorWorkerFixture", "bin",
            configuration, "net10.0");
        var workerAssembly = Path.Combine(workerDirectory, "GeneratorWorkerFixture.dll");
        Assert.True(File.Exists(compilerAssembly));
        Assert.True(File.Exists(workerAssembly));

        var projectDirectory = Path.Combine(Path.GetTempPath(), "cx-generator-integration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(projectDirectory);
        try
        {
            var libraryDirectory = Path.Combine(projectDirectory, "library");
            var applicationDirectory = Path.Combine(projectDirectory, "application");
            Directory.CreateDirectory(libraryDirectory);
            Directory.CreateDirectory(applicationDirectory);
            var projectPath = Path.Combine(applicationDirectory, "Application.cxproj");
            var generatedOutput = Path.Combine(libraryDirectory, ".obj", "generators", "sample", "Generated.g.cx");
            var consumerOutput = Path.Combine(applicationDirectory, ".obj", "generators", "consumer", "ConsumerWitness.g.cx");
            var libraryC = Path.Combine(libraryDirectory, ".obj", "GeneratedLibrary.c");
            var applicationC = Path.Combine(applicationDirectory, ".obj", "GeneratedApplication.c");
            var runtimeConfig = Path.Combine(workerDirectory, "GeneratorWorkerFixture.runtimeconfig.json");
            var dependencyFile = Path.Combine(workerDirectory, "GeneratorWorkerFixture.deps.json");
            File.WriteAllText(Path.Combine(libraryDirectory, "value.txt"), "73\n");
            File.WriteAllText(Path.Combine(libraryDirectory, "Target.cx"), """
                public final class Target {
                    public string Name;
                    public constructor(string name) { Name = name; }
                }
                """);
            File.WriteAllText(Path.Combine(applicationDirectory, "Program.cx"), """
                import GeneratedLibrary;

                public int Main() { return Generated.GetValue() == 73 ? 0 : 1; }
                """);
            File.WriteAllText(Path.Combine(libraryDirectory, "GeneratedLibrary.cxproj"), $$"""
                name: GeneratedLibrary
                type: Library
                generators:
                - id: sample
                  executable: dotnet
                  arguments:
                  - exec
                  - --runtimeconfig
                  - {{Yaml(runtimeConfig)}}
                  - --depsfile
                  - {{Yaml(dependencyFile)}}
                  - {{Yaml(workerAssembly)}}
                  - integration
                  additional_files:
                  - value.txt
                  timeout_seconds: 30
                """);
            File.WriteAllText(projectPath, $$"""
                name: GeneratedApplication
                type: Executable
                project_references:
                - ../library/GeneratedLibrary.cxproj
                generators:
                - id: consumer
                  executable: dotnet
                  arguments:
                  - exec
                  - --runtimeconfig
                  - {{Yaml(runtimeConfig)}}
                  - --depsfile
                  - {{Yaml(dependencyFile)}}
                  - {{Yaml(workerAssembly)}}
                  - consumer
                  timeout_seconds: 30
                """);

            var startInfo = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = repositoryRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            startInfo.ArgumentList.Add("exec");
            startInfo.ArgumentList.Add("--runtimeconfig");
            startInfo.ArgumentList.Add(Path.Combine(compilerDirectory, "CxCompiler.runtimeconfig.json"));
            startInfo.ArgumentList.Add("--depsfile");
            startInfo.ArgumentList.Add(Path.Combine(compilerDirectory, "CxCompiler.deps.json"));
            startInfo.ArgumentList.Add(compilerAssembly);
            startInfo.ArgumentList.Add("--emit-only");
            startInfo.ArgumentList.Add(projectPath);

            using var process = Process.Start(startInfo)!;
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            Assert.True(process.ExitCode == 0,
                $"Compiler failed. stdout:\n{stdout}\nstderr:\n{stderr}\nproject:\n{File.ReadAllText(projectPath)}");
            Assert.Contains("GetValue", File.ReadAllText(generatedOutput));
            Assert.Contains("Generated", File.ReadAllText(libraryC));
            Assert.Contains("Generated", File.ReadAllText(applicationC));
            Assert.Contains("ConsumerWitness", File.ReadAllText(consumerOutput));
        }
        finally
        {
            if (Directory.Exists(projectDirectory)) Directory.Delete(projectDirectory, recursive: true);
        }
    }

    private static string Yaml(string value) => "'" + value.Replace("'", "''") + "'";

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

using System.Security.Cryptography;
using System.Text.Json;
using CxCompiler.Model.Errors;

namespace CxCompiler.Tests;

public sealed class RestoredNativePackageTests
{
    [Fact]
    public void RestoredNativeMetadataAndAssetsFlowToConsumerAndStaleLocksAreRejected()
    {
        var root = Directory.CreateTempSubdirectory("cxc-native-package-").FullName;
        try
        {
            var package = Path.Combine(root, ".packages", "binding", "1.0.0");
            Directory.CreateDirectory(Path.Combine(package, "source"));
            File.WriteAllText(Path.Combine(package, "package.cxpm"), """
                name: binding
                version: 1.0.0
                type: Library
                native_libraries:
                  fixture:
                    provider: package
                    linux:
                      architectures: [x64]
                      headers: [fixture.h]
                      include_directories: [sdk/include]
                      link_libraries: ['binary/linux-{arch}/libfixture.so']
                      runtime_artifacts: ['binary/linux-{arch}/libfixture.so']
                """);
            File.WriteAllText(Path.Combine(package, "source", "api.cx"), """
                import System.Interop;
                namespace Binding;
                @NativeImport("fixture", entryPoint = "native_symbol") public extern int Native();
                """);
            var project = Path.Combine(root, "app.cxproj");
            File.WriteAllText(project, "name: app\nversion: 1.0.0\ntype: Executable\ndependencies: ['binding@1.0.0']\n");
            File.WriteAllText(Path.Combine(root, "main.cx"), "import Binding; public unsafe int Main() { return Native(); }");
            var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(project))).ToLowerInvariant();
            File.WriteAllText(Path.Combine(root, "cxpm.lock"), JsonSerializer.Serialize(new {
                LockVersion = 1, ProjectHash = hash, Packages = new Dictionary<string, object> {
                    ["binding"] = new { Id = "binding", Version = "1.0.0" }
                }
            }));
            new Compiler().Compile([project]);
            Assert.Contains("native_symbol()", File.ReadAllText(Path.Combine(root, ".obj", "app.c")));
            var cmake = File.ReadAllText(Path.Combine(package, ".obj", "CMakeLists.txt"));
            Assert.Contains("../source/sdk/include", cmake);
            Assert.Contains("../bin/linux-${_cx_native_arch}/libfixture.so", cmake);
            Assert.Contains("INTERFACE_CX_NATIVE_RUNTIME_FILES", cmake);
            Assert.Contains("BUILD_RPATH \"$ORIGIN\"", File.ReadAllText(Path.Combine(root, ".obj", "CMakeLists.txt")));
            File.AppendAllText(project, "description: changed\n");
            Assert.Contains("does not match", Assert.Throws<CompilationErrorException>(() => new Compiler().Compile([project])).Message);
        }
        finally { Directory.Delete(root, true); }
    }
}

using System.Security.Cryptography;
using System.Text.Json;

namespace CxCompiler.Tests;

public sealed class SourceLayoutTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeSupportIsDiscoveredWithoutCompilingSiblingExamples(bool restored)
    {
        var root = Directory.CreateTempSubdirectory("cxc-source-layout-").FullName;
        try
        {
            var packageRoot = restored ? Path.Combine(root, ".packages", "layout", "1.0.0") : root;
            var nativeRoot = restored ? Path.Combine(packageRoot, "source") : root;
            var source = Path.Combine(nativeRoot, "src");
            Directory.CreateDirectory(source);
            Directory.CreateDirectory(Path.Combine(nativeRoot, "native"));
            Directory.CreateDirectory(Path.Combine(nativeRoot, "include"));
            Directory.CreateDirectory(Path.Combine(nativeRoot, "platform", "win"));
            Directory.CreateDirectory(Path.Combine(nativeRoot, "examples"));
            var manifest = Path.Combine(restored ? packageRoot : source, restored ? "package.cxpm" : "layout.cxproj");
            File.WriteAllText(manifest, "name: layout\nversion: 1.0.0\ntype: Library\n");
            File.WriteAllText(Path.Combine(source, "api.cx"), "public class LayoutValue {}");
            File.WriteAllText(Path.Combine(nativeRoot, "native", "adapter.c"), "int layout_adapter(void) { return 1; }");
            File.WriteAllText(Path.Combine(nativeRoot, "include", "adapter.h"), "#pragma once\n");
            File.WriteAllText(Path.Combine(nativeRoot, "platform", "win", "backend.c"), "int layout_win(void) { return 1; }");
            File.WriteAllText(Path.Combine(nativeRoot, "examples", "unrelated.c"), "#error must not compile");
            File.WriteAllText(Path.Combine(nativeRoot, "examples", "unrelated.cx"), "invalid CX source");
            if (restored)
            {
                File.AppendAllText(manifest, """
                    package:
                      root: ..
                    native_libraries:
                      layout:
                        provider: package
                        windows:
                          include_directories: ['../include']
                        linux:
                          include_directories: ['../include']

                    """);
            }
            var input = manifest;
            if (restored)
            {
                input = Path.Combine(root, "consumer.cxproj");
                File.WriteAllText(input, "name: consumer\nversion: 1.0.0\ntype: Library\ndependencies: ['layout@1.0.0']\n");
                var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(input))).ToLowerInvariant();
                File.WriteAllText(Path.Combine(root, "cxpm.lock"), JsonSerializer.Serialize(new {
                    LockVersion = 1, ProjectHash = hash, Packages = new Dictionary<string, object> {
                        ["layout"] = new { Id = "layout", Version = "1.0.0" }
                    }
                }));
            }
            new Compiler().Compile([input]);
            var cmake = File.ReadAllText(Path.Combine(Path.GetDirectoryName(manifest)!, ".obj", "CMakeLists.txt"));
            Assert.Contains("native/adapter.c", cmake);
            Assert.Contains("platform/win/backend.c", cmake);
            Assert.Contains("if(WIN32)", cmake);
            Assert.Contains("/include", cmake);
            Assert.DoesNotContain("unrelated", cmake);
            if (restored)
            {
                Assert.Contains("../source/include", cmake);
                Assert.DoesNotContain("source/../include", cmake);
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}

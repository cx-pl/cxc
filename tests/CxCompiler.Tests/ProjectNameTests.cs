using CxCompiler.Model.Errors;

namespace CxCompiler.Tests;

public sealed class ProjectNameTests
{
    [Theory]
    [InlineData("bad_name")]
    [InlineData("_Leading")]
    [InlineData("Trailing_")]
    [InlineData("Two__Parts")]
    public void RejectsUnderscoresInProjectManifests(string name)
    {
        WithDirectory(directory =>
        {
            var manifest = Path.Combine(directory, "app.cxproj");
            File.WriteAllText(manifest, $"name: {name}\ntype: Executable\n");
            File.WriteAllText(Path.Combine(directory, "main.cx"), "public int Main() { return 0; }");
            var error = Assert.Throws<CompilationErrorException>(() => new Compiler().Compile([manifest]));
            Assert.Contains(name, error.Message);
            Assert.Contains("underscores", error.Message);
            Assert.False(Directory.Exists(Path.Combine(directory, ".obj")));
        });
    }

    [Theory]
    [InlineData("--module-name", "bad_name")]
    [InlineData("--module-name=bad_name", null)]
    public void RejectsUnderscoresInModuleOverrides(string option, string? value)
    {
        string[] arguments = value is null ? [option, "missing.cx"] : [option, value, "missing.cx"];
        var error = Assert.Throws<CommandLineException>(() => new Compiler().Compile(arguments));
        Assert.Contains("underscores", error.Message);
    }

    [Fact]
    public void RejectsUnderscoresInReferencedProjectsBeforeEmittingAnything()
    {
        WithDirectory(directory =>
        {
            var library = Directory.CreateDirectory(Path.Combine(directory, "library")).FullName;
            var app = Directory.CreateDirectory(Path.Combine(directory, "app")).FullName;
            File.WriteAllText(Path.Combine(library, "library.cxproj"), "name: bad_library\ntype: Library\n");
            var manifest = Path.Combine(app, "app.cxproj");
            File.WriteAllText(manifest, "name: app\ntype: Executable\nproject_references: [../library/library.cxproj]\n");
            var error = Assert.Throws<CompilationErrorException>(() => new Compiler().Compile([manifest]));
            Assert.Contains("bad_library", error.Message);
            Assert.False(Directory.Exists(Path.Combine(library, ".obj")));
            Assert.False(Directory.Exists(Path.Combine(app, ".obj")));
        });
    }

    [Fact]
    public void AnOverrideDoesNotHideAnInvalidManifestName()
    {
        WithDirectory(directory =>
        {
            var manifest = Path.Combine(directory, "app.cxproj");
            File.WriteAllText(manifest, "name: bad_name\ntype: Executable\n");
            var error = Assert.Throws<CompilationErrorException>(() =>
                new Compiler().Compile(["--module-name", "GoodName", manifest]));
            Assert.Contains("bad_name", error.Message);
        });
    }

    [Fact]
    public void ASourceFilenameWithUnderscoresRequiresAValidOverride()
    {
        WithDirectory(directory =>
        {
            var source = Path.Combine(directory, "source_file.cx");
            var output = Path.Combine(directory, "generated");
            File.WriteAllText(source, "public int Main() { int _value = 0; return _value; }");
            var error = Assert.Throws<CompilationErrorException>(() =>
                new Compiler().Compile(["-o", output, source]));
            Assert.Contains("source_file", error.Message);
            Assert.False(Directory.Exists(output));

            new Compiler().Compile(["--module-name", "SourceFile2", "-o", output, source]);
            Assert.True(File.Exists(Path.Combine(output, "SourceFile2.c")));
            Assert.Contains("SourceFile2_Main", File.ReadAllText(Path.Combine(output, "SourceFile2.c")));
        });
    }

    private static void WithDirectory(Action<string> test)
    {
        var directory = Directory.CreateTempSubdirectory("cxc-project-name-").FullName;
        try { test(directory); }
        finally { Directory.Delete(directory, recursive: true); }
    }
}

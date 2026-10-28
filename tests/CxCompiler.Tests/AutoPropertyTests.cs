using CxCompiler.Model.Project;
using CxCompiler.Model.Types;
using CxCompiler.OutputGenerators;
using CxCompiler.Semantics;

namespace CxCompiler.Tests;

public sealed class AutoPropertyTests
{
    [Fact]
    public void InstanceAutoPropertiesGenerateBackingAccessorsWithoutReflectingStorage()
    {
        var project = CxProject.CreateDefaultApplicationProject("auto_property_test");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public class Probe {
                public int Value { const get; set; }
            }
            public int Main() {
                Probe probe = new Probe();
                probe.Value = 17;
                return probe.Value;
            }
            """));

        new SemanticBinder().Bind(project);

        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-auto-property-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(Path.Combine(directory, "auto_property_test.c"));
            Assert.Contains("__cx_auto_property_Value", source);
            Assert.Contains("auto_property_test_Probe_Value___const_get", source);
            Assert.Contains("auto_property_test_Probe_Value___set", source);
            Assert.DoesNotContain("\"__cx_auto_property_Value\"", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}

using CxCompiler.Model.Project;
using CxCompiler.OutputGenerators;
using CxCompiler.Semantics;

namespace CxCompiler.Tests;

public sealed class GenericReferenceMetadataTests
{
    [Fact]
    public void EmitsConstructedInterfaceFieldInClosedGenericReferenceLayout()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_interface_field");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public interface IComparer<T> {
                bool Equals(T left, T right);
            }
            public class Holder<T> where T : class {
                private IComparer<T> _comparer;
                private uint[] _buckets;
                private int _mask;
                public constructor(IComparer<T> comparer = null) {
                    _comparer = comparer;
                    _buckets = new uint[8u];
                    _mask = 7;
                }
                public IComparer<T> Comparer { const get { return _comparer; } }
                public uint Capacity { const get { return _buckets.Length; } }
            }
            public int Run() {
                Holder<string> holder = new Holder<string>();
                return holder.Comparer == null ? 0 : 1;
            }
            """));
        new SemanticBinder().Bind(project);

        var outputDirectory = Path.Combine(
            Path.GetTempPath(), $"cxc-generic-interface-field-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(outputDirectory);
            CCodeOutputGenerator.GenerateOutput(
                project, Path.Combine(outputDirectory, "input.cx"));

            var header = File.ReadAllText(
                Path.Combine(outputDirectory, "generic_interface_field.h"));
            Assert.Contains("cx_iface_ref", header);
            Assert.Contains("_comparer", header);
            Assert.Contains(
                "CX_ID_4(generic_interface_field, Holder, Comparer, __const_get)", header);
        }
        finally
        {
            if (Directory.Exists(outputDirectory))
            {
                Directory.Delete(outputDirectory, recursive: true);
            }
        }
    }
}

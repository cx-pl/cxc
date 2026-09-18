using CxCompiler.Model.Project;
using CxCompiler.Model.Types;
using CxCompiler.OutputGenerators;
using CxCompiler.Semantics;

namespace CxCompiler.Tests;

public sealed class PropertyAccessorTests
{
    [Fact]
    public void PreservesConstTypesAndConstMethodReceiversInCSignatures()
    {
        var project = CxProject.CreateDefaultApplicationProject();
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public struct Character {
                public extern char Lower() const;
            }

            public static class Output {
                public extern static void Write(const string value);
                public static const string Empty { extern const get; }
            }
            """));
        new SemanticBinder().Bind(project);

        var outputDirectory = Path.Combine(
            Path.GetTempPath(),
            $"cxc-tests-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(outputDirectory);
            CCodeOutputGenerator.GenerateOutput(
                project,
                Path.Combine(outputDirectory, "ConstSignatures.cx"));
            var header = File.ReadAllText(Path.Combine(outputDirectory, "unnamed.h"));

            Assert.Contains("const struct CX_ID_2(unnamed, Character)* __this", header);
            Assert.Contains("const struct CX_ID_3(cxcore, System, String)* value", header);
            Assert.Contains(
                "extern CX_EXPORT const struct CX_ID_3(cxcore, System, String)* CX_ID_4(unnamed, Output, Empty, __const_get)()",
                header);
        }
        finally
        {
            if (Directory.Exists(outputDirectory))
            {
                Directory.Delete(outputDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public void PreservesAccessorParametersAndEmitsMatchingCSignatures()
    {
        var project = CxProject.CreateDefaultApplicationProject();
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public class Container<T> {
                public T Item {
                    extern const get(uint index);
                    extern set(uint index);
                }
            }
            """));
        new SemanticBinder().Bind(project);

        var container = Assert.IsType<ClassDeclaration>(
            project.CompilationContexts.Single().DeclarationScope.Declarations.Single());
        var property = Assert.IsType<PropertyDeclaration>(
            container.MemberDeclarations.Declarations.Single());
        Assert.All(property.PropertyAccessorDeclarations, accessor =>
        {
            var parameter = Assert.Single(accessor.Parameters);
            Assert.Equal("index", parameter.Name);
        });

        var outputDirectory = Path.Combine(
            Path.GetTempPath(),
            $"cxc-tests-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(outputDirectory);
            CCodeOutputGenerator.GenerateOutput(
                project,
                Path.Combine(outputDirectory, "Properties.cx"));
            var header = File.ReadAllText(Path.Combine(outputDirectory, "unnamed.h"));

            Assert.Contains("CX_ID_4(unnamed, Container, Item, __const_get)", header);
            Assert.Contains("const struct CX_ID_2(unnamed, Container)* __this", header);
            Assert.Contains("cx_uint index", header);
            Assert.Contains("void* __returnValue", header);
            Assert.Contains("CX_ID_4(unnamed, Container, Item, __set)", header);
            Assert.Contains("void* value", header);
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

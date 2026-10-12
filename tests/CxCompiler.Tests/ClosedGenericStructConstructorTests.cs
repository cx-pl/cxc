using CxCompiler.Model.Project;
using CxCompiler.OutputGenerators;
using CxCompiler.Semantics;

namespace CxCompiler.Tests;

public sealed class ClosedGenericStructConstructorTests
{
    [Fact]
    public void ConstructorCanCopyOneMatchingParameterToMultipleFields()
    {
        var project = CxProject.CreateDefaultApplicationProject(
            "closed_generic_struct_constructor_copy");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public struct Pair<T> {
                public T First;
                public T Second;
                public constructor(T value) {
                    First = value;
                    Second = value;
                }
            }
            public int Run() {
                Pair<int> pair = new Pair<int>(21);
                return pair.First + pair.Second;
            }
            """));

        new SemanticBinder().Bind(project);

        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-closed-generic-struct-constructor-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(
                Path.Combine(directory, "closed_generic_struct_constructor_copy.c"));

            Assert.Contains("First", source);
            Assert.Contains("Second", source);
            Assert.DoesNotContain("_unknowntype_", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ConstructorCanUseBuiltInExpressionsForConcreteFields()
    {
        var project = CxProject.CreateDefaultApplicationProject(
            "closed_generic_struct_constructor_expression");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public struct Pair<T> {
                public T Value;
                public int Count;
                public constructor(T value, int count) {
                    Value = value;
                    Count = count + 1;
                }
            }
            public int Main() {
                Pair<int> pair = new Pair<int>(40, 1);
                return pair.Value + pair.Count;
            }
            """));

        new SemanticBinder().Bind(project);

        var outputDirectory = Path.Combine(Path.GetTempPath(),
            $"cxc-closed-generic-struct-constructor-expression-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(outputDirectory);
            CCodeOutputGenerator.GenerateOutput(project,
                Path.Combine(outputDirectory, "closed_generic_struct_constructor_expression.cx"));
            var source = File.ReadAllText(Directory.GetFiles(outputDirectory, "*.c").Single());
            Assert.Contains("__this->Value = value;", source);
            Assert.Contains("__this->Count = (count + 1);", source);
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

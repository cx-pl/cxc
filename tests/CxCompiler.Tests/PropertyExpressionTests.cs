using CxCompiler.Model.Errors;
using CxCompiler.Model.Expressions;
using CxCompiler.Model.Project;
using CxCompiler.Model.Statements;
using CxCompiler.Model.Types;
using CxCompiler.OutputGenerators;
using CxCompiler.Semantics;

namespace CxCompiler.Tests;

public sealed class PropertyExpressionTests
{
    [Fact]
    public void BindsImplicitPropertyGetterAndSetter()
    {
        var project = CreateProject("""
            public class Counter {
                public int Value { extern get; extern set; }

                public int Update() {
                    Value = 3;
                    return Value;
                }
            }
            """);

        new SemanticBinder().Bind(project);

        var function = GetFunction(project, "Update");
        var assignment = Assert.IsType<AssignmentExpression>(
            Assert.IsType<ExpressionStatement>(function.Body![0]).Expression);
        Assert.NotNull(assignment.PropertySetter);
        Assert.Equal("__cx_property_value_0", assignment.TemporaryName);

        var getter = Assert.IsType<IdentifierExpression>(
            Assert.IsType<ReturnStatement>(function.Body[1]).Expression);
        Assert.NotNull(getter.PropertyGetter);
    }

    [Fact]
    public void SelectsConstGetterForConstReceiver()
    {
        var project = CreateProject("""
            public struct Value {
                public int Number { extern get; extern const get; }

                public int Read() const {
                    return this.Number;
                }
            }
            """);

        new SemanticBinder().Bind(project);

        var function = GetFunction(project, "Read");
        var access = Assert.IsType<MemberAccessExpression>(
            Assert.IsType<ReturnStatement>(Assert.Single(function.Body!)).Expression);
        Assert.True(access.PropertyGetter!.Const);
    }

    [Fact]
    public void BindsStaticPropertyAccess()
    {
        var project = CreateProject("""
            public static class Settings {
                public static int Current { extern get; extern set; }

                public static int Update() {
                    Settings.Current = 4;
                    return Settings.Current;
                }
            }
            """);

        new SemanticBinder().Bind(project);

        var function = GetFunction(project, "Update");
        var assignment = Assert.IsType<AssignmentExpression>(
            Assert.IsType<ExpressionStatement>(function.Body![0]).Expression);
        Assert.True(assignment.TargetProperty!.IsStatic);
    }

    [Fact]
    public void BindsIndexedPropertyGetterAndSetter()
    {
        var project = CreateProject("""
            public class Values {
                public int Item {
                    extern const get(uint index);
                    extern set(uint index);
                }

                public int Read(uint index) const {
                    return this.Item[index];
                }

                public void Write(uint index) {
                    this.Item[index] = 9;
                }
            }
            """);

        new SemanticBinder().Bind(project);

        var read = GetFunction(project, "Read");
        var indexedGetter = Assert.IsType<ArrayAccessExpression>(
            Assert.IsType<ReturnStatement>(Assert.Single(read.Body!)).Expression);
        Assert.NotNull(indexedGetter.PropertyGetter);

        var write = GetFunction(project, "Write");
        var assignment = Assert.IsType<AssignmentExpression>(
            Assert.IsType<ExpressionStatement>(Assert.Single(write.Body!)).Expression);
        Assert.NotNull(assignment.PropertySetter);
    }

    [Fact]
    public void BindsCoreStringProperties()
    {
        var project = CreateProject("""
            uint Length(string text) {
                return text.Length;
            }

            char First(string text) {
                return text.Item[0u];
            }
            """);

        new SemanticBinder().Bind(project);

        var declarations = project.CompilationContexts.Single().DeclarationScope.Declarations;
        var length = Assert.IsType<FunctionDeclaration>(declarations[0]);
        var lengthAccess = Assert.IsType<MemberAccessExpression>(
            Assert.IsType<ReturnStatement>(Assert.Single(length.Body!)).Expression);
        Assert.Equal("System.String.Length", lengthAccess.TargetProperty!.FullName.ToString());

        var first = Assert.IsType<FunctionDeclaration>(declarations[1]);
        var itemAccess = Assert.IsType<ArrayAccessExpression>(
            Assert.IsType<ReturnStatement>(Assert.Single(first.Body!)).Expression);
        Assert.Equal("System.String.Item", itemAccess.TargetProperty!.FullName.ToString());
    }

    [Fact]
    public void RejectsWritingReadOnlyProperty()
    {
        var project = CreateProject("""
            public class Value {
                public int Number { extern get; }

                public void Change() {
                    Number = 2;
                }
            }
            """);

        var exception = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(project));

        Assert.Contains("not writable", exception.Message);
    }

    [Fact]
    public void EmitsAccessorCallsForAllPropertyForms()
    {
        var project = CreateProject("""
            public class Values {
                public static int Count { extern get; }
                public int Number { extern get; extern set; }
                public int Item { extern const get(uint index); }

                public int Read(Values other) {
                    Number = other.Number;
                    return this.Item[0u] + Values.Count;
                }
            }
            """);
        new SemanticBinder().Bind(project);
        var source = GenerateSource(project);

        Assert.Contains("cx_int __cx_property_value_0;", source);
        Assert.Contains("CX_ID_4(unnamed, Values, Number, __get)(other)", source);
        Assert.Contains("CX_ID_4(unnamed, Values, Number, __set)(__this, __cx_property_value_0 =", source);
        Assert.Contains("CX_ID_4(unnamed, Values, Item, __const_get)(__this, 0u)", source);
        Assert.Contains("CX_ID_4(unnamed, Values, Count, __get)()", source);
    }

    private static CxProject CreateProject(string source)
    {
        var project = CxProject.CreateDefaultApplicationProject();
        project.AddCompilationContext(CompilerTestHelper.Parse(source));
        return project;
    }

    private static FunctionDeclaration GetFunction(CxProject project, string name)
    {
        var type = Assert.IsType<ClassDeclaration>(
            project.CompilationContexts.Single().DeclarationScope.Declarations.Single());
        return Assert.IsType<FunctionDeclaration>(
            type.MemberDeclarations.Declarations.Single(member => member.Name == name));
    }

    private static string GenerateSource(CxProject project)
    {
        var outputDirectory = Path.Combine(
            Path.GetTempPath(),
            $"cxc-tests-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(outputDirectory);
            CCodeOutputGenerator.GenerateOutput(
                project,
                Path.Combine(outputDirectory, "Properties.cx"));
            return File.ReadAllText(Path.Combine(outputDirectory, "unnamed.c"));
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

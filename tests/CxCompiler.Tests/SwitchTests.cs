using CxCompiler.Model.Errors;
using CxCompiler.Model.Project;
using CxCompiler.Model.Statements;
using CxCompiler.Model.Types;
using CxCompiler.OutputGenerators;
using CxCompiler.Semantics;

namespace CxCompiler.Tests;

public sealed class SwitchTests
{
    [Fact]
    public void ParserPreservesSwitchSectionsAndLabels()
    {
        var project = CreateProject("""
            void Select(int value) {
                switch (value) {
                    case 0:
                    case 1:
                        break;
                    default:
                        return;
                }
            }
            """);

        var function = Assert.IsType<FunctionDeclaration>(
            project.CompilationContexts.Single().DeclarationScope.Declarations.Single());
        var statement = Assert.IsType<SwitchStatement>(function.Body!.Single());

        Assert.Equal(2, statement.Sections.Count);
        Assert.Equal(2, statement.Sections[0].Labels.Count);
        Assert.True(statement.Sections[1].Labels.Single().IsDefault);
    }

    [Fact]
    public void BinderAcceptsBreakInSwitchButNotContinueWithoutLoop()
    {
        var validProject = CreateProject("""
            void Select(int value) {
                switch (value) {
                    default:
                        break;
                }
            }
            """);
        new SemanticBinder().Bind(validProject);

        var invalidProject = CreateProject("""
            void Select(int value) {
                switch (value) {
                    default:
                        continue;
                }
            }
            """);
        var exception = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(invalidProject));

        Assert.Contains("'continue' statement can only be used inside a loop", exception.Message);
    }

    [Fact]
    public void BinderChecksSelectorAndCaseTypes()
    {
        var invalidSelector = CreateProject("""
            void Select(object value) {
                switch (value) {
                    default:
                        break;
                }
            }
            """);
        var selectorException = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(invalidSelector));
        Assert.Contains("Switch expression type 'System.Object' is not supported", selectorException.Message);

        var invalidCase = CreateProject("""
            void Select(int value) {
                switch (value) {
                    case "zero":
                        break;
                }
            }
            """);
        var caseException = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(invalidCase));
        Assert.Contains("Switch case type 'System.String' does not match selector type 'System.Int'", caseException.Message);
    }

    [Fact]
    public void BinderAcceptsStringSwitchesAndBooleanFilters()
    {
        var project = CreateProject("""
            int Select(string value, bool enabled) {
                switch (value) {
                    case "ready" when enabled:
                        return 1;
                    case null:
                        return 2;
                    default:
                        return 3;
                }
            }
            """);
        new SemanticBinder().Bind(project);

        var invalidFilter = CreateProject("""
            void Select(int value) {
                switch (value) {
                    case 1 when 2:
                        break;
                    default:
                        break;
                }
            }
            """);
        var exception = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(invalidFilter));
        Assert.Contains("Switch case filter must have type 'System.Bool'", exception.Message);
    }

    [Fact]
    public void BinderChecksSwitchExpressionFallbackAndArmTypes()
    {
        var validProject = CreateProject("""
            int Select(string value) {
                return value switch {
                    "ready" => 1,
                    _ => 0,
                };
            }
            """);
        new SemanticBinder().Bind(validProject);

        var missingFallback = CreateProject("""
            int Select(int value) {
                return value switch { 1 => 1 };
            }
            """);
        var fallbackException = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(missingFallback));
        Assert.Contains("must end with an unfiltered discard arm '_'", fallbackException.Message);

        var invalidArms = CreateProject("""
            int Select(int value) {
                return value switch { 1 => true, _ => 0 };
            }
            """);
        var armException = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(invalidArms));
        Assert.Contains("Switch expression arms have incompatible types", armException.Message);
    }

    [Fact]
    public void CompleteSwitchSatisfiesReturnAnalysis()
    {
        var completeProject = CreateProject("""
            int Select(int value) {
                switch (value) {
                    case 0:
                        return 10;
                    default:
                        return 20;
                }
            }
            """);
        new SemanticBinder().Bind(completeProject);

        var incompleteProject = CreateProject("""
            int Select(int value) {
                switch (value) {
                    case 0:
                        return 10;
                }
            }
            """);
        var exception = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(incompleteProject));

        Assert.Contains("must return a value of type 'System.Int'", exception.Message);
    }

    [Fact]
    public void EmitsSwitchCasesAndIsolatedSectionScopes()
    {
        var project = CreateProject("""
            int Select(int value) {
                switch (value) {
                    case 0:
                    case 1:
                        int result = 10;
                        return result;
                    default:
                        return 20;
                }
            }
            """);
        new SemanticBinder().Bind(project);
        var outputDirectory = Path.Combine(
            Path.GetTempPath(),
            $"cxc-tests-{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(outputDirectory);
            CCodeOutputGenerator.GenerateOutput(
                project,
                Path.Combine(outputDirectory, "Switch.cx"));
            var generatedSource = File.ReadAllText(
                Path.Combine(outputDirectory, "unnamed.c"));

            Assert.Contains("switch (value)", generatedSource);
            Assert.Contains("case 0:", generatedSource);
            Assert.Contains("case 1:", generatedSource);
            Assert.Contains("default:", generatedSource);
            Assert.Contains("cx_int result = 10;", generatedSource);
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
    public void EmitsStringSwitchDispatchAndSwitchExpression()
    {
        var project = CreateProject("""
            int Select(string value, bool enabled) {
                switch (value) {
                    case "ready" when enabled:
                        break;
                    default:
                        break;
                }
                return value switch {
                    "ready" when enabled => 1,
                    _ => 0,
                };
            }
            """);
        new SemanticBinder().Bind(project);
        var outputDirectory = Path.Combine(
            Path.GetTempPath(),
            $"cxc-tests-{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(outputDirectory);
            CCodeOutputGenerator.GenerateOutput(
                project,
                Path.Combine(outputDirectory, "Switch.cx"));
            var generatedSource = File.ReadAllText(
                Path.Combine(outputDirectory, "unnamed.c"));

            Assert.Contains("memcmp(", generatedSource);
            Assert.Contains("__cx_switch_case_", generatedSource);
            Assert.Contains("case 0:", generatedSource);
            Assert.Contains("__cx_switch_value_", generatedSource);
        }
        finally
        {
            if (Directory.Exists(outputDirectory))
            {
                Directory.Delete(outputDirectory, recursive: true);
            }
        }
    }

    private static CxProject CreateProject(string source)
    {
        var project = CxProject.CreateDefaultApplicationProject();
        project.AddCompilationContext(CompilerTestHelper.Parse(source));
        return project;
    }
}

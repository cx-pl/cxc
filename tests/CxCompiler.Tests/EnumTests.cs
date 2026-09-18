using CxCompiler.Model.Errors;
using CxCompiler.Model.Expressions;
using CxCompiler.Model.Project;
using CxCompiler.Model.Types;
using CxCompiler.OutputGenerators;
using CxCompiler.Semantics;

namespace CxCompiler.Tests;

public sealed class EnumTests
{
    [Fact]
    public void ParsesCommaSeparatedMembersAndExplicitValues()
    {
        var project = CreateProject("""
            public enum Color {
                Red,
                Green = 5,
                Blue,
            }
            """);

        var declaration = Assert.IsType<EnumDeclaration>(
            project.CompilationContexts.Single().DeclarationScope.Declarations.Single());
        Assert.Equal(Visibility.Public, declaration.Visibility);
        Assert.Equal(["Red", "Green", "Blue"], declaration.Members.Select(member => member.Name));
        Assert.Null(declaration.Members[0].Value);
        Assert.Equal("5", declaration.Members[1].Value!.SourceText);
    }

    [Fact]
    public void BindsEnumTypesMembersAndSwitchLabels()
    {
        var project = CreateProject("""
            enum Color { Red, Green }

            int ToCode(Color color) {
                switch (color) {
                    case Color.Red:
                        return 1;
                    case Color.Green:
                        return 2;
                    default:
                        return 0;
                }
            }
            """);

        new SemanticBinder().Bind(project);

        var function = Assert.IsType<FunctionDeclaration>(
            project.CompilationContexts.Single().DeclarationScope.Declarations[1]);
        var enumType = Assert.IsType<NamedType>(function.Parameters.Single().ParameterType);
        Assert.Equal(ClassType.Enum, enumType.ClassType);
        var switchStatement = Assert.IsType<Model.Statements.SwitchStatement>(
            function.Body!.Single());
        var member = Assert.IsType<MemberAccessExpression>(
            switchStatement.Sections[0].Labels.Single().Value);
        Assert.Equal("Red", member.TargetEnumMember!.Declaration.Name);
    }

    [Fact]
    public void EmitsEnumDefinitionsTypesAndMemberConstants()
    {
        var project = CreateProject("""
            namespace Drawing;

            public enum Color { Red, Green = 5, Blue }

            Color Favorite() {
                return Color.Green;
            }
            """);
        new SemanticBinder().Bind(project);

        var (header, source) = GenerateOutput(project);

        Assert.Contains("typedef enum CX_ID_3(unnamed, Drawing, Color) {", header);
        Assert.Contains("CX_ID_4(unnamed, Drawing, Color, Red),", header);
        Assert.Contains("CX_ID_4(unnamed, Drawing, Color, Green) = 5,", header);
        Assert.Contains("} CX_ID_3(unnamed, Drawing, Color);", header);
        Assert.Contains(
            "CX_ID_3(unnamed, Drawing, Color) CX_ID_3(unnamed, Drawing, Favorite)()",
            source);
        Assert.Contains("return CX_ID_4(unnamed, Drawing, Color, Green);", source);
    }

    [Fact]
    public void RejectsUnknownAndCrossEnumMembers()
    {
        var unknownMember = CreateProject("""
            enum Color { Red }
            Color Pick() { return Color.Blue; }
            """);
        var unknownException = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(unknownMember));
        Assert.Contains("has no member named 'Blue'", unknownException.Message);

        var crossEnum = CreateProject("""
            enum Color { Red }
            enum Direction { Left }
            Color Pick() { return Direction.Left; }
            """);
        var typeException = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(crossEnum));
        Assert.Contains("returns 'unnamed.Direction'", typeException.Message);
        Assert.Contains("'unnamed.Color' is required", typeException.Message);
    }

    [Fact]
    public void RejectsDuplicateNamesAndNonIntegerValues()
    {
        var duplicateException = Assert.Throws<CompilationErrorException>(() =>
            CreateProject("enum Color { Red, Red }"));
        Assert.Contains("already contains a member", duplicateException.Message);

        var nonInteger = CreateProject("enum Answer { No = false, Yes = true }");
        var valueException = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(nonInteger));
        Assert.Contains("must have an integer value", valueException.Message);
    }

    [Fact]
    public void RejectsAssignmentToEnumMembers()
    {
        var project = CreateProject("""
            enum Color { Red }
            void Change() { Color.Red = Color.Red; }
            """);

        var exception = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(project));

        Assert.Contains("Enum member 'Color.Red' cannot be assigned to", exception.Message);
    }

    private static CxProject CreateProject(string source)
    {
        var project = CxProject.CreateDefaultApplicationProject();
        project.AddCompilationContext(CompilerTestHelper.Parse(source));
        return project;
    }

    private static (string Header, string Source) GenerateOutput(CxProject project)
    {
        var outputDirectory = Path.Combine(
            Path.GetTempPath(),
            $"cxc-tests-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(outputDirectory);
            CCodeOutputGenerator.GenerateOutput(
                project,
                Path.Combine(outputDirectory, "Enums.cx"));
            return (
                File.ReadAllText(Path.Combine(outputDirectory, "unnamed.h")),
                File.ReadAllText(Path.Combine(outputDirectory, "unnamed.c")));
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

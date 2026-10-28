using CxCompiler.Model.Expressions;
using CxCompiler.Model.Project;
using CxCompiler.Model.Statements;
using CxCompiler.Model.Types;
using CxCompiler.Model.Types.BuiltInTypes;
using CxCompiler.OutputGenerators;
using CxCompiler.Semantics;

namespace CxCompiler.Tests;

public sealed class StringOperatorTests
{
    [Fact]
    public void AllowsExternOperatorDeclarationsWithoutBodies()
    {
        var project = CxProject.CreateDefaultApplicationProject();
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public class NativeComparable {
                public extern bool operator ==(NativeComparable other) const;
            }
            public bool Equal(NativeComparable left, NativeComparable right) {
                return left == right;
            }
            """));

        new SemanticBinder().Bind(project);

        var declarations = project.CompilationContexts.Single().DeclarationScope.Declarations;
        var nativeClass = Assert.Single(declarations.OfType<ClassDeclaration>());
        var equality = Assert.Single(
            nativeClass.MemberDeclarations.Declarations.OfType<FunctionDeclaration>(),
            function => function.OperatorToken == "==");
        Assert.Null(equality.Body);
        Assert.Contains(MemberModifier.Extern, equality.MemberModifiers);
        var equalFunction = Assert.Single(declarations.OfType<FunctionDeclaration>());
        var expression = Assert.IsType<BinaryExpression>(
            Assert.IsType<ReturnStatement>(Assert.Single(equalFunction.Body!)).Expression);
        Assert.Same(equality, expression.OperatorSymbol!.Declaration);
    }

    [Fact]
    public void BindsContentEqualityOperatorsToCxcore()
    {
        var project = CxProject.CreateDefaultApplicationProject();
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public bool Equal(string left, string right) { return left == right; }
            public bool NotEqual(string left, string right) { return left != right; }
            public bool LessThan(string left, string right) { return left < right; }
            public bool LessThanOrEqual(string left, string right) { return left <= right; }
            public bool GreaterThan(string left, string right) { return left > right; }
            public bool GreaterThanOrEqual(string left, string right) { return left >= right; }
            """));

        new SemanticBinder().Bind(project);

        var functions = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<FunctionDeclaration>()
            .ToDictionary(declaration => declaration.Name);
        var equal = Assert.IsType<BinaryExpression>(
            Assert.IsType<ReturnStatement>(Assert.Single(functions["Equal"].Body!)).Expression);
        var notEqual = Assert.IsType<BinaryExpression>(
            Assert.IsType<ReturnStatement>(Assert.Single(functions["NotEqual"].Body!)).Expression);
        var relational = new[] { "LessThan", "LessThanOrEqual", "GreaterThan", "GreaterThanOrEqual" }
            .Select(name => Assert.IsType<BinaryExpression>(
                Assert.IsType<ReturnStatement>(Assert.Single(functions[name].Body!)).Expression))
            .ToArray();

        Assert.Equal("System.String.__cx_operator_equal", equal.OperatorSymbol!.FullName.ToString());
        Assert.Equal("System.String.__cx_operator_not_equal", notEqual.OperatorSymbol!.FullName.ToString());
        Assert.Equal("cxcore", equal.OperatorSymbol.ModuleName);
        Assert.Equal("cxcore", notEqual.OperatorSymbol.ModuleName);
        Assert.Equal(
            ["<", "<=", ">", ">="],
            relational.Select(expression => expression.OperatorSymbol!.Declaration!.OperatorToken!)
                .ToArray());
        Assert.All(relational, expression =>
            Assert.StartsWith("System.String.__cx_operator_", expression.OperatorSymbol!.FullName.ToString()));

        var outputDirectory = Path.Combine(Path.GetTempPath(), $"cxc-string-operators-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(outputDirectory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(outputDirectory, "input.cx"));
            var source = File.ReadAllText(Path.Combine(outputDirectory, "unnamed.c"));
            Assert.Contains("cxcore_System_String___cx_operator_equal(left, right)", source);
            Assert.Contains("cxcore_System_String___cx_operator_not_equal(left, right)", source);
            Assert.Contains("cxcore_System_String___cx_operator_less(left, right)", source);
            Assert.Contains("cxcore_System_String___cx_operator_greater_equal(left, right)", source);
        }
        finally
        {
            if (Directory.Exists(outputDirectory)) Directory.Delete(outputDirectory, recursive: true);
        }
    }

    [Fact]
    public void BindsStringConcatenationToTheCoreOperator()
    {
        var project = CxProject.CreateDefaultApplicationProject();
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public string Join(string left, string right) { return left + right; }
            public string JoinWithNull(string value) { return null + value; }
            public string JoinNullSuffix(string value) { return value + null; }
            """));

        new SemanticBinder().Bind(project);

        var declarations = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<FunctionDeclaration>().ToDictionary(function => function.Name);
        var join = Assert.IsType<BinaryExpression>(
            Assert.IsType<ReturnStatement>(Assert.Single(declarations["Join"].Body!)).Expression);
        Assert.Equal("System.String.__cx_operator_plus", join.OperatorSymbol!.FullName.ToString());
        Assert.Equal("cxcore", join.OperatorSymbol.ModuleName);
        Assert.Same(BuiltInSystemTypes.String, join.InferredType);

        var directory = Path.Combine(Path.GetTempPath(), $"cxc-string-add-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(Path.Combine(directory, "unnamed.c"));
            Assert.Contains(
                "cxcore_System_String___cx_operator_plus(left, right)", source);
            Assert.Contains(
                "cxcore_System_String___cx_operator_plus(CX_NULL, value)", source);
            Assert.Contains(
                "cxcore_System_String___cx_operator_plus(value, CX_NULL)", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}

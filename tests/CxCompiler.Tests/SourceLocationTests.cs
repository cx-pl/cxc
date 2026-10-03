using Antlr4.Runtime;
using CxCompiler.Grammar;
using CxCompiler.Model.Errors;
using CxCompiler.Model.Project;
using CxCompiler.Model.Types;
using CxCompiler.Semantics;

namespace CxCompiler.Tests;

public sealed class SourceLocationTests
{
    [Fact]
    public void ParserAttachesFileAndRangeToDeclarationsStatementsAndExpressions()
    {
        var context = CompilerTestHelper.Parse(
            "public int Run() { return 42; }",
            "sample.cx");
        var function = Assert.IsType<FunctionDeclaration>(
            Assert.Single(context.DeclarationScope.Declarations));
        var statement = Assert.Single(function.Body!);
        var expression = Assert.IsType<CxCompiler.Model.Expressions.LiteralExpression>(
            Assert.IsType<CxCompiler.Model.Statements.ReturnStatement>(statement).Expression);

        Assert.Equal("sample.cx", function.SourceSpan!.Value.FilePath);
        Assert.Equal((1, 1), (function.SourceSpan.Value.StartLine, function.SourceSpan.Value.StartColumn));
        Assert.Equal("sample.cx", statement.SourceSpan!.Value.FilePath);
        Assert.Equal("sample.cx", expression.SourceSpan!.Value.FilePath);
        Assert.Equal((1, 27), (expression.SourceSpan.Value.StartLine, expression.SourceSpan.Value.StartColumn));
    }

    [Fact]
    public void ParserAttachesSpansToMemberDeclarationsAndParameters()
    {
        var context = CompilerTestHelper.Parse(
            "public class Box { public int Count; public int Add(int amount) { return amount; } }",
            "members.cx");
        var type = Assert.IsType<ClassDeclaration>(Assert.Single(context.DeclarationScope.Declarations));
        var field = Assert.IsType<FieldDeclaration>(type.MemberDeclarations.Declarations[0]);
        var method = Assert.IsType<FunctionDeclaration>(type.MemberDeclarations.Declarations[1]);

        Assert.Equal("members.cx", field.SourceSpan!.Value.FilePath);
        Assert.Equal("members.cx", method.SourceSpan!.Value.FilePath);
        Assert.Equal("members.cx", Assert.Single(method.Parameters).SourceSpan!.Value.FilePath);
    }

    [Fact]
    public void LexerAndParserDiagnosticsIncludeSourceAndOneBasedLocation()
    {
        const string source = "public int Run() {\n  return 1 + @;\n}";
        const string sourcePath = "broken.cx";
        var listener = new ParserErrorListener(sourcePath);
        var input = new AntlrInputStream(source) { name = sourcePath };
        var lexer = new CxLexer(input);
        lexer.RemoveErrorListeners();
        lexer.AddErrorListener(listener);
        var parser = new CxParser(new CommonTokenStream(lexer));
        parser.RemoveErrorListeners();
        parser.AddErrorListener(listener);
        parser.compilationUnit();

        Assert.NotEmpty(listener.Diagnostics);
        Assert.All(listener.Diagnostics, diagnostic => Assert.StartsWith("broken.cx(", diagnostic));
        Assert.Contains(listener.Diagnostics, diagnostic =>
            diagnostic.StartsWith("broken.cx(2,14): error:", StringComparison.Ordinal));
    }

    [Fact]
    public void SemanticExpressionDiagnosticHasStableTextAndExactLocation()
    {
        var project = CxProject.CreateDefaultApplicationProject();
        project.AddCompilationContext(CompilerTestHelper.Parse(
            "int Run() { return missing; }",
            "semantic.cx"));

        var exception = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(project));

        Assert.Equal(
            "semantic.cx(1,20): error: Cannot resolve value 'missing'.",
            exception.Message);
        Assert.Equal("Cannot resolve value 'missing'.", exception.DiagnosticText);
        Assert.Equal("semantic.cx", exception.SourceSpan!.Value.FilePath);
    }

    [Fact]
    public void SemanticStatementDiagnosticHasStableTextAndExactLocation()
    {
        var project = CxProject.CreateDefaultApplicationProject();
        project.AddCompilationContext(CompilerTestHelper.Parse(
            "void Run() { break; }",
            "statement.cx"));

        var exception = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(project));

        Assert.Equal(
            "statement.cx(1,14): error: The 'break' statement can only be used inside a loop or switch.",
            exception.Message);
        Assert.Equal("The 'break' statement can only be used inside a loop or switch.",
            exception.DiagnosticText);
    }
}

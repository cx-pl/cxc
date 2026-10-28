using Antlr4.Runtime;
using CxCompiler.Grammar;
using CxCompiler.Model.Types;

namespace CxCompiler.Tests;

[Trait("Area", "Syntax")]
public sealed class OperatorDeclarationSyntaxTests
{
    [Theory]
    [InlineData("+")]
    [InlineData("-")]
    [InlineData("*")]
    [InlineData("/")]
    [InlineData("%")]
    [InlineData("!")]
    [InlineData("~")]
    [InlineData("&")]
    [InlineData("|")]
    [InlineData("^")]
    [InlineData("<<")]
    [InlineData(">>")]
    [InlineData("==")]
    [InlineData("!=")]
    [InlineData("<")]
    [InlineData("<=")]
    [InlineData(">")]
    [InlineData(">=")]
    [InlineData("++")]
    [InlineData("--")]
    [InlineData("+=")]
    [InlineData("-=")]
    [InlineData("*=")]
    [InlineData("/=")]
    [InlineData("%=")]
    [InlineData("&=")]
    [InlineData("|=")]
    [InlineData("^=")]
    [InlineData("<<=")]
    [InlineData(">>=")]
    public void ParsesOperatorDeclarationWithStandardLexer(string token)
    {
        var context = CompilerTestHelper.Parse($$"""
            public struct Value {
                public Value operator {{token}}(Value other) const { return other; }
            }
            """);
        var owner = Assert.IsType<ClassDeclaration>(Assert.Single(context.DeclarationScope.Declarations));
        var function = Assert.IsType<FunctionDeclaration>(Assert.Single(owner.MemberDeclarations.Declarations));

        Assert.Equal(token, function.OperatorToken);
        Assert.StartsWith("__cx_operator_", function.Name);
        Assert.True(function.Const);
        Assert.Single(function.Parameters);
    }

    [Fact]
    public void ParseTreePreservesOperatorKeywordAndSymbol()
    {
        var lexer = new CxLexer(new AntlrInputStream("int operator /* comment */ +(int other);"));
        var parser = new CxParser(new CommonTokenStream(lexer));

        var declaration = parser.functionDeclaration();

        Assert.Equal(0, parser.NumberOfSyntaxErrors);
        Assert.Equal(TokenConstants.EOF, parser.CurrentToken.Type);
        Assert.Equal("operator", declaration.Operator().GetText());
        Assert.Equal("+", declaration.operatorToken.GetText());
        Assert.Null(declaration.name);
    }

    [Fact]
    public void ParsesGenericRightShiftOperatorsAlongsideNestedGenericClosers()
    {
        var context = CompilerTestHelper.Parse("""
            public class Box<T> { }
            public struct Value {
                public Value operator /* comment */ >><T>(T other) const { return this; }
                public Value operator >><Box<int>>(Box<int> other) const { return this; }
            }
            """);
        var owner = Assert.IsType<ClassDeclaration>(context.DeclarationScope.Declarations[1]);
        var functions = owner.MemberDeclarations.Declarations.Cast<FunctionDeclaration>().ToArray();

        Assert.Equal(2, functions.Length);
        Assert.All(functions, function =>
        {
            Assert.Equal(">>", function.OperatorToken);
            Assert.Equal("__cx_operator_right_shift", function.Name);
            Assert.Single(function.UnclassifiedGenericTypeArguments);
        });
    }

    [Fact]
    public void OrdinaryIdentifierIsNotDecodedAsOperatorDeclaration()
    {
        var context = CompilerTestHelper.Parse("int __cx_operator_plus(int other) { return other; }");
        var function = Assert.IsType<FunctionDeclaration>(Assert.Single(context.DeclarationScope.Declarations));

        Assert.Equal("__cx_operator_plus", function.Name);
        Assert.Null(function.OperatorToken);
    }

    [Theory]
    [InlineData("")]
    [InlineData("name")]
    [InlineData("&&")]
    [InlineData("||")]
    [InlineData("=")]
    [InlineData("??")]
    [InlineData("=>")]
    public void RejectsMissingOrUnsupportedOperatorSymbol(string token)
    {
        var listener = new ParserErrorListener("operator.cx");
        var lexer = new CxLexer(new AntlrInputStream($"int operator {token}();"));
        var parser = new CxParser(new CommonTokenStream(lexer));
        parser.RemoveErrorListeners();
        parser.AddErrorListener(listener);

        parser.compilationUnit();

        Assert.True(listener.HasErrors);
        Assert.All(listener.Diagnostics, diagnostic => Assert.StartsWith("operator.cx(", diagnostic));
    }
}

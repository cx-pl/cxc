using CxCompiler.Model.Expressions;
using CxCompiler.Model.Statements;
using CxCompiler.Model.Types;

namespace CxCompiler.Tests;

public sealed class FunctionBodyTests
{
    private const string HelloWorldSource = """
        import System;

        public void Main() {
            Console.WriteLine("Hello world!");
        }
        """;

    [Fact]
    public void ParserRetainsHelloWorldInvocation()
    {
        var context = CompilerTestHelper.Parse(HelloWorldSource);
        var function = Assert.IsType<FunctionDeclaration>(
            Assert.Single(context.DeclarationScope.Declarations));
        var statement = Assert.IsType<ExpressionStatement>(Assert.Single(function.Body!));
        var invocation = Assert.IsType<InvocationExpression>(statement.Expression);
        var target = Assert.IsType<IdentifierExpression>(invocation.Target);
        var argument = Assert.IsType<LiteralExpression>(Assert.Single(invocation.Arguments));

        Assert.Equal("Console.WriteLine", target.Identifier.ToString());
        Assert.Equal("\"Hello world!\"", argument.SourceText);
    }
}

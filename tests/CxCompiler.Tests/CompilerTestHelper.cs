using Antlr4.Runtime;
using CxCompiler.Grammar;
using CxCompiler.Model;
using CxCompiler.ParserVisitors;

namespace CxCompiler.Tests;

internal static class CompilerTestHelper
{
    public static CompilationContext Parse(string source, string sourcePath = "test.cx")
    {
        var listener = new ParserErrorListener(sourcePath);
        var input = new AntlrInputStream(source) { name = sourcePath };
        var lexer = new OperatorAwareCxLexer(input);
        lexer.RemoveErrorListeners();
        lexer.AddErrorListener(listener);
        var parser = new CxParser(new CommonTokenStream(lexer));
        parser.RemoveErrorListeners();
        parser.AddErrorListener(listener);
        var syntaxTree = parser.compilationUnit();

        Assert.True(!listener.HasErrors,
            string.Join(Environment.NewLine, listener.Diagnostics));
        return new CompilationUnitParserVisitor().Visit(syntaxTree);
    }
}

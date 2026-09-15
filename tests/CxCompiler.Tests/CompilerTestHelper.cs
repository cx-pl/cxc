using Antlr4.Runtime;
using CxCompiler.Grammar;
using CxCompiler.Model;
using CxCompiler.ParserVisitors;

namespace CxCompiler.Tests;

internal static class CompilerTestHelper
{
    public static CompilationContext Parse(string source)
    {
        var lexer = new CxLexer(new AntlrInputStream(source));
        var parser = new CxParser(new CommonTokenStream(lexer));
        var syntaxTree = parser.compilationUnit();

        Assert.Equal(0, parser.NumberOfSyntaxErrors);
        return new CompilationUnitParserVisitor().Visit(syntaxTree);
    }
}

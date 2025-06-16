using Antlr4.Runtime.Misc;
using Antlr4.Runtime.Tree;
using CxCompiler.Grammar;
using CxCompiler.Model;
using CxCompiler.Model.Types;

namespace CxCompiler.ParserVisitors;

public class ClassTypeParserVisitor : CxParserBaseVisitor<(ClassType classType, string? customTypeName)>
{
    public override (ClassType classType, string? customTypeName) VisitClassType([NotNull] CxParser.ClassTypeContext context)
    {
        var terminalNode = (ITerminalNode)context.GetChild(0);
        var symbolType = terminalNode.Symbol.Type;
        ClassType classType = symbolType switch
        {
            CxLexer.Class => ClassType.Class,
            CxLexer.Struct => ClassType.Struct,
            CxLexer.Interface => ClassType.Interface,
            CxLexer.Identifier => ClassType.Custom,
            _ => throw new InternalCompilerException($"Unknown class type {context.GetText()}"),
        };

        string? customTypeName = null;
        if (classType == ClassType.Custom)
        {
            customTypeName = context.Identifier().GetText();
        }

        return (classType, customTypeName);
    }
}
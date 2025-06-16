using Antlr4.Runtime.Misc;
using Antlr4.Runtime.Tree;
using CxCompiler.Grammar;
using CxCompiler.Model;
using CxCompiler.Model.Types;

namespace CxCompiler.ParserVisitors;

public class ClassModifiersParserVisitor : CxParserBaseVisitor<IReadOnlyCollection<ClassModifier>>
{
    override public IReadOnlyCollection<ClassModifier> VisitClassModifiers([NotNull] CxParser.ClassModifiersContext context)
    {
        if (context.children == null)
        {
            return [];
        }

        var modifiers = new List<ClassModifier>();
        foreach (var child in context.children)
        {
            modifiers.Add(child.GetText() switch
            {
                "public" => ClassModifier.Public,
                "protected" => ClassModifier.Protected,
                "private" => ClassModifier.Private,
                "internal" => ClassModifier.Internal,
                "abstract" => ClassModifier.Abstract,
                "final" => ClassModifier.Final,
                "static" => ClassModifier.Static,
                _ => throw new InternalCompilerException($"Unknown class modifier {child.GetText()}"),
            });
        }
        return modifiers;
    }
}
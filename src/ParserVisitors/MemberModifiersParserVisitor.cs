using Antlr4.Runtime.Misc;
using Antlr4.Runtime.Tree;
using CxCompiler.Grammar;
using CxCompiler.Model;
using CxCompiler.Model.Types;

namespace CxCompiler.ParserVisitors;

public class MemberModifiersParserVisitor : CxParserBaseVisitor<MemberModifier[]>
{
    public override MemberModifier[] VisitMemberModifiers([NotNull] CxParser.MemberModifiersContext context)
    {
        if (context.children == null)
        {
            return [];
        }

        var modifiers = new List<MemberModifier>();
        foreach (var child in context.children)
        {
            modifiers.Add(child.GetText() switch
            {
                "public" => MemberModifier.Public,
                "protected" => MemberModifier.Protected,
                "private" => MemberModifier.Private,
                "internal" => MemberModifier.Internal,
                "abstract" => MemberModifier.Abstract,
                "virtual" => MemberModifier.Virtual,
                "override" => MemberModifier.Override,
                "final" => MemberModifier.Final,
                "static" => MemberModifier.Static,
                "extern" => MemberModifier.Extern,
                _ => throw new InternalCompilerException($"Unknown member modifier {child.GetText()}"),
            });
        }
        return modifiers.ToArray();
    }
}
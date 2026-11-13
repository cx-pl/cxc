using Antlr4.Runtime.Misc;
using Antlr4.Runtime.Tree;
using CxCompiler.Grammar;
using CxCompiler.Model.Types;

namespace CxCompiler.ParserVisitors;

public sealed class GenericTypeArgumentsParserVisitor : CxParserBaseVisitor<TypeBase[]>
{
    public override TypeBase[] VisitGenericTypeArguments(
        [NotNull] CxParser.GenericTypeArgumentsContext context)
    {
        var arguments = new List<TypeBase>();
        CollectArguments(context.genericTypeArgumentList(), arguments);
        return arguments.ToArray();
    }

    public override TypeBase[] VisitGenericTypeArgumentList(
        [NotNull] CxParser.GenericTypeArgumentListContext context)
    {
        var arguments = new List<TypeBase>();
        CollectArguments(context, arguments);
        return arguments.ToArray();
    }

    private static void CollectArguments(IParseTree node, ICollection<TypeBase> arguments)
    {
        if (node is CxParser.TypeNameContext typeName)
        {
            arguments.Add(new TypeNameContextVisitor().Visit(typeName));
            return;
        }

        for (var index = 0; index < node.ChildCount; index++)
        {
            CollectArguments(node.GetChild(index), arguments);
        }
    }
}

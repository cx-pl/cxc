using Antlr4.Runtime.Misc;
using CxCompiler.Grammar;

namespace CxCompiler.ParserVisitors;

public class GenericParamsParserVisitor : CxParserBaseVisitor<string[]>
{
    private List<string> _genericParams = new();

    public override string[] VisitGenericParams([NotNull] CxParser.GenericParamsContext context)
    {
        if (context == null)
        {
            return [];
        }

        VisitChildren(context);
        return _genericParams.ToArray();
    }

    public override string[] VisitGenericParamList([NotNull] CxParser.GenericParamListContext context)
    {
        var paramName = context.Identifier().GetText();
        _genericParams.Add(paramName);
        return base.VisitGenericParamList(context);
    }
}
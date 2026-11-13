using Antlr4.Runtime.Misc;
using CxCompiler.Grammar;

namespace CxCompiler.ParserVisitors;

public class GenericParamsParserVisitor : CxParserBaseVisitor<string[]>
{
    private List<string> _genericParams = new();

    public override string[] VisitGenericTypeParameters(
        [NotNull] CxParser.GenericTypeParametersContext context)
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
        if (context.genericParamList() is { } previous)
        {
            VisitGenericParamList(previous);
        }
        var paramName = context.Identifier().GetText();
        _genericParams.Add(paramName);
        return _genericParams.ToArray();
    }
}

using CxCompiler.Grammar;
using CxCompiler.Model.Common;
using CxCompiler.Model.Types;

namespace CxCompiler.ParserVisitors;

internal static class FunctionParameterParser
{
    public static FunctionParameter Parse(CxParser.FunctionParameterContext context, TypeBase type)
    {
        var defaultIdentifier = context.qualifiedIdentifier();
        return new FunctionParameter(
            context.name.Text,
            type,
            context.literal()?.GetText() ?? defaultIdentifier?.GetText(),
            defaultIdentifier is not null).WithSourceSpan(context);
    }
}

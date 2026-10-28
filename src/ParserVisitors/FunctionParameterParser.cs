using CxCompiler.Grammar;
using CxCompiler.Model.Common;
using CxCompiler.Model.Types;

namespace CxCompiler.ParserVisitors;

internal static class FunctionParameterParser
{
    public static FunctionParameter Parse(CxParser.FunctionParameterContext context, TypeBase type)
    {
        var defaultIdentifier = context.qualifiedIdentifier();
        var nameOf = context.nameOfExpression() is { } nameOfContext
            ? (CxCompiler.Model.Expressions.NameOfExpression)new ExpressionParserVisitor().Visit(nameOfContext)
            : null;
        var defaultText = context.sign is not null
            ? context.sign.Text + (context.IntegerLiteral() ?? context.FloatingLiteral()).GetText()
            : nameOf?.SourceText ?? context.literal()?.GetText() ?? defaultIdentifier?.GetText();
        return new FunctionParameter(
            context.name.Text,
            type,
            defaultText,
            defaultIdentifier is not null)
        {
            Annotations = AnnotationParser.Parse(context.annotations()),
            DefaultNameOfExpression = nameOf,
        }.WithSourceSpan(context);
    }
}

using CxCompiler.Grammar;
using CxCompiler.Model.Common;

namespace CxCompiler.ParserVisitors;

internal static class AnnotationParser
{
    internal static IReadOnlyList<AnnotationApplication> Parse(CxParser.AnnotationsContext? context) =>
        context?.annotation().Select(annotation => new AnnotationApplication(
            new QualifiedIdentifierContextVisitor().Visit(annotation.qualifiedIdentifier()),
            annotation.target?.Text,
            annotation.annotationArgument().Select(argument => new AnnotationArgument(
                argument.name?.Text, argument.expression().GetText())).ToArray())
            .WithSourceSpan(annotation)).ToArray() ?? [];
}

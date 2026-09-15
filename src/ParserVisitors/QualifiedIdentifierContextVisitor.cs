using Antlr4.Runtime.Misc;
using CxCompiler.Grammar;
using CxCompiler.Model.Common;
using static CxCompiler.Grammar.CxParser;

namespace CxCompiler.ParserVisitors;

public class QualifiedIdentifierContextVisitor : CxParserBaseVisitor<QualifiedIdentifier>
{
    public override QualifiedIdentifier VisitSimpleIdentifier([NotNull] SimpleIdentifierContext context)
    {
        return new QualifiedIdentifier(context.identifier.Text);
    }

    public override QualifiedIdentifier VisitCombinedQualifiedIdentifier([NotNull] CombinedQualifiedIdentifierContext context)
    {
        var baseQualifiedIdentifier = base.Visit(context.baseQualifiedIdentifier);

        return new QualifiedIdentifier(baseQualifiedIdentifier, context.identifier.Text);
    }
}
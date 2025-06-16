using CxCompiler.Grammar;
using CxCompiler.Model.Common;

namespace CxCompiler.Model.Extensions;

public static class GrammarModelExtensions
{
    public static QualifiedIdentifier ToQualifiedIdentifier(this CxParser.QualifiedIdentifierContext context)
    {
        if (context is CxParser.SimpleIdentifierContext
            simpleQdentifierContext)
        {
            return new QualifiedIdentifier(simpleQdentifierContext.identifier.Text);
        }
        else if (context is CxParser.CombinedQualifiedIdentifierContext combinedQualifiedIdentifierContext)
        {
            return new QualifiedIdentifier(
                combinedQualifiedIdentifierContext.baseQualifiedIdentifier.ToQualifiedIdentifier(),
                combinedQualifiedIdentifierContext.identifier.Text);
        }
        else
        {
            throw new InternalCompilerException($"Invalid QualifiedIdentifierContext type {context.GetType().Name}");
        }
    }
}

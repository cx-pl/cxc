using Antlr4.Runtime;

namespace CxCompiler.Model.Common;

/// <summary>A source range with one-based line and column values.</summary>
public readonly record struct SourceSpan(
    string FilePath,
    int StartLine,
    int StartColumn,
    int EndLine,
    int EndColumn)
{
    public static SourceSpan From(ParserRuleContext context)
    {
        var start = context.Start;
        var stop = context.Stop ?? start;
        return new SourceSpan(
            start.InputStream.SourceName,
            start.Line,
            start.Column + 1,
            stop.Line,
            stop.Column + Math.Max(stop.Text?.Length ?? 1, 1) + 1);
    }
}

public interface IHasSourceSpan
{
    SourceSpan? SourceSpan { get; set; }
}

public static class SourceSpanExtensions
{
    public static T WithSourceSpan<T>(this T node, ParserRuleContext context)
        where T : IHasSourceSpan
    {
        node.SourceSpan = SourceSpan.From(context);
        return node;
    }
}

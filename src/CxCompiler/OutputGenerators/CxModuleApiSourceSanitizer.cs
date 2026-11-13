using Antlr4.Runtime;
using CxCompiler.Grammar;

namespace CxCompiler.OutputGenerators;

internal static class CxModuleApiSourceSanitizer
{
    private sealed record Edit(int Start, int End, string Replacement);

    private sealed class BodyCollector : CxParserBaseVisitor<object?>
    {
        public List<Edit> Edits { get; } = [];

        public override object? VisitFunctionDeclaration(
            CxParser.FunctionDeclarationContext context)
        {
            AddFunctionBody(context.functionBody(), context.memberModifiers(),
                context.typeNameOrVoid().Start.StartIndex);
            return base.VisitFunctionDeclaration(context);
        }

        public override object? VisitConstructorDeclaration(
            CxParser.ConstructorDeclarationContext context)
        {
            AddFunctionBody(context.functionBody(), context.memberModifiers(),
                context.Constructor().Symbol.StartIndex);
            return base.VisitConstructorDeclaration(context);
        }

        public override object? VisitPropertyAccessorDeclaration(
            CxParser.PropertyAccessorDeclarationContext context)
        {
            var body = context.propertyAccessorBody();
            if (body.LeftBrace() is not null || body.Arrow() is not null)
            {
                AddReplacement(body.Start, body.Stop, ";");
                if (context.Extern() is null)
                {
                    Edits.Add(new Edit(context.Start.StartIndex,
                        context.Start.StartIndex - 1, "extern "));
                }
            }
            return base.VisitPropertyAccessorDeclaration(context);
        }

        private void AddFunctionBody(
            CxParser.FunctionBodyContext body,
            CxParser.MemberModifiersContext modifiers,
            int declarationStart)
        {
            if (body.LeftBrace() is null && body.Arrow() is null) return;

            AddReplacement(body.Start, body.Stop, ";");
            if (modifiers.memberModifier().Any(modifier => modifier.Extern() is not null))
                return;

            var modifierEnd = modifiers.memberModifier().LastOrDefault()?.Stop?.StopIndex;
            if (modifierEnd is { } end)
                Edits.Add(new Edit(end + 1, end, " extern"));
            else
                Edits.Add(new Edit(declarationStart, declarationStart - 1, "extern "));
        }

        private void AddReplacement(IToken start, IToken stop, string replacement) =>
            Edits.Add(new Edit(start.StartIndex, stop.StopIndex, replacement));
    }

    public static string CreateApiSource(string source, string sourceName)
    {
        var lexer = new CxLexer(new AntlrInputStream(source) { name = sourceName });
        lexer.RemoveErrorListeners();
        var parser = new CxParser(new CommonTokenStream(lexer));
        parser.RemoveErrorListeners();
        var tree = parser.compilationUnit();
        var collector = new BodyCollector();
        collector.Visit(tree);

        var result = source;
        foreach (var edit in collector.Edits
                     .OrderByDescending(edit => edit.Start)
                     .ThenByDescending(edit => edit.End))
        {
            if (edit.Start < 0 || edit.Start > result.Length ||
                (edit.End >= edit.Start && edit.End >= result.Length))
            {
                throw new InvalidOperationException(
                    $"Invalid source edit while creating the API view of '{sourceName}'.");
            }

            if (edit.End < edit.Start)
            {
                result = result.Insert(edit.Start, edit.Replacement);
            }
            else
            {
                result = result.Remove(edit.Start, edit.End - edit.Start + 1)
                    .Insert(edit.Start, edit.Replacement);
            }
        }

        return result;
    }
}

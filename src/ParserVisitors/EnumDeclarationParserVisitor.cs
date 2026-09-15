using Antlr4.Runtime.Misc;
using CxCompiler.Grammar;
using CxCompiler.Model;
using CxCompiler.Model.Common;
using CxCompiler.Model.Expressions;
using CxCompiler.Model.Types;

namespace CxCompiler.ParserVisitors;

public sealed class EnumDeclarationParserVisitor : CxParserBaseVisitor<EnumDeclaration>
{
    private readonly QualifiedIdentifier _namespace;

    public EnumDeclarationParserVisitor(QualifiedIdentifier @namespace)
    {
        _namespace = @namespace;
    }

    public override EnumDeclaration VisitEnumDeclaration(
        [NotNull] CxParser.EnumDeclarationContext context)
    {
        var declaration = new EnumDeclaration(
            context.name.Text,
            _namespace,
            ParseVisibility(context.visibilityModifier()?.GetText()));

        foreach (var memberContext in context.enumDeclarationBody().enumMemberDeclaration())
        {
            declaration.AddMember(new EnumMemberDeclaration(
                memberContext.Identifier().GetText(),
                declaration,
                memberContext.literal() is not { } literal
                    ? null
                    : new LiteralExpression(literal.GetText())));
        }

        return declaration;
    }

    private static Visibility ParseVisibility(string? text)
    {
        return text switch
        {
            "public" => Visibility.Public,
            "protected" => Visibility.Protected,
            "internal" => Visibility.Internal,
            "private" or null => Visibility.Private,
            _ => throw new InternalCompilerException($"Unknown visibility '{text}'."),
        };
    }
}

using Antlr4.Runtime.Misc;
using CxCompiler.Grammar;
using CxCompiler.Model.Expressions;
using CxCompiler.Model.Statements;

namespace CxCompiler.ParserVisitors;

public sealed class StatementParserVisitor : CxParserBaseVisitor<StatementBase>
{
    public static IReadOnlyList<StatementBase> ParseStatements(CxParser.StatementsContext? context)
    {
        if (context is null)
        {
            return [];
        }

        var statements = new List<StatementBase>();
        AddStatements(context, statements);
        return statements;
    }

    public override StatementBase VisitExpressionStatement([NotNull] CxParser.ExpressionStatementContext context)
    {
        var expression = new ExpressionParserVisitor().Visit(context.expression());
        return new ExpressionStatement(expression);
    }

    public override StatementBase VisitReturnStatement([NotNull] CxParser.ReturnStatementContext context)
    {
        var expression = context.expression() is { } expressionContext
            ? new ExpressionParserVisitor().Visit(expressionContext)
            : null;
        return new ReturnStatement(expression);
    }

    public override StatementBase VisitBreakStatement([NotNull] CxParser.BreakStatementContext context)
    {
        return new BreakStatement();
    }

    public override StatementBase VisitContinueStatement([NotNull] CxParser.ContinueStatementContext context)
    {
        return new ContinueStatement();
    }

    public override StatementBase VisitLocalVariableDeclarationStatement(
        [NotNull] CxParser.LocalVariableDeclarationStatementContext context)
    {
        return ParseLocalVariableDeclaration(context.localVariableDeclaration());
    }

    public override StatementBase VisitIfStatement([NotNull] CxParser.IfStatementContext context)
    {
        var embeddedStatements = context.embeddedStatement();
        return new IfStatement(
            ParseExpression(context.expression()),
            Visit(embeddedStatements[0]),
            embeddedStatements.Length > 1 ? Visit(embeddedStatements[1]) : null);
    }

    public override StatementBase VisitSwitchStatement(
        [NotNull] CxParser.SwitchStatementContext context)
    {
        return new SwitchStatement(
            ParseExpression(context.expression()),
            context.switchSection().Select(ParseSwitchSection).ToArray());
    }

    public override StatementBase VisitWhileStatement([NotNull] CxParser.WhileStatementContext context)
    {
        return new WhileStatement(
            ParseExpression(context.expression()),
            Visit(context.embeddedStatement()));
    }

    public override StatementBase VisitDoStatement([NotNull] CxParser.DoStatementContext context)
    {
        return new DoWhileStatement(
            Visit(context.embeddedStatement()),
            ParseExpression(context.expression()));
    }

    public override StatementBase VisitForStatement([NotNull] CxParser.ForStatementContext context)
    {
        var initializer = context.forInitializer();
        var declarationInitializer = initializer?.localVariableDeclaration() is { } declaration
            ? ParseLocalVariableDeclaration(declaration)
            : null;
        var initializerExpressions = initializer?.expression()
            .Select(ParseExpression)
            .ToArray() ?? [];
        var condition = context.forCheck()?.expression() is { } conditionExpression
            ? ParseExpression(conditionExpression)
            : null;
        var iterators = context.forIterator()?.expression()
            .Select(ParseExpression)
            .ToArray() ?? [];

        return new ForStatement(
            declarationInitializer,
            initializerExpressions,
            condition,
            iterators,
            Visit(context.embeddedStatement()));
    }

    public override StatementBase VisitForeachStatement(
        [NotNull] CxParser.ForeachStatementContext context)
    {
        return new ForeachStatement(
            new TypeNameContextVisitor().Visit(context.typeName()),
            context.Identifier().GetText(),
            ParseExpression(context.expression()),
            Visit(context.embeddedStatement()));
    }

    private static LocalVariableDeclarationStatement ParseLocalVariableDeclaration(
        CxParser.LocalVariableDeclarationContext declaration)
    {
        var type = new TypeNameContextVisitor().Visit(declaration.typeName());
        var declarators = new List<LocalVariableDeclarator>();
        AddDeclarators(declaration.variableDeclarations(), declarators);
        return new LocalVariableDeclarationStatement(type, declarators);
    }

    private static SwitchSection ParseSwitchSection(CxParser.SwitchSectionContext context)
    {
        var labels = context.switchLabel().Select(label => new SwitchLabel(
            label.expression() is { } value ? ParseExpression(value) : null,
            label.switchLabelFilter()?.expression() is { } filter
                ? ParseExpression(filter)
                : null)).ToArray();
        return new SwitchSection(labels, ParseStatements(context.statements()));
    }

    private static ExpressionBase ParseExpression(
        CxParser.ExpressionContext context)
    {
        return new ExpressionParserVisitor().Visit(context);
    }

    public override StatementBase VisitEmbeddedStatement([NotNull] CxParser.EmbeddedStatementContext context)
    {
        if (context.LeftBrace() is not null)
        {
            return new BlockStatement(ParseStatements(context.statements()));
        }
        if (context.Semicolon() is not null)
        {
            return new EmptyStatement();
        }

        return base.VisitEmbeddedStatement(context);
    }

    private static void AddStatements(
        CxParser.StatementsContext context,
        ICollection<StatementBase> statements)
    {
        if (context.statements() is { } precedingStatements)
        {
            AddStatements(precedingStatements, statements);
        }

        statements.Add(new StatementParserVisitor().Visit(context.statement()));
    }

    private static void AddDeclarators(
        CxParser.VariableDeclarationsContext context,
        ICollection<LocalVariableDeclarator> declarators)
    {
        if (context.variableDeclarations() is { } precedingDeclarations)
        {
            AddDeclarators(precedingDeclarations, declarators);
        }

        var declaration = context.variableDeclaration();
        var initializer = declaration.expression() is { } expression
            ? new ExpressionParserVisitor().Visit(expression)
            : null;
        declarators.Add(new LocalVariableDeclarator(
            declaration.Identifier().GetText(),
            initializer));
    }
}

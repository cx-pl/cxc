using Antlr4.Runtime.Misc;
using CxCompiler.Grammar;
using CxCompiler.Model.Common;
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
        return new ExpressionStatement(expression).WithSourceSpan(context);
    }

    public override StatementBase VisitReturnStatement([NotNull] CxParser.ReturnStatementContext context)
    {
        var expression = context.expression() is { } expressionContext
            ? new ExpressionParserVisitor().Visit(expressionContext)
            : null;
        return new ReturnStatement(expression).WithSourceSpan(context);
    }

    public override StatementBase VisitBreakStatement([NotNull] CxParser.BreakStatementContext context)
    {
        return new BreakStatement().WithSourceSpan(context);
    }

    public override StatementBase VisitContinueStatement([NotNull] CxParser.ContinueStatementContext context)
    {
        return new ContinueStatement().WithSourceSpan(context);
    }

    public override StatementBase VisitLocalVariableDeclarationStatement(
        [NotNull] CxParser.LocalVariableDeclarationStatementContext context)
    {
        return ParseLocalVariableDeclaration(context.localVariableDeclaration())
            .WithSourceSpan(context);
    }

    public override StatementBase VisitIfStatement([NotNull] CxParser.IfStatementContext context)
    {
        var embeddedStatements = context.embeddedStatement();
        return new IfStatement(
            ParseExpression(context.expression()),
            Visit(embeddedStatements[0]),
            embeddedStatements.Length > 1 ? Visit(embeddedStatements[1]) : null).WithSourceSpan(context);
    }

    public override StatementBase VisitSwitchStatement(
        [NotNull] CxParser.SwitchStatementContext context)
    {
        return new SwitchStatement(
            ParseExpression(context.expression()),
            context.switchSection().Select(ParseSwitchSection).ToArray()).WithSourceSpan(context);
    }

    public override StatementBase VisitWhileStatement([NotNull] CxParser.WhileStatementContext context)
    {
        return new WhileStatement(
            ParseExpression(context.expression()),
            Visit(context.embeddedStatement())).WithSourceSpan(context);
    }

    public override StatementBase VisitDoStatement([NotNull] CxParser.DoStatementContext context)
    {
        return new DoWhileStatement(
            Visit(context.embeddedStatement()),
            ParseExpression(context.expression())).WithSourceSpan(context);
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
            Visit(context.embeddedStatement())).WithSourceSpan(context);
    }

    public override StatementBase VisitForeachStatement(
        [NotNull] CxParser.ForeachStatementContext context)
    {
        return new ForeachStatement(
            new TypeNameContextVisitor().Visit(context.typeName()),
            context.Identifier().GetText(),
            ParseExpression(context.expression()),
            Visit(context.embeddedStatement())).WithSourceSpan(context);
    }

    public override StatementBase VisitThrowStatement([NotNull] CxParser.ThrowStatementContext context)
    {
        return new ThrowStatement(context.expression() is { } expression
            ? ParseExpression(expression)
            : null).WithSourceSpan(context);
    }

    public override StatementBase VisitTryStatement([NotNull] CxParser.TryStatementContext context)
    {
        var catchClauses = context.catchClauses()?.catchClause()
            .Select(ParseCatchClause)
            .ToArray() ?? [];
        return new TryStatement(
            Visit(context.embeddedStatement()),
            catchClauses,
            context.finallyClause()?.embeddedStatement() is { } finallyBody
                ? Visit(finallyBody)
                : null).WithSourceSpan(context);
    }

    private CatchClause ParseCatchClause(CxParser.CatchClauseContext context)
    {
        return new CatchClause(
            new TypeNameContextVisitor().Visit(context.typeName()),
            context.Identifier()?.GetText(),
            context.exceptionFilter()?.expression() is { } filter
                ? ParseExpression(filter)
                : null,
            Visit(context.embeddedStatement()));
    }

    private static LocalVariableDeclarationStatement ParseLocalVariableDeclaration(
        CxParser.LocalVariableDeclarationContext declaration)
    {
        var type = new TypeNameContextVisitor().Visit(declaration.typeName());
        var declarators = new List<LocalVariableDeclarator>();
        AddDeclarators(declaration.variableDeclarations(), declarators);
        return new LocalVariableDeclarationStatement(type, declarators).WithSourceSpan(declaration);
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
            return new BlockStatement(ParseStatements(context.statements())).WithSourceSpan(context);
        }
        if (context.Semicolon() is not null)
        {
            return new EmptyStatement().WithSourceSpan(context);
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

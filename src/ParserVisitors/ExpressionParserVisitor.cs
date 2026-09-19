using Antlr4.Runtime;
using Antlr4.Runtime.Misc;
using Antlr4.Runtime.Tree;
using CxCompiler.Grammar;
using CxCompiler.Model;
using CxCompiler.Model.Common;
using CxCompiler.Model.Expressions;
using CxCompiler.Model.Types;

namespace CxCompiler.ParserVisitors;

public sealed class ExpressionParserVisitor : CxParserBaseVisitor<ExpressionBase>
{
    public static IReadOnlyList<ExpressionBase> ParseArguments(
        CxParser.FunctionInvocationContext context)
    {
        return new ExpressionParserVisitor().GetArguments(context);
    }

    public override ExpressionBase VisitAssignmentExpression([NotNull] CxParser.AssignmentExpressionContext context)
    {
        return new AssignmentExpression(
            Visit(context.primaryExpression()),
            context.assignOperator().GetText(),
            Visit(context.expression()));
    }

    public override ExpressionBase VisitConditionalExpression([NotNull] CxParser.ConditionalExpressionContext context)
    {
        if (context.Question() is null)
        {
            return Visit(context.nullCoalescingExpression());
        }

        var branches = context.throwableExpression();
        return new ConditionalExpression(
            Visit(context.nullCoalescingExpression()),
            VisitConditionalBranch(branches[0]),
            VisitConditionalBranch(branches[1]));
    }

    public override ExpressionBase VisitNullCoalescingExpression([NotNull] CxParser.NullCoalescingExpressionContext context)
    {
        var left = Visit(context.conditionalOrExpression());
        return context.nullCoalescingExpression() is { } right
            ? new NullCoalescingExpression(left, Visit(right))
            : left;
    }
    public override ExpressionBase VisitConditionalOrExpression([NotNull] CxParser.ConditionalOrExpressionContext context) => BuildBinary(context);
    public override ExpressionBase VisitConditionalAndExpression([NotNull] CxParser.ConditionalAndExpressionContext context) => BuildBinary(context);
    public override ExpressionBase VisitInclusiveOrExpression([NotNull] CxParser.InclusiveOrExpressionContext context) => BuildBinary(context);
    public override ExpressionBase VisitExclusiveOrExpression([NotNull] CxParser.ExclusiveOrExpressionContext context) => BuildBinary(context);
    public override ExpressionBase VisitAndExpression([NotNull] CxParser.AndExpressionContext context) => BuildBinary(context);
    public override ExpressionBase VisitEqualityExpression([NotNull] CxParser.EqualityExpressionContext context) => BuildBinary(context);
    public override ExpressionBase VisitRelationalExpression([NotNull] CxParser.RelationalExpressionContext context) => BuildBinary(context);
    public override ExpressionBase VisitShiftExpression([NotNull] CxParser.ShiftExpressionContext context) => BuildBinary(context);
    public override ExpressionBase VisitAdditiveExpression([NotNull] CxParser.AdditiveExpressionContext context) => BuildBinary(context);
    public override ExpressionBase VisitMultiplicativeExpression([NotNull] CxParser.MultiplicativeExpressionContext context) => BuildBinary(context);

    public override ExpressionBase VisitUnaryExpression([NotNull] CxParser.UnaryExpressionContext context)
    {
        if (context.primaryExpression() is { } primaryExpression)
        {
            return Visit(primaryExpression);
        }
        if (context.LeftParen() is not null || context.Await() is not null)
        {
            throw new InternalCompilerException($"Unary expression '{context.GetText()}' is not yet supported.");
        }

        var operatorText = context.children.OfType<ITerminalNode>().First().GetText();
        return new UnaryExpression(operatorText, Visit(context.unaryExpression()));
    }

    public override ExpressionBase VisitPrimaryExpression([NotNull] CxParser.PrimaryExpressionContext context)
    {
        var expression = Visit(context.primaryExpressionStart());

        foreach (var child in context.children.Skip(1))
        {
            expression = child switch
            {
                CxParser.MemberAccessContext memberAccess =>
                    new MemberAccessExpression(expression, memberAccess.Identifier().GetText()),
                CxParser.FunctionInvocationContext invocation =>
                    new InvocationExpression(expression, GetArguments(invocation)),
                CxParser.ArrayExpressionContext arrayAccess =>
                    new ArrayAccessExpression(
                        expression,
                        arrayAccess.expression().Select(Visit).ToArray()),
                ITerminalNode terminal when terminal.GetText() is "++" or "--" =>
                    new UnaryExpression(terminal.GetText(), expression, postfix: true),
                ITerminalNode terminal => throw new InternalCompilerException(
                    $"Expression operator '{terminal.GetText()}' is not yet supported."),
                _ => throw new InternalCompilerException(
                    $"Expression element '{child.GetText()}' is not yet supported."),
            };
        }

        return expression;
    }

    public override ExpressionBase VisitPrimaryExpressionStart([NotNull] CxParser.PrimaryExpressionStartContext context)
    {
        if (context.literal() is { } literal)
        {
            return Visit(literal);
        }

        if (context.Identifier() is { } identifier)
        {
            return new IdentifierExpression(new QualifiedIdentifier(identifier.GetText()));
        }

        if (context.This() is not null)
        {
            return new ThisExpression();
        }

        if (context.expression() is { } parenthesizedExpression)
        {
            return Visit(parenthesizedExpression);
        }

        if (context.arrayCreationExpression() is { } arrayCreation)
        {
            var elementTypeContext = arrayCreation.arrayElementType();
            var elementType = elementTypeContext.builtInType() is { } builtInType
                ? new TypeNameContextVisitor().Visit(builtInType)
                : new NamedType(
                    new QualifiedIdentifierContextVisitor()
                        .Visit(elementTypeContext.qualifiedIdentifier())
                        .ToString(),
                    new GenericParamsParserVisitor()
                        .VisitGenericParams(elementTypeContext.genericParams()));
            return new ArrayCreationExpression(
                elementType,
                Visit(arrayCreation.expression()));
        }

        if (context.New() is not null && context.typeName() is { } typeName)
        {
            return new ObjectCreationExpression(
                new TypeNameContextVisitor().Visit(typeName),
                GetArguments(context.functionInvocation()));
        }

        throw new InternalCompilerException($"Expression '{context.GetText()}' is not yet supported.");
    }

    public override ExpressionBase VisitLiteral([NotNull] CxParser.LiteralContext context)
    {
        return new LiteralExpression(context.GetText());
    }

    private IReadOnlyList<ExpressionBase> GetArguments(CxParser.FunctionInvocationContext context)
    {
        var arguments = new List<ExpressionBase>();
        AddArguments(context.functionInvocationArguments(), arguments);
        return arguments;
    }

    private void AddArguments(
        CxParser.FunctionInvocationArgumentsContext? context,
        ICollection<ExpressionBase> arguments)
    {
        if (context is null)
        {
            return;
        }

        AddArguments(context.functionInvocationArguments(), arguments);
        arguments.Add(Visit(context.functionInvocationArgument().expression()));
    }

    private ExpressionBase BuildBinary(ParserRuleContext context)
    {
        ExpressionBase? expression = null;
        string? operatorText = null;

        foreach (var child in context.children)
        {
            if (child is ITerminalNode terminal)
            {
                operatorText = terminal.GetText();
                continue;
            }
            if (child is not ParserRuleContext operandContext)
            {
                continue;
            }

            var operand = Visit(operandContext);
            expression = expression is null
                ? operand
                : new BinaryExpression(
                    expression,
                    operatorText ?? throw new InternalCompilerException("Binary operator is missing."),
                    operand);
            operatorText = null;
        }

        return expression ?? throw new InternalCompilerException("Binary expression is empty.");
    }

    private ExpressionBase VisitConditionalBranch(CxParser.ThrowableExpressionContext context)
    {
        if (context.expression() is { } expression)
        {
            return Visit(expression);
        }

        throw new InternalCompilerException(
            "Throw expressions in conditional branches are not yet supported.");
    }
}

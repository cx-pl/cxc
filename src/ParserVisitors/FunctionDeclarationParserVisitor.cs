using Antlr4.Runtime.Misc;
using CxCompiler.Grammar;
using CxCompiler.Model.Common;
using CxCompiler.Model.Types;

namespace CxCompiler.ParserVisitors;

public class FunctionDeclarationParserVisitor : CxParserBaseVisitor<FunctionDeclaration>
{
    private readonly QualifiedIdentifier _namespace;
    private readonly ClassDeclaration? _parentClassDeclaration;
    private FunctionDeclaration _functionDeclaration = null!;

    public FunctionDeclarationParserVisitor(QualifiedIdentifier @namespace, ClassDeclaration? parentClassDeclaration)
    {
        _namespace = @namespace;
        _parentClassDeclaration = parentClassDeclaration;
    }

    public override FunctionDeclaration VisitFunctionDeclaration([NotNull] CxParser.FunctionDeclarationContext context)
    {
        var returnType = ResolveGenericType(
            new TypeNameContextVisitor().Visit(context.returnType));
        var memberModifiers = new MemberModifiersParserVisitor().Visit(context.memberModifiers());

        var operatorToken = OperatorNames.GetToken(context.name.Text);
        var genericConstraints = context.genericConstraintClauses()?
            .genericConstraintClause()
            .Select(clause => new GenericTypeConstraint(
                clause.parameter.Text,
                clause.typeName()
                    .Select(type => new TypeNameContextVisitor().Visit(type))
                    .ToArray(),
                clause.Class() is not null
                    ? GenericConstraintKind.ReferenceType
                    : clause.Struct() is not null
                        ? GenericConstraintKind.ValueType
                        : GenericConstraintKind.Type))
            .ToArray() ?? [];
        var conceptConstraints = context.conceptConstraintClause()
            .Select(conceptClause => new GenericConceptConstraint(
                conceptClause.parameter.Text,
                conceptClause.qualifiedIdentifier()
                    .Select(type => new QualifiedIdentifierContextVisitor().Visit(type))
                    .ToArray()))
            .ToArray();
        var unclassifiedGenericTypeArguments = context.functionGenericTypeList() is { } genericList
            ? EnumerateFunctionGenericTypeArguments(
                    genericList.functionGenericTypeArgumentList())
                .Select(argument => new FunctionGenericTypeArgument(
                    new TypeNameContextVisitor().Visit(argument).WithSourceSpan(argument),
                    IsSimpleIdentifierType(argument) ? argument.namedType!.GetText() : null))
                .ToArray()
            : [];
        _functionDeclaration = new FunctionDeclaration(
            operatorToken is null ? context.name.Text : OperatorNames.GetDeclarationName(operatorToken),
            _namespace,
            returnType,
            memberModifiers,
            _parentClassDeclaration,
            context.Const() != null,
            [],
            operatorToken,
            genericConstraints,
            conceptConstraints,
            [],
            unclassifiedGenericTypeArguments).WithSourceSpan(context);
        _functionDeclaration.Annotations = AnnotationParser.Parse(context.annotations());

        foreach (var parameter in EnumerateFunctionParameters(context.functionParameters()))
        {
            VisitFunctionParameter(parameter);
        }

        var functionBody = context.functionBody();
        if (functionBody.LeftBrace() is not null)
        {
            _functionDeclaration.SetBody(
                StatementParserVisitor.ParseStatements(functionBody.statements(), _namespace));
        }

        return _functionDeclaration;
    }

    public override FunctionDeclaration VisitFunctionParameter([NotNull] CxParser.FunctionParameterContext context)
    {
        var type = ResolveGenericType(
            new TypeNameContextVisitor().Visit(context.typeName()));

        _functionDeclaration.AddParameter(FunctionParameterParser.Parse(context, type));

        return base.VisitFunctionParameter(context);
    }

    private static IEnumerable<CxParser.FunctionParameterContext> EnumerateFunctionParameters(
        CxParser.FunctionParametersContext? context)
    {
        if (context is null)
        {
            yield break;
        }
        foreach (var previous in EnumerateFunctionParameters(context.functionParameters()))
        {
            yield return previous;
        }
        if (context.functionParameter() is { } parameter)
        {
            yield return parameter;
        }
    }

    private static IEnumerable<CxParser.TypeNameContext> EnumerateFunctionGenericTypeArguments(
        CxParser.FunctionGenericTypeArgumentListContext context)
    {
        if (context.functionGenericTypeArgumentList() is { } previous)
        {
            foreach (var argument in EnumerateFunctionGenericTypeArguments(previous))
            {
                yield return argument;
            }
        }
        if (context.typeName() is { } type)
        {
            yield return type;
        }
    }

    private static bool IsSimpleIdentifierType(CxParser.TypeNameContext context) =>
        context.Const() is null && context.namedType is not null &&
        context.namedTypeGenericArguments is null && context.arrayDimension().Length == 0 &&
        context.Question() is null &&
        context.namedType.Start.Type == CxParser.Identifier &&
        context.namedType.Stop.Type == CxParser.Identifier &&
        context.namedType.Start.TokenIndex == context.namedType.Stop.TokenIndex;

    private TypeBase ResolveGenericType(TypeBase type)
    {
        if (_parentClassDeclaration is null)
        {
            return type;
        }

        var constType = type as ConstType;
        var effectiveType = constType?.UnderlyingType ?? type;
        if (effectiveType is not NamedType namedType ||
            !_parentClassDeclaration.GenericTypeNames.Contains(namedType.Name))
        {
            return type;
        }

        var genericType = new GenericType(namedType.Name);
        return constType is null ? genericType : new ConstType(genericType);
    }

}

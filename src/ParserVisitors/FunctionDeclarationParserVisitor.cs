using Antlr4.Runtime.Misc;
using CxCompiler.Grammar;
using CxCompiler.Model.Common;
using CxCompiler.Model.Types;

namespace CxCompiler.ParserVisitors;

public class FunctionDeclarationParserVisitor : CxParserBaseVisitor<FunctionDeclaration>
{
    private QualifiedIdentifier _namespace;
    private ClassDeclaration? _parentClassDeclaration = null;
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
        _functionDeclaration = new FunctionDeclaration(
            operatorToken is null ? context.name.Text : OperatorNames.GetDeclarationName(operatorToken),
            _namespace,
            returnType,
            memberModifiers,
            _parentClassDeclaration,
            context.Const() != null,
            new GenericParamsParserVisitor().VisitGenericTypeParameters(
                context.genericTypeParameters()),
            operatorToken).WithSourceSpan(context);

        base.VisitChildren(context);

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

        var funtionParameter = new FunctionParameter(
            context.name.Text,
            type,
            null).WithSourceSpan(context); // TODO: Handle default value if present

        _functionDeclaration.AddParameter(funtionParameter);

        return base.VisitFunctionParameter(context);
    }

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

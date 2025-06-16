using Antlr4.Runtime.Misc;
using CxCompiler.Grammar;
using CxCompiler.Model.Common;
using CxCompiler.Model.Types;

namespace CxCompiler.ParserVisitors;

public class FunctionDeclarationParserVisitor : CxParserBaseVisitor<FunctionDeclaration>
{
    private QualifiedIdentifier _namespace;
    private ClassDeclaration? _parentClassDeclaration = null;
    private FunctionDeclaration _functionDeclaration = null;

    public FunctionDeclarationParserVisitor(QualifiedIdentifier @namespace, ClassDeclaration? parentClassDeclaration)
    {
        _namespace = @namespace;
        _parentClassDeclaration = parentClassDeclaration;
    }

    public override FunctionDeclaration VisitFunctionDeclaration([NotNull] CxParser.FunctionDeclarationContext context)
    {
        var returnType = new TypeNameContextVisitor().Visit(context.returnType);
        var memberModifiers = new MemberModifiersParserVisitor().Visit(context.memberModifiers());

        _functionDeclaration = new FunctionDeclaration(
            context.name.Text,
            _namespace,
            returnType,
            memberModifiers,
            _parentClassDeclaration);

        base.VisitChildren(context);

        return _functionDeclaration;
    }

    public override FunctionDeclaration VisitFunctionParameter([NotNull] CxParser.FunctionParameterContext context)
    {
        var typeNameContextVisitor = new TypeNameContextVisitor();
        var type = typeNameContextVisitor.Visit(context.typeName());

        var funtionParameter = new FunctionParameter(
            context.name.Text,
            type,
            null); // TODO: Handle default value if present

        _functionDeclaration.AddParameter(funtionParameter);

        return base.VisitFunctionParameter(context);
    }
}

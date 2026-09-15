using Antlr4.Runtime.Misc;
using CxCompiler.Grammar;
using CxCompiler.Model.Common;
using CxCompiler.Model.Types;
using CxCompiler.Model.Types.BuiltInTypes;

namespace CxCompiler.ParserVisitors;

public class ConstructorDeclarationParserVisitor : CxParserBaseVisitor<ConstructorDeclaration>
{
    private QualifiedIdentifier _namespace;
    private readonly ClassDeclaration _parentClassDeclaration;
    private ConstructorDeclaration _constructorDeclaration = null!;

    public ConstructorDeclarationParserVisitor(QualifiedIdentifier @namespace, ClassDeclaration parentClassDeclaration)
    {
        _namespace = @namespace;
        _parentClassDeclaration = parentClassDeclaration;
    }

    public override ConstructorDeclaration VisitConstructorDeclaration([NotNull] CxParser.ConstructorDeclarationContext context)
    {
        var memberModifiers = new MemberModifiersParserVisitor().Visit(context.memberModifiers());

        _constructorDeclaration = new ConstructorDeclaration(
            _namespace,
            memberModifiers,
            _parentClassDeclaration);

        base.VisitChildren(context);

        var functionBody = context.functionBody();
        if (functionBody.LeftBrace() is not null)
        {
            _constructorDeclaration.SetBody(
                StatementParserVisitor.ParseStatements(functionBody.statements()));
        }

        return _constructorDeclaration;
    }

    public override ConstructorDeclaration VisitFunctionParameter([NotNull] CxParser.FunctionParameterContext context)
    {
        TypeBase type;
        string typeNameText = context.typeName().GetText();
        if (_parentClassDeclaration.GenericTypeNames.Contains(typeNameText))
        {
            type = BuiltInSystemTypes.Ptr;
        }
        else
        {
            type = new TypeNameContextVisitor().Visit(context.typeName());
        }

        var funtionParameter = new FunctionParameter(
            context.name.Text,
            type,
            null); // TODO: Handle default value if present

        _constructorDeclaration.AddParameter(funtionParameter);

        return base.VisitFunctionParameter(context);
    }
}

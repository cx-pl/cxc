using Antlr4.Runtime.Misc;
using CxCompiler.Grammar;
using CxCompiler.Model.Common;
using CxCompiler.Model.Types;
using CxCompiler.Model.Types.BuiltInTypes;

namespace CxCompiler.ParserVisitors;

public class FieldsDeclarationParserVisitor : CxParserBaseVisitor<FieldDeclaration[]>
{
    private readonly QualifiedIdentifier _className;
    private readonly ClassDeclaration _classDeclaration;
    private TypeBase _fieldType = BuiltInSystemTypes.Void;
    private MemberModifier[] _memberModifiers = [];
    private List<FieldDeclaration> _fieldDeclarations = new();

    public FieldsDeclarationParserVisitor(QualifiedIdentifier className, ClassDeclaration classDeclaration)
    {
        _className = className;
        _classDeclaration = classDeclaration;
    }

    public override FieldDeclaration[] VisitFieldsDeclaration([NotNull] CxParser.FieldsDeclarationContext context)
    {
        _memberModifiers = new MemberModifiersParserVisitor().Visit(context.memberModifiers());
        _fieldType = new TypeNameContextVisitor().Visit(context.typeName());

        base.VisitChildren(context);

        return _fieldDeclarations.ToArray();
    }

    public override FieldDeclaration[] VisitFieldInitializer([NotNull] CxParser.FieldInitializerContext context)
    {
        var fieldDeclaration = new FieldDeclaration(
            context.Identifier().GetText(),
            _fieldType,
            _memberModifiers,
            _classDeclaration);

        _fieldDeclarations.Add(fieldDeclaration);

        return base.VisitFieldInitializer(context);
    }
}
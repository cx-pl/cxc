using Antlr4.Runtime.Misc;
using CxCompiler.Grammar;
using CxCompiler.Model;
using CxCompiler.Model.Types;
using CxCompiler.Model.Types.BuiltInTypes;

namespace CxCompiler.ParserVisitors;

public class PropertyDeclarationParserVisitor : CxParserBaseVisitor<PropertyDeclaration>
{
    private readonly ClassDeclaration _classDeclaration;
    private TypeBase _fieldType = BuiltInSystemTypes.Void;
    private MemberModifier[] _memberModifiers = [];
    private PropertyDeclaration _propertyDeclaration = null!;

    public PropertyDeclarationParserVisitor(ClassDeclaration classDeclaration)
    {
        _classDeclaration = classDeclaration;
    }
    public override PropertyDeclaration VisitPropertyDeclaration([NotNull] CxParser.PropertyDeclarationContext context)
    {
        _memberModifiers = new MemberModifiersParserVisitor().Visit(context.memberModifiers());
        _fieldType = GetFieldTypeName(context);

        _propertyDeclaration = new PropertyDeclaration(
            context.Identifier().GetText(),
            _classDeclaration.FullName,
            _fieldType,
            _memberModifiers,
            _classDeclaration);

        base.VisitChildren(context);

        return _propertyDeclaration;
    }

    public override PropertyDeclaration VisitPropertyAccessorDeclaration([NotNull] CxParser.PropertyAccessorDeclarationContext context)
    {
        string name;
        if (context.Get() != null)
        {
            name = "get";
        }
        else if(context.Set() != null)
        {
            name = "set";
        }
        else
        {
            throw new InternalCompilerException("Unknown propertyo accessor declaration");
        }

        var propertyAccessorDeclaration = new PropertyAccessorDeclaration(
            _propertyDeclaration,
            name,
            context.Extern() != null,
            context.Const() != null);

        if (context.propertyParams() is { } propertyParams)
        {
            foreach (var parameterContext in propertyParams.propertyParam())
            {
                if (name == "set" &&
                    parameterContext.Identifier().GetText() == "value")
                {
                    continue;
                }
                var parameterType = parameterContext.typeName() is { } typeName
                    ? new TypeNameContextVisitor().Visit(typeName)
                    : _fieldType;
                propertyAccessorDeclaration.AddParameter(new FunctionParameter(
                    parameterContext.Identifier().GetText(),
                    parameterType,
                    null));
            }
        }

        _propertyDeclaration.AddAccessor(propertyAccessorDeclaration);

        if (context.propertyAccessorBody() is { } body && body.Semicolon() is null)
        {
            var statements = body.LeftBrace() is not null
                ? StatementParserVisitor.ParseStatements(body.statements())
                : [new StatementParserVisitor().Visit(body.statement())];
            propertyAccessorDeclaration.SetBody(statements);
        }

        return base.VisitPropertyAccessorDeclaration(context);
    }

    private TypeBase GetFieldTypeName(CxParser.PropertyDeclarationContext context)
    {
        var fieldType = new TypeNameContextVisitor().Visit(context.typeName());
        var constType = fieldType as ConstType;
        var effectiveType = constType?.UnderlyingType ?? fieldType;

        if (effectiveType is NamedType namedType &&
            HasGenericTypeDeclaration(_classDeclaration, namedType.Name))
        {
            var genericType = new GenericType(namedType.Name);
            return constType is null ? genericType : new ConstType(genericType);
        }

        return fieldType;
    }

    private static bool HasGenericTypeDeclaration(ClassDeclaration classDeclaration, string name)
    {
        return classDeclaration.GenericTypeNames.Contains(name);
    }
}

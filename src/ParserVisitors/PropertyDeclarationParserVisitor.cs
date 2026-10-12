using Antlr4.Runtime.Misc;
using CxCompiler.Grammar;
using CxCompiler.Model;
using CxCompiler.Model.Common;
using CxCompiler.Model.Expressions;
using CxCompiler.Model.Statements;
using CxCompiler.Model.Types;
using CxCompiler.Model.Types.BuiltInTypes;

namespace CxCompiler.ParserVisitors;

public class PropertyDeclarationParserVisitor : CxParserBaseVisitor<PropertyDeclaration>
{
    private readonly ClassDeclaration _classDeclaration;
    private readonly bool _isExtensionDeclaration;
    private TypeBase _fieldType = BuiltInSystemTypes.Void;
    private MemberModifier[] _memberModifiers = [];
    private PropertyDeclaration _propertyDeclaration = null!;

    public PropertyDeclarationParserVisitor(
        ClassDeclaration classDeclaration,
        bool isExtensionDeclaration = false)
    {
        _classDeclaration = classDeclaration;
        _isExtensionDeclaration = isExtensionDeclaration;
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
            _classDeclaration).WithSourceSpan(context);

        base.VisitChildren(context);

        var autoAccessors = _propertyDeclaration.PropertyAccessorDeclarations
            .Where(accessor => !accessor.Extern && accessor.BodyFunction is null)
            .ToArray();
        if (autoAccessors.Length > 0 && !_isExtensionDeclaration &&
            _classDeclaration.ClassType != ClassType.Interface)
        {
            if (_propertyDeclaration.IsExtension)
            {
                throw new CxCompiler.Model.Errors.CompilationErrorException(
                    "Auto-properties are supported only on classes and structs.",
                    _propertyDeclaration.SourceSpan);
            }

            var backingFieldName = $"__cx_auto_property_{_propertyDeclaration.Name}";
            if (_classDeclaration.MemberDeclarations.Declarations.Any(declaration =>
                declaration.Name == backingFieldName))
            {
                throw new CxCompiler.Model.Errors.CompilationErrorException(
                    $"Generated backing field '{backingFieldName}' conflicts with a member.",
                    _propertyDeclaration.SourceSpan);
            }

            var backingField = new FieldDeclaration(
                backingFieldName,
                _propertyDeclaration.Type,
                _propertyDeclaration.IsStatic ? [MemberModifier.Static] : [],
                _classDeclaration)
            {
                IsAutoPropertyBackingField = true,
            };
            _classDeclaration.MemberDeclarations.AddDeclaration(backingField);

            foreach (var accessor in autoAccessors)
            {
                var backingFieldExpression = new IdentifierExpression(backingFieldName);
                var statements = accessor.Name == "get"
                    ? new StatementBase[]
                    {
                        new ReturnStatement(backingFieldExpression),
                    }
                    : [new ExpressionStatement(new AssignmentExpression(
                        backingFieldExpression,
                        "=",
                        new IdentifierExpression("value")))];
                accessor.SetBody(statements);
            }
        }

        return _propertyDeclaration;
    }

    public override PropertyDeclaration VisitPropertyAccessorDeclaration([NotNull] CxParser.PropertyAccessorDeclarationContext context)
    {
        string name;
        if (context.Get() != null)
        {
            name = "get";
        }
        else if (context.Set() != null)
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
            context.Const() != null).WithSourceSpan(context);

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
                    null).WithSourceSpan(parameterContext));
            }
        }

        _propertyDeclaration.AddAccessor(propertyAccessorDeclaration);

        if (context.propertyAccessorBody() is { } body && body.Semicolon() is null)
        {
            var statements = body.LeftBrace() is not null
                ? StatementParserVisitor.ParseStatements(
                    body.statements(), _classDeclaration.FullName)
                : [new StatementParserVisitor(_classDeclaration.FullName).Visit(body.statement())];
            if (name == "get" && body.Arrow() is not null && statements[0] is
                ExpressionStatement expressionStatement)
            {
                statements = [new ReturnStatement(expressionStatement.Expression)];
            }
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

using Antlr4.Runtime.Misc;
using CxCompiler.Grammar;
using CxCompiler.Model;
using CxCompiler.Model.Common;
using CxCompiler.Model.Types;

namespace CxCompiler.ParserVisitors;

public class ClassDeclarationParserVisitor : CxParserBaseVisitor<DeclarationBase>
{
    private ClassDeclaration? _classDeclaration = null;
    private bool _parseExtensionMembers;
    public DeclarationScope DeclarationScope { get; }

    public ClassDeclarationParserVisitor(DeclarationScope parentDeclarationScope, string? @namespace = null)
    {
        DeclarationScope = new DeclarationScope(parentDeclarationScope);

        if (!string.IsNullOrEmpty(@namespace))
        {
            DeclarationScope.SetNamespace(new QualifiedIdentifier(@namespace));
        }
    }

    public override DeclarationBase VisitClassDeclaration([NotNull] CxParser.ClassDeclarationContext context)
    {
        var classModifiers = new ClassModifiersParserVisitor().VisitClassModifiers(context.classModifiers());
        var (classType, customTypeName) = new ClassTypeParserVisitor().VisitClassType(context.classType());
        var genericParams = new GenericParamsParserVisitor()
            .VisitGenericTypeParameters(context.genericTypeParameters());
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

        if (_classDeclaration == null)
        {
            _classDeclaration = new ClassDeclaration(
                classModifiers,
                context.partial != null,
                classType,
                customTypeName,
                context.name.Text,
                genericParams,
                DeclarationScope,
                genericConstraints,
                conceptConstraints);

            _classDeclaration.Annotations = AnnotationParser.Parse(context.annotations());
            AddBaseTypes(context.@base?.classBaseList());

            base.VisitClassDeclaration(context);
        }
        else
        {
            var innerClassDeclarationParserVisitor = new ClassDeclarationParserVisitor(DeclarationScope, _classDeclaration.Name);
            var innerClassDeclaration = innerClassDeclarationParserVisitor.Visit(context);
            _classDeclaration.MemberDeclarations.AddDeclaration(innerClassDeclaration);
        }

        return _classDeclaration.WithSourceSpan(context);
    }

    public ClassDeclaration ParseExtensionMembers(
        string name,
        QualifiedIdentifier @namespace,
        CxParser.ClassDeclarationBodyContext body,
        string[] genericTypeNames)
    {
        var scope = new DeclarationScope(@namespace: @namespace);
        _classDeclaration = new ClassDeclaration(
            [], false, ClassType.Class, null, name, genericTypeNames, scope);
        _parseExtensionMembers = true;
        Visit(body);
        return _classDeclaration;
    }

    private void AddBaseTypes(CxParser.ClassBaseListContext? context)
    {
        if (context is null)
        {
            return;
        }

        AddBaseTypes(context.list);
        _classDeclaration!.AddBaseType(
            new TypeNameContextVisitor().Visit(context.name));
    }

    public override DeclarationBase VisitFieldsDeclaration([NotNull] CxParser.FieldsDeclarationContext context)
    {
        if (_classDeclaration is null)
        {
            throw new InternalCompilerException("Class declaration is null");
        }

        var fieldsDeclaration = new FieldsDeclarationParserVisitor(_classDeclaration).Visit(context);
        _classDeclaration.MemberDeclarations.AddDeclarations(fieldsDeclaration);
        return _classDeclaration;
    }

    public override DeclarationBase VisitPropertyDeclaration([NotNull] CxParser.PropertyDeclarationContext context)
    {
        if (_classDeclaration is null)
        {
            throw new InternalCompilerException("Class declaration is null");
        }

        var propertyDeclaration = new PropertyDeclarationParserVisitor(
            _classDeclaration, _parseExtensionMembers).Visit(context);
        _classDeclaration.MemberDeclarations.AddDeclaration(propertyDeclaration);
        return _classDeclaration;
    }

    public override DeclarationBase VisitConstructorDeclaration([NotNull] CxParser.ConstructorDeclarationContext context)
    {
        if (_classDeclaration is null)
        {
            throw new InternalCompilerException("Class declaration is null");
        }

        var constructorDeclaration = new ConstructorDeclarationParserVisitor(_classDeclaration.FullName, _classDeclaration).Visit(context);
        _classDeclaration.MemberDeclarations.AddDeclaration(constructorDeclaration);
        return _classDeclaration;
    }

    public override DeclarationBase VisitFunctionDeclaration([NotNull] CxParser.FunctionDeclarationContext context)
    {
        if (_classDeclaration is null)
        {
            throw new InternalCompilerException("Class declaration is null");
        }

        var functionDeclaration = new FunctionDeclarationParserVisitor(_classDeclaration.FullName, _classDeclaration).Visit(context);
        _classDeclaration.MemberDeclarations.AddDeclaration(functionDeclaration);
        return _classDeclaration;
    }
}

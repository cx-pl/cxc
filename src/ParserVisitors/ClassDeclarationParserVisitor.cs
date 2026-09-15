using Antlr4.Runtime.Misc;
using CxCompiler.Grammar;
using CxCompiler.Model;
using CxCompiler.Model.Common;
using CxCompiler.Model.Types;

namespace CxCompiler.ParserVisitors;

public class ClassDeclarationParserVisitor : CxParserBaseVisitor<DeclarationBase>
{
    private ClassDeclaration? _classDeclaration = null;
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
        var genericParams = new GenericParamsParserVisitor().VisitGenericParams(context.genericParams());

        if (_classDeclaration == null)
        {
            _classDeclaration = new ClassDeclaration(
                classModifiers,
                context.partial != null,
                classType,
                customTypeName,
                context.name.Text,
                genericParams,
                // TODO: Get Base types
                DeclarationScope);

            base.VisitClassDeclaration(context);
        }
        else
        {
            var innerClassDeclarationParserVisitor = new ClassDeclarationParserVisitor(DeclarationScope, _classDeclaration.Name);
            var innerClassDeclaration = innerClassDeclarationParserVisitor.Visit(context);
            _classDeclaration.MemberDeclarations.AddDeclaration(innerClassDeclaration);
        }

        return _classDeclaration;
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

        var propertyDeclaration = new PropertyDeclarationParserVisitor(_classDeclaration).Visit(context);
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

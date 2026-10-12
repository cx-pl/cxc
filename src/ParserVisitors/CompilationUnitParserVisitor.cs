using Antlr4.Runtime.Misc;
using CxCompiler.Grammar;
using CxCompiler.Model;
using CxCompiler.Model.Common;
using CxCompiler.Model.Errors;
using CxCompiler.Model.Types;

namespace CxCompiler.ParserVisitors;

public class CompilationUnitParserVisitor : CxParserBaseVisitor<CompilationContext>
{
    private readonly CompilationContext compilationContext;

    public CompilationUnitParserVisitor()
    {
        compilationContext = new CompilationContext();
    }

    public override CompilationContext VisitImportStatement([NotNull] CxParser.ImportStatementContext context)
    {
        var importName = new QualifiedIdentifierContextVisitor().Visit(context.qualifiedIdentifier());

        compilationContext.AddImport(importName);
        return compilationContext;
    }

    public override CompilationContext VisitNamespaceDeclaration([NotNull] CxParser.NamespaceDeclarationContext context)
    {
        var namespaceName = new QualifiedIdentifierContextVisitor().Visit(context.qualifiedIdentifier());

        compilationContext.SetNamespace(namespaceName);
        return compilationContext;
    }

    public override CompilationContext VisitClassDeclaration([NotNull] CxParser.ClassDeclarationContext context)
    {
        var classDeclaration = new ClassDeclarationParserVisitor(compilationContext.DeclarationScope)
            .Visit(context).WithSourceSpan(context);

        compilationContext.DeclarationScope.AddDeclaration(classDeclaration);
        return compilationContext;
    }

    public override CompilationContext VisitFunctionDeclaration([NotNull] CxParser.FunctionDeclarationContext context)
    {
        var functionDeclaration = new FunctionDeclarationParserVisitor(compilationContext.Namespace, null)
            .Visit(context).WithSourceSpan(context);

        compilationContext.DeclarationScope.AddDeclaration(functionDeclaration);
        return compilationContext;
    }

    public override CompilationContext VisitEnumDeclaration([NotNull] CxParser.EnumDeclarationContext context)
    {
        var enumDeclaration = new EnumDeclarationParserVisitor(
            compilationContext.Namespace).Visit(context).WithSourceSpan(context);
        compilationContext.DeclarationScope.AddDeclaration(enumDeclaration);
        return compilationContext;
    }

    public override CompilationContext VisitDelegateDeclaration(
        [NotNull] CxParser.DelegateDeclarationContext context)
    {
        var declaration = new DelegateDeclarationParserVisitor(compilationContext.Namespace)
            .Visit(context);
        compilationContext.DeclarationScope.AddDeclaration(declaration);
        return compilationContext;
    }

    public override CompilationContext VisitAliasDeclaration([NotNull] CxParser.AliasDeclarationContext context)
    {
        var genericTypeNames = new GenericParamsParserVisitor()
            .VisitGenericTypeParameters(context.genericTypeParameters());
        var targetType = GenericTypeSubstitution.BindParameters(
            NamedType.CreateWithTypeArguments(
                context.target.GetText(),
                context.genericTypeArguments() is { } typeArguments
                    ? new GenericTypeArgumentsParserVisitor().VisitGenericTypeArguments(typeArguments)
                    : []),
            genericTypeNames);
        var declaration = new AliasDeclaration(
            context.Identifier().GetText(),
            compilationContext.Namespace,
            genericTypeNames,
            targetType)
            .WithSourceSpan(context);
        compilationContext.DeclarationScope.AddDeclaration(declaration);
        return compilationContext;
    }

    public override CompilationContext VisitExtensionDeclaration([NotNull] CxParser.ExtensionDeclarationContext context)
    {
        var baseList = context.@base.classBaseList();
        if (HasMultipleBaseTypes(baseList))
        {
            throw new CompilationErrorException("An extension declaration must specify exactly one target type.")
                .WithSourceSpan(SourceSpan.From(context));
        }

        var extensionName = context.name.Text;
        var genericTypeNames = new GenericParamsParserVisitor()
            .VisitGenericTypeParameters(context.genericTypeParameters());
        var members = new ClassDeclarationParserVisitor(compilationContext.DeclarationScope)
            .ParseExtensionMembers(extensionName, compilationContext.Namespace, context.body, genericTypeNames);
        var visibility = context.visibilityModifier()?.GetText() switch
        {
            null => Visibility.Private,
            "public" => Visibility.Public,
            "protected" => Visibility.Protected,
            "internal" => Visibility.Internal,
            _ => Visibility.Private,
        };
        var declaration = new ExtensionDeclaration(
            extensionName,
            compilationContext.Namespace,
            visibility,
              genericTypeNames,
              GenericTypeSubstitution.BindParameters(
                  new TypeNameContextVisitor().Visit(baseList.name), genericTypeNames),
            members).WithSourceSpan(context);
        compilationContext.DeclarationScope.AddDeclaration(declaration);
        return compilationContext;
    }

    public override CompilationContext VisitConceptDeclaration(
        [NotNull] CxParser.ConceptDeclarationContext context)
    {
        var requirements = context.functionDeclaration()
            .Select(requirement => new FunctionDeclarationParserVisitor(
                compilationContext.Namespace, null).Visit(requirement))
            .ToArray();
        var propertyRequirements = context.conceptPropertyRequirement()
            .Select(requirement =>
            {
                var accessors = requirement.conceptPropertyAccessorRequirement();
                var getterRequirements = accessors
                    .Where(accessor => accessor.Get() is not null)
                    .ToArray();
                var setterRequirementCount = accessors.Count(accessor =>
                    accessor.Set() is not null);
                if (getterRequirements.Length > 1 || setterRequirementCount > 1)
                {
                    throw new CompilationErrorException(
                        $"Concept property requirement '{requirement.Identifier().GetText()}' " +
                        "declares an accessor more than once.")
                        .WithSourceSpan(SourceSpan.From(requirement));
                }
                return new ConceptPropertyRequirement(
                    requirement.Identifier().GetText(),
                    new TypeNameContextVisitor().Visit(requirement.typeName()),
                    getterRequirements.Length > 0,
                    getterRequirements.Any(accessor => accessor.Const() is not null),
                    accessors.Any(accessor => accessor.Set() is not null));
            })
            .ToArray();
        var visibility = context.visibilityModifier()?.GetText() switch
        {
            "public" => Visibility.Public,
            "protected" => Visibility.Protected,
            "internal" => Visibility.Internal,
            _ => Visibility.Private,
        };
        var declaration = new ConceptDeclaration(
            context.name.Text,
            compilationContext.Namespace,
            visibility,
            requirements,
            propertyRequirements,
            context.@base?.qualifiedIdentifier()
                .Select(identifier =>
                    new QualifiedIdentifierContextVisitor().Visit(identifier))
                .ToArray()).WithSourceSpan(context);
        compilationContext.DeclarationScope.AddDeclaration(declaration);
        return compilationContext;
    }

    private static bool HasMultipleBaseTypes(CxParser.ClassBaseListContext context) =>
        context.list is not null;
}

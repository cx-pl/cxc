using Antlr4.Runtime.Misc;
using CxCompiler.Grammar;
using CxCompiler.Model;

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
        var classDeclaration = new ClassDeclarationParserVisitor(compilationContext.DeclarationScope).Visit(context);

        compilationContext.DeclarationScope.AddDeclaration(classDeclaration);
        return compilationContext;
    }

    public override CompilationContext VisitFunctionDeclaration([NotNull] CxParser.FunctionDeclarationContext context)
    {
        var functionDeclaration = new FunctionDeclarationParserVisitor(compilationContext.Namespace, null).Visit(context);

        compilationContext.DeclarationScope.AddDeclaration(functionDeclaration);
        return compilationContext;
    }

    public override CompilationContext VisitEnumDeclaration([NotNull] CxParser.EnumDeclarationContext context)
    {
        var enumDeclaration = new EnumDeclarationParserVisitor(
            compilationContext.Namespace).Visit(context);
        compilationContext.DeclarationScope.AddDeclaration(enumDeclaration);
        return compilationContext;
    }

    public override CompilationContext VisitTypedefDeclaration([NotNull] CxParser.TypedefDeclarationContext context)
    {
        throw new NotImplementedException("Typedef declarations are not yet supported.");
    }

    public override CompilationContext VisitExtensionDeclaration([NotNull] CxParser.ExtensionDeclarationContext context)
    {
        throw new NotImplementedException("Extension declarations are not yet supported.");
    }
}

using Antlr4.Runtime.Misc;
using CxCompiler.Grammar;
using CxCompiler.Model;
using CxCompiler.Model.Extensions;

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
        compilationContext.AddImport(context.name.ToQualifiedIdentifier());
        return compilationContext;
    }

    public override CompilationContext VisitNamespaceDeclaration([NotNull] CxParser.NamespaceDeclarationContext context)
    {
        compilationContext.SetNamespace(context.name.ToQualifiedIdentifier());
        return compilationContext;
    }

    public override CompilationContext VisitClassDeclaration([NotNull] CxParser.ClassDeclarationContext context)
    {
        var classDeclarationParserVisitor = new ClassDeclarationParserVisitor(compilationContext.DeclarationScope);
        var classDeclaration = classDeclarationParserVisitor.Visit(context);
        compilationContext.DeclarationScope.AddDeclaration(classDeclaration);
        return compilationContext;
    }

    public override CompilationContext VisitFunctionDeclaration([NotNull] CxParser.FunctionDeclarationContext context)
    {
        var functionDeclarationParserVisitor = new FunctionDeclarationParserVisitor(compilationContext.Namespace, null);
        var functionDeclaration = functionDeclarationParserVisitor.Visit(context);
        compilationContext.DeclarationScope.AddDeclaration(functionDeclaration);
        return compilationContext;
    }

    // TODO: Handle typedef declarations
    //public override CompilationContext VisitTypedefDeclaration([NotNull] CxParser.TypedefDeclarationContext context)
    //{
    //    return base.VisitTypedefDeclaration(context);
    //}
}

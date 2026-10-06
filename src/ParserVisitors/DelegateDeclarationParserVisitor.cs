using Antlr4.Runtime.Misc;
using CxCompiler.Grammar;
using CxCompiler.Model;
using CxCompiler.Model.Common;
using CxCompiler.Model.Types;
using CxCompiler.Model.Types.BuiltInTypes;

namespace CxCompiler.ParserVisitors;

public sealed class DelegateDeclarationParserVisitor : CxParserBaseVisitor<DelegateDeclaration>
{
    private readonly QualifiedIdentifier _namespace;

    public DelegateDeclarationParserVisitor(QualifiedIdentifier @namespace)
    {
        _namespace = @namespace;
    }

    public override DelegateDeclaration VisitDelegateDeclaration(
        [NotNull] CxParser.DelegateDeclarationContext context)
    {
        var typeVisitor = new TypeNameContextVisitor();
        var returnType = typeVisitor.Visit(context.returnType);
        var parameterTypes = new List<TypeBase>();
        for (var parameters = context.parameters;
            parameters is not null;
            parameters = parameters.functionParameters())
        {
            if (parameters.functionParameter() is { } parameter)
            {
                parameterTypes.Add(typeVisitor.Visit(parameter.typeName()));
            }
        }
        var signature = new FunctionType(
            QualifiedIdentifier.Empty,
            returnType,
            parameterTypes);
        var visibility = context.visibilityModifier()?.GetText() switch
        {
            "public" => Visibility.Public,
            "protected" => Visibility.Protected,
            "internal" => Visibility.Internal,
            "private" or null => Visibility.Private,
            var value => throw new InternalCompilerException(
                $"Unknown delegate visibility '{value}'."),
        };
        return new DelegateDeclaration(
            context.name.Text,
            _namespace,
            signature,
            visibility).WithSourceSpan(context);
    }
}

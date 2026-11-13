using CxCompiler.Model.Common;
using CxCompiler.Model.Errors;
using CxCompiler.Model.Expressions;
using CxCompiler.Model.Types;

namespace CxCompiler.Semantics;

public sealed partial class SemanticBinder
{
    private void BindNameOf(NameOfExpression expression, FunctionDeclaration function,
        IReadOnlyList<QualifiedIdentifier> imports, LocalScope scope)
    {
        var currentNamespace = GetCurrentNamespace(function);
        ExpressionBase? operand = expression.Operand;
        if (expression.Type is NamedType named)
        {
            if (IsGenericTypeParameterInScope(function, named.Name)) return;
            var parts = named.Name.Split('.');
            operand = new IdentifierExpression(new QualifiedIdentifier(parts[0]));
            foreach (var part in parts.Skip(1)) operand = new MemberAccessExpression(operand, part);
            if (named.TypeArguments.Count > 0)
            {
                ResolveTypeReference(named, currentNamespace, imports);
                return;
            }
        }
        else if (expression.Type is not null)
        {
            ResolveTypeReference(expression.Type, currentNamespace, imports);
            return;
        }
        if (operand is null) throw new CompilationErrorException("nameof requires a name.");
        if (TryResolveTypeExpression(operand, currentNamespace, imports, scope) is not null) return;
        if (TryFlattenIdentifier(operand, out var qualified))
        {
            if (qualified.Parts.Length == 1 &&
                (scope.TryLookup(qualified.Parts[0], out _) ||
                 scope.TryLookupFunctions(qualified.Parts[0], out _) ||
                 FindNamedFunctions(qualified, currentNamespace, imports).Count > 0 ||
                 function.ParentClassDeclaration is { } owner && FindFunctions(owner, qualified.Parts[0]).Count > 0))
                return;
            var namespaces = GetCandidateNames(qualified, currentNamespace, imports);
            if (_aliases.Any(candidate => namespaces.Contains(candidate.Declaration.FullName))) return;
            if (_types.Any(candidate => namespaces.Any(ns =>
                    candidate.Declaration.FullName.ToString().StartsWith(ns.ToString() + ".", StringComparison.Ordinal)))) return;
        }
        if (operand is MemberAccessExpression member)
        {
            var staticType = TryResolveTypeExpression(member.Target, currentNamespace, imports, scope);
            if (staticType?.Declaration is EnumDeclaration enumeration)
            {
                if (enumeration.Members.Any(value => value.Name == member.MemberName)) return;
                throw new CompilationErrorException($"Enum '{enumeration.FullName}' has no member named '{member.MemberName}'.");
            }
            var owner = staticType?.Declaration as ClassDeclaration ??
                GetClassDeclarationIncludingConstructed(BindExpression(member.Target, function, imports, scope));
            if (owner is not null && (FindFunctions(owner, member.MemberName).Count > 0 ||
                FindField(owner, member.MemberName, staticOnly: null) is not null ||
                FindProperty(owner, member.MemberName, staticOnly: null) is not null)) return;
        }
        // Binding validates names and member access; nameof's operand is never emitted.
        BindExpression(operand, function, imports, scope);
    }
}

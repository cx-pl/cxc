using CxCompiler.Model.Common;
using CxCompiler.Model.Errors;

namespace CxCompiler.Model.Types;

public sealed class ConceptDeclaration : DeclarationBase
{
    private readonly IReadOnlyList<FunctionDeclaration> _declaredOperatorRequirements;
    private readonly IReadOnlyList<FunctionDeclaration> _declaredMethodRequirements;
    private readonly IReadOnlyList<ConceptPropertyRequirement> _declaredPropertyRequirements;

    public Visibility Visibility { get; }
    public IReadOnlyList<QualifiedIdentifier> BaseConceptNames { get; }
    public IReadOnlyList<FunctionDeclaration> DeclaredOperatorRequirements =>
        _declaredOperatorRequirements;
    public IReadOnlyList<FunctionDeclaration> DeclaredMethodRequirements =>
        _declaredMethodRequirements;
    public IReadOnlyList<ConceptPropertyRequirement> DeclaredPropertyRequirements =>
        _declaredPropertyRequirements;
    public IReadOnlyList<FunctionDeclaration> OperatorRequirements { get; private set; }
    public IReadOnlyList<FunctionDeclaration> MethodRequirements { get; private set; }
    public IReadOnlyList<ConceptPropertyRequirement> PropertyRequirements { get; private set; }

    public static string GetMethodBindingKey(
        FunctionDeclaration requirement,
        IReadOnlyList<TypeBase>? genericTypeArguments = null)
    {
        var genericParameters = requirement.GenericTypeNames
            .Select((name, index) => (name, index))
            .ToDictionary(pair => pair.name, pair => pair.index, StringComparer.Ordinal);
        var parameterTypes = string.Join(",", requirement.Parameters.Select(
            parameter => GetTypeBindingName(parameter.ParameterType, genericParameters)));
        var genericArguments = genericTypeArguments is { Count: > 0 }
            ? $"<{string.Join(",", genericTypeArguments.Select(type => GetTypeBindingName(type)))}>"
            : string.Empty;
        var arity = requirement.GenericTypeNames.Length > 0
            ? $"`{requirement.GenericTypeNames.Length}"
            : string.Empty;
        return $"method:{requirement.Name}{arity}{genericArguments}({parameterTypes})";
    }

    private static string GetTypeBindingName(
        TypeBase type,
        IReadOnlyDictionary<string, int>? genericParameters = null) => type switch
    {
        ConstType constant => $"const {GetTypeBindingName(constant.UnderlyingType, genericParameters)}",
        GenericType generic when genericParameters?.TryGetValue(generic.Name, out var index) == true =>
            $"`{index}",
        ArrayType array => $"{GetTypeBindingName(array.ElementType, genericParameters)}[]",
        NullableType nullable => $"{GetTypeBindingName(nullable.UnderlyingType, genericParameters)}?",
        NamedType named when named.TypeArguments.Count > 0 =>
            $"{named.Name}<{string.Join(",", named.TypeArguments.Select(argument =>
                GetTypeBindingName(argument, genericParameters)))}>",
        _ => type.Name,
    };

    public ConceptDeclaration(
        string name,
        QualifiedIdentifier @namespace,
        Visibility visibility,
        IReadOnlyList<FunctionDeclaration> operatorRequirements,
        IReadOnlyList<ConceptPropertyRequirement>? propertyRequirements = null,
        IReadOnlyList<QualifiedIdentifier>? baseConceptNames = null)
        : base("concept", @namespace, name)
    {
        Visibility = visibility;
        BaseConceptNames = baseConceptNames ?? [];
        if (operatorRequirements.Any(requirement => requirement.Body is not null))
        {
            throw new CompilationErrorException(
                $"Concept '{name}' requirements cannot have bodies.");
        }

        _declaredOperatorRequirements = operatorRequirements
            .Where(requirement => requirement.OperatorToken is not null)
            .ToArray();
        _declaredMethodRequirements = operatorRequirements
            .Where(requirement => requirement.OperatorToken is null)
            .ToArray();
        _declaredPropertyRequirements = propertyRequirements ?? [];
        OperatorRequirements = _declaredOperatorRequirements;
        MethodRequirements = _declaredMethodRequirements;
        PropertyRequirements = _declaredPropertyRequirements;
    }

    public void InheritRequirements(IEnumerable<ConceptDeclaration> baseConcepts)
    {
        var bases = baseConcepts.ToArray();
        OperatorRequirements = bases.SelectMany(concept => concept.OperatorRequirements)
            .Distinct()
            .Concat(_declaredOperatorRequirements).ToArray();
        MethodRequirements = bases.SelectMany(concept => concept.MethodRequirements)
            .Distinct()
            .Concat(_declaredMethodRequirements).ToArray();
        PropertyRequirements = bases.SelectMany(concept => concept.PropertyRequirements)
            .Distinct<ConceptPropertyRequirement>(ReferenceEqualityComparer.Instance)
            .Concat(_declaredPropertyRequirements).ToArray();
    }
}

public sealed record ConceptPropertyRequirement(
    string Name,
    TypeBase Type,
    bool HasGetter,
    bool GetterConst,
    bool HasSetter);

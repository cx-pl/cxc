using CxCompiler.Model.Types;
using CxCompiler.Semantics;

namespace CxCompiler.Model.Expressions;

public sealed class ObjectCreationExpression : ExpressionBase
{
    private readonly Dictionary<string, (NamedType RequestedType, FunctionSymbol Constructor)> _closedConstructions =
        new(StringComparer.Ordinal);

    public TypeBase RequestedType { get; private set; }

    private readonly List<ExpressionBase> _arguments;
    public IReadOnlyList<ExpressionBase> Arguments => _arguments;

    public FunctionSymbol? Constructor { get; private set; }

    public ClassType? ClassType { get; private set; }

    public string? TemporaryName { get; private set; }

    public ObjectCreationExpression(
        TypeBase requestedType,
        IReadOnlyList<ExpressionBase> arguments)
    {
        RequestedType = requestedType;
        _arguments = arguments.ToList();
    }

    public void BindConstructor(
        FunctionSymbol constructor,
        ClassType classType,
        string temporaryName)
    {
        Constructor = constructor;
        ClassType = classType;
        TemporaryName = temporaryName;
    }

    public void BindGenericTypeParameters(IReadOnlyCollection<string> parameterNames) =>
        RequestedType = GenericTypeSubstitution.BindParameters(RequestedType, parameterNames);

    public void AddDefaultArguments(IEnumerable<ExpressionBase> arguments) =>
        _arguments.AddRange(arguments);

    public void BindClosedConstruction(
        string callerSpecialization,
        NamedType requestedType,
        FunctionSymbol constructor) =>
        _closedConstructions[callerSpecialization] = (requestedType, constructor);

    public bool TryGetClosedConstruction(
        string callerSpecialization,
        out NamedType requestedType,
        out FunctionSymbol constructor)
    {
        if (_closedConstructions.TryGetValue(callerSpecialization, out var construction))
        {
            requestedType = construction.RequestedType;
            constructor = construction.Constructor;
            return true;
        }
        requestedType = null!;
        constructor = null!;
        return false;
    }

    public void ApplyClosedConstruction(string callerSpecialization)
    {
        if (_closedConstructions.TryGetValue(callerSpecialization, out var construction))
        {
            RequestedType = construction.RequestedType;
            Constructor = construction.Constructor;
        }
    }

    public void RestoreConstruction(TypeBase requestedType, FunctionSymbol? constructor)
    {
        RequestedType = requestedType;
        Constructor = constructor;
    }
}

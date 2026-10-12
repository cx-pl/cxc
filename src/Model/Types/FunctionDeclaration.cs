using CxCompiler.Model.Common;

using CxCompiler.Model.Statements;
using CxCompiler.Model.Expressions;

namespace CxCompiler.Model.Types;

public class FunctionDeclaration : DeclarationBase
{
    public TypeBase ReturnType { get; internal set; }
    public string[] GenericTypeNames { get; private set; }
    public IReadOnlyList<TypeBase> SpecializationTypeArguments { get; private set; }
    public IReadOnlyList<FunctionGenericTypeArgument> UnclassifiedGenericTypeArguments { get; private set; }
    public bool HasUnclassifiedGenericTypeArguments =>
        UnclassifiedGenericTypeArguments.Count != 0;
    public bool IsGenericSpecialization => SpecializationTypeArguments.Count > 0;
    public IReadOnlyList<GenericTypeConstraint> GenericConstraints { get; private set; }
    public IReadOnlyList<GenericConceptConstraint> ConceptConstraints { get; }

    private List<FunctionParameter> _parameters = new();
    public IReadOnlyList<FunctionParameter> Parameters => _parameters.AsReadOnly();

    public MemberModifier[] MemberModifiers { get; }

    public bool Const { get; }

    public string? OperatorToken { get; }
    public string? LocalCName { get; private set; }
    public LambdaExpression? LambdaOwner { get; private set; }
    public bool IsExtension { get; internal set; }
    public TypeBase? ExtensionTargetType { get; internal set; }

    public bool IsStatic => ParentClassDeclaration is null || MemberModifiers.Contains(MemberModifier.Static);

    public ClassDeclaration? ParentClassDeclaration { get; }

    public IReadOnlyList<StatementBase>? Body { get; private set; }

    public int? VirtualSlotIndex { get; private set; }
    public FunctionDeclaration? VirtualContract { get; private set; }

    public FunctionDeclaration(
        string name, QualifiedIdentifier @namespace, TypeBase returnType,
        MemberModifier[] memberModifiers, ClassDeclaration? parentClassDeclaration,
        bool @const = false,
        string[]? genericTypeNames = null,
        string? operatorToken = null,
        IReadOnlyList<GenericTypeConstraint>? genericConstraints = null,
        IReadOnlyList<GenericConceptConstraint>? conceptConstraints = null,
        IReadOnlyList<TypeBase>? specializationTypeArguments = null,
        IReadOnlyList<FunctionGenericTypeArgument>? unclassifiedGenericTypeArguments = null)
        : base("function", @namespace, name)
    {
        ReturnType = returnType;
        MemberModifiers = memberModifiers;
        ParentClassDeclaration = parentClassDeclaration;
        Const = @const;
        GenericTypeNames = genericTypeNames ?? [];
        OperatorToken = operatorToken;
        GenericConstraints = genericConstraints ?? [];
        ConceptConstraints = conceptConstraints ?? [];
        SpecializationTypeArguments = specializationTypeArguments ?? [];
        UnclassifiedGenericTypeArguments = unclassifiedGenericTypeArguments ?? [];
    }

    public void ClassifyGenericTypeArgumentsAsParameters(IEnumerable<string> names)
    {
        GenericTypeNames = names.ToArray();
        SpecializationTypeArguments = [];
        UnclassifiedGenericTypeArguments = [];
    }

    public void ClassifyGenericTypeArgumentsAsSpecialization(IEnumerable<TypeBase> arguments)
    {
        GenericTypeNames = [];
        SpecializationTypeArguments = arguments.ToArray();
        UnclassifiedGenericTypeArguments = [];
    }

    public void AddParameter(FunctionParameter parameter)
    {
        _parameters.Add(parameter);
    }

    public void SetGenericConstraints(IReadOnlyList<GenericTypeConstraint> constraints)
    {
        GenericConstraints = constraints;
    }

    public void SetBody(IReadOnlyList<StatementBase> body)
    {
        Body = body;
    }

    public void SetLocalCName(string cName)
    {
        LocalCName = cName;
    }

    public void SetLambdaOwner(LambdaExpression lambda) => LambdaOwner = lambda;

    public void BindVirtualSlot(int slotIndex, FunctionDeclaration contract)
    {
        VirtualSlotIndex = slotIndex;
        VirtualContract = contract;
    }
}

public sealed record FunctionGenericTypeArgument(TypeBase Type, string? SimpleIdentifier);

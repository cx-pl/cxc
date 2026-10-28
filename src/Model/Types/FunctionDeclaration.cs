using CxCompiler.Model.Common;

using CxCompiler.Model.Statements;
using CxCompiler.Model.Expressions;

namespace CxCompiler.Model.Types;

public class FunctionDeclaration : DeclarationBase
{
    public TypeBase ReturnType { get; internal set; }
    public NativeImportMetadata? NativeImport { get; internal set; }
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
    public bool IsAsync { get; }
    public bool IsPendingAwaitRelay { get; private set; }
    public bool IsIterator { get; private set; }
    public bool IsIteratorFactory { get; private set; }
    public TypeBase? IteratorElementType { get; private set; }
    public ClassDeclaration? GeneratedIteratorType { get; private set; }

    public string? OperatorToken { get; }
    public string? LocalCName { get; private set; }
    public LambdaExpression? LambdaOwner { get; private set; }
    public bool IsExtension { get; internal set; }
    public TypeBase? ExtensionTargetType { get; internal set; }

    public bool IsStatic => ParentClassDeclaration is null || MemberModifiers.Contains(MemberModifier.Static);

    public ClassDeclaration? ParentClassDeclaration { get; }

    public IReadOnlyList<StatementBase>? Body { get; private set; }
    public IReadOnlyDictionary<LocalVariableSymbol, string> IteratorCaptureCells { get; private set; } =
        new Dictionary<LocalVariableSymbol, string>();

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
        IReadOnlyList<FunctionGenericTypeArgument>? unclassifiedGenericTypeArguments = null,
        bool isAsync = false)
        : base("function", @namespace, name)
    {
        ReturnType = returnType;
        MemberModifiers = memberModifiers;
        ParentClassDeclaration = parentClassDeclaration;
        Const = @const;
        IsAsync = isAsync;
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

    public void SetIteratorElementType(TypeBase elementType)
    {
        IsIterator = true;
        IteratorElementType = elementType;
    }

    public void MarkIteratorFactory(TypeBase elementType, ClassDeclaration iteratorType)
    {
        SetIteratorElementType(elementType);
        IsIteratorFactory = true;
        GeneratedIteratorType = iteratorType;
    }

    public void MarkPendingAwaitRelay() => IsPendingAwaitRelay = true;


    public void SetLocalCName(string cName)
    {
        LocalCName = cName;
    }

    public void SetLambdaOwner(LambdaExpression lambda) => LambdaOwner = lambda;

    public void SetIteratorCaptureCells(
        IReadOnlyDictionary<LocalVariableSymbol, string> captureCells) =>
        IteratorCaptureCells = captureCells;

    public void BindVirtualSlot(int slotIndex, FunctionDeclaration contract)
    {
        VirtualSlotIndex = slotIndex;
        VirtualContract = contract;
    }
}

public sealed record FunctionGenericTypeArgument(TypeBase Type, string? SimpleIdentifier);

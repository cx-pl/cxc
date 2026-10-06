using CxCompiler.Model.Common;

using CxCompiler.Model.Statements;
using CxCompiler.Model.Expressions;

namespace CxCompiler.Model.Types;

public class FunctionDeclaration : DeclarationBase
{
    public TypeBase ReturnType { get; internal set; }
    public string[] GenericTypeNames { get; }

    private List<FunctionParameter> _parameters = new();
    public IReadOnlyList<FunctionParameter> Parameters => _parameters.AsReadOnly();

    public MemberModifier[] MemberModifiers { get; }

    public bool Const { get; }

    public string? OperatorToken { get; }
    public string? LocalCName { get; private set; }
    public LambdaExpression? LambdaOwner { get; private set; }

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
        string? operatorToken = null)
        : base("function", @namespace, name)
    {
        ReturnType = returnType;
        MemberModifiers = memberModifiers;
        ParentClassDeclaration = parentClassDeclaration;
        Const = @const;
        GenericTypeNames = genericTypeNames ?? [];
        OperatorToken = operatorToken;
    }

    public void AddParameter(FunctionParameter parameter)
    {
        _parameters.Add(parameter);
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

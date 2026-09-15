using CxCompiler.Model.Common;

using CxCompiler.Model.Statements;

namespace CxCompiler.Model.Types;

public class FunctionDeclaration : DeclarationBase
{
    public TypeBase ReturnType { get; }

    private List<FunctionParameter> _parameters = new();
    public IReadOnlyList<FunctionParameter> Parameters => _parameters.AsReadOnly();

    public MemberModifier[] MemberModifiers { get; }

    public bool Const { get; }

    public bool IsStatic => ParentClassDeclaration is null || MemberModifiers.Contains(MemberModifier.Static);
    
    public ClassDeclaration? ParentClassDeclaration { get; }

    public IReadOnlyList<StatementBase>? Body { get; private set; }

    public int? VirtualSlotIndex { get; private set; }
    public FunctionDeclaration? VirtualContract { get; private set; }

    public FunctionDeclaration(
        string name, QualifiedIdentifier @namespace, TypeBase returnType, 
        MemberModifier[] memberModifiers, ClassDeclaration? parentClassDeclaration,
        bool @const = false)
        : base("function", @namespace, name)
    {
        ReturnType = returnType;
        MemberModifiers = memberModifiers;
        ParentClassDeclaration = parentClassDeclaration;
        Const = @const;
    }

    public void AddParameter(FunctionParameter parameter)
    {
        _parameters.Add(parameter);
    }

    public void SetBody(IReadOnlyList<StatementBase> body)
    {
        Body = body;
    }

    public void BindVirtualSlot(int slotIndex, FunctionDeclaration contract)
    {
        VirtualSlotIndex = slotIndex;
        VirtualContract = contract;
    }
}

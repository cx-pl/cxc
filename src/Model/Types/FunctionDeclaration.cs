using CxCompiler.Model.Common;

namespace CxCompiler.Model.Types;

public class FunctionDeclaration : DeclarationBase
{
    public TypeBase ReturnType { get; }

    public List<FunctionParameter> _parameters = new();
    public List<FunctionParameter> Parameters => _parameters;

    public MemberModifier[] MemberModifiers { get; }

    public bool IsStatic => MemberModifiers.Contains(MemberModifier.Static);
    
    public ClassDeclaration? ParentClassDeclaration { get; }

    // TODO: public StatementBase[] Body { get; }

    public FunctionDeclaration(
        string name, QualifiedIdentifier @namespace, TypeBase returnType, 
        MemberModifier[] memberModifiers, ClassDeclaration? parentClassDeclaration)
        : base(name, @namespace)
    {
        ReturnType = returnType;
        MemberModifiers = memberModifiers;
        ParentClassDeclaration = parentClassDeclaration;
    }

    public void AddParameter(FunctionParameter parameter)
    {
        _parameters.Add(parameter);
    }
}

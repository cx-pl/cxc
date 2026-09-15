using CxCompiler.Model.Common;

namespace CxCompiler.Model.Types;

public class PropertyAccessorDeclaration : DeclarationBase
{
    public PropertyDeclaration ParentPropertyDeclaration { get; }

    public bool Extern { get; }
    public bool Const { get; }

    private List<FunctionParameter> _parameters = new();
    public List<FunctionParameter> Parameters => _parameters;

    // TODO: public StatementBase[] Body { get; }

    public PropertyAccessorDeclaration(
        PropertyDeclaration parentPropertyDeclaration,
        string name, bool @extern, bool @const)
        : base("propertyAccessor", parentPropertyDeclaration.FullName, name)
    {
        ParentPropertyDeclaration = parentPropertyDeclaration;
        Extern = @extern;
        Const = @const;
    }

    public void AddParameter(FunctionParameter parameter)
    {
        _parameters.Add(parameter);
    }
}

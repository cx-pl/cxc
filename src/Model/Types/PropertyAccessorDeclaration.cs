using CxCompiler.Model.Common;
using CxCompiler.Model.Statements;
using CxCompiler.Model.Types.BuiltInTypes;

namespace CxCompiler.Model.Types;

public class PropertyAccessorDeclaration : DeclarationBase
{
    public PropertyDeclaration ParentPropertyDeclaration { get; }

    public bool Extern { get; }
    public bool Const { get; }

    private readonly List<FunctionParameter> _parameters = [];
    public List<FunctionParameter> Parameters => _parameters;

    public FunctionDeclaration? BodyFunction { get; private set; }
    public IReadOnlyList<StatementBase>? Body => BodyFunction?.Body;

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

    public void SetBody(IReadOnlyList<StatementBase> body)
    {
        var returnType = Name == "set"
            ? BuiltInSystemTypes.Void
            : ParentPropertyDeclaration.Type;
        var function = new FunctionDeclaration(
            Name,
            ParentPropertyDeclaration.FullName,
            returnType,
            ParentPropertyDeclaration.MemberModifiers,
            ParentPropertyDeclaration.ParentClassDeclaration,
            Const);
        foreach (var parameter in Parameters)
        {
            function.AddParameter(parameter);
        }
        if (Name == "set")
        {
            function.AddParameter(new FunctionParameter(
                "value",
                ParentPropertyDeclaration.Type,
                null));
        }
        function.SetBody(body);
        BodyFunction = function;
    }
}

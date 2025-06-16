using CxCompiler.Model.Common;
using CxCompiler.Model.Literals;

namespace CxCompiler.Model.Types;

public class FunctionParameter : DeclarationBase
{
    public TypeBase Type { get; }
    public LiteralBase? DefaultValue { get; }

    public FunctionParameter(string name, TypeBase type, LiteralBase? defaultValue)
        : base(name)
    {
        Type = type;
        DefaultValue = defaultValue;
    }
}
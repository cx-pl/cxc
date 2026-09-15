using CxCompiler.Model.Common;
using CxCompiler.Model.Literals;

namespace CxCompiler.Model.Types;

public class FunctionParameter : DeclarationBase
{
    public TypeBase ParameterType { get; }
    public LiteralBase? DefaultValue { get; }

    public FunctionParameter(string name, TypeBase parameterType, LiteralBase? defaultValue)
        : base("functionParameter", name)
    {
        ParameterType = parameterType;
        DefaultValue = defaultValue;
    }
}
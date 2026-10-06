using CxCompiler.Model.Common;
using CxCompiler.Model.Literals;
using CxCompiler.Model.Expressions;

namespace CxCompiler.Model.Types;

public class FunctionParameter : DeclarationBase
{
    public TypeBase ParameterType { get; internal set; }
    public LiteralBase? DefaultValue { get; }
    public LocalVariableSymbol? LocalSymbol { get; private set; }

    public FunctionParameter(string name, TypeBase parameterType, LiteralBase? defaultValue)
        : base("functionParameter", name)
    {
        ParameterType = parameterType;
        DefaultValue = defaultValue;
    }

    public void BindLocalSymbol(LocalVariableSymbol symbol) => LocalSymbol = symbol;
}

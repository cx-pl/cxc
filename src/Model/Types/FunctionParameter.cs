using CxCompiler.Model.Common;
using CxCompiler.Model.Expressions;

namespace CxCompiler.Model.Types;

public class FunctionParameter : DeclarationBase
{
    public TypeBase ParameterType { get; internal set; }
    public string? DefaultValueText { get; }
    public bool DefaultValueIsIdentifier { get; }
    public LocalVariableSymbol? LocalSymbol { get; private set; }

    public FunctionParameter(
        string name,
        TypeBase parameterType,
        string? defaultValueText,
        bool defaultValueIsIdentifier = false)
        : base("functionParameter", name)
    {
        ParameterType = parameterType;
        DefaultValueText = defaultValueText;
        DefaultValueIsIdentifier = defaultValueIsIdentifier;
    }

    public void BindLocalSymbol(LocalVariableSymbol symbol) => LocalSymbol = symbol;
}

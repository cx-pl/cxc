using CxCompiler.Model.Types;

namespace CxCompiler.Model.Expressions;

public sealed class LambdaCapture(LocalVariableSymbol local, string environmentFieldName)
{
    public LocalVariableSymbol Local { get; } = local;
    public string EnvironmentFieldName { get; } = environmentFieldName;
}

public sealed class LocalVariableSymbol(
    string name,
    TypeBase type,
    LambdaExpression? ownerLambda = null,
    bool isThis = false,
    string? cellName = null)
{
    public string Name { get; } = name;
    public TypeBase Type { get; } = type;
    public LambdaExpression? OwnerLambda { get; } = ownerLambda;
    public bool IsThis { get; } = isThis;
    public bool IsCaptured { get; private set; }
    public string CellName { get; } = cellName ?? "__cx_generated_this_cell";

    public void MarkCaptured() => IsCaptured = true;
}

namespace CxCompiler.Model.Expressions;

using CxCompiler.Semantics;

public sealed class UnaryExpression : ExpressionBase
{
    public string Operator { get; }
    public ExpressionBase Operand { get; }
    public bool Postfix { get; }
    public FunctionSymbol? OperatorSymbol { get; private set; }
    public int OperatorBaseDepth { get; private set; }
    public string? OperatorTemporaryName { get; private set; }
    public string? OperatorOldValueTemporaryName { get; private set; }

    public UnaryExpression(string @operator, ExpressionBase operand, bool postfix = false)
    {
        Operator = @operator;
        Operand = operand;
        Postfix = postfix;
    }

    public void BindOperator(
        FunctionSymbol symbol,
        int baseDepth,
        string? temporaryName = null,
        string? oldValueTemporaryName = null)
    {
        OperatorSymbol = symbol;
        OperatorBaseDepth = baseDepth;
        OperatorTemporaryName = temporaryName;
        OperatorOldValueTemporaryName = oldValueTemporaryName;
    }
}

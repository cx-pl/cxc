namespace CxCompiler.Model.Expressions;

using CxCompiler.Semantics;

public sealed class BinaryExpression : ExpressionBase
{
    public ExpressionBase Left { get; }
    public string Operator { get; }
    public ExpressionBase Right { get; }
    public FunctionSymbol? OperatorSymbol { get; private set; }
    public int OperatorBaseDepth { get; private set; }
    public string? OperatorReceiverTemporaryName { get; private set; }

    public BinaryExpression(ExpressionBase left, string @operator, ExpressionBase right)
    {
        Left = left;
        Operator = @operator;
        Right = right;
    }

    public void BindOperator(FunctionSymbol symbol, int baseDepth, string? receiverTemporaryName = null)
    {
        OperatorSymbol = symbol;
        OperatorBaseDepth = baseDepth;
        OperatorReceiverTemporaryName = receiverTemporaryName;
    }
}

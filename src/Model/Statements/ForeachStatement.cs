using CxCompiler.Model.Types;
using CxCompiler.Model.Expressions;
using CxCompiler.Semantics;

namespace CxCompiler.Model.Statements;

public sealed class ForeachStatement : StatementBase
{
    public TypeBase DeclaredType { get; }
    public string VariableName { get; }
    public ExpressionBase Collection { get; }
    public StatementBase Body { get; }
    public TypeBase? VariableType { get; private set; }
    public LocalVariableSymbol? VariableSymbol { get; private set; }
    public LocalVariableSymbol? EnumeratorSymbol { get; private set; }
    public InvocationExpression? GetEnumeratorInvocation { get; private set; }
    public InvocationExpression? MoveNextInvocation { get; private set; }
    public ExpressionBase? CurrentExpression { get; private set; }

    public ForeachStatement(
        TypeBase declaredType,
        string variableName,
        ExpressionBase collection,
        StatementBase body)
    {
        DeclaredType = declaredType;
        VariableName = variableName;
        Collection = collection;
        Body = body;
    }

    public void BindVariableType(TypeBase type, LocalVariableSymbol symbol)
    {
        VariableType = type;
        VariableSymbol = symbol;
    }

    public void BindEnumerator(
        TypeBase variableType,
        LocalVariableSymbol variableSymbol,
        LocalVariableSymbol enumeratorSymbol,
        InvocationExpression getEnumeratorInvocation,
        InvocationExpression moveNextInvocation,
        ExpressionBase currentExpression)
    {
        VariableType = variableType;
        VariableSymbol = variableSymbol;
        EnumeratorSymbol = enumeratorSymbol;
        GetEnumeratorInvocation = getEnumeratorInvocation;
        MoveNextInvocation = moveNextInvocation;
        CurrentExpression = currentExpression;
    }
}

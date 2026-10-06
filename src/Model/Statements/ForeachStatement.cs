using CxCompiler.Model.Types;
using CxCompiler.Model.Expressions;

namespace CxCompiler.Model.Statements;

public sealed class ForeachStatement : StatementBase
{
    public TypeBase DeclaredType { get; }
    public string VariableName { get; }
    public ExpressionBase Collection { get; }
    public StatementBase Body { get; }
    public TypeBase? VariableType { get; private set; }
    public LocalVariableSymbol? VariableSymbol { get; private set; }

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
}

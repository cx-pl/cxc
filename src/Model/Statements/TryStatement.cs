using CxCompiler.Model.Expressions;
using CxCompiler.Model.Types;

namespace CxCompiler.Model.Statements;

public sealed class TryStatement : StatementBase
{
    public StatementBase Body { get; }
    public IReadOnlyList<CatchClause> CatchClauses { get; }
    public StatementBase? FinallyBody { get; }

    public TryStatement(
        StatementBase body,
        IReadOnlyList<CatchClause> catchClauses,
        StatementBase? finallyBody)
    {
        Body = body;
        CatchClauses = catchClauses;
        FinallyBody = finallyBody;
    }
}

public sealed class CatchClause
{
    public TypeBase ExceptionType { get; }
    public string? VariableName { get; }
    public ExpressionBase? Filter { get; }
    public StatementBase Body { get; }

    public CatchClause(
        TypeBase exceptionType,
        string? variableName,
        ExpressionBase? filter,
        StatementBase body)
    {
        ExceptionType = exceptionType;
        VariableName = variableName;
        Filter = filter;
        Body = body;
    }
}

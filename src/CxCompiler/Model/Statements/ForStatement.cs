using CxCompiler.Model.Expressions;

namespace CxCompiler.Model.Statements;

public sealed class ForStatement : StatementBase
{
    public LocalVariableDeclarationStatement? DeclarationInitializer { get; }
    public IReadOnlyList<ExpressionBase> InitializerExpressions { get; }
    public ExpressionBase? Condition { get; }
    public IReadOnlyList<ExpressionBase> Iterators { get; }
    public StatementBase Body { get; }

    public ForStatement(
        LocalVariableDeclarationStatement? declarationInitializer,
        IReadOnlyList<ExpressionBase> initializerExpressions,
        ExpressionBase? condition,
        IReadOnlyList<ExpressionBase> iterators,
        StatementBase body)
    {
        DeclarationInitializer = declarationInitializer;
        InitializerExpressions = initializerExpressions;
        Condition = condition;
        Iterators = iterators;
        Body = body;
    }
}

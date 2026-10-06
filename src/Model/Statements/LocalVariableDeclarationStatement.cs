using CxCompiler.Model.Types;
using CxCompiler.Model.Expressions;

namespace CxCompiler.Model.Statements;

public sealed class LocalVariableDeclarationStatement : StatementBase
{
    public TypeBase DeclaredType { get; internal set; }
    public IReadOnlyList<LocalVariableDeclarator> Declarators { get; }

    public LocalVariableDeclarationStatement(
        TypeBase declaredType,
        IReadOnlyList<LocalVariableDeclarator> declarators)
    {
        DeclaredType = declaredType;
        Declarators = declarators;
    }
}

public sealed class LocalVariableDeclarator
{
    public string Name { get; }
    public ExpressionBase? Initializer { get; }
    public TypeBase? Type { get; private set; }
    public LocalVariableSymbol? Symbol { get; private set; }

    public LocalVariableDeclarator(string name, ExpressionBase? initializer)
    {
        Name = name;
        Initializer = initializer;
    }

    public void BindType(TypeBase type, LocalVariableSymbol symbol)
    {
        Type = type;
        Symbol = symbol;
    }
}

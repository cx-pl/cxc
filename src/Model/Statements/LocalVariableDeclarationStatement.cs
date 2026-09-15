using CxCompiler.Model.Expressions;
using CxCompiler.Model.Types;

namespace CxCompiler.Model.Statements;

public sealed class LocalVariableDeclarationStatement : StatementBase
{
    public TypeBase DeclaredType { get; }
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

    public LocalVariableDeclarator(string name, ExpressionBase? initializer)
    {
        Name = name;
        Initializer = initializer;
    }

    public void BindType(TypeBase type)
    {
        Type = type;
    }
}

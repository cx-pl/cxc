using CxCompiler.Model.Types;
using CxCompiler.Semantics;

namespace CxCompiler.Model.Statements;

public sealed class LocalFunctionDeclarationStatement(
    FunctionDeclaration function) : StatementBase
{
    public FunctionDeclaration Function { get; } = function;
    public FunctionSymbol? Symbol { get; private set; }

    public void BindSymbol(FunctionSymbol symbol) => Symbol = symbol;
}

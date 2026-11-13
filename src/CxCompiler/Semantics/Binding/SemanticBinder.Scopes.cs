using CxCompiler.Model.Common;
using CxCompiler.Model.Expressions;
using CxCompiler.Model.Types;

namespace CxCompiler.Semantics;

public sealed partial class SemanticBinder
{

    private sealed class LocalScope
    {
        private readonly LocalScope? _parent;
        private readonly LocalScope _root;
        private readonly Dictionary<string, LocalVariableSymbol> _locals = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<FunctionSymbol>> _functions = new(StringComparer.Ordinal);
        private int _nextCellId;

        public LambdaExpression? CurrentLambda { get; }

        public LocalScope(LocalScope? parent = null, LambdaExpression? currentLambda = null)
        {
            _parent = parent;
            _root = parent?._root ?? this;
            CurrentLambda = currentLambda ?? parent?.CurrentLambda;
        }

        public bool TryDeclare(string name, TypeBase type, out LocalVariableSymbol symbol)
        {
            return TryDeclare(name, type, null, out symbol);
        }

        public bool TryDeclare(
            string name,
            TypeBase type,
            LocalVariableSymbol? existingSymbol,
            out LocalVariableSymbol symbol)
        {
            symbol = existingSymbol ?? new LocalVariableSymbol(
                name,
                type,
                CurrentLambda,
                cellName: $"__cx_generated_cell_{++_root._nextCellId}");
            return !_functions.ContainsKey(name) && _locals.TryAdd(name, symbol);
        }

        public bool TryDeclareFunction(string name, FunctionSymbol symbol)
        {
            if (_locals.ContainsKey(name))
            {
                return false;
            }
            if (!_functions.TryGetValue(name, out var functions))
            {
                functions = [];
                _functions.Add(name, functions);
            }
            functions.Add(symbol);
            return true;
        }

        public bool TryLookupFunctions(string name, out IReadOnlyList<FunctionSymbol> functions)
        {
            if (_functions.TryGetValue(name, out var found))
            {
                functions = found;
                return true;
            }
            if (_parent is not null)
            {
                return _parent.TryLookupFunctions(name, out functions);
            }
            functions = [];
            return false;
        }

        public IReadOnlyList<FunctionSymbol> GetDeclaredFunctions(string name) =>
            _functions.TryGetValue(name, out var functions) ? functions : [];

        public IReadOnlyList<(string Name, FunctionSymbol Symbol)> GetVisibleFunctions()
        {
            var result = new Dictionary<string, List<FunctionSymbol>>(StringComparer.Ordinal);
            if (_parent is not null)
            {
                foreach (var (name, symbol) in _parent.GetVisibleFunctions())
                {
                    if (!result.TryGetValue(name, out var inherited))
                    {
                        inherited = [];
                        result.Add(name, inherited);
                    }
                    inherited.Add(symbol);
                }
            }
            foreach (var (name, symbols) in _functions)
            {
                result[name] = symbols.ToList();
            }
            return result.SelectMany(item => item.Value.Select(symbol => (item.Key, symbol))).ToArray();
        }

        public bool TryLookup(string name, out TypeBase type)
        {
            if (TryLookupSymbol(name, out var symbol))
            {
                type = symbol.Type;
                return true;
            }

            type = null!;
            return false;
        }

        public bool TryLookupSymbol(string name, out LocalVariableSymbol symbol)
        {
            if (_locals.TryGetValue(name, out symbol!))
            {
                return true;
            }

            if (_parent is not null)
            {
                return _parent.TryLookupSymbol(name, out symbol);
            }

            symbol = null!;
            return false;
        }
    }

    private sealed record TypeSymbol(
        string ModuleName,
        ClassDeclaration Declaration,
        TypeBase Type);

    private sealed record EnumTypeSymbol(
        string ModuleName,
        EnumDeclaration Declaration,
        TypeBase Type);

    private sealed record DelegateTypeSymbol(
        string ModuleName,
        DelegateDeclaration Declaration);

    private sealed record AliasSymbol(
        string ModuleName,
        AliasDeclaration Declaration,
        IReadOnlyList<QualifiedIdentifier> Imports);

    private sealed record ResolvedTypeSymbol(
        string ModuleName,
        DeclarationBase Declaration,
        TypeBase Type);

    private sealed record ConstructorSymbol(
        TypeBase ConstructedType,
        ClassType ClassType,
        FunctionSymbol Constructor);

    private sealed record PropertyReference(
        PropertySymbol Property,
        ExpressionBase? Receiver,
        bool ReceiverIsConst,
        int ReceiverBaseDepth,
        TypeBase? ReceiverType);
}

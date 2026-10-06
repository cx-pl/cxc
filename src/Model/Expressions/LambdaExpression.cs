using CxCompiler.Model.Statements;
using CxCompiler.Model.Types;
using CxCompiler.Model.Types.BuiltInTypes;

namespace CxCompiler.Model.Expressions;

public sealed class LambdaExpression(
    IReadOnlyList<LambdaParameter> parameters,
    ExpressionBase? expressionBody,
    IReadOnlyList<StatementBase>? statementBody) : ExpressionBase
{
    public IReadOnlyList<LambdaParameter> Parameters { get; } = parameters;
    public ExpressionBase? ExpressionBody { get; } = expressionBody;
    public IReadOnlyList<StatementBase>? StatementBody { get; } = statementBody;
    public FunctionDeclaration? GeneratedFunction { get; private set; }
    public IReadOnlyList<LambdaCapture> Captures => _captures;
    public ClassDeclaration? EnclosingClassDeclaration { get; private set; }
    public LocalVariableSymbol? ThisSymbol { get; private set; }
    public LambdaExpression? EnclosingLambda { get; private set; }

    private readonly List<LambdaCapture> _captures = [];

    public LambdaCapture Capture(LocalVariableSymbol local)
    {
        var existing = _captures.FirstOrDefault(capture => ReferenceEquals(capture.Local, local));
        if (existing is not null)
        {
            return existing;
        }

        local.MarkCaptured();
        var capture = new LambdaCapture(local, $"capture_{_captures.Count}");
        _captures.Add(capture);
        return capture;
    }

    public void SetEnclosingContext(
        ClassDeclaration? enclosingClassDeclaration,
        LocalVariableSymbol? thisSymbol,
        LambdaExpression? enclosingLambda)
    {
        EnclosingClassDeclaration = enclosingClassDeclaration;
        ThisSymbol = thisSymbol;
        EnclosingLambda = enclosingLambda;
    }

    public void Bind(FunctionDeclaration function, FunctionType functionType)
    {
        GeneratedFunction = function;
        function.SetLambdaOwner(this);
        SetInferredType(functionType);
    }
}

public sealed class LambdaParameter(string name, TypeBase? type)
{
    public string Name { get; } = name;
    public TypeBase? Type { get; internal set; } = type;
    public LocalVariableSymbol? LocalSymbol { get; internal set; }
}

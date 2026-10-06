using Antlr4.Runtime;
using CxCompiler.Model.Types;

namespace CxCompiler.Grammar;

/// <summary>
/// Presents an <c>operator &lt;token&gt;</c> declaration name as a synthetic
/// identifier so the existing function-declaration grammar can parse it.
/// </summary>
public sealed class OperatorAwareCxLexer : CxLexer
{
    private readonly Queue<IToken> _pendingTokens = new();

    public OperatorAwareCxLexer(ICharStream input)
        : base(input)
    {
    }

    public override IToken NextToken()
    {
        if (_pendingTokens.TryDequeue(out var pending))
        {
            return pending;
        }

        var token = base.NextToken();
        if (token.Type != Operator)
        {
            return token;
        }

        var skipped = new List<IToken>();
        var candidate = base.NextToken();
        while (candidate.Type != TokenConstants.EOF &&
            candidate.Channel != TokenConstants.DefaultChannel)
        {
            skipped.Add(candidate);
            candidate = base.NextToken();
        }

        if (OperatorNames.IsSupported(candidate.Text ?? string.Empty))
        {
            return new CommonToken(token)
            {
                Type = Identifier,
                Text = OperatorNames.GetDeclarationName(candidate.Text!),
                StopIndex = candidate.StopIndex,
            };
        }

        foreach (var hidden in skipped)
        {
            _pendingTokens.Enqueue(hidden);
        }
        _pendingTokens.Enqueue(candidate);
        return token;
    }
}

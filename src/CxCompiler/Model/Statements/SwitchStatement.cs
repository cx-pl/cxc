using CxCompiler.Model.Expressions;

namespace CxCompiler.Model.Statements;

public sealed class SwitchStatement : StatementBase
{
    public ExpressionBase Expression { get; }
    public IReadOnlyList<SwitchSection> Sections { get; }
    public string? SelectorTemporaryName { get; private set; }
    public string? CaseIndexTemporaryName { get; private set; }

    public SwitchStatement(ExpressionBase expression, IReadOnlyList<SwitchSection> sections)
    {
        Expression = expression;
        Sections = sections;
    }

    public void BindTemporaries(string selectorTemporaryName, string caseIndexTemporaryName)
    {
        SelectorTemporaryName = selectorTemporaryName;
        CaseIndexTemporaryName = caseIndexTemporaryName;
    }
}

public sealed class SwitchSection
{
    public IReadOnlyList<SwitchLabel> Labels { get; }
    public IReadOnlyList<StatementBase> Statements { get; }

    public SwitchSection(
        IReadOnlyList<SwitchLabel> labels,
        IReadOnlyList<StatementBase> statements)
    {
        Labels = labels;
        Statements = statements;
    }
}

public sealed class SwitchLabel
{
    public bool IsDefault => Value is null;
    public ExpressionBase? Value { get; }
    public ExpressionBase? Filter { get; }

    public SwitchLabel(ExpressionBase? value, ExpressionBase? filter = null)
    {
        Value = value;
        Filter = filter;
    }
}

namespace CxCompiler.Model.Expressions;

public sealed class MemberAccessExpression : ExpressionBase
{
    public ExpressionBase Target { get; }
    public string MemberName { get; }

    public MemberAccessExpression(ExpressionBase target, string memberName)
    {
        Target = target;
        MemberName = memberName;
    }
}

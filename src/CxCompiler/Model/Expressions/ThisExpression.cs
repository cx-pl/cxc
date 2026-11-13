namespace CxCompiler.Model.Expressions;

public sealed class ThisExpression : ExpressionBase
{
    public LambdaCapture? TargetCapture { get; private set; }

    public void BindCapture(LambdaCapture capture) => TargetCapture = capture;
}

namespace CxCompiler.Model.Expressions;

public abstract class ExpressionBase : CxCompiler.Model.Common.IHasSourceSpan
{
    public CxCompiler.Model.Common.SourceSpan? SourceSpan { get; set; }
    public CxCompiler.Model.Types.TypeBase? InferredType { get; private set; }
    public int? InterfaceUpcastSlotIndex { get; private set; }

    public void SetInferredType(CxCompiler.Model.Types.TypeBase type)
    {
        InferredType = type;
    }

    public void BindInterfaceUpcast(int slotIndex)
    {
        InterfaceUpcastSlotIndex = slotIndex;
    }
}

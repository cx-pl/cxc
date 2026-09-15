namespace CxCompiler.Model.Expressions;

public abstract class ExpressionBase
{
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

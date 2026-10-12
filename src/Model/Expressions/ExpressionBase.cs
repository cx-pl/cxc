namespace CxCompiler.Model.Expressions;

public abstract class ExpressionBase : CxCompiler.Model.Common.IHasSourceSpan
{
    public CxCompiler.Model.Common.SourceSpan? SourceSpan { get; set; }
    public CxCompiler.Model.Types.TypeBase? InferredType { get; private set; }
    public CxCompiler.Model.Types.TypeBase? ClosedGenericType { get; private set; }
    public int? InterfaceUpcastSlotIndex { get; private set; }

    public void SetInferredType(CxCompiler.Model.Types.TypeBase type)
    {
        InferredType = type;
    }

    public void SetClosedGenericType(CxCompiler.Model.Types.TypeBase? type)
    {
        ClosedGenericType = type;
    }

    public void RestoreTypes(
        CxCompiler.Model.Types.TypeBase? inferredType,
        CxCompiler.Model.Types.TypeBase? closedGenericType)
    {
        InferredType = inferredType;
        ClosedGenericType = closedGenericType;
    }

    public void BindInterfaceUpcast(int slotIndex)
    {
        InterfaceUpcastSlotIndex = slotIndex;
    }
}

namespace CxCompiler.Model.Types;

public sealed record NativeTypeMetadata(string CType, string Header, bool Opaque,
    string Kind, int Size, int Alignment)
{
    public bool OwnLayout { get; init; }
    public int Pack { get; init; }
}

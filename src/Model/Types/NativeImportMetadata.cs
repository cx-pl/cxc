namespace CxCompiler.Model.Types;

public sealed record NativeImportMetadata(string Library, string EntryPoint,
    string CallingConvention, string ErrorPolicy, IReadOnlyList<string> Headers)
{
    public bool Blocking { get; init; }
}

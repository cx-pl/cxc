using CxCompiler.Model.Common;

namespace CxCompiler.Model.Types.BuiltInTypes;

// Sizes are deliberately not inferred from the compiler host. Generated sizeof
// and SDK types describe the C compiler's target ABI.
public sealed class NativeScalarType(string name, string cType, bool signed)
    : IntTypeBase(name, new QualifiedIdentifier("System", "Interop"))
{
    public string CType { get; } = cType;
    public override int Size => 0;
    public override bool Signed => signed;
}

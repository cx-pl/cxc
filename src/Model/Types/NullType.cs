using CxCompiler.Model.Common;

namespace CxCompiler.Model.Types;

public sealed class NullType : TypeBase
{
    public NullType()
        : base("null", QualifiedIdentifier.Empty)
    {
    }
}

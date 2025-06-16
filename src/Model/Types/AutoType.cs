using CxCompiler.Model.Common;

namespace CxCompiler.Model.Types;

public class AutoType : TypeBase
{
    public bool Const { get; }

    public AutoType(bool @const)
        : base(string.Empty, QualifiedIdentifier.Empty)
    {
        Const = @const;
    }
}
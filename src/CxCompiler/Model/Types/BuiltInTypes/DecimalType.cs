using CxCompiler.Model.Common;

namespace CxCompiler.Model.Types.BuiltInTypes;

public sealed class DecimalType(QualifiedIdentifier @namespace) : ValueTypeBase("Decimal", @namespace)
{
    public override int Size => 16;
}

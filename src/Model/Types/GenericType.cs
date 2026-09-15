using CxCompiler.Model.Common;

namespace CxCompiler.Model.Types;

public class GenericType : TypeBase
{
    public GenericType(string name) 
        : base(name, QualifiedIdentifier.Empty)
    {
    }
}
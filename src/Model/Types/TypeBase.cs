using CxCompiler.Model.Common;

namespace CxCompiler.Model.Types;

public abstract class TypeBase : DeclarationBase
{
    protected TypeBase(string name, QualifiedIdentifier @namespace) 
        : base(name, @namespace)
    {
    }
}

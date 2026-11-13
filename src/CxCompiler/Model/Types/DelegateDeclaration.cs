using CxCompiler.Model.Common;
using CxCompiler.Model.Types.BuiltInTypes;

namespace CxCompiler.Model.Types;

public sealed class DelegateDeclaration : DeclarationBase
{
    public Visibility Visibility { get; }
    public FunctionType Signature { get; internal set; }

    public DelegateDeclaration(
        string name,
        QualifiedIdentifier @namespace,
        FunctionType signature,
        Visibility visibility)
        : base("delegate", @namespace, name)
    {
        Signature = signature;
        Visibility = visibility;
    }
}

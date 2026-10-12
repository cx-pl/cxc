using CxCompiler.Model.Common;

namespace CxCompiler.Model.Types;

public sealed class AliasDeclaration : DeclarationBase
{
    public string[] GenericTypeNames { get; }
    public TypeBase TargetType { get; }

    public AliasDeclaration(
        string name,
        QualifiedIdentifier @namespace,
        string[] genericTypeNames,
        TypeBase targetType)
        : base("alias", @namespace, name)
    {
        GenericTypeNames = genericTypeNames;
        TargetType = targetType;
    }
}

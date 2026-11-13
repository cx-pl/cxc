using CxCompiler.Model.Common;

namespace CxCompiler.Model.Types;

public sealed class ExtensionDeclaration : DeclarationBase
{
    public Visibility Visibility { get; }
    public string[] GenericTypeNames { get; }
    public TypeBase TargetType { get; }
    public ClassDeclaration Members { get; }

    public ExtensionDeclaration(
        string name,
        QualifiedIdentifier @namespace,
        Visibility visibility,
        string[] genericTypeNames,
        TypeBase targetType,
        ClassDeclaration members)
        : base("extension", @namespace, name)
    {
        Visibility = visibility;
        GenericTypeNames = genericTypeNames;
        TargetType = targetType;
        Members = members;
    }
}

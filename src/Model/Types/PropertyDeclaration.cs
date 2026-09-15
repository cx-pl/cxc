using CxCompiler.Model.Common;

namespace CxCompiler.Model.Types;

public class PropertyDeclaration : DeclarationBase
{
    public TypeBase Type { get; }
    public MemberModifier[] MemberModifiers { get; }
    public bool IsStatic => MemberModifiers.Contains(MemberModifier.Static);
    public ClassDeclaration ParentClassDeclaration { get; }

    private List<PropertyAccessorDeclaration> _propertyAccessorDeclarations = new();
    public IReadOnlyCollection<PropertyAccessorDeclaration> PropertyAccessorDeclarations => _propertyAccessorDeclarations;

    public PropertyDeclaration(
        string name, QualifiedIdentifier @namespace, TypeBase type,
        MemberModifier[] memberModifiers, ClassDeclaration parentClassDeclaration)
        : base("property", @namespace, name)
    {
        Type = type;
        MemberModifiers = memberModifiers;
        ParentClassDeclaration = parentClassDeclaration;
    }

    public void AddAccessor(PropertyAccessorDeclaration accessor)
    {
        _propertyAccessorDeclarations.Add(accessor);
    }
}

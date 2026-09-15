using CxCompiler.Model.Common;

namespace CxCompiler.Model.Types;

public class FieldDeclaration : DeclarationBase
{
    public TypeBase Type { get; }
    public MemberModifier[] MemberModifiers { get; }
    public bool IsStatic => MemberModifiers.Contains(MemberModifier.Static);
    public ClassDeclaration ParentClassDeclaration { get; }
    public CxCompiler.Model.Expressions.ExpressionBase? Initializer { get; }

    public FieldDeclaration(
        string name, TypeBase type,
        MemberModifier[] memberModifiers, ClassDeclaration parentClassDeclaration,
        CxCompiler.Model.Expressions.ExpressionBase? initializer = null)
        : base("field", parentClassDeclaration.FullName, name)
    {
        Type = type;
        MemberModifiers = memberModifiers;
        ParentClassDeclaration = parentClassDeclaration;
        Initializer = initializer;
    }
}

using CxCompiler.Model.Common;

namespace CxCompiler.Model.Types;

public class ConstructorDeclaration : FunctionDeclaration
{
    public ConstructorInitializer? Initializer { get; private set; }

    public ConstructorDeclaration(
        QualifiedIdentifier @namespace, 
        MemberModifier[] memberModifiers, ClassDeclaration parentClassDeclaration)
        : base("__constructor", @namespace, BuiltInTypes.BuiltInSystemTypes.Void, memberModifiers, parentClassDeclaration)
    {
    }

    public void SetInitializer(ConstructorInitializer initializer)
    {
        Initializer = initializer;
    }
}

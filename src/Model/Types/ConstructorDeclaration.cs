using CxCompiler.Model.Common;

namespace CxCompiler.Model.Types;

public class ConstructorDeclaration : FunctionDeclaration
{
    public ConstructorDeclaration(
        QualifiedIdentifier @namespace, 
        MemberModifier[] memberModifiers, ClassDeclaration parentClassDeclaration)
        : base("__constructor", @namespace, BuiltInTypes.BuiltInSystemTypes.Void, memberModifiers, parentClassDeclaration)
    {
    }
}

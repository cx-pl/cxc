using CxCompiler.Model.Common;
using CxCompiler.Model.Errors;

namespace CxCompiler.Model.Types;

public class ClassDeclaration : DeclarationBase
{
    public Visibility Visibility { get; }
    public ClassType ClassType { get; }
    public string? CustomClassTypeName { get; }
    public bool IsPartial { get; }
    public bool IsAbstract { get; }
    public bool IsFinal { get; }
    public bool IsStatic { get; }

    private readonly List<QualifiedIdentifier> _baseTypes = [];
    public IReadOnlyList<QualifiedIdentifier> BaseTypes => _baseTypes.AsReadOnly();

    public DeclarationScope MemberDeclarations { get; }

    public ClassDeclaration(
        IReadOnlyCollection<ClassModifier> classModifiers,
        bool partial,
        ClassType classType,
        string? customClassTypeName,
        string name,
        DeclarationScope declarationScope)
        : base(name, declarationScope.FullNamespace)
    {        
        Visibility = Visibility.Private;
        IsPartial = partial;
        ClassType = classType;
        CustomClassTypeName = customClassTypeName;
        MemberDeclarations = declarationScope;

        foreach (var modifier in classModifiers)
        {
            switch (modifier)
            {
                case ClassModifier.Public:
                case ClassModifier.Protected:
                case ClassModifier.Internal:
                    Visibility = MapVisibility(modifier);
                    break;

                case ClassModifier.Abstract:
                    ValidateModifier(modifier);
                    IsAbstract = true;
                    break;

                case ClassModifier.Final:
                    ValidateModifier(modifier);
                    IsFinal = true;
                    break;

                case ClassModifier.Static:
                    ValidateModifier(modifier);
                    IsStatic = true;
                    break;
            }
        }

        Visibility MapVisibility(ClassModifier classModifier)
        {
            if (Visibility != Visibility.Private)
            {
                throw new CompilationErrorException($"Type '{name}' cannot be both '{classModifier.ToString().ToLower()}' and '{Visibility.ToString().ToLower()}'");
            }

            // TODO: Allow Visibility.Private, ClassModifier.Internal and ClassModifier.Protected only in nested types

            return classModifier switch
            {
                ClassModifier.Public => Visibility.Public,
                ClassModifier.Protected => Visibility.Protected,
                ClassModifier.Private => Visibility.Private,
                ClassModifier.Internal => Visibility.Internal,
                _ => throw new InternalCompilerException($"Invalid visibility modifier '{classModifier.ToString().ToLower()}'")
            };
        }

        void ValidateModifier(ClassModifier classModifier)
        {
            if (IsAbstract || IsFinal || IsStatic)
            {
                throw new CompilationErrorException($"Invalid modifier '{classModifier.ToString().ToLower()}'");
            }
        }
    }

    public void AddBaseType(QualifiedIdentifier baseType)
    {
        _baseTypes.Add(baseType);
    }
}

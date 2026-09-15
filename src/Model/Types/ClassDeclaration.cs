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
    public string[] GenericTypeNames { get; }

    private readonly List<TypeBase> _baseTypes = [];
    public IReadOnlyList<TypeBase> BaseTypes => _baseTypes.AsReadOnly();
    public TypeBase? BaseClassType { get; private set; }
    public ClassDeclaration? BaseClassDeclaration { get; private set; }

    private readonly List<ClassDeclaration> _baseInterfaces = [];
    public IReadOnlyList<ClassDeclaration> BaseInterfaces => _baseInterfaces;

    private readonly List<VirtualMethodSlot> _virtualMethodSlots = [];
    public IReadOnlyList<VirtualMethodSlot> VirtualMethodSlots => _virtualMethodSlots;

    private readonly List<InterfaceDispatchSlot> _interfaceDispatchSlots = [];
    public IReadOnlyList<InterfaceDispatchSlot> InterfaceDispatchSlots => _interfaceDispatchSlots;

    private readonly List<ClassDeclaration> _interfaceUpcastTargets = [];
    public IReadOnlyList<ClassDeclaration> InterfaceUpcastTargets => _interfaceUpcastTargets;

    private readonly List<InterfaceDispatchTable> _interfaceDispatchTables = [];
    public IReadOnlyList<InterfaceDispatchTable> InterfaceDispatchTables =>
        _interfaceDispatchTables;

    public DeclarationScope MemberDeclarations { get; }

    public ClassDeclaration(
        IReadOnlyCollection<ClassModifier> classModifiers,
        bool partial,
        ClassType classType,
        string? customClassTypeName,
        string name,
        string[] genericTypeNames,
        DeclarationScope declarationScope)
        : base("class", declarationScope.FullNamespace, name)
    {        
        Visibility = Visibility.Private;
        IsPartial = partial;
        ClassType = classType;
        CustomClassTypeName = customClassTypeName;
        MemberDeclarations = declarationScope;
        GenericTypeNames = genericTypeNames;

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

    public void AddBaseType(TypeBase baseType)
    {
        _baseTypes.Add(baseType);
    }

    public void SetBaseClass(TypeBase baseType, ClassDeclaration? declaration)
    {
        BaseClassType = baseType;
        BaseClassDeclaration = declaration;
    }

    public void AddBaseInterface(ClassDeclaration declaration)
    {
        _baseInterfaces.Add(declaration);
    }

    public void SetVirtualMethodSlots(IEnumerable<VirtualMethodSlot> slots)
    {
        _virtualMethodSlots.Clear();
        _virtualMethodSlots.AddRange(slots);
    }

    public void SetInterfaceDispatchSlots(IEnumerable<InterfaceDispatchSlot> slots)
    {
        _interfaceDispatchSlots.Clear();
        _interfaceDispatchSlots.AddRange(slots);
    }

    public void SetInterfaceUpcastTargets(IEnumerable<ClassDeclaration> interfaces)
    {
        _interfaceUpcastTargets.Clear();
        _interfaceUpcastTargets.AddRange(interfaces);
    }

    public void SetInterfaceDispatchTables(IEnumerable<InterfaceDispatchTable> tables)
    {
        _interfaceDispatchTables.Clear();
        _interfaceDispatchTables.AddRange(tables);
    }
}

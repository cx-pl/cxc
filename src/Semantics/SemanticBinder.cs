using CxCompiler.Model;
using CxCompiler.Model.Common;
using CxCompiler.Model.Errors;
using CxCompiler.Model.Expressions;
using CxCompiler.Model.Project;
using CxCompiler.Model.Statements;
using CxCompiler.Model.Types;
using CxCompiler.Model.Types.BuiltInTypes;

namespace CxCompiler.Semantics;

public sealed class SemanticBinder
{
    private readonly List<FunctionSymbol> _symbols = [];
    private readonly List<TypeSymbol> _types = [];
    private readonly List<ConstructorSymbol> _constructors = [];
    private readonly List<FieldSymbol> _fields = [];
    private readonly List<PropertySymbol> _properties = [];
    private readonly List<EnumTypeSymbol> _enums = [];
    private int _loopDepth;
    private int _breakableDepth;
    private int _objectCreationIndex;
    private int _propertyAssignmentIndex;
    private int _interfaceReceiverIndex;

    public void Bind(CxProject project)
    {
        _symbols.Clear();
        _types.Clear();
        _constructors.Clear();
        _fields.Clear();
        _properties.Clear();
        _enums.Clear();
        AddCoreSymbols();

        foreach (var context in project.CompilationContexts)
        {
            AddProjectTypes(project.Name, context.DeclarationScope.Declarations);
        }

        foreach (var context in project.CompilationContexts)
        {
            ResolveBaseTypes(
                project.Name,
                context.DeclarationScope.Declarations,
                context.Imports);
        }
        ValidateInheritanceCycles();

        foreach (var context in project.CompilationContexts)
        {
            ResolveDeclarationTypes(
                context.DeclarationScope.Declarations,
                context.Namespace,
                context.Imports);
            AddProjectSymbols(project.Name, context.DeclarationScope.Declarations);
        }

        BindVirtualMethods();

        BindFieldInitializers();
        BindEnumMembers();

        foreach (var context in project.CompilationContexts)
        {
            foreach (var function in EnumerateFunctions(context.DeclarationScope.Declarations))
            {
                BindFunction(function, context.Imports);
            }
            foreach (var accessor in EnumeratePropertyAccessors(
                context.DeclarationScope.Declarations))
            {
                BindPropertyAccessor(accessor, context.Imports);
            }
        }
        ValidateConstructorInitializerCycles();
    }

    private void AddCoreSymbols()
    {
        AddCoreFunction("Console", "Read", [BuiltInSystemTypes.UInt], BuiltInSystemTypes.String);
        AddCoreFunction("Console", "ReadLine", [], BuiltInSystemTypes.String);
        AddCoreFunction("Console", "Write", [BuiltInSystemTypes.String], BuiltInSystemTypes.UInt);
        AddCoreFunction("Console", "WriteLine", [BuiltInSystemTypes.String], BuiltInSystemTypes.UInt);
        AddCoreConstructor(BuiltInSystemTypes.Object, ClassType.Class, "Object", [], 1);
        AddCoreConstructor(
            BuiltInSystemTypes.String,
            ClassType.Class,
            "String",
            [BuiltInSystemTypes.UInt, BuiltInSystemTypes.Char],
            3);
        AddCoreProperty(
            BuiltInSystemTypes.String,
            ClassType.Class,
            "String",
            "Length",
            BuiltInSystemTypes.UInt,
            [new PropertyAccessorSymbol("get", true, [])]);
        AddCoreProperty(
            BuiltInSystemTypes.String,
            ClassType.Class,
            "String",
            "Item",
            BuiltInSystemTypes.Char,
            [new PropertyAccessorSymbol("get", true, [BuiltInSystemTypes.UInt])]);
    }

    private void AddCoreFunction(
        string typeName,
        string functionName,
        IReadOnlyList<TypeBase> parameterTypes,
        TypeBase returnType)
    {
        _symbols.Add(new FunctionSymbol(
            "cxcore",
            new QualifiedIdentifier("System", typeName, functionName),
            parameterTypes,
            returnType));
    }

    private void AddCoreConstructor(
        TypeBase constructedType,
        ClassType classType,
        string typeName,
        IReadOnlyList<TypeBase> parameterTypes,
        int overloadIndex)
    {
        _constructors.Add(new ConstructorSymbol(
            constructedType,
            classType,
            new FunctionSymbol(
                "cxcore",
                new QualifiedIdentifier("System", typeName, "__constructor"),
                parameterTypes,
                BuiltInSystemTypes.Void,
                overloadIndex)));
    }

    private void AddCoreProperty(
        TypeBase containingType,
        ClassType classType,
        string typeName,
        string propertyName,
        TypeBase propertyType,
        IReadOnlyList<PropertyAccessorSymbol> accessors)
    {
        _properties.Add(new PropertySymbol(
            "cxcore",
            new QualifiedIdentifier("System", typeName, propertyName),
            containingType,
            classType,
            propertyType,
            false,
            accessors));
    }

    private void AddProjectTypes(
        string moduleName,
        IEnumerable<DeclarationBase> declarations)
    {
        foreach (var enumDeclaration in declarations.OfType<EnumDeclaration>())
        {
            var type = new NamedType(enumDeclaration.FullName.ToString(), []);
            type.SetResolvedType(
                enumDeclaration.FullName,
                moduleName,
                ClassType.Enum);
            _enums.Add(new EnumTypeSymbol(moduleName, enumDeclaration, type));
        }

        foreach (var classDeclaration in declarations.OfType<ClassDeclaration>())
        {
            var type = new NamedType(classDeclaration.FullName.ToString(), []);
            type.SetResolvedType(
                classDeclaration.FullName,
                moduleName,
                classDeclaration.ClassType);
            _types.Add(new TypeSymbol(moduleName, classDeclaration, type));
            foreach (var field in classDeclaration.MemberDeclarations.Declarations.OfType<FieldDeclaration>())
            {
                _fields.Add(new FieldSymbol(
                    moduleName,
                    type,
                    classDeclaration.ClassType,
                    field));
            }
            foreach (var property in classDeclaration.MemberDeclarations.Declarations.OfType<PropertyDeclaration>())
            {
                _properties.Add(new PropertySymbol(
                    moduleName,
                    property.FullName,
                    type,
                    classDeclaration.ClassType,
                    property.Type,
                    property.IsStatic,
                    property.PropertyAccessorDeclarations
                        .Select(accessor => new PropertyAccessorSymbol(
                            accessor.Name,
                            accessor.Const,
                            accessor.Parameters
                                .Select(parameter => parameter.ParameterType)
                                .ToArray()))
                        .ToArray()));
            }
            AddProjectTypes(
                moduleName,
                classDeclaration.MemberDeclarations.Declarations);
        }
    }

    private void ResolveDeclarationTypes(
        IEnumerable<DeclarationBase> declarations,
        QualifiedIdentifier currentNamespace,
        IReadOnlyList<QualifiedIdentifier> imports)
    {
        foreach (var declaration in declarations)
        {
            switch (declaration)
            {
                case ClassDeclaration classDeclaration:
                    foreach (var field in classDeclaration.MemberDeclarations.Declarations.OfType<FieldDeclaration>())
                    {
                        ResolveTypeReference(field.Type, currentNamespace, imports);
                    }
                    foreach (var property in classDeclaration.MemberDeclarations.Declarations.OfType<PropertyDeclaration>())
                    {
                        ResolveTypeReference(property.Type, currentNamespace, imports);
                        foreach (var parameter in property.PropertyAccessorDeclarations
                            .SelectMany(accessor => accessor.Parameters))
                        {
                            ResolveTypeReference(parameter.ParameterType, currentNamespace, imports);
                        }
                    }
                    ResolveDeclarationTypes(
                        classDeclaration.MemberDeclarations.Declarations,
                        classDeclaration.Namespace,
                        imports);
                    break;

                case FunctionDeclaration function:
                    ResolveTypeReference(function.ReturnType, currentNamespace, imports);
                    foreach (var parameter in function.Parameters)
                    {
                        ResolveTypeReference(parameter.ParameterType, currentNamespace, imports);
                    }
                    break;
            }
        }
    }

    private void ResolveBaseTypes(
        string moduleName,
        IEnumerable<DeclarationBase> declarations,
        IReadOnlyList<QualifiedIdentifier> imports)
    {
        foreach (var classDeclaration in declarations.OfType<ClassDeclaration>())
        {
            if (classDeclaration.IsStatic && classDeclaration.BaseTypes.Count > 0)
            {
                throw new CompilationErrorException(
                    $"Static type '{classDeclaration.FullName}' cannot declare base types.");
            }

            foreach (var baseType in classDeclaration.BaseTypes)
            {
                ResolveTypeReference(baseType, classDeclaration.Namespace, imports);
                if (baseType is ObjectType)
                {
                    SetBaseClass(classDeclaration, baseType, null);
                    continue;
                }
                if (UnwrapConst(baseType) is not NamedType namedType ||
                    namedType.ResolvedTypeFullName.ToString() == "void")
                {
                    throw new CompilationErrorException(
                        $"Cannot resolve base type '{GetTypeName(baseType)}' for " +
                        $"'{classDeclaration.FullName}'.");
                }

                var baseSymbol = _types.SingleOrDefault(candidate =>
                    IsType(candidate.Type, namedType))
                    ?? throw new CompilationErrorException(
                        $"Base type '{GetTypeName(baseType)}' is not a class or interface.");
                switch (baseSymbol.Declaration.ClassType)
                {
                    case ClassType.Interface:
                        if (classDeclaration.BaseInterfaces.Contains(baseSymbol.Declaration))
                        {
                            throw new CompilationErrorException(
                                $"Type '{classDeclaration.FullName}' lists interface " +
                                $"'{baseSymbol.Declaration.FullName}' more than once.");
                        }
                        classDeclaration.AddBaseInterface(baseSymbol.Declaration);
                        break;

                    case ClassType.Class:
                        SetBaseClass(classDeclaration, baseType, baseSymbol.Declaration);
                        break;

                    default:
                        throw new CompilationErrorException(
                            $"Type '{classDeclaration.FullName}' cannot derive from " +
                            $"'{baseSymbol.Declaration.FullName}'.");
                }
            }

            if (classDeclaration.ClassType == ClassType.Class &&
                !classDeclaration.IsStatic &&
                classDeclaration.BaseClassType is null &&
                !(moduleName == "cxcore" &&
                  classDeclaration.FullName == BuiltInSystemTypes.Object.FullName))
            {
                classDeclaration.SetBaseClass(BuiltInSystemTypes.Object, null);
            }

            ResolveBaseTypes(
                moduleName,
                classDeclaration.MemberDeclarations.Declarations,
                imports);
        }

        static void SetBaseClass(
            ClassDeclaration declaration,
            TypeBase baseType,
            ClassDeclaration? baseDeclaration)
        {
            if (declaration.ClassType != ClassType.Class)
            {
                throw new CompilationErrorException(
                    $"{declaration.ClassType} '{declaration.FullName}' cannot have a base class.");
            }
            if (declaration.BaseClassType is not null)
            {
                throw new CompilationErrorException(
                    $"Type '{declaration.FullName}' cannot have more than one base class.");
            }
            if (baseDeclaration?.IsFinal == true)
            {
                throw new CompilationErrorException(
                    $"Type '{declaration.FullName}' cannot derive from final type " +
                    $"'{baseDeclaration.FullName}'.");
            }
            if (baseDeclaration?.IsStatic == true)
            {
                throw new CompilationErrorException(
                    $"Type '{declaration.FullName}' cannot derive from static type " +
                    $"'{baseDeclaration.FullName}'.");
            }
            declaration.SetBaseClass(baseType, baseDeclaration);
        }
    }

    private void ValidateInheritanceCycles()
    {
        var visiting = new HashSet<ClassDeclaration>();
        var visited = new HashSet<ClassDeclaration>();
        foreach (var type in _types)
        {
            Visit(type.Declaration);
        }

        void Visit(ClassDeclaration declaration)
        {
            if (visited.Contains(declaration))
            {
                return;
            }
            if (!visiting.Add(declaration))
            {
                throw new CompilationErrorException(
                    $"Inheritance cycle detected at type '{declaration.FullName}'.");
            }

            if (declaration.BaseClassDeclaration is not null)
            {
                Visit(declaration.BaseClassDeclaration);
            }
            foreach (var baseInterface in declaration.BaseInterfaces)
            {
                Visit(baseInterface);
            }

            visiting.Remove(declaration);
            visited.Add(declaration);
        }
    }

    private void AddProjectSymbols(
        string moduleName,
        IEnumerable<DeclarationBase> declarations)
    {
        var overloadIndexes = new Dictionary<QualifiedIdentifier, int>();
        foreach (var function in EnumerateFunctions(declarations))
        {
            overloadIndexes.TryGetValue(function.FullName, out var overloadIndex);
            overloadIndex++;
            overloadIndexes[function.FullName] = overloadIndex;

            var symbol = new FunctionSymbol(
                moduleName,
                function.FullName,
                function.Parameters.Select(parameter => parameter.ParameterType).ToArray(),
                function.ReturnType,
                overloadIndex,
                function);
            _symbols.Add(symbol);

            if (function is ConstructorDeclaration constructor)
            {
                var type = _types.Single(typeSymbol =>
                    ReferenceEquals(typeSymbol.Declaration, constructor.ParentClassDeclaration));
                _constructors.Add(new ConstructorSymbol(
                    type.Type,
                    constructor.ParentClassDeclaration!.ClassType,
                    symbol));
            }
        }
    }

    private void BindVirtualMethods()
    {
        foreach (var interfaceDeclaration in _types
            .Select(type => type.Declaration)
            .Where(type => type.ClassType == ClassType.Interface)
            .OrderBy(GetInterfaceDepth))
        {
            var dispatchSlots = new List<InterfaceDispatchSlot>();
            foreach (var baseInterface in interfaceDeclaration.BaseInterfaces)
            {
                foreach (var inheritedSlot in baseInterface.InterfaceDispatchSlots)
                {
                    var existing = dispatchSlots.SingleOrDefault(slot =>
                        InterfaceSlotSignaturesMatch(slot, inheritedSlot));
                    if (existing is not null)
                    {
                        if (!IsType(existing.ReturnType, inheritedSlot.ReturnType))
                        {
                            throw new CompilationErrorException(
                                $"Interface '{interfaceDeclaration.FullName}' inherits incompatible " +
                                $"return types for member '{inheritedSlot.Contract.Name}'.");
                        }
                        continue;
                    }
                    dispatchSlots.Add(new InterfaceDispatchSlot(
                        dispatchSlots.Count + 1,
                        inheritedSlot.Kind,
                        inheritedSlot.Contract,
                        null));
                }
            }

            foreach (var member in interfaceDeclaration.MemberDeclarations.Declarations)
            {
                if (member is FunctionDeclaration function && function is not ConstructorDeclaration)
                {
                    if (function.IsStatic)
                    {
                        throw new CompilationErrorException(
                            $"Interface function '{function.FullName}' cannot be static.");
                    }
                    if (function.Body is not null)
                    {
                        throw new CompilationErrorException(
                            $"Interface function '{function.FullName}' cannot have a body.");
                    }
                    var candidate = new InterfaceDispatchSlot(
                        dispatchSlots.Count + 1,
                        InterfaceDispatchSlotKind.Method,
                        function,
                        null);
                    if (dispatchSlots.Any(slot => InterfaceSlotSignaturesMatch(slot, candidate)))
                    {
                        throw new CompilationErrorException(
                            $"Interface '{interfaceDeclaration.FullName}' contains duplicate function " +
                            $"'{function.Name}'.");
                    }
                    dispatchSlots.Add(candidate);
                    continue;
                }

                if (member is not PropertyDeclaration property)
                {
                    continue;
                }
                if (property.IsStatic)
                {
                    throw new CompilationErrorException(
                        $"Interface property '{property.FullName}' cannot be static.");
                }
                foreach (var accessor in property.PropertyAccessorDeclarations)
                {
                    if (accessor.Body is not null)
                    {
                        throw new CompilationErrorException(
                            $"Interface property accessor '{accessor.FullName}' cannot have a body.");
                    }
                    var kind = accessor.Name == "get"
                        ? InterfaceDispatchSlotKind.PropertyGetter
                        : InterfaceDispatchSlotKind.PropertySetter;
                    var candidate = new InterfaceDispatchSlot(
                        dispatchSlots.Count + 1,
                        kind,
                        accessor,
                        null);
                    if (dispatchSlots.Any(slot => InterfaceSlotSignaturesMatch(slot, candidate)))
                    {
                        throw new CompilationErrorException(
                            $"Interface '{interfaceDeclaration.FullName}' contains duplicate property accessor " +
                            $"'{property.Name}.{accessor.Name}'.");
                    }
                    dispatchSlots.Add(candidate);
                }
            }

            interfaceDeclaration.SetInterfaceDispatchSlots(dispatchSlots);
            interfaceDeclaration.SetInterfaceUpcastTargets(GetImplementedInterfaces(interfaceDeclaration)
                .OrderBy(@interface => @interface.FullName.ToString(), StringComparer.Ordinal));
            var methodSlots = dispatchSlots
                .Where(slot => slot.Kind == InterfaceDispatchSlotKind.Method)
                .Select(slot => new VirtualMethodSlot(
                    slot.Index,
                    slot.ContractFunction!,
                    null))
                .ToArray();
            foreach (var slot in methodSlots)
            {
                slot.Contract.BindVirtualSlot(slot.Index, slot.Contract);
            }
            interfaceDeclaration.SetVirtualMethodSlots(methodSlots);
        }

        foreach (var type in _types
            .Select(type => type.Declaration)
            .OrderBy(GetInheritanceDepth))
        {
            if (type.ClassType != ClassType.Class || type.IsStatic)
            {
                continue;
            }

            var slots = type.BaseClassDeclaration?.VirtualMethodSlots
                .Select(slot => new VirtualMethodSlot(
                    slot.Index,
                    slot.Contract,
                    slot.Implementation))
                .ToList() ?? [];

            foreach (var function in type.MemberDeclarations.Declarations
                .OfType<FunctionDeclaration>()
                .Where(function => function is not ConstructorDeclaration))
            {
                var isAbstract = function.MemberModifiers.Contains(MemberModifier.Abstract);
                var isVirtual = function.MemberModifiers.Contains(MemberModifier.Virtual);
                var isOverride = function.MemberModifiers.Contains(MemberModifier.Override);
                var isFinal = function.MemberModifiers.Contains(MemberModifier.Final);

                if (!(isAbstract || isVirtual || isOverride || isFinal))
                {
                    var abstractSlot = slots.SingleOrDefault(slot =>
                        slot.Implementation is null && FunctionSignaturesMatch(slot.Contract, function));
                    if (abstractSlot is not null)
                    {
                        ValidateOverride(type, abstractSlot, function);
                        ReplaceSlot(slots, abstractSlot, function);
                    }
                    continue;
                }

                if (function.IsStatic)
                {
                    throw new CompilationErrorException(
                        $"Static function '{function.FullName}' cannot be virtual, abstract, override, or final.");
                }
                if (isAbstract && function.Body is not null)
                {
                    throw new CompilationErrorException(
                        $"Abstract function '{function.FullName}' cannot have a body.");
                }
                if (isAbstract && !type.IsAbstract)
                {
                    throw new CompilationErrorException(
                        $"Abstract function '{function.FullName}' must be declared in an abstract class.");
                }
                if (!isAbstract &&
                    function.Body is null &&
                    !function.MemberModifiers.Contains(MemberModifier.Extern))
                {
                    throw new CompilationErrorException(
                        $"Virtual function '{function.FullName}' must have a body.");
                }

                if (isOverride)
                {
                    var inheritedSlot = slots.SingleOrDefault(slot =>
                        FunctionSignaturesMatch(slot.Contract, function));
                    if (inheritedSlot is null)
                    {
                        throw new CompilationErrorException(
                            $"Function '{function.FullName}' has no matching virtual function to override.");
                    }
                    ValidateOverride(type, inheritedSlot, function);
                    function.BindVirtualSlot(inheritedSlot.Index, inheritedSlot.Contract);
                    ReplaceSlot(slots, inheritedSlot, isAbstract ? null : function);
                    continue;
                }

                if (isFinal)
                {
                    throw new CompilationErrorException(
                        $"Function '{function.FullName}' can only be final when it overrides a virtual function.");
                }

                var slotIndex = slots.Count + 1;
                function.BindVirtualSlot(slotIndex, function);
                slots.Add(new VirtualMethodSlot(
                    slotIndex,
                    function,
                    isAbstract ? null : function));
            }

            type.SetVirtualMethodSlots(slots);
            if (!type.IsAbstract && slots.Any(slot => slot.Implementation is null))
            {
                var missing = slots.First(slot => slot.Implementation is null).Contract;
                throw new CompilationErrorException(
                    $"Non-abstract class '{type.FullName}' does not implement abstract function " +
                    $"'{missing.FullName}'.");
            }

            type.SetInterfaceDispatchTables(GetImplementedInterfaces(type)
                .OrderBy(@interface => @interface.FullName.ToString(), StringComparer.Ordinal)
                .Select(@interface => BindInterfaceDispatchTable(type, @interface))
                .ToArray());
        }

        static int GetInheritanceDepth(ClassDeclaration declaration)
        {
            var depth = 0;
            while (declaration.BaseClassDeclaration is { } baseClass)
            {
                depth++;
                declaration = baseClass;
            }
            return depth;
        }

        static int GetInterfaceDepth(ClassDeclaration declaration)
        {
            return declaration.BaseInterfaces.Count == 0
                ? 0
                : declaration.BaseInterfaces.Max(GetInterfaceDepth) + 1;
        }

        static void ValidateOverride(
            ClassDeclaration type,
            VirtualMethodSlot slot,
            FunctionDeclaration implementation)
        {
            if (slot.Implementation?.MemberModifiers.Contains(MemberModifier.Final) == true)
            {
                throw new CompilationErrorException(
                    $"Function '{implementation.FullName}' cannot override final function " +
                    $"'{slot.Implementation.FullName}'.");
            }
            if (!IsType(slot.Contract.ReturnType, implementation.ReturnType))
            {
                throw new CompilationErrorException(
                    $"Function '{implementation.FullName}' must return " +
                    $"'{GetTypeName(slot.Contract.ReturnType)}' to override " +
                    $"'{slot.Contract.FullName}'.");
            }
            if (implementation.MemberModifiers.Contains(MemberModifier.Abstract) && !type.IsAbstract)
            {
                throw new CompilationErrorException(
                    $"Abstract override '{implementation.FullName}' must be declared in an abstract class.");
            }
        }

        static void ReplaceSlot(
            List<VirtualMethodSlot> slots,
            VirtualMethodSlot inheritedSlot,
            FunctionDeclaration? implementation)
        {
            var slot = new VirtualMethodSlot(
                inheritedSlot.Index,
                inheritedSlot.Contract,
                implementation);
            slots[slots.IndexOf(inheritedSlot)] = slot;
            if (implementation is not null)
            {
                implementation.BindVirtualSlot(slot.Index, slot.Contract);
            }
        }
    }

    private InterfaceDispatchTable BindInterfaceDispatchTable(
        ClassDeclaration type,
        ClassDeclaration interfaceDeclaration)
    {
        var slots = new List<InterfaceDispatchSlot>();
        foreach (var contractSlot in interfaceDeclaration.InterfaceDispatchSlots)
        {
            if (contractSlot.Kind == InterfaceDispatchSlotKind.Method)
            {
                var contract = contractSlot.ContractFunction!;
                var implementation = FindInterfaceImplementation(type, contract);
                if (implementation is not null &&
                    !IsType(contract.ReturnType, implementation.ReturnType))
                {
                    throw new CompilationErrorException(
                        $"Function '{implementation.FullName}' must return " +
                        $"'{GetTypeName(contract.ReturnType)}' to implement " +
                        $"'{contract.FullName}'.");
                }
                if (implementation is not null &&
                    !implementation.MemberModifiers.Contains(MemberModifier.Public))
                {
                    throw new CompilationErrorException(
                        $"Function '{implementation.FullName}' must be public to implement " +
                        $"interface function '{contract.FullName}'.");
                }

                var concreteImplementation = implementation is not null &&
                    (implementation.Body is not null ||
                     implementation.MemberModifiers.Contains(MemberModifier.Extern))
                        ? implementation
                        : null;
                if (concreteImplementation is null && !type.IsAbstract)
                {
                    throw new CompilationErrorException(
                        $"Non-abstract class '{type.FullName}' does not implement interface function " +
                        $"'{contract.FullName}'.");
                }
                slots.Add(contractSlot with { Implementation = concreteImplementation });
                continue;
            }

            var contractAccessor = contractSlot.ContractAccessor!;
            var contractProperty = contractAccessor.ParentPropertyDeclaration;
            var implementationProperty = FindInterfacePropertyImplementation(type, contractProperty);
            if (implementationProperty is not null &&
                !IsType(contractProperty.Type, implementationProperty.Type))
            {
                throw new CompilationErrorException(
                    $"Property '{implementationProperty.FullName}' must have type " +
                    $"'{GetTypeName(contractProperty.Type)}' to implement " +
                    $"'{contractProperty.FullName}'.");
            }
            if (implementationProperty is not null &&
                !implementationProperty.MemberModifiers.Contains(MemberModifier.Public))
            {
                throw new CompilationErrorException(
                    $"Property '{implementationProperty.FullName}' must be public to implement " +
                    $"interface property '{contractProperty.FullName}'.");
            }
            var implementationAccessor = implementationProperty?.PropertyAccessorDeclarations
                .SingleOrDefault(accessor => PropertyAccessorSignaturesMatch(contractAccessor, accessor));
            var concreteAccessor = implementationAccessor is not null &&
                (implementationAccessor.BodyFunction is not null || implementationAccessor.Extern)
                    ? implementationAccessor
                    : null;
            if (concreteAccessor is null && !type.IsAbstract)
            {
                throw new CompilationErrorException(
                    $"Non-abstract class '{type.FullName}' does not implement interface property accessor " +
                    $"'{contractProperty.FullName}.{contractAccessor.Name}'.");
            }
            slots.Add(contractSlot with { Implementation = concreteAccessor });
        }
        return new InterfaceDispatchTable(interfaceDeclaration, slots);
    }

    private static bool InterfaceSlotSignaturesMatch(
        InterfaceDispatchSlot left,
        InterfaceDispatchSlot right)
    {
        if (left.Kind != right.Kind)
        {
            return false;
        }
        if (left.ContractFunction is { } leftFunction &&
            right.ContractFunction is { } rightFunction)
        {
            return FunctionSignaturesMatch(leftFunction, rightFunction);
        }

        var leftAccessor = left.ContractAccessor!;
        var rightAccessor = right.ContractAccessor!;
        return leftAccessor.ParentPropertyDeclaration.Name ==
                rightAccessor.ParentPropertyDeclaration.Name &&
            PropertyAccessorSignaturesMatch(leftAccessor, rightAccessor);
    }

    private static bool PropertyAccessorSignaturesMatch(
        PropertyAccessorDeclaration contract,
        PropertyAccessorDeclaration implementation)
    {
        return contract.Name == implementation.Name &&
            contract.Const == implementation.Const &&
            contract.Parameters.Count == implementation.Parameters.Count &&
            contract.Parameters.Zip(implementation.Parameters).All(pair =>
                IsType(pair.First.ParameterType, pair.Second.ParameterType));
    }

    private static PropertyDeclaration? FindInterfacePropertyImplementation(
        ClassDeclaration type,
        PropertyDeclaration contract)
    {
        for (var current = type; current is not null; current = current.BaseClassDeclaration)
        {
            var implementation = current.MemberDeclarations.Declarations
                .OfType<PropertyDeclaration>()
                .Where(property => !property.IsStatic && property.Name == contract.Name)
                .SingleOrDefault();
            if (implementation is not null)
            {
                return implementation;
            }
        }
        return null;
    }

    private FunctionDeclaration? FindInterfaceImplementation(
        ClassDeclaration type,
        FunctionDeclaration contract)
    {
        for (var current = type; current is not null; current = current.BaseClassDeclaration)
        {
            var implementation = current.MemberDeclarations.Declarations
                .OfType<FunctionDeclaration>()
                .Where(function => function is not ConstructorDeclaration &&
                    !function.IsStatic &&
                    FunctionSignaturesMatch(contract, function))
                .SingleOrDefault();
            if (implementation is not null)
            {
                return implementation;
            }
        }
        return null;
    }

    private static bool FunctionSignaturesMatch(
        FunctionDeclaration contract,
        FunctionDeclaration implementation)
    {
        return contract.Name == implementation.Name &&
            contract.Const == implementation.Const &&
            contract.Parameters.Count == implementation.Parameters.Count &&
            contract.Parameters.Zip(implementation.Parameters).All(pair =>
                IsType(pair.First.ParameterType, pair.Second.ParameterType));
    }

    private void BindFieldInitializers()
    {
        foreach (var field in _fields.Where(field => field.Declaration.Initializer is not null))
        {
            var initializer = (LiteralExpression)field.Declaration.Initializer!;
            var initializerType = BindLiteral(initializer);
            initializer.SetInferredType(initializerType);
            if (!CanAssign(field.Declaration.Type, initializerType))
            {
                throw new CompilationErrorException(
                    $"Cannot initialize field '{field.Declaration.FullName}' of type " +
                    $"'{GetTypeName(field.Declaration.Type)}' with '{GetTypeName(initializerType)}'.");
            }
            ApplyContextualType(initializer, field.Declaration.Type);
        }
    }

    private void BindEnumMembers()
    {
        foreach (var enumType in _enums)
        {
            foreach (var member in enumType.Declaration.Members
                .Where(member => member.Value is not null))
            {
                var valueType = BindLiteral(member.Value!);
                member.Value!.SetInferredType(valueType);
                if (!IsInteger(valueType))
                {
                    throw new CompilationErrorException(
                        $"Enum member '{member.FullName}' must have an integer value.");
                }
            }
        }
    }

    private void BindFunction(
        FunctionDeclaration function,
        IReadOnlyList<QualifiedIdentifier> imports)
    {
        if (function.Body is null)
        {
            return;
        }

        _loopDepth = 0;
        _breakableDepth = 0;
        _objectCreationIndex = 0;
        _propertyAssignmentIndex = 0;
        _interfaceReceiverIndex = 0;

        var scope = new LocalScope();
        foreach (var parameter in function.Parameters)
        {
            if (!scope.TryDeclare(parameter.Name, parameter.ParameterType))
            {
                throw new CompilationErrorException(
                    $"Parameter '{parameter.Name}' is already declared in function '{function.FullName}'.");
            }
        }

        if (function is ConstructorDeclaration constructor)
        {
            BindConstructorInitializer(constructor, imports, scope);
        }

        BindStatements(function.Body, function, imports, scope);

        if (!IsType(function.ReturnType, BuiltInSystemTypes.Void) &&
            !AlwaysReturns(function.Body))
        {
            throw new CompilationErrorException(
                $"Function '{function.FullName}' must return a value of type '{GetTypeName(function.ReturnType)}'.");
        }
    }

    private void BindConstructorInitializer(
        ConstructorDeclaration constructor,
        IReadOnlyList<QualifiedIdentifier> imports,
        LocalScope scope)
    {
        var parent = constructor.ParentClassDeclaration!;
        if (parent.ClassType != ClassType.Class && constructor.Initializer is null)
        {
            return;
        }

        if (constructor.Initializer is null)
        {
            if (parent.BaseClassType is null)
            {
                return;
            }
            constructor.SetInitializer(new ConstructorInitializer(
                ConstructorInitializerKind.Base,
                []));
        }

        var initializer = constructor.Initializer!;
        var argumentTypes = initializer.Arguments
            .Select(argument => BindExpression(argument, constructor, imports, scope))
            .ToArray();
        TypeBase targetType;
        if (initializer.Kind == ConstructorInitializerKind.This)
        {
            targetType = _types.Single(type =>
                ReferenceEquals(type.Declaration, parent)).Type;
        }
        else
        {
            targetType = parent.BaseClassType
                ?? throw new CompilationErrorException(
                    $"Constructor '{constructor.FullName}' cannot invoke base because " +
                    $"'{parent.FullName}' has no base class.");
        }

        var candidates = _constructors
            .Where(candidate =>
                IsType(candidate.ConstructedType, targetType) &&
                ParametersMatch(
                    candidate.Constructor.ParameterTypes,
                    argumentTypes))
            .ToArray();
        if (candidates.Length == 0)
        {
            var targetName = initializer.Kind == ConstructorInitializerKind.This
                ? parent.FullName
                : GetTypeName(targetType);
            throw new CompilationErrorException(
                $"No constructor for '{targetName}' accepts " +
                $"({string.Join(", ", argumentTypes.Select(GetTypeName))}).");
        }
        if (candidates.Length > 1)
        {
            throw new CompilationErrorException(
                $"Constructor initializer for '{constructor.FullName}' is ambiguous.");
        }

        var target = candidates[0].Constructor;
        initializer.BindTarget(target);
        foreach (var pair in initializer.Arguments.Zip(target.ParameterTypes))
        {
            ApplyContextualType(pair.First, pair.Second);
        }
    }

    private void ValidateConstructorInitializerCycles()
    {
        foreach (var constructor in _constructors
            .Select(symbol => symbol.Constructor.Declaration)
            .OfType<ConstructorDeclaration>())
        {
            var visiting = new HashSet<ConstructorDeclaration>();
            var current = constructor;
            while (current.Initializer is
                { Kind: ConstructorInitializerKind.This, Target.Declaration: ConstructorDeclaration next })
            {
                if (!visiting.Add(current))
                {
                    throw new CompilationErrorException(
                        $"Constructor initializer cycle detected in '{constructor.ParentClassDeclaration!.FullName}'.");
                }
                current = next;
            }
        }
    }

    private void BindPropertyAccessor(
        PropertyAccessorDeclaration accessor,
        IReadOnlyList<QualifiedIdentifier> imports)
    {
        if (accessor.BodyFunction is null)
        {
            return;
        }
        if (accessor.Extern)
        {
            throw new CompilationErrorException(
                $"Extern property accessor '{accessor.FullName}' cannot have a body.");
        }

        var propertyType = UnwrapConst(accessor.ParentPropertyDeclaration.Type);
        if (propertyType is GenericType ||
            propertyType is NamedType namedType && namedType.GenericParams.Length > 0)
        {
            throw new CompilationErrorException(
                $"Generic property accessor bodies are not supported yet: '{accessor.FullName}'.");
        }

        BindFunction(accessor.BodyFunction, imports);
    }

    private void BindStatements(
        IEnumerable<StatementBase> statements,
        FunctionDeclaration function,
        IReadOnlyList<QualifiedIdentifier> imports,
        LocalScope scope)
    {
        foreach (var statement in statements)
        {
            switch (statement)
            {
                case ExpressionStatement expressionStatement:
                    BindExpression(expressionStatement.Expression, function, imports, scope);
                    break;

                case ReturnStatement returnStatement:
                    BindReturnStatement(returnStatement, function, imports, scope);
                    break;

                case LocalVariableDeclarationStatement declarationStatement:
                    BindLocalDeclaration(declarationStatement, function, imports, scope);
                    break;

                case BlockStatement blockStatement:
                    BindStatements(
                        blockStatement.Statements,
                        function,
                        imports,
                        new LocalScope(scope));
                    break;

                case IfStatement ifStatement:
                    BindCondition(ifStatement.Condition, function, imports, scope);
                    BindStatement(
                        ifStatement.ThenStatement,
                        function,
                        imports,
                        new LocalScope(scope));
                    if (ifStatement.ElseStatement is not null)
                    {
                        BindStatement(
                            ifStatement.ElseStatement,
                            function,
                            imports,
                            new LocalScope(scope));
                    }
                    break;

                case SwitchStatement switchStatement:
                    BindSwitchStatement(switchStatement, function, imports, scope);
                    break;

                case WhileStatement whileStatement:
                    BindCondition(whileStatement.Condition, function, imports, scope);
                    BindLoopBody(
                        whileStatement.Body,
                        function,
                        imports,
                        new LocalScope(scope));
                    break;

                case DoWhileStatement doWhileStatement:
                    BindLoopBody(
                        doWhileStatement.Body,
                        function,
                        imports,
                        new LocalScope(scope));
                    BindCondition(doWhileStatement.Condition, function, imports, scope);
                    break;

                case ForStatement forStatement:
                    BindForStatement(forStatement, function, imports, scope);
                    break;

                case ForeachStatement foreachStatement:
                    BindForeachStatement(foreachStatement, function, imports, scope);
                    break;

                case EmptyStatement:
                    break;

                case BreakStatement:
                    ValidateBreak();
                    break;

                case ContinueStatement:
                    ValidateContinue();
                    break;

                default:
                    throw new InternalCompilerException(
                        $"Binding statement '{statement.GetType().Name}' is not yet supported.");
            }
        }
    }

    private void BindStatement(
        StatementBase statement,
        FunctionDeclaration function,
        IReadOnlyList<QualifiedIdentifier> imports,
        LocalScope scope)
    {
        BindStatements([statement], function, imports, scope);
    }

    private void BindLoopBody(
        StatementBase statement,
        FunctionDeclaration function,
        IReadOnlyList<QualifiedIdentifier> imports,
        LocalScope scope)
    {
        _loopDepth++;
        _breakableDepth++;
        try
        {
            BindStatement(statement, function, imports, scope);
        }
        finally
        {
            _loopDepth--;
            _breakableDepth--;
        }
    }

    private void ValidateBreak()
    {
        if (_breakableDepth == 0)
        {
            throw new CompilationErrorException(
                "The 'break' statement can only be used inside a loop or switch.");
        }
    }

    private void ValidateContinue()
    {
        if (_loopDepth == 0)
        {
            throw new CompilationErrorException(
                "The 'continue' statement can only be used inside a loop.");
        }
    }

    private void BindSwitchStatement(
        SwitchStatement statement,
        FunctionDeclaration function,
        IReadOnlyList<QualifiedIdentifier> imports,
        LocalScope scope)
    {
        var selectorType = BindExpression(statement.Expression, function, imports, scope);
        if (!IsSwitchType(selectorType))
        {
            throw new CompilationErrorException(
                $"Switch expression type '{GetTypeName(selectorType)}' is not supported.");
        }

        var hasDefault = false;
        var caseValues = new HashSet<string>(StringComparer.Ordinal);
        foreach (var label in statement.Sections.SelectMany(section => section.Labels))
        {
            if (label.IsDefault)
            {
                if (hasDefault)
                {
                    throw new CompilationErrorException("A switch statement can contain only one default label.");
                }
                hasDefault = true;
                continue;
            }
            if (label.Filter is not null)
            {
                throw new CompilationErrorException("Switch case filters are not supported yet.");
            }
            var labelType = BindExpression(label.Value!, function, imports, scope);
            var caseValue = label.Value switch
            {
                LiteralExpression literal => literal.SourceText,
                MemberAccessExpression { TargetEnumMember: not null } memberAccess =>
                    memberAccess.TargetEnumMember.Declaration.FullName.ToString(),
                _ => throw new CompilationErrorException(
                    "Switch case labels must be literals or enum members."),
            };
            if (!IsType(selectorType, labelType))
            {
                throw new CompilationErrorException(
                    $"Switch case type '{GetTypeName(labelType)}' does not match " +
                    $"selector type '{GetTypeName(selectorType)}'.");
            }
            if (!caseValues.Add(caseValue))
            {
                throw new CompilationErrorException(
                    $"Switch case label '{caseValue}' is duplicated.");
            }
        }

        _breakableDepth++;
        try
        {
            foreach (var section in statement.Sections)
            {
                BindStatements(section.Statements, function, imports, new LocalScope(scope));
            }
        }
        finally
        {
            _breakableDepth--;
        }
    }

    private void BindCondition(
        ExpressionBase condition,
        FunctionDeclaration function,
        IReadOnlyList<QualifiedIdentifier> imports,
        LocalScope scope)
    {
        var conditionType = BindExpression(condition, function, imports, scope);
        if (!IsType(conditionType, BuiltInSystemTypes.Bool))
        {
            throw new CompilationErrorException(
                $"Condition must have type 'System.Bool', but found '{GetTypeName(conditionType)}'.");
        }
    }

    private void BindForStatement(
        ForStatement statement,
        FunctionDeclaration function,
        IReadOnlyList<QualifiedIdentifier> imports,
        LocalScope parentScope)
    {
        var scope = new LocalScope(parentScope);
        if (statement.DeclarationInitializer is not null)
        {
            BindLocalDeclaration(statement.DeclarationInitializer, function, imports, scope);
        }
        foreach (var initializer in statement.InitializerExpressions)
        {
            BindExpression(initializer, function, imports, scope);
        }
        if (statement.Condition is not null)
        {
            BindCondition(statement.Condition, function, imports, scope);
        }
        foreach (var iterator in statement.Iterators)
        {
            BindExpression(iterator, function, imports, scope);
        }

        BindLoopBody(statement.Body, function, imports, new LocalScope(scope));
    }

    private void BindForeachStatement(
        ForeachStatement statement,
        FunctionDeclaration function,
        IReadOnlyList<QualifiedIdentifier> imports,
        LocalScope parentScope)
    {
        var collectionType = BindExpression(statement.Collection, function, imports, parentScope);
        if (collectionType is not ArrayType arrayType)
        {
            throw new CompilationErrorException(
                $"The foreach collection must be an array, but found '{GetTypeName(collectionType)}'.");
        }

        var variableType = statement.DeclaredType is AutoType
            ? arrayType.ElementType
            : statement.DeclaredType;
        if (!IsType(variableType, arrayType.ElementType))
        {
            throw new CompilationErrorException(
                $"Cannot iterate array elements of type '{GetTypeName(arrayType.ElementType)}' " +
                $"as '{GetTypeName(variableType)}'.");
        }

        var scope = new LocalScope(parentScope);
        if (!scope.TryDeclare(statement.VariableName, variableType))
        {
            throw new CompilationErrorException(
                $"Local '{statement.VariableName}' is already declared in this scope.");
        }

        statement.BindVariableType(variableType);
        BindLoopBody(statement.Body, function, imports, scope);
    }

    private void BindLocalDeclaration(
        LocalVariableDeclarationStatement statement,
        FunctionDeclaration function,
        IReadOnlyList<QualifiedIdentifier> imports,
        LocalScope scope)
    {
        ResolveTypeReference(
            statement.DeclaredType,
            function.ParentClassDeclaration?.Namespace ?? function.Namespace,
            imports);
        foreach (var declarator in statement.Declarators)
        {
            var initializerType = declarator.Initializer is null
                ? null
                : BindExpression(declarator.Initializer, function, imports, scope);
            var variableType = statement.DeclaredType is AutoType
                ? initializerType ?? throw new CompilationErrorException(
                    $"Implicitly typed local '{declarator.Name}' requires an initializer.")
                : statement.DeclaredType;

            if (statement.DeclaredType is AutoType && initializerType is NullType)
            {
                throw new CompilationErrorException(
                    $"Cannot infer the type of local '{declarator.Name}' from null.");
            }

            if (initializerType is not null && !CanAssign(variableType, initializerType))
            {
                throw new CompilationErrorException(
                    $"Cannot initialize local '{declarator.Name}' of type '{GetTypeName(variableType)}' " +
                    $"with '{GetTypeName(initializerType)}'.");
            }
            if (declarator.Initializer is not null)
            {
                ApplyContextualType(declarator.Initializer, variableType);
            }
            if (!scope.TryDeclare(declarator.Name, variableType))
            {
                throw new CompilationErrorException(
                    $"Local '{declarator.Name}' is already declared in this scope.");
            }

            declarator.BindType(variableType);
        }
    }

    private void BindReturnStatement(
        ReturnStatement statement,
        FunctionDeclaration function,
        IReadOnlyList<QualifiedIdentifier> imports,
        LocalScope scope)
    {
        if (statement.Expression is null)
        {
            if (!IsType(function.ReturnType, BuiltInSystemTypes.Void))
            {
                throw new CompilationErrorException(
                    $"Function '{function.FullName}' must return a value of type '{GetTypeName(function.ReturnType)}'.");
            }
            return;
        }

        if (IsType(function.ReturnType, BuiltInSystemTypes.Void))
        {
            throw new CompilationErrorException(
                $"Void function '{function.FullName}' cannot return a value.");
        }

        var expressionType = BindExpression(statement.Expression, function, imports, scope);
        if (!CanAssign(function.ReturnType, expressionType))
        {
            throw new CompilationErrorException(
                $"Function '{function.FullName}' returns '{GetTypeName(expressionType)}', " +
                $"but '{GetTypeName(function.ReturnType)}' is required.");
        }
        ApplyContextualType(statement.Expression, function.ReturnType);
    }

    private TypeBase BindExpression(
        ExpressionBase expression,
        FunctionDeclaration function,
        IReadOnlyList<QualifiedIdentifier> imports,
        LocalScope scope)
    {
        TypeBase type;
        switch (expression)
        {
            case LiteralExpression literal:
                type = BindLiteral(literal);
                break;

            case IdentifierExpression identifier:
                type = BindIdentifier(identifier, function, scope);
                break;

            case ThisExpression:
                type = BindThis(function);
                break;

            case MemberAccessExpression memberAccess:
                type = BindMemberAccess(memberAccess, function, imports, scope);
                break;

            case InvocationExpression invocation:
                type = BindInvocation(invocation, function, imports, scope);
                break;

            case BinaryExpression binary:
                type = BindBinary(binary, function, imports, scope);
                break;

            case ConditionalExpression conditional:
                type = BindConditional(conditional, function, imports, scope);
                break;

            case NullCoalescingExpression coalescing:
                type = BindNullCoalescing(coalescing, function, imports, scope);
                break;

            case UnaryExpression unary:
                type = BindUnary(unary, function, imports, scope);
                break;

            case AssignmentExpression assignment:
                type = BindAssignment(assignment, function, imports, scope);
                break;

            case ArrayCreationExpression arrayCreation:
                type = BindArrayCreation(arrayCreation, function, imports, scope);
                break;

            case ArrayAccessExpression arrayAccess:
                type = BindArrayAccess(arrayAccess, function, imports, scope);
                break;

            case ObjectCreationExpression objectCreation:
                type = BindObjectCreation(objectCreation, function, imports, scope);
                break;

            default:
                throw new CompilationErrorException(
                    $"Cannot determine the type of expression '{expression.GetType().Name}'.");
        }

        expression.SetInferredType(type);
        return type;
    }

    private TypeBase BindIdentifier(
        IdentifierExpression identifier,
        FunctionDeclaration function,
        LocalScope scope)
    {
        if (identifier.Identifier.Parts.Length == 1 &&
            scope.TryLookup(identifier.Identifier.Parts[0], out var localType))
        {
            return localType;
        }

        if (identifier.Identifier.Parts.Length == 1 &&
            function.ParentClassDeclaration is { } parentClass)
        {
            var fieldMatch = FindField(
                parentClass,
                identifier.Identifier.Parts[0],
                staticOnly: null);
            if (fieldMatch is not null)
            {
                var field = fieldMatch.Value.Symbol;
                if (!field.Declaration.IsStatic && function.IsStatic)
                {
                    throw new CompilationErrorException(
                        $"Instance field '{field.Declaration.Name}' cannot be used from a static function.");
                }
                identifier.BindField(
                    field,
                    field.Declaration.IsStatic ? 0 : fieldMatch.Value.BaseDepth);
                return field.Declaration.Type;
            }

            var propertyMatch = FindProperty(
                parentClass,
                identifier.Identifier.Parts[0],
                staticOnly: null);
            if (propertyMatch is not null)
            {
                var property = propertyMatch.Value.Symbol;
                if (!property.IsStatic && function.IsStatic)
                {
                    throw new CompilationErrorException(
                        $"Instance property '{identifier.Identifier.Parts[0]}' cannot be used from a static function.");
                }
                EnsurePropertyValueIsSupported(property);
                var getter = SelectPropertyAccessor(
                    property,
                    "get",
                    [],
                    !property.IsStatic && function.Const);
                identifier.BindProperty(
                    property,
                    getter,
                    property.IsStatic ? 0 : propertyMatch.Value.BaseDepth);
                return property.Type;
            }
        }

        throw new CompilationErrorException($"Cannot resolve value '{identifier.Identifier}'.");
    }

    private TypeBase BindThis(FunctionDeclaration function)
    {
        if (function.IsStatic || function.ParentClassDeclaration is null)
        {
            throw new CompilationErrorException(
                "The 'this' expression is only available in instance functions and constructors.");
        }

        var type = _types.Single(type =>
            ReferenceEquals(type.Declaration, function.ParentClassDeclaration)).Type;
        return function.Const ? new ConstType(type) : type;
    }

    private TypeBase BindMemberAccess(
        MemberAccessExpression memberAccess,
        FunctionDeclaration function,
        IReadOnlyList<QualifiedIdentifier> imports,
        LocalScope scope)
    {
        var currentNamespace = function.ParentClassDeclaration?.Namespace ?? function.Namespace;
        var staticTarget = TryResolveTypeExpression(
            memberAccess.Target,
            currentNamespace,
            imports,
            scope);
        if (staticTarget is not null)
        {
            if (staticTarget.Declaration is EnumDeclaration enumDeclaration)
            {
                var enumMember = enumDeclaration.Members.SingleOrDefault(
                    member => member.Name == memberAccess.MemberName)
                    ?? throw new CompilationErrorException(
                        $"Enum '{enumDeclaration.FullName}' has no member named " +
                        $"'{memberAccess.MemberName}'.");
                memberAccess.BindEnumMember(new EnumMemberSymbol(
                    staticTarget.ModuleName,
                    enumMember,
                    staticTarget.Type));
                return staticTarget.Type;
            }

            var staticClass = staticTarget.Declaration as ClassDeclaration;
            var staticField = staticClass is null
                ? null
                : FindField(staticClass, memberAccess.MemberName, staticOnly: true);
            if (staticField is null)
            {
                var staticProperty = staticClass is null
                    ? null
                    : FindProperty(staticClass, memberAccess.MemberName, staticOnly: true);
                if (staticProperty is null)
                {
                    throw new CompilationErrorException(
                        $"Static member '{staticTarget.Declaration.FullName}.{memberAccess.MemberName}' does not exist.");
                }
                EnsurePropertyValueIsSupported(staticProperty.Value.Symbol);
                var getter = SelectPropertyAccessor(
                    staticProperty.Value.Symbol, "get", [], false);
                memberAccess.BindProperty(staticProperty.Value.Symbol, getter);
                return staticProperty.Value.Symbol.Type;
            }
            memberAccess.BindField(staticField.Value.Symbol);
            return staticField.Value.Symbol.Declaration.Type;
        }

        var targetType = BindExpression(memberAccess.Target, function, imports, scope);
        var targetClass = GetClassDeclaration(targetType);
        var field = targetClass is null
            ? _fields
                .Where(candidate =>
                    IsType(candidate.ContainingType, targetType) &&
                    candidate.Declaration.Name == memberAccess.MemberName &&
                    !candidate.Declaration.IsStatic)
                .Select(candidate => ((FieldSymbol Symbol, int BaseDepth)?)(candidate, 0))
                .SingleOrDefault()
            : FindField(targetClass, memberAccess.MemberName, staticOnly: false);
        if (field is null)
        {
            var property = targetClass is null
                ? _properties
                    .Where(candidate =>
                        IsType(candidate.ContainingType, targetType) &&
                        candidate.FullName.Parts[^1] == memberAccess.MemberName &&
                        !candidate.IsStatic)
                    .Select(candidate => ((PropertySymbol Symbol, int BaseDepth)?)(candidate, 0))
                    .SingleOrDefault()
                : FindProperty(targetClass, memberAccess.MemberName, staticOnly: false);
            if (property is null)
            {
                throw new CompilationErrorException(
                    $"Instance member '{GetTypeName(targetType)}.{memberAccess.MemberName}' does not exist.");
            }
            EnsurePropertyValueIsSupported(property.Value.Symbol);
            var getter = SelectPropertyAccessor(
                property.Value.Symbol,
                "get",
                [],
                UnwrapConst(targetType) != targetType);
            var dispatch = GetInterfacePropertyDispatch(
                targetType,
                property.Value.Symbol,
                getter);
            memberAccess.BindProperty(
                property.Value.Symbol,
                getter,
                property.Value.BaseDepth,
                dispatch.SlotIndex,
                dispatch.TemporaryName);
            return property.Value.Symbol.Type;
        }

        memberAccess.BindField(field.Value.Symbol, field.Value.BaseDepth);
        return field.Value.Symbol.Declaration.Type;
    }

    private ResolvedTypeSymbol? TryResolveTypeExpression(
        ExpressionBase expression,
        QualifiedIdentifier currentNamespace,
        IReadOnlyList<QualifiedIdentifier> imports,
        LocalScope scope)
    {
        if (!TryFlattenIdentifier(expression, out var sourceName) ||
            sourceName.IsEmpty ||
            scope.TryLookup(sourceName.Parts[0], out _))
        {
            return null;
        }

        var candidateNames = GetCandidateNames(sourceName, currentNamespace, imports);
        var classCandidates = _types
            .Where(candidate => candidateNames.Contains(candidate.Declaration.FullName))
            .ToArray();
        var enumCandidates = _enums
            .Where(candidate => candidateNames.Contains(candidate.Declaration.FullName))
            .ToArray();
        if (classCandidates.Length + enumCandidates.Length > 1)
        {
            throw new CompilationErrorException($"Type name '{sourceName}' is ambiguous.");
        }
        if (classCandidates.SingleOrDefault() is { } classCandidate)
        {
            return new ResolvedTypeSymbol(
                classCandidate.ModuleName,
                classCandidate.Declaration,
                classCandidate.Type);
        }
        if (enumCandidates.SingleOrDefault() is { } enumCandidate)
        {
            return new ResolvedTypeSymbol(
                enumCandidate.ModuleName,
                enumCandidate.Declaration,
                enumCandidate.Type);
        }
        return null;
    }

    private PropertyReference? ResolvePropertyReference(
        ExpressionBase expression,
        FunctionDeclaration function,
        IReadOnlyList<QualifiedIdentifier> imports,
        LocalScope scope)
    {
        if (expression is IdentifierExpression identifier &&
            identifier.Identifier.Parts.Length == 1 &&
            function.ParentClassDeclaration is { } parentClass)
        {
            if (scope.TryLookup(identifier.Identifier.Parts[0], out _))
            {
                return null;
            }
            var propertyMatch = FindProperty(
                parentClass,
                identifier.Identifier.Parts[0],
                staticOnly: null);
            if (propertyMatch is null)
            {
                return null;
            }
            var property = propertyMatch.Value.Symbol;
            if (!property.IsStatic && function.IsStatic)
            {
                throw new CompilationErrorException(
                    $"Instance property '{identifier.Identifier}' cannot be used from a static function.");
            }
            return new PropertyReference(
                property,
                null,
                !property.IsStatic && function.Const,
                property.IsStatic ? 0 : propertyMatch.Value.BaseDepth,
                null);
        }

        if (expression is not MemberAccessExpression memberAccess)
        {
            return null;
        }

        var currentNamespace = function.ParentClassDeclaration?.Namespace ?? function.Namespace;
        var staticTarget = TryResolveTypeExpression(
            memberAccess.Target,
            currentNamespace,
            imports,
            scope);
        if (staticTarget is not null)
        {
            var targetClass = staticTarget.Declaration as ClassDeclaration;
            var property = targetClass is null
                ? null
                : FindProperty(targetClass, memberAccess.MemberName, staticOnly: true);
            return property is null
                ? null
                : new PropertyReference(property.Value.Symbol, null, false, 0, null);
        }

        var receiverType = BindExpression(memberAccess.Target, function, imports, scope);
        var receiverClass = GetClassDeclaration(receiverType);
        var instanceProperty = receiverClass is null
            ? _properties
                .Where(candidate =>
                    IsType(candidate.ContainingType, receiverType) &&
                    candidate.FullName.Parts[^1] == memberAccess.MemberName &&
                    !candidate.IsStatic)
                .Select(candidate => ((PropertySymbol Symbol, int BaseDepth)?)(candidate, 0))
                .SingleOrDefault()
            : FindProperty(receiverClass, memberAccess.MemberName, staticOnly: false);
        return instanceProperty is null
            ? null
            : new PropertyReference(
                instanceProperty.Value.Symbol,
                memberAccess.Target,
                receiverType is ConstType,
                instanceProperty.Value.BaseDepth,
                receiverType);
    }

    private (int? SlotIndex, string? TemporaryName) GetInterfacePropertyDispatch(
        TypeBase? receiverType,
        PropertySymbol property,
        PropertyAccessorSymbol accessor)
    {
        if (receiverType is null ||
            GetClassDeclaration(receiverType) is not { ClassType: ClassType.Interface } receiverInterface)
        {
            return (null, null);
        }

        var kind = accessor.Name == "get"
            ? InterfaceDispatchSlotKind.PropertyGetter
            : InterfaceDispatchSlotKind.PropertySetter;
        var slot = receiverInterface.InterfaceDispatchSlots.Single(candidate =>
        {
            if (candidate.Kind != kind || candidate.ContractAccessor is not { } contract)
            {
                return false;
            }
            return contract.ParentPropertyDeclaration.FullName == property.FullName &&
                contract.Const == accessor.Const &&
                contract.Parameters.Count == accessor.ParameterTypes.Count &&
                contract.Parameters.Zip(accessor.ParameterTypes).All(pair =>
                    IsType(pair.First.ParameterType, pair.Second));
        });
        return (slot.Index, $"__cx_iface_receiver_{_interfaceReceiverIndex++}");
    }

    private PropertyAccessorSymbol SelectPropertyAccessor(
        PropertySymbol property,
        string accessorName,
        IReadOnlyList<TypeBase> argumentTypes,
        bool receiverIsConst)
    {
        if (receiverIsConst && accessorName == "set")
        {
            throw new CompilationErrorException(
                $"Property '{property.FullName}' cannot be assigned through a const receiver.");
        }

        var candidates = property.Accessors
            .Where(accessor => accessor.Name == accessorName &&
                ParametersMatch(accessor.ParameterTypes, argumentTypes))
            .ToArray();
        if (accessorName == "get")
        {
            candidates = receiverIsConst
                ? candidates.Where(accessor => accessor.Const).ToArray()
                : candidates.OrderBy(accessor => accessor.Const).ToArray();
        }
        if (candidates.Length == 0)
        {
            var operation = accessorName == "get" ? "readable" : "writable";
            throw new CompilationErrorException(
                $"Property '{property.FullName}' is not {operation} with " +
                $"({string.Join(", ", argumentTypes.Select(GetTypeName))}).");
        }

        return candidates[0];
    }

    private static void EnsurePropertyValueIsSupported(PropertySymbol property)
    {
        var type = UnwrapConst(property.Type);
        if (type is GenericType ||
            type is NamedType namedType && namedType.GenericParams.Length > 0)
        {
            throw new CompilationErrorException(
                $"Generic property '{property.FullName}' cannot be used in expressions yet.");
        }
    }

    private static bool TryFlattenIdentifier(
        ExpressionBase expression,
        out QualifiedIdentifier identifier)
    {
        switch (expression)
        {
            case IdentifierExpression source:
                identifier = source.Identifier;
                return true;
            case MemberAccessExpression memberAccess when
                TryFlattenIdentifier(memberAccess.Target, out var target):
                identifier = new QualifiedIdentifier(target, memberAccess.MemberName);
                return true;
            default:
                identifier = QualifiedIdentifier.Empty;
                return false;
        }
    }

    private TypeBase BindInvocation(
        InvocationExpression invocation,
        FunctionDeclaration function,
        IReadOnlyList<QualifiedIdentifier> imports,
        LocalScope scope)
    {
        var argumentTypes = new List<TypeBase>();
        foreach (var argument in invocation.Arguments)
        {
            argumentTypes.Add(BindExpression(argument, function, imports, scope));
        }

        var currentNamespace = function.ParentClassDeclaration?.Namespace ?? function.Namespace;
        ExpressionBase? receiver = null;
        TypeBase? receiverType = null;
        IReadOnlyList<(FunctionSymbol Symbol, int BaseDepth)> namedCandidates;
        string sourceDisplay;

        if (invocation.Target is IdentifierExpression identifier &&
            identifier.Identifier.Parts.Length == 1 &&
            function.ParentClassDeclaration is { } currentClass)
        {
            sourceDisplay = identifier.Identifier.ToString();
            namedCandidates = FindFunctions(currentClass, sourceDisplay);
            if (namedCandidates.Count == 0)
            {
                namedCandidates = FindNamedFunctions(
                    identifier.Identifier,
                    currentNamespace,
                    imports);
            }
        }
        else if (invocation.Target is MemberAccessExpression memberAccess)
        {
            sourceDisplay = memberAccess.MemberName;
            var staticTarget = TryResolveTypeExpression(
                memberAccess.Target,
                currentNamespace,
                imports,
                scope);
            if (staticTarget?.Declaration is ClassDeclaration staticClass)
            {
                namedCandidates = FindFunctions(staticClass, memberAccess.MemberName)
                    .Where(candidate => candidate.Symbol.Declaration?.IsStatic == true)
                    .ToArray();
            }
            else if (staticTarget is not null)
            {
                namedCandidates = [];
            }
            else if (TryFlattenIdentifier(invocation.Target, out var qualifiedName) &&
                !scope.TryLookup(qualifiedName.Parts[0], out _) &&
                FindNamedFunctions(qualifiedName, currentNamespace, imports) is { Count: > 0 } qualifiedCandidates)
            {
                sourceDisplay = qualifiedName.ToString();
                namedCandidates = qualifiedCandidates;
            }
            else if (TryFlattenIdentifier(invocation.Target, out qualifiedName) &&
                !CanResolveValueName(
                    qualifiedName.Parts[0],
                    function.ParentClassDeclaration,
                    scope))
            {
                sourceDisplay = qualifiedName.ToString();
                namedCandidates = FindNamedFunctions(
                    qualifiedName,
                    currentNamespace,
                    imports);
            }
            else
            {
                receiver = memberAccess.Target;
                receiverType = BindExpression(receiver, function, imports, scope);
                var receiverClass = GetClassDeclaration(receiverType);
                namedCandidates = receiverClass is null
                    ? []
                    : FindFunctions(receiverClass, memberAccess.MemberName)
                        .Where(candidate => candidate.Symbol.Declaration?.IsStatic == false)
                        .ToArray();
            }

            if (namedCandidates.Count == 0 && receiver is null)
            {
                var flattenedName = FlattenIdentifier(invocation.Target);
                sourceDisplay = flattenedName.ToString();
                namedCandidates = FindNamedFunctions(
                    flattenedName,
                    currentNamespace,
                    imports);
            }
        }
        else
        {
            var sourceName = FlattenIdentifier(invocation.Target);
            sourceDisplay = sourceName.ToString();
            namedCandidates = FindNamedFunctions(sourceName, currentNamespace, imports);
        }

        if (namedCandidates.Count == 0)
        {
            throw new CompilationErrorException(
                $"Cannot resolve function '{sourceDisplay}' with {invocation.Arguments.Count} argument(s).");
        }

        var candidates = namedCandidates
            .Where(candidate => ParametersMatch(
                candidate.Symbol.ParameterTypes,
                argumentTypes))
            .ToArray();

        if (function.IsStatic && receiver is null)
        {
            candidates = candidates
                .Where(candidate => candidate.Symbol.Declaration?.IsStatic != false)
                .ToArray();
        }

        if (receiverType is ConstType || receiver is null && function.Const)
        {
            candidates = candidates
                .Where(candidate => candidate.Symbol.Declaration?.Const == true || candidate.Symbol.Declaration?.IsStatic == true)
                .ToArray();
        }

        if (candidates.Length > 0)
        {
            var closestDepth = candidates.Min(candidate => candidate.BaseDepth);
            candidates = candidates
                .Where(candidate => candidate.BaseDepth == closestDepth)
                .ToArray();
        }

        if (candidates.Length == 0)
        {
            throw new CompilationErrorException(
                $"No overload of '{sourceDisplay}' accepts ({string.Join(", ", argumentTypes.Select(GetTypeName))}).");
        }

        if (candidates.Length > 1)
        {
            throw new CompilationErrorException(
                $"Function call '{sourceDisplay}' with {invocation.Arguments.Count} argument(s) is ambiguous.");
        }

        var target = candidates[0];
        int? dispatchSlotIndex = null;
        if (receiverType is not null &&
            GetClassDeclaration(receiverType) is { ClassType: ClassType.Interface } receiverInterface &&
            target.Symbol.Declaration is { } targetDeclaration)
        {
            dispatchSlotIndex = receiverInterface.VirtualMethodSlots
                .Single(slot => FunctionSignaturesMatch(slot.Contract, targetDeclaration))
                .Index;
        }
        invocation.BindTarget(
            target.Symbol,
            receiver,
            target.BaseDepth,
            dispatchSlotIndex,
            dispatchSlotIndex is null
                ? null
                : $"__cx_iface_receiver_{_interfaceReceiverIndex++}");
        foreach (var pair in invocation.Arguments.Zip(target.Symbol.ParameterTypes))
        {
            ApplyContextualType(pair.First, pair.Second);
        }
        return target.Symbol.ReturnType;
    }

    private IReadOnlyList<(FunctionSymbol Symbol, int BaseDepth)> FindNamedFunctions(
        QualifiedIdentifier sourceName,
        QualifiedIdentifier currentNamespace,
        IReadOnlyList<QualifiedIdentifier> imports)
    {
        var candidateNames = GetCandidateNames(sourceName, currentNamespace, imports);
        return _symbols
            .Where(symbol => candidateNames.Contains(symbol.FullName))
            .Select(symbol => (symbol, 0))
            .ToArray();
    }

    private bool CanResolveValueName(
        string name,
        ClassDeclaration? currentClass,
        LocalScope scope)
    {
        return scope.TryLookup(name, out _) ||
            currentClass is not null &&
            (FindField(currentClass, name, staticOnly: null) is not null ||
             FindProperty(currentClass, name, staticOnly: null) is not null);
    }

    private IReadOnlyList<(FunctionSymbol Symbol, int BaseDepth)> FindFunctions(
        ClassDeclaration declaration,
        string name)
    {
        var hierarchy = declaration.ClassType == ClassType.Interface
            ? EnumerateTypeHierarchy(declaration)
            : EnumerateBaseClasses(declaration);
        return hierarchy
            .SelectMany(type => _symbols
                .Where(symbol =>
                    symbol.Declaration is { } function &&
                    ReferenceEquals(function.ParentClassDeclaration, type.Declaration) &&
                    function.Name == name)
                .Select(symbol => (symbol, type.BaseDepth)))
            .ToArray();
    }

    private static IEnumerable<(ClassDeclaration Declaration, int BaseDepth)>
        EnumerateBaseClasses(ClassDeclaration declaration)
    {
        var depth = 0;
        for (var current = declaration; current is not null; current = current.BaseClassDeclaration)
        {
            yield return (current, depth++);
        }
    }

    private TypeBase BindObjectCreation(
        ObjectCreationExpression creation,
        FunctionDeclaration function,
        IReadOnlyList<QualifiedIdentifier> imports,
        LocalScope scope)
    {
        var currentNamespace = function.ParentClassDeclaration?.Namespace ?? function.Namespace;
        ResolveTypeReference(creation.RequestedType, currentNamespace, imports);

        var argumentTypes = creation.Arguments
            .Select(argument => BindExpression(argument, function, imports, scope))
            .ToArray();
        var declaredType = _types.SingleOrDefault(candidate =>
            IsType(candidate.Type, creation.RequestedType));
        if (declaredType?.Declaration is { ClassType: ClassType.Interface })
        {
            throw new CompilationErrorException(
                $"Interface '{GetTypeName(creation.RequestedType)}' cannot be constructed.");
        }
        if (declaredType?.Declaration is { IsStatic: true })
        {
            throw new CompilationErrorException(
                $"Static type '{GetTypeName(creation.RequestedType)}' cannot be constructed.");
        }
        if (declaredType?.Declaration is { IsAbstract: true })
        {
            throw new CompilationErrorException(
                $"Abstract type '{GetTypeName(creation.RequestedType)}' cannot be constructed.");
        }

        var typeCandidates = _constructors
            .Where(candidate => IsType(candidate.ConstructedType, creation.RequestedType))
            .ToArray();
        if (typeCandidates.Length == 0)
        {
            throw new CompilationErrorException(
                $"Type '{GetTypeName(creation.RequestedType)}' has no available constructor.");
        }

        var classType = typeCandidates[0].ClassType;
        var declaration = typeCandidates
            .Select(candidate => candidate.Constructor.Declaration?.ParentClassDeclaration)
            .FirstOrDefault(candidate => candidate is not null);
        if (classType == ClassType.Interface)
        {
            throw new CompilationErrorException(
                $"Interface '{GetTypeName(creation.RequestedType)}' cannot be constructed.");
        }
        if (declaration is { IsStatic: true })
        {
            throw new CompilationErrorException(
                $"Static type '{GetTypeName(creation.RequestedType)}' cannot be constructed.");
        }
        if (declaration is { IsAbstract: true })
        {
            throw new CompilationErrorException(
                $"Abstract type '{GetTypeName(creation.RequestedType)}' cannot be constructed.");
        }

        var matchingConstructors = typeCandidates
            .Where(candidate => ParametersMatch(
                candidate.Constructor.ParameterTypes,
                argumentTypes))
            .ToArray();
        if (matchingConstructors.Length == 0)
        {
            throw new CompilationErrorException(
                $"No constructor for '{GetTypeName(creation.RequestedType)}' accepts " +
                $"({string.Join(", ", argumentTypes.Select(GetTypeName))}).");
        }
        if (matchingConstructors.Length > 1)
        {
            throw new CompilationErrorException(
                $"Constructor call for '{GetTypeName(creation.RequestedType)}' is ambiguous.");
        }

        var constructor = matchingConstructors[0];
        foreach (var pair in creation.Arguments.Zip(constructor.Constructor.ParameterTypes))
        {
            ApplyContextualType(pair.First, pair.Second);
        }
        creation.BindConstructor(
            constructor.Constructor,
            constructor.ClassType,
            $"__cx_new_{_objectCreationIndex++}");
        return constructor.ConstructedType;
    }

    private void ResolveTypeReference(
        TypeBase type,
        QualifiedIdentifier currentNamespace,
        IReadOnlyList<QualifiedIdentifier> imports)
    {
        switch (type)
        {
            case ConstType constType:
                ResolveTypeReference(constType.UnderlyingType, currentNamespace, imports);
                return;
            case ArrayType arrayType:
                ResolveTypeReference(arrayType.ElementType, currentNamespace, imports);
                return;
            case NullableType nullableType:
                ResolveTypeReference(nullableType.UnderlyingType, currentNamespace, imports);
                return;
            case NamedType namedType when namedType.ResolvedTypeFullName.ToString() == "void":
                var sourceName = new QualifiedIdentifier(
                    namedType.Name.Split('.', StringSplitOptions.RemoveEmptyEntries));
                var candidateNames = GetCandidateNames(sourceName, currentNamespace, imports);
                var classCandidates = _types
                    .Where(candidate => candidateNames.Contains(candidate.Declaration.FullName))
                    .ToArray();
                var enumCandidates = _enums
                    .Where(candidate => candidateNames.Contains(candidate.Declaration.FullName))
                    .ToArray();
                if (classCandidates.Length + enumCandidates.Length > 1)
                {
                    throw new CompilationErrorException(
                        $"Type name '{namedType.Name}' is ambiguous.");
                }
                if (classCandidates.SingleOrDefault() is { } classCandidate)
                {
                    namedType.SetResolvedType(
                        classCandidate.Declaration.FullName,
                        classCandidate.ModuleName,
                        classCandidate.Declaration.ClassType);
                }
                else if (enumCandidates.SingleOrDefault() is { } enumCandidate)
                {
                    namedType.SetResolvedType(
                        enumCandidate.Declaration.FullName,
                        enumCandidate.ModuleName,
                        ClassType.Enum);
                }
                return;
        }
    }

    private TypeBase BindBinary(
        BinaryExpression binary,
        FunctionDeclaration function,
        IReadOnlyList<QualifiedIdentifier> imports,
        LocalScope scope)
    {
        var leftType = BindExpression(binary.Left, function, imports, scope);
        var rightType = BindExpression(binary.Right, function, imports, scope);
        return GetBinaryResultType(binary.Operator, leftType, rightType);
    }

    private TypeBase BindConditional(
        ConditionalExpression conditional,
        FunctionDeclaration function,
        IReadOnlyList<QualifiedIdentifier> imports,
        LocalScope scope)
    {
        var conditionType = BindExpression(conditional.Condition, function, imports, scope);
        if (!IsType(conditionType, BuiltInSystemTypes.Bool))
        {
            throw new CompilationErrorException(
                $"Conditional expression requires a bool condition, but found '{GetTypeName(conditionType)}'.");
        }

        var whenTrueType = BindExpression(conditional.WhenTrue, function, imports, scope);
        var whenFalseType = BindExpression(conditional.WhenFalse, function, imports, scope);
        if (whenTrueType is NullType && IsNullAssignable(whenFalseType))
        {
            ApplyContextualType(conditional.WhenTrue, whenFalseType);
            return whenFalseType;
        }
        if (whenFalseType is NullType && IsNullAssignable(whenTrueType))
        {
            ApplyContextualType(conditional.WhenFalse, whenTrueType);
            return whenTrueType;
        }
        if (!IsType(whenTrueType, whenFalseType))
        {
            if (CanAssign(whenTrueType, whenFalseType))
            {
                ApplyContextualType(conditional.WhenFalse, whenTrueType);
                return whenTrueType;
            }
            if (CanAssign(whenFalseType, whenTrueType))
            {
                ApplyContextualType(conditional.WhenTrue, whenFalseType);
                return whenFalseType;
            }
            throw new CompilationErrorException(
                $"Conditional expression branches must have the same type, but found " +
                $"'{GetTypeName(whenTrueType)}' and '{GetTypeName(whenFalseType)}'.");
        }

        return whenTrueType;
    }

    private TypeBase BindNullCoalescing(
        NullCoalescingExpression coalescing,
        FunctionDeclaration function,
        IReadOnlyList<QualifiedIdentifier> imports,
        LocalScope scope)
    {
        var leftType = BindExpression(coalescing.Left, function, imports, scope);
        var rightType = BindExpression(coalescing.Right, function, imports, scope);

        if (coalescing.Left is not IdentifierExpression and
            not LiteralExpression { SourceText: "null" })
        {
            throw new CompilationErrorException(
                "The left operand of '??' must currently be a local or parameter so it is evaluated only once.");
        }

        if (leftType is NullType)
        {
            if (!IsNullAssignable(rightType))
            {
                throw new CompilationErrorException(
                    $"Operator '??' cannot use '{GetTypeName(rightType)}' as a null fallback.");
            }
            ApplyContextualType(coalescing.Left, rightType);
            return rightType;
        }

        var effectiveLeftType = UnwrapConst(leftType);
        var resultType = effectiveLeftType is NullableType nullableType
            ? nullableType.UnderlyingType
            : leftType;
        if (!IsNullAssignable(leftType))
        {
            throw new CompilationErrorException(
                $"Operator '??' requires a nullable or reference left operand, but found '{GetTypeName(leftType)}'.");
        }
        if (!CanAssign(resultType, rightType))
        {
            throw new CompilationErrorException(
                $"Operator '??' fallback has type '{GetTypeName(rightType)}', " +
                $"but '{GetTypeName(resultType)}' is required.");
        }

        ApplyContextualType(coalescing.Right, resultType);
        return resultType;
    }

    private TypeBase BindUnary(
        UnaryExpression unary,
        FunctionDeclaration function,
        IReadOnlyList<QualifiedIdentifier> imports,
        LocalScope scope)
    {
        var operandType = BindExpression(unary.Operand, function, imports, scope);
        var valueType = UnwrapConst(operandType);
        if (unary.Operator is "++" or "--")
        {
            if (unary.Operand is not IdentifierExpression and
                not ArrayAccessExpression and
                not MemberAccessExpression)
            {
                throw new CompilationErrorException(
                    $"Operator '{unary.Operator}' requires an assignable value.");
            }
            if (operandType is ConstType)
            {
                throw new CompilationErrorException(
                    $"Operator '{unary.Operator}' requires a writable value.");
            }
            if (unary.Operand is IdentifierExpression { TargetProperty: not null } or
                MemberAccessExpression { TargetProperty: not null } or
                ArrayAccessExpression { TargetProperty: not null })
            {
                throw new CompilationErrorException(
                    $"Operator '{unary.Operator}' on properties is not supported yet.");
            }
            ValidateWritableField(unary.Operand);
        }

        return unary.Operator switch
        {
            "+" or "-" when IsNumeric(valueType) => valueType,
            "~" when IsInteger(valueType) => valueType,
            "!" when IsType(valueType, BuiltInSystemTypes.Bool) => BuiltInSystemTypes.Bool,
            "++" or "--" when IsNumeric(valueType) => valueType,
            _ => throw new CompilationErrorException(
                $"Operator '{unary.Operator}' cannot be applied to '{GetTypeName(operandType)}'."),
        };
    }

    private TypeBase BindAssignment(
        AssignmentExpression assignment,
        FunctionDeclaration function,
        IReadOnlyList<QualifiedIdentifier> imports,
        LocalScope scope)
    {
        var propertyAssignmentType = TryBindPropertyAssignment(
            assignment,
            function,
            imports,
            scope);
        if (propertyAssignmentType is not null)
        {
            return propertyAssignmentType;
        }

        if (assignment.Target is not IdentifierExpression and
            not ArrayAccessExpression and
            not MemberAccessExpression)
        {
            throw new CompilationErrorException(
                $"Expression '{assignment.Target.GetType().Name}' cannot be assigned to.");
        }

        var targetType = BindExpression(assignment.Target, function, imports, scope);
        ValidateWritableField(assignment.Target);
        var valueType = BindExpression(assignment.Value, function, imports, scope);

        if (assignment.Operator == "=")
        {
            if (!CanAssign(targetType, valueType))
            {
                throw new CompilationErrorException(
                    $"Cannot assign '{GetTypeName(valueType)}' to '{GetTypeName(targetType)}'.");
            }
            ApplyContextualType(assignment.Value, targetType);
        }
        else
        {
            var resultType = GetBinaryResultType(
                assignment.Operator[..^1],
                targetType,
                valueType);
            if (!IsType(resultType, targetType))
            {
                throw new CompilationErrorException(
                    $"Compound assignment '{assignment.Operator}' changes the target type.");
            }
        }

        return targetType;
    }

    private TypeBase? TryBindPropertyAssignment(
        AssignmentExpression assignment,
        FunctionDeclaration function,
        IReadOnlyList<QualifiedIdentifier> imports,
        LocalScope scope)
    {
        var propertyExpression = assignment.Target is ArrayAccessExpression arrayAccess
            ? arrayAccess.Target
            : assignment.Target;
        var propertyReference = ResolvePropertyReference(
            propertyExpression,
            function,
            imports,
            scope);
        if (propertyReference is null)
        {
            return null;
        }

        var indexExpressions = assignment.Target is ArrayAccessExpression indexed
            ? indexed.Indices
            : [];
        if (assignment.Target is ArrayAccessExpression &&
            !propertyReference.Property.Accessors.Any(accessor =>
                accessor.ParameterTypes.Count > 0))
        {
            return null;
        }
        if (assignment.Operator != "=")
        {
            throw new CompilationErrorException(
                "Compound assignment to properties is not supported yet.");
        }

        EnsurePropertyValueIsSupported(propertyReference.Property);
        var argumentTypes = indexExpressions
            .Select(index => BindExpression(index, function, imports, scope))
            .ToArray();
        var setter = SelectPropertyAccessor(
            propertyReference.Property,
            "set",
            argumentTypes,
            propertyReference.ReceiverIsConst);
        var valueType = BindExpression(assignment.Value, function, imports, scope);
        if (!CanAssign(propertyReference.Property.Type, valueType))
        {
            throw new CompilationErrorException(
                $"Cannot assign '{GetTypeName(valueType)}' to property " +
                $"'{propertyReference.Property.FullName}' of type " +
                $"'{GetTypeName(propertyReference.Property.Type)}'.");
        }

        ApplyContextualType(assignment.Value, propertyReference.Property.Type);
        var dispatch = GetInterfacePropertyDispatch(
            propertyReference.ReceiverType,
            propertyReference.Property,
            setter);
        assignment.BindPropertySetter(
            propertyReference.Property,
            setter,
            $"__cx_property_value_{_propertyAssignmentIndex++}",
            propertyReference.ReceiverBaseDepth,
            dispatch.SlotIndex,
            dispatch.TemporaryName);
        return propertyReference.Property.Type;
    }

    private static void ValidateWritableField(ExpressionBase expression)
    {
        if (expression is MemberAccessExpression { TargetEnumMember: not null } enumMember)
        {
            throw new CompilationErrorException(
                $"Enum member '{enumMember.TargetEnumMember.Declaration.FullName}' cannot be assigned to.");
        }

        var field = expression switch
        {
            IdentifierExpression identifier => identifier.TargetField,
            MemberAccessExpression memberAccess => memberAccess.TargetField,
            _ => null,
        };
        if (field?.Declaration.Type is ConstType)
        {
            throw new CompilationErrorException(
                $"Const field '{field.Declaration.FullName}' cannot be assigned to.");
        }
    }

    private TypeBase BindArrayCreation(
        ArrayCreationExpression expression,
        FunctionDeclaration function,
        IReadOnlyList<QualifiedIdentifier> imports,
        LocalScope scope)
    {
        ResolveTypeReference(
            expression.ElementType,
            function.ParentClassDeclaration?.Namespace ?? function.Namespace,
            imports);
        if (expression.ElementType is VoidType or AutoType or ArrayType)
        {
            throw new CompilationErrorException(
                $"Arrays of type '{GetTypeName(expression.ElementType)}' cannot be created.");
        }

        var lengthType = BindExpression(expression.Length, function, imports, scope);
        if (!IsInteger(lengthType))
        {
            throw new CompilationErrorException(
                $"Array length must be an integer, but found '{GetTypeName(lengthType)}'.");
        }
        if (expression.Length is UnaryExpression
            {
                Operator: "-",
                Operand: LiteralExpression
            })
        {
            throw new CompilationErrorException("Array length cannot be negative.");
        }

        return new ArrayType(expression.ElementType);
    }

    private TypeBase BindArrayAccess(
        ArrayAccessExpression expression,
        FunctionDeclaration function,
        IReadOnlyList<QualifiedIdentifier> imports,
        LocalScope scope)
    {
        var propertyReference = ResolvePropertyReference(
            expression.Target,
            function,
            imports,
            scope);
        if (propertyReference is not null &&
            propertyReference.Property.Accessors.Any(accessor =>
                accessor.Name == "get" && accessor.ParameterTypes.Count > 0))
        {
            EnsurePropertyValueIsSupported(propertyReference.Property);
            var argumentTypes = expression.Indices
                .Select(index => BindExpression(index, function, imports, scope))
                .ToArray();
            var getter = SelectPropertyAccessor(
                propertyReference.Property,
                "get",
                argumentTypes,
                propertyReference.ReceiverIsConst);
            var dispatch = GetInterfacePropertyDispatch(
                propertyReference.ReceiverType,
                propertyReference.Property,
                getter);
            expression.BindProperty(
                propertyReference.Property,
                getter,
                propertyReference.ReceiverBaseDepth,
                dispatch.SlotIndex,
                dispatch.TemporaryName);
            return propertyReference.Property.Type;
        }

        var targetType = BindExpression(expression.Target, function, imports, scope);
        if (targetType is not ArrayType arrayType)
        {
            throw new CompilationErrorException(
                $"Cannot index expression of type '{GetTypeName(targetType)}'.");
        }
        if (expression.Indices.Count != 1)
        {
            throw new CompilationErrorException("Only one-dimensional array indexing is supported.");
        }

        var indexType = BindExpression(expression.Indices[0], function, imports, scope);
        if (!IsInteger(indexType))
        {
            throw new CompilationErrorException(
                $"Array index must be an integer, but found '{GetTypeName(indexType)}'.");
        }

        return arrayType.ElementType;
    }

    private static TypeBase BindLiteral(LiteralExpression literal)
    {
        if (literal.IsString)
        {
            return BuiltInSystemTypes.String;
        }
        if (literal.SourceText.StartsWith('\''))
        {
            return BuiltInSystemTypes.Char;
        }
        if (literal.SourceText is "true" or "false")
        {
            return BuiltInSystemTypes.Bool;
        }
        if (literal.SourceText.Contains('.') ||
            literal.SourceText.Contains('e', StringComparison.OrdinalIgnoreCase))
        {
            return literal.SourceText.EndsWith("f", StringComparison.OrdinalIgnoreCase)
                ? BuiltInSystemTypes.Float
                : BuiltInSystemTypes.Double;
        }
        if (literal.SourceText == "null")
        {
            return BuiltInSystemTypes.Null;
        }

        var suffix = literal.SourceText.TrimEnd('l', 'L', 'u', 'U');
        var isUnsigned = literal.SourceText.EndsWith("u", StringComparison.OrdinalIgnoreCase) ||
            literal.SourceText.EndsWith("ul", StringComparison.OrdinalIgnoreCase) ||
            literal.SourceText.EndsWith("lu", StringComparison.OrdinalIgnoreCase);
        var isLong = suffix.Length != literal.SourceText.Length &&
            literal.SourceText.Contains('l', StringComparison.OrdinalIgnoreCase);
        return (isUnsigned, isLong) switch
        {
            (true, true) => BuiltInSystemTypes.ULong,
            (true, false) => BuiltInSystemTypes.UInt,
            (false, true) => BuiltInSystemTypes.Long,
            _ => BuiltInSystemTypes.Int,
        };
    }

    private static TypeBase GetBinaryResultType(
        string @operator,
        TypeBase leftType,
        TypeBase rightType)
    {
        if (@operator is "==" or "!=" &&
            (leftType is NullType && IsNullAssignable(rightType) ||
             rightType is NullType && IsNullAssignable(leftType)))
        {
            return BuiltInSystemTypes.Bool;
        }

        if (!IsType(leftType, rightType))
        {
            throw new CompilationErrorException(
                $"Operator '{@operator}' cannot combine '{GetTypeName(leftType)}' and '{GetTypeName(rightType)}'.");
        }

        return @operator switch
        {
            "+" or "-" or "*" or "/" or "%" when IsNumeric(leftType) => leftType,
            "<<" or ">>" when IsInteger(leftType) => leftType,
            "&" or "|" or "^" when IsInteger(leftType) || IsType(leftType, BuiltInSystemTypes.Bool) => leftType,
            "&&" or "||" when IsType(leftType, BuiltInSystemTypes.Bool) => BuiltInSystemTypes.Bool,
            "==" or "!=" => BuiltInSystemTypes.Bool,
            "<" or "<=" or ">" or ">=" when IsNumeric(leftType) => BuiltInSystemTypes.Bool,
            _ => throw new CompilationErrorException(
                $"Operator '{@operator}' cannot be applied to '{GetTypeName(leftType)}'."),
        };
    }

    private static TypeBase ResolveValue(
        QualifiedIdentifier identifier,
        LocalScope scope)
    {
        if (identifier.Parts.Length != 1 ||
            !scope.TryLookup(identifier.Parts[0], out var type))
        {
            throw new CompilationErrorException($"Cannot resolve value '{identifier}'.");
        }

        return type;
    }

    private static bool IsNumeric(TypeBase type)
    {
        return 
            IsType(type, BuiltInSystemTypes.Char) ||
            IsInteger(type) ||
            IsType(type, BuiltInSystemTypes.Float) ||
            IsType(type, BuiltInSystemTypes.Double);
    }

    private static bool IsInteger(TypeBase type)
    {
        return UnwrapConst(type) is IntTypeBase;
    }

    private static bool IsSwitchType(TypeBase type)
    {
        return IsInteger(type) ||
            IsType(type, BuiltInSystemTypes.Bool) ||
            IsType(type, BuiltInSystemTypes.Char) ||
            UnwrapConst(type) is NamedType { ClassType: ClassType.Enum };
    }

    private static bool AlwaysReturns(IEnumerable<StatementBase> statements)
    {
        return statements.Any(AlwaysReturns);
    }

    private static bool AlwaysReturns(StatementBase statement)
    {
        return statement switch
        {
            ReturnStatement => true,
            BlockStatement block => AlwaysReturns(block.Statements),
            IfStatement { ElseStatement: not null } conditional =>
                AlwaysReturns(conditional.ThenStatement) &&
                AlwaysReturns(conditional.ElseStatement),
            SwitchStatement switchStatement =>
                switchStatement.Sections.SelectMany(section => section.Labels).Any(label => label.IsDefault) &&
                switchStatement.Sections.Count > 0 &&
                switchStatement.Sections.All(section => AlwaysReturns(section.Statements)),
            _ => false,
        };
    }

    private bool ParametersMatch(
        IReadOnlyList<TypeBase> parameterTypes,
        IReadOnlyList<TypeBase> argumentTypes)
    {
        return parameterTypes.Count == argumentTypes.Count &&
            parameterTypes.Zip(argumentTypes).All(pair => CanAssign(pair.First, pair.Second));
    }

    private bool CanAssign(TypeBase targetType, TypeBase valueType)
    {
        if (IsType(targetType, valueType) ||
            valueType is NullType && IsNullAssignable(targetType))
        {
            return true;
        }

        var unwrappedTarget = UnwrapConst(targetType);
        var unwrappedValue = UnwrapConst(valueType);
        var valueClass = GetClassDeclaration(unwrappedValue);
        var targetClass = GetClassDeclaration(unwrappedTarget);
        if (valueClass?.ClassType is not (ClassType.Class or ClassType.Interface))
        {
            return false;
        }
        if (unwrappedTarget is ObjectType)
        {
            return valueClass.ClassType == ClassType.Class;
        }

        return targetClass?.ClassType switch
        {
            ClassType.Class when valueClass.ClassType == ClassType.Class => EnumerateTypeHierarchy(valueClass).Any(candidate =>
                ReferenceEquals(candidate.Declaration, targetClass)),
            ClassType.Interface when valueClass.ClassType == ClassType.Class =>
                GetImplementedInterfaces(valueClass).Any(@interface =>
                    ReferenceEquals(@interface, targetClass)),
            ClassType.Interface when valueClass.ClassType == ClassType.Interface =>
                GetImplementedInterfaces(valueClass).Any(@interface =>
                    ReferenceEquals(@interface, targetClass)),
            _ => false,
        };
    }

    private static bool IsNullAssignable(TypeBase type)
    {
        type = UnwrapConst(type);
        return type is NullType or ReferenceTypeBase or ArrayType or NullableType or PtrType ||
            type is NamedType { ClassType: ClassType.Class or ClassType.Interface };
    }

    private void ApplyContextualType(ExpressionBase expression, TypeBase type)
    {
        if (expression.InferredType is { } sourceType &&
            GetClassDeclaration(sourceType) is { ClassType: ClassType.Interface } sourceInterface &&
            GetClassDeclaration(type) is { ClassType: ClassType.Interface } targetInterface &&
            !ReferenceEquals(sourceInterface, targetInterface))
        {
            var upcastIndex = sourceInterface.InterfaceUpcastTargets
                .Select((candidate, index) => (candidate, index))
                .Single(pair => ReferenceEquals(pair.candidate, targetInterface))
                .index;
            expression.BindInterfaceUpcast(
                sourceInterface.InterfaceDispatchSlots.Count + 1 + upcastIndex);
        }

        switch (expression)
        {
            case LiteralExpression { SourceText: "null" }:
                expression.SetInferredType(type);
                break;
            case ConditionalExpression conditional:
                ApplyContextualType(conditional.WhenTrue, type);
                ApplyContextualType(conditional.WhenFalse, type);
                break;
        }
    }

    private static bool IsType(TypeBase left, TypeBase right)
    {
        left = UnwrapConst(left);
        right = UnwrapConst(right);

        if (left is ArrayType leftArray && right is ArrayType rightArray)
        {
            return IsType(leftArray.ElementType, rightArray.ElementType);
        }

        return GetTypeName(left) == GetTypeName(right);
    }

    private static QualifiedIdentifier GetTypeName(TypeBase type)
    {
        type = UnwrapConst(type);
        return type is NamedType namedType && namedType.ResolvedTypeFullName.ToString() != "void"
            ? namedType.ResolvedTypeFullName
            : type.FullName;
    }

    private static TypeBase UnwrapConst(TypeBase type)
    {
        return type is ConstType constType ? constType.UnderlyingType : type;
    }

    private static HashSet<QualifiedIdentifier> GetCandidateNames(
        QualifiedIdentifier sourceName,
        QualifiedIdentifier currentNamespace,
        IReadOnlyList<QualifiedIdentifier> imports)
    {
        var candidates = new HashSet<QualifiedIdentifier>
        {
            sourceName,
            new(currentNamespace, sourceName),
        };

        foreach (var import in imports)
        {
            candidates.Add(new QualifiedIdentifier(import, sourceName));
        }

        return candidates;
    }

    private static QualifiedIdentifier FlattenIdentifier(ExpressionBase expression)
    {
        return expression switch
        {
            IdentifierExpression identifier => identifier.Identifier,
            MemberAccessExpression memberAccess =>
                new QualifiedIdentifier(FlattenIdentifier(memberAccess.Target), memberAccess.MemberName),
            _ => throw new CompilationErrorException(
                $"Expression '{expression.GetType().Name}' cannot be invoked."),
        };
    }

    private static IEnumerable<FunctionDeclaration> EnumerateFunctions(
        IEnumerable<DeclarationBase> declarations)
    {
        foreach (var declaration in declarations)
        {
            if (declaration is FunctionDeclaration function)
            {
                yield return function;
            }
            else if (declaration is ClassDeclaration classDeclaration)
            {
                foreach (var member in EnumerateFunctions(classDeclaration.MemberDeclarations.Declarations))
                {
                    yield return member;
                }
            }
        }
    }

    private static IEnumerable<PropertyAccessorDeclaration> EnumeratePropertyAccessors(
        IEnumerable<DeclarationBase> declarations)
    {
        foreach (var declaration in declarations)
        {
            if (declaration is PropertyDeclaration property)
            {
                foreach (var accessor in property.PropertyAccessorDeclarations)
                {
                    yield return accessor;
                }
            }
            else if (declaration is ClassDeclaration classDeclaration)
            {
                foreach (var accessor in EnumeratePropertyAccessors(
                    classDeclaration.MemberDeclarations.Declarations))
                {
                    yield return accessor;
                }
            }
        }
    }

    private ClassDeclaration? GetClassDeclaration(TypeBase type)
    {
        type = UnwrapConst(type);
        return _types.SingleOrDefault(candidate => IsType(candidate.Type, type))?.Declaration;
    }

    private IEnumerable<(ClassDeclaration Declaration, int BaseDepth)> EnumerateTypeHierarchy(
        ClassDeclaration declaration)
    {
        var visited = new HashSet<ClassDeclaration>();
        var pending = new Queue<(ClassDeclaration Declaration, int BaseDepth)>();
        pending.Enqueue((declaration, 0));
        while (pending.TryDequeue(out var current))
        {
            if (!visited.Add(current.Declaration))
            {
                continue;
            }
            yield return current;

            if (current.Declaration.BaseClassDeclaration is not null)
            {
                pending.Enqueue((
                    current.Declaration.BaseClassDeclaration,
                    current.BaseDepth + 1));
            }
            foreach (var baseInterface in current.Declaration.BaseInterfaces)
            {
                pending.Enqueue((baseInterface, current.BaseDepth));
            }
        }
    }

    private IEnumerable<ClassDeclaration> GetImplementedInterfaces(
        ClassDeclaration declaration)
    {
        var visited = new HashSet<ClassDeclaration>();
        for (var current = declaration; current is not null; current = current.BaseClassDeclaration)
        {
            foreach (var @interface in current.BaseInterfaces)
            {
                foreach (var result in Visit(@interface))
                {
                    yield return result;
                }
            }
        }

        IEnumerable<ClassDeclaration> Visit(ClassDeclaration @interface)
        {
            if (!visited.Add(@interface))
            {
                yield break;
            }
            yield return @interface;
            foreach (var baseInterface in @interface.BaseInterfaces)
            {
                foreach (var result in Visit(baseInterface))
                {
                    yield return result;
                }
            }
        }
    }

    private (FieldSymbol Symbol, int BaseDepth)? FindField(
        ClassDeclaration declaration,
        string name,
        bool? staticOnly)
    {
        foreach (var type in EnumerateTypeHierarchy(declaration))
        {
            var field = _fields.SingleOrDefault(candidate =>
                ReferenceEquals(candidate.Declaration.ParentClassDeclaration, type.Declaration) &&
                candidate.Declaration.Name == name &&
                (staticOnly is null || candidate.Declaration.IsStatic == staticOnly));
            if (field is not null)
            {
                return (field, type.BaseDepth);
            }
        }
        return null;
    }

    private (PropertySymbol Symbol, int BaseDepth)? FindProperty(
        ClassDeclaration declaration,
        string name,
        bool? staticOnly)
    {
        foreach (var type in EnumerateTypeHierarchy(declaration))
        {
            var property = _properties.SingleOrDefault(candidate =>
                candidate.FullName == new QualifiedIdentifier(type.Declaration.FullName, name) &&
                (staticOnly is null || candidate.IsStatic == staticOnly));
            if (property is not null)
            {
                return (property, type.BaseDepth);
            }
        }
        return null;
    }

    private sealed class LocalScope
    {
        private readonly LocalScope? _parent;
        private readonly Dictionary<string, TypeBase> _locals = new(StringComparer.Ordinal);

        public LocalScope(LocalScope? parent = null)
        {
            _parent = parent;
        }

        public bool TryDeclare(string name, TypeBase type)
        {
            return _locals.TryAdd(name, type);
        }

        public bool TryLookup(string name, out TypeBase type)
        {
            if (_locals.TryGetValue(name, out type!))
            {
                return true;
            }

            if (_parent is not null)
            {
                return _parent.TryLookup(name, out type);
            }

            type = null!;
            return false;
        }
    }

    private sealed record TypeSymbol(
        string ModuleName,
        ClassDeclaration Declaration,
        TypeBase Type);

    private sealed record EnumTypeSymbol(
        string ModuleName,
        EnumDeclaration Declaration,
        TypeBase Type);

    private sealed record ResolvedTypeSymbol(
        string ModuleName,
        DeclarationBase Declaration,
        TypeBase Type);

    private sealed record ConstructorSymbol(
        TypeBase ConstructedType,
        ClassType ClassType,
        FunctionSymbol Constructor);

    private sealed record PropertyReference(
        PropertySymbol Property,
        ExpressionBase? Receiver,
        bool ReceiverIsConst,
        int ReceiverBaseDepth,
        TypeBase? ReceiverType);
}

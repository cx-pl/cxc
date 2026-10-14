using CxCompiler.Model.Common;
using CxCompiler.Model.Errors;
using CxCompiler.Model.Types;
using CxCompiler.Model.Types.BuiltInTypes;

namespace CxCompiler.Semantics;

public sealed partial class SemanticBinder
{

    private void ResolveBaseTypes(
        string moduleName,
        IEnumerable<DeclarationBase> declarations,
        IReadOnlyList<QualifiedIdentifier> imports)
    {
        foreach (var classDeclaration in declarations.OfType<ClassDeclaration>()
            .Where(declaration => !IsReferencedModule(moduleName) ||
                declaration.Visibility == Visibility.Public))
        {
            try
            {
                var declaredInterfaces = new HashSet<string>(StringComparer.Ordinal);
                var declaredClassBase = false;
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
                        if (declaredClassBase)
                        {
                            throw new CompilationErrorException(
                                $"Type '{classDeclaration.FullName}' cannot have more than one base class.");
                        }
                        declaredClassBase = true;
                        if (classDeclaration.BaseClassType is null)
                        {
                            SetBaseClass(classDeclaration, baseType, null);
                        }
                        continue;
                    }
                    if (UnwrapConst(baseType) is not NamedType namedType ||
                        namedType.ResolvedTypeFullName.ToString() == "void")
                    {
                        throw new CompilationErrorException(
                            $"Cannot resolve base type '{GetTypeName(baseType)}' for " +
                            $"'{classDeclaration.FullName}'.");
                    }
                    if (namedType.ResolvedTypeFullName.ToString() == "cxcore.System.Exception")
                    {
                        if (declaredClassBase)
                        {
                            throw new CompilationErrorException(
                                $"Type '{classDeclaration.FullName}' cannot have more than one base class.");
                        }
                        declaredClassBase = true;
                        if (classDeclaration.BaseClassType is null)
                        {
                            SetBaseClass(classDeclaration, baseType, null);
                        }
                        continue;
                    }

                    var baseSymbol = _types.SingleOrDefault(candidate =>
                        MatchesConstructedDeclaration(candidate, namedType))
                        ?? throw new CompilationErrorException(
                            $"Base type '{GetTypeName(baseType)}' is not a class or interface.");
                    switch (baseSymbol.Declaration.ClassType)
                    {
                        case ClassType.Interface:
                            var interfaceKey = GetTypeName(namedType).ToString();
                            if (!declaredInterfaces.Add(interfaceKey))
                            {
                                throw new CompilationErrorException(
                                    $"Type '{classDeclaration.FullName}' lists interface " +
                                    $"'{baseSymbol.Declaration.FullName}' more than once.");
                            }
                            if (!classDeclaration.BaseInterfaces.Contains(baseSymbol.Declaration))
                            {
                                classDeclaration.AddBaseInterface(baseSymbol.Declaration);
                            }
                            break;

                        case ClassType.Class:
                            if (declaredClassBase)
                            {
                                throw new CompilationErrorException(
                                    $"Type '{classDeclaration.FullName}' cannot have more than one base class.");
                            }
                            declaredClassBase = true;
                            if (classDeclaration.BaseClassType is null)
                            {
                                SetBaseClass(classDeclaration, baseType, baseSymbol.Declaration);
                            }
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
                    var objectDeclaration = _types.SingleOrDefault(candidate =>
                        IsType(candidate.Type, BuiltInSystemTypes.Object))?.Declaration;
                    classDeclaration.SetBaseClass(BuiltInSystemTypes.Object, objectDeclaration);
                }

                ResolveBaseTypes(
                    moduleName,
                    classDeclaration.MemberDeclarations.Declarations,
                    imports);
            }
            catch (CompilationErrorException exception) when (
                exception.SourceSpan is null && classDeclaration.SourceSpan is not null)
            {
                throw exception.WithSourceSpan(classDeclaration.SourceSpan);
            }
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
                    $"Inheritance cycle detected at type '{declaration.FullName}'.",
                    declaration.SourceSpan);
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
                    var inheritedContract = dispatchSlots.SingleOrDefault(slot =>
                        slot.Kind == InterfaceDispatchSlotKind.Method &&
                        InterfaceSlotSignaturesMatch(slot, candidate));
                    if (inheritedContract is not null)
                    {
                        if (!IsType(inheritedContract.ReturnType, function.ReturnType))
                        {
                            throw new CompilationErrorException(
                                $"Interface function '{function.FullName}' must preserve the return " +
                                $"type of redeclared contract '{inheritedContract.Contract.FullName}'.",
                                function.SourceSpan);
                        }
                        if (!GenericMethodConstraintsMatch(
                            inheritedContract.ContractFunction!, function))
                        {
                            throw new CompilationErrorException(
                                $"Interface function '{function.FullName}' must preserve the generic " +
                                $"constraints of redeclared contract " +
                                $"'{inheritedContract.Contract.FullName}'.",
                                function.SourceSpan);
                        }
                        function.BindVirtualSlot(
                            inheritedContract.Index,
                            inheritedContract.ContractFunction!);
                        continue;
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
            if (type.ClassType is not (ClassType.Class or ClassType.Struct) || type.IsStatic)
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
                .Where(function => function is not ConstructorDeclaration &&
                    !function.IsGenericSpecialization))
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
                        var sameNameCandidates = slots
                            .Where(slot => slot.Contract.Name == function.Name)
                            .Select(slot =>
                                $"  {GetTypeName(slot.Contract.ReturnType)} {slot.Contract.FullName}(" +
                                string.Join(", ", slot.Contract.Parameters.Select(parameter =>
                                    GetTypeName(parameter.ParameterType))) + ")")
                            .ToArray();
                        var candidateDetails = sameNameCandidates.Length == 0
                            ? string.Empty
                            : " Candidates:\n" + string.Join("\n", sameNameCandidates);
                        throw new CompilationErrorException(
                            $"Function '{function.FullName}' has no matching virtual function to override." +
                            candidateDetails,
                            function.SourceSpan);
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
            if (slot.Contract.GenericTypeNames.Length !=
                implementation.GenericTypeNames.Length)
            {
                throw new CompilationErrorException(
                    $"Function '{implementation.FullName}' must preserve the generic arity of " +
                    $"'{slot.Contract.FullName}'.",
                    implementation.SourceSpan);
            }
            if (slot.Implementation?.MemberModifiers.Contains(MemberModifier.Final) == true)
            {
                throw new CompilationErrorException(
                    $"Function '{implementation.FullName}' cannot override final function " +
                    $"'{slot.Implementation.FullName}'.",
                    implementation.SourceSpan);
            }
            var genericArguments = slot.Contract.GenericTypeNames
                .Zip(implementation.GenericTypeNames)
                .ToDictionary(
                    pair => pair.First,
                    pair => (TypeBase)new GenericType(pair.Second),
                    StringComparer.Ordinal);
            if (!IsType(
                    GenericTypeSubstitution.Substitute(
                        slot.Contract.ReturnType, genericArguments),
                    implementation.ReturnType))
            {
                throw new CompilationErrorException(
                    $"Function '{implementation.FullName}' must return " +
                    $"'{GetTypeName(slot.Contract.ReturnType)}' to override " +
                    $"'{slot.Contract.FullName}'.",
                    implementation.SourceSpan);
            }
            if (!GenericMethodConstraintsMatch(slot.Contract, implementation))
            {
                throw new CompilationErrorException(
                    $"Function '{implementation.FullName}' must preserve the generic constraints of " +
                    $"'{slot.Contract.FullName}'.",
                    implementation.SourceSpan);
            }
            if (implementation.MemberModifiers.Contains(MemberModifier.Abstract) && !type.IsAbstract)
            {
                throw new CompilationErrorException(
                    $"Abstract override '{implementation.FullName}' must be declared in an abstract class.",
                    implementation.SourceSpan);
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
        var constructedInterface = interfaceDeclaration.GenericTypeNames.Length == 0
            ? null
            : FindConstructedInterfaceType(type, interfaceDeclaration);
        var interfaceArguments = GetInterfaceTypeArguments(
            interfaceDeclaration, constructedInterface);
        var slots = new List<InterfaceDispatchSlot>();
        foreach (var contractSlot in interfaceDeclaration.InterfaceDispatchSlots)
        {
            if (contractSlot.Kind == InterfaceDispatchSlotKind.Method)
            {
                var contract = contractSlot.ContractFunction!;
                var implementation = FindInterfaceImplementation(
                    type, contract, interfaceArguments);
                var contractReturnType = GenericTypeSubstitution.Substitute(
                    contract.ReturnType, interfaceArguments);
                if (implementation is not null &&
                    !IsType(contractReturnType, implementation.ReturnType))
                {
                    throw new CompilationErrorException(
                        $"Function '{implementation.FullName}' must return " +
                        $"'{GetTypeName(contractReturnType)}' to implement " +
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
            var contractPropertyType = GenericTypeSubstitution.Substitute(
                contractProperty.Type, interfaceArguments);
            if (implementationProperty is not null &&
                !IsType(contractPropertyType, implementationProperty.Type))
            {
                throw new CompilationErrorException(
                    $"Property '{implementationProperty.FullName}' must have type " +
                    $"'{GetTypeName(contractPropertyType)}' to implement " +
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
                .SingleOrDefault(accessor => ConstructedInterfacePropertyAccessorSignaturesMatch(
                    contractAccessor, accessor, interfaceArguments));
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
        return new InterfaceDispatchTable(
            interfaceDeclaration, slots, constructedInterface);
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
        FunctionDeclaration contract,
        IReadOnlyDictionary<string, TypeBase> interfaceArguments)
    {
        for (var current = type; current is not null; current = current.BaseClassDeclaration)
        {
            var implementation = current.MemberDeclarations.Declarations
                .OfType<FunctionDeclaration>()
                .Where(function => function is not ConstructorDeclaration &&
                    !function.IsStatic &&
                    ConstructedInterfaceFunctionSignaturesMatch(
                        contract, function, interfaceArguments))
                .SingleOrDefault();
            if (implementation is not null)
            {
                return implementation;
            }
        }
        return null;
    }

    private bool ConstructedInterfaceFunctionSignaturesMatch(
        FunctionDeclaration contract,
        FunctionDeclaration implementation,
        IReadOnlyDictionary<string, TypeBase> interfaceArguments) =>
        contract.Name == implementation.Name &&
        contract.Const == implementation.Const &&
        contract.Parameters.Count == implementation.Parameters.Count &&
        contract.Parameters.Zip(implementation.Parameters).All(pair =>
            IsType(
                GenericTypeSubstitution.Substitute(
                    pair.First.ParameterType, interfaceArguments),
                pair.Second.ParameterType));

    private static bool ConstructedInterfacePropertyAccessorSignaturesMatch(
        PropertyAccessorDeclaration contract,
        PropertyAccessorDeclaration implementation,
        IReadOnlyDictionary<string, TypeBase> interfaceArguments) =>
        contract.Name == implementation.Name &&
        contract.Const == implementation.Const &&
        contract.Parameters.Count == implementation.Parameters.Count &&
        contract.Parameters.Zip(implementation.Parameters).All(pair =>
            IsType(
                GenericTypeSubstitution.Substitute(
                    pair.First.ParameterType, interfaceArguments),
                pair.Second.ParameterType));

    private NamedType? FindConstructedInterfaceType(
        ClassDeclaration type,
        ClassDeclaration target)
    {
        foreach (var baseType in type.BaseTypes.OfType<NamedType>())
        {
            if (ReferenceEquals(GetClassDeclarationIncludingConstructed(baseType), target))
            {
                return baseType;
            }

            if (FindConstructedInterfaceType(baseType, target, []) is { } inherited)
            {
                return inherited;
            }
        }

        return null;
    }

    private NamedType? FindConstructedInterfaceType(
        NamedType currentType,
        ClassDeclaration target,
        HashSet<string> visited)
    {
        var currentDeclaration = GetClassDeclarationIncludingConstructed(currentType);
        if (currentDeclaration is null ||
            !visited.Add(currentType.FullName.ToString() + "<" + string.Join(",",
                currentType.TypeArguments.Select(GetTypeName)) + ">"))
        {
            return null;
        }

        var arguments = GetInterfaceTypeArguments(currentDeclaration, currentType);
        foreach (var baseType in currentDeclaration.BaseTypes.OfType<NamedType>())
        {
            var closedBaseType = GenericTypeSubstitution.Substitute(baseType, arguments) as NamedType;
            if (closedBaseType is null)
            {
                continue;
            }
            if (ReferenceEquals(
                    GetClassDeclarationIncludingConstructed(closedBaseType), target))
            {
                return closedBaseType;
            }
            if (FindConstructedInterfaceType(closedBaseType, target, visited) is { } inherited)
            {
                return inherited;
            }
        }

        return null;
    }

    private static IReadOnlyDictionary<string, TypeBase> GetInterfaceTypeArguments(
        ClassDeclaration declaration,
        NamedType? constructedType) => declaration.GenericTypeNames
        .Zip(constructedType?.TypeArguments ?? [])
        .ToDictionary(pair => pair.First, pair => pair.Second, StringComparer.Ordinal);

    private static bool FunctionSignaturesMatch(
        FunctionDeclaration contract,
        FunctionDeclaration implementation)
    {
        if (contract.Name != implementation.Name ||
            contract.Const != implementation.Const ||
            contract.GenericTypeNames.Length != implementation.GenericTypeNames.Length ||
            contract.Parameters.Count != implementation.Parameters.Count)
        {
            return false;
        }

        var genericArguments = contract.GenericTypeNames
            .Zip(implementation.GenericTypeNames)
            .ToDictionary(
                pair => pair.First,
                pair => (TypeBase)new GenericType(pair.Second),
                StringComparer.Ordinal);
        return
            contract.Parameters.Zip(implementation.Parameters).All(pair =>
                IsType(
                    GenericTypeSubstitution.Substitute(pair.First.ParameterType, genericArguments),
                    pair.Second.ParameterType));
    }

    private static bool GenericMethodConstraintsMatch(
        FunctionDeclaration contract,
        FunctionDeclaration implementation)
    {
        var contractParameters = contract.GenericTypeNames
            .Select((name, index) => (name, index))
            .ToDictionary(item => item.name, item => item.index, StringComparer.Ordinal);
        var implementationParameters = implementation.GenericTypeNames
            .Select((name, index) => (name, index))
            .ToDictionary(item => item.name, item => item.index, StringComparer.Ordinal);
        if (!ConstraintsMatch(contract.GenericConstraints, implementation.GenericConstraints) ||
            !ConceptConstraintsMatch(contract.ConceptConstraints, implementation.ConceptConstraints))
        {
            return false;
        }

        return true;

        bool ConstraintsMatch(
            IReadOnlyList<GenericTypeConstraint> expected,
            IReadOnlyList<GenericTypeConstraint> actual)
        {
            if (expected.Count != actual.Count)
            {
                return false;
            }
            foreach (var constraint in expected)
            {
                if (!contractParameters.TryGetValue(constraint.ParameterName, out var index))
                {
                    return false;
                }
                var matching = actual.SingleOrDefault(candidate =>
                    implementationParameters.TryGetValue(candidate.ParameterName, out var candidateIndex) &&
                    candidateIndex == index);
                if (matching is null || constraint.Kind != matching.Kind ||
                    constraint.Types.Count != matching.Types.Count)
                {
                    return false;
                }
                var substitutions = contract.GenericTypeNames
                    .Zip(implementation.GenericTypeNames)
                    .ToDictionary(
                        pair => pair.First,
                        pair => (TypeBase)new GenericType(pair.Second),
                        StringComparer.Ordinal);
                var unmatched = matching.Types.ToList();
                foreach (var requiredType in constraint.Types)
                {
                    var substituted = GenericTypeSubstitution.Substitute(
                        requiredType, substitutions);
                    var matchIndex = unmatched.FindIndex(candidate => IsType(candidate, substituted));
                    if (matchIndex < 0)
                    {
                        return false;
                    }
                    unmatched.RemoveAt(matchIndex);
                }
            }
            return true;
        }

        bool ConceptConstraintsMatch(
            IReadOnlyList<GenericConceptConstraint> expected,
            IReadOnlyList<GenericConceptConstraint> actual)
        {
            if (expected.Count != actual.Count)
            {
                return false;
            }
            foreach (var constraint in expected)
            {
                if (!contractParameters.TryGetValue(constraint.ParameterName, out var index))
                {
                    return false;
                }
                var matching = actual.SingleOrDefault(candidate =>
                    implementationParameters.TryGetValue(candidate.ParameterName, out var candidateIndex) &&
                    candidateIndex == index);
                if (matching is null || !constraint.Concepts.ToHashSet().SetEquals(matching.Concepts))
                {
                    return false;
                }
            }
            return true;
        }
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
                pending.Enqueue((baseInterface, current.BaseDepth + 1));
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
}

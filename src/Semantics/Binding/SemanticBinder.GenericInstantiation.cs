using CxCompiler.Model;
using CxCompiler.Model.Common;
using CxCompiler.Model.Errors;
using CxCompiler.Model.Expressions;
using CxCompiler.Model.Statements;
using CxCompiler.Model.Types;
using CxCompiler.Model.Types.BuiltInTypes;
using CxCompiler.OutputGenerators;

namespace CxCompiler.Semantics;

public sealed partial class SemanticBinder
{

    private void RegisterClosedGenericClassFunctionInstances()
    {
        foreach (var (type, declaration) in _project!.GenericTypeInstances.ToArray())
        {
            if (declaration.GenericTypeNames.Length == 0)
            {
                continue;
            }

            var ownerFunctions = declaration.MemberDeclarations.Declarations
                .OfType<FunctionDeclaration>()
                .Concat(declaration.InterfaceDispatchTables
                    .SelectMany(table => table.Slots)
                    .Select(slot => slot.ImplementationAccessor?.BodyFunction)
                    .OfType<FunctionDeclaration>()
                    .Where(IsClosedGenericConstructedResultAccessor));
            foreach (var method in ownerFunctions
                .Where(method => method is not ConstructorDeclaration &&
                    method.GenericTypeNames.Length == 0 &&
                    !method.IsStatic && method.VirtualSlotIndex is null))
            {
                var symbol = _symbols.SingleOrDefault(candidate =>
                    ReferenceEquals(candidate.Declaration, method));
                if (symbol is null)
                {
                    if (!IsClosedGenericConstructedResultAccessor(method))
                    {
                        continue;
                    }
                    symbol = new FunctionSymbol(
                        method.ParentClassDeclaration?.ProjectName ?? _project!.Name,
                        method.FullName,
                        method.Parameters.Select(parameter => parameter.ParameterType).ToArray(),
                        method.ReturnType,
                        overloadIndex: 1,
                        declaration: method);
                }
                var instance = CloseContainingClassFunction(symbol, type);
                if (instance.SpecializationName is not null)
                {
                    _project.AddGenericFunctionInstance(instance);
                }
            }
        }
    }

    private static bool IsClosedGenericConstructedResultAccessor(
        FunctionDeclaration function) =>
        function.Name == "get" &&
        function.ParentClassDeclaration is { GenericTypeNames.Length: > 0 } owner &&
        owner.MemberDeclarations.Declarations.OfType<PropertyDeclaration>()
            .Any(property => property.Name == "Current" &&
                property.PropertyAccessorDeclarations.Any(accessor =>
                    ReferenceEquals(accessor.BodyFunction, function)) &&
                ContainsOpenTypeParameter(property.Type)) &&
        function.Body is
            [ReturnStatement { Expression: ObjectCreationExpression creation }] &&
        ContainsOpenTypeParameter(creation.RequestedType);

    private void RegisterClosedGenericClassFunctionTargets()
    {
        var processed = new HashSet<string>(StringComparer.Ordinal);
        while (true)
        {
            var callers = _project!.GenericFunctionInstances
                .Where(instance => instance.SpecializationName is not null &&
                    instance.ClosedContainingType is not null &&
                    instance.ClosedContainingType.TypeArguments.All(argument =>
                        !ContainsOpenTypeParameter(argument)) &&
                    instance.Declaration?.ParentClassDeclaration is
                        { GenericTypeNames.Length: > 0 } &&
                    processed.Add(instance.SpecializationName))
                .ToArray();
            if (callers.Length == 0)
            {
                break;
            }

            foreach (var caller in callers)
            {
                var declaration = caller.Declaration!;
                var owner = declaration.ParentClassDeclaration!;
                var closedOwner = caller.ClosedContainingType!;
                var ownerArguments = owner.GenericTypeNames.Zip(closedOwner.TypeArguments)
                    .ToDictionary(pair => pair.First, pair => pair.Second, StringComparer.Ordinal);
                foreach (var invocation in CCodeOutputGenerator.EnumerateBodyExpressions(
                    declaration.Body ?? []).OfType<InvocationExpression>())
                {
                    if (invocation.TargetSymbol is not { Declaration: { } targetDeclaration } target ||
                        targetDeclaration.ParentClassDeclaration is not
                            { GenericTypeNames.Length: > 0 } targetOwner ||
                        targetDeclaration.GenericTypeNames.Length != 0)
                    {
                        continue;
                    }

                    TypeBase? receiverType = invocation.Receiver?.InferredType;
                    if (receiverType is null &&
                        ReferenceEquals(targetOwner, owner))
                    {
                        receiverType = closedOwner;
                    }
                    else if (receiverType is not null)
                    {
                        receiverType = CloseConstructedTypes(
                            GenericTypeSubstitution.Substitute(receiverType, ownerArguments));
                    }
                    if (receiverType is null)
                    {
                        continue;
                    }
                    if (ContainsOpenTypeParameter(receiverType))
                    {
                        continue;
                    }

                    var closedTarget = CloseContainingClassFunction(target, receiverType);
                    if (closedTarget.SpecializationName is null)
                    {
                        continue;
                    }
                    invocation.BindClosedGenericTarget(
                        caller.SpecializationName!, closedTarget);
                    _project.AddGenericFunctionInstance(closedTarget);
                }
            }
        }
    }

    private void RegisterClosedGenericConstructorObjectCreations()
    {
        var processedSpecializations = new HashSet<string>(StringComparer.Ordinal);
        while (true)
        {
            var callers = _project!.GenericFunctionInstances
                .Where(caller => caller.SpecializationName is { } specialization &&
                    !processedSpecializations.Contains(specialization))
                .ToArray();
            if (callers.Length == 0)
            {
                break;
            }

            foreach (var caller in callers)
            {
                if (caller.SpecializationName is not { } callerSpecialization ||
                    !processedSpecializations.Add(callerSpecialization) ||
                    caller.ClosedContainingType is not { } closedOwner ||
                    closedOwner.TypeArguments.Any(ContainsOpenTypeParameter) ||
                    caller.Declaration is not
                    {
                        ParentClassDeclaration: { GenericTypeNames.Length: > 0 } owner,
                        Body: { } body,
                    })
                {
                    continue;
                }

                var ownerArguments = owner.GenericTypeNames.Zip(closedOwner.TypeArguments)
                    .ToDictionary(pair => pair.First, pair => pair.Second, StringComparer.Ordinal);
                foreach (var creation in CCodeOutputGenerator.EnumerateBodyExpressions(body)
                    .OfType<ObjectCreationExpression>())
                {
                    if (creation.Constructor is not { Declaration: { } targetConstructor } openConstructor ||
                        targetConstructor.ParentClassDeclaration is not
                            { GenericTypeNames.Length: > 0 } targetDeclaration)
                    {
                        continue;
                    }

                    var requestedType = GenericTypeSubstitution.Substitute(
                        creation.RequestedType, ownerArguments);
                    requestedType = CloseConstructedTypes(requestedType);
                    if (requestedType is not NamedType
                        { ConstructedIdentity: { } closedIdentity } closedTarget ||
                        targetDeclaration.GenericTypeNames.Length != closedTarget.TypeArguments.Count)
                    {
                        continue;
                    }

                    var targetArguments = targetDeclaration.GenericTypeNames
                        .Zip(closedTarget.TypeArguments)
                        .ToDictionary(pair => pair.First, pair => pair.Second, StringComparer.Ordinal);
                    var parameterTypes = openConstructor.ParameterTypes
                        .Select(parameter => CloseConstructedTypes(
                            GenericTypeSubstitution.Substitute(parameter, targetArguments)))
                        .ToArray();
                    var returnType = CloseConstructedTypes(
                        GenericTypeSubstitution.Substitute(openConstructor.ReturnType, targetArguments));
                    var functionName = openConstructor.FullName.ToString() +
                        $"#constructor@{closedIdentity.CanonicalName}";
                    string GetSpecializationName(string moduleName) =>
                        GenericTypeIdentity.CreateFunction(
                            moduleName, functionName, closedTarget.TypeArguments,
                            parameterTypes, returnType).CIdentifier;

                    var moduleName = openConstructor.ModuleName;
                    var specializationName = GetSpecializationName(moduleName);
                    if (IsReferencedModule(moduleName) &&
                        !_project.HasReferencedGenericFunctionSpecialization(specializationName))
                    {
                        moduleName = _project.Name;
                        specializationName = GetSpecializationName(moduleName);
                    }

                    var closedConstructor = new FunctionSymbol(
                        moduleName,
                        openConstructor.FullName,
                        parameterTypes,
                        returnType,
                        openConstructor.OverloadIndex,
                        openConstructor.Declaration,
                        specializationName,
                        closedTarget);
                    _project.AddGenericFunctionInstance(closedConstructor);
                    creation.BindClosedConstruction(
                        callerSpecialization, closedTarget, closedConstructor);
                }
            }
        }
    }

    private void RegisterClosedClassConceptFunctionInstances()
    {
        foreach (var (type, declaration) in _project!.GenericTypeInstances
            .Where(instance => instance.Declaration.ConceptConstraints.Count > 0))
        {
            foreach (var method in declaration.MemberDeclarations.Declarations
                .OfType<FunctionDeclaration>()
                .Where(method => IsClosedClassConceptMethodReturnBody(method) ||
                    IsClosedClassConceptVoidCallBody(method)))
            {
                var symbol = _symbols.SingleOrDefault(candidate =>
                    ReferenceEquals(candidate.Declaration, method));
                if (symbol is null)
                {
                    continue;
                }
                var instance = CloseContainingClassFunction(symbol, type);
                if (instance.SpecializationName is not null)
                {
                    _project.AddGenericFunctionInstance(instance);
                }
            }
        }
    }

    private void RegisterClosedGenericForwardCall(FunctionSymbol caller)
    {
        var callerName = caller.SpecializationName;
        var declaration = caller.Declaration;
        if (callerName is null || declaration is null ||
            caller.ImplementationDeclaration is not null ||
            !IsGenericForwardCallReturnBody(declaration) ||
            !_expandedGenericForwardCalls.Add(callerName))
        {
            return;
        }

        var invocation = (InvocationExpression)((ReturnStatement)declaration.Body![0]).Expression!;
        var hasMatchingReturnParameter = invocation.Arguments
            .OfType<IdentifierExpression>()
            .Select(argument => declaration.Parameters.SingleOrDefault(parameter =>
                parameter.Name == argument.Identifier.ToString()))
            .Any(parameter => parameter?.ParameterType is GenericType parameterType &&
                declaration.ReturnType is GenericType returnType &&
                parameterType.Name == returnType.Name);
        if (!hasMatchingReturnParameter || invocation.Arguments.Any(argument =>
                !IsSupportedGenericForwardArgument(argument, declaration)))
        {
            throw new CompilationErrorException(
                "Generic forwarding arguments must be matching type-parameter parameters " +
                "or pure built-in expressions over concrete parameters and literals.",
                invocation.SourceSpan);
        }
        var openTarget = invocation.TargetSymbol;
        if (openTarget?.Declaration is not { GenericTypeNames.Length: > 0 })
        {
            throw new CompilationErrorException(
                $"Generic forwarding body '{declaration.FullName}' must call a generic function.",
                invocation.SourceSpan);
        }

        var argumentTypes = invocation.Arguments
            .Select(argument => argument.InferredType is { } type
                ? GenericTypeSubstitution.Substitute(type, caller.GenericTypeArguments)
                : throw new InternalCompilerException(
                    "Generic forwarding argument has no inferred type."))
            .ToArray();
        var explicitTypeArguments = invocation.ExplicitTypeArguments
            .Select(type => GenericTypeSubstitution.Substitute(
                type, caller.GenericTypeArguments))
            .ToArray();
        if (explicitTypeArguments.Length == 0 &&
            openTarget.Declaration is { } genericTargetDeclaration &&
            genericTargetDeclaration.GenericTypeNames.All(
                openTarget.GenericTypeArguments.ContainsKey))
        {
            explicitTypeArguments = genericTargetDeclaration.GenericTypeNames
                .Select(name => GenericTypeSubstitution.Substitute(
                    openTarget.GenericTypeArguments[name], caller.GenericTypeArguments))
                .ToArray();
        }
        var closedTarget = CloseGenericFunction(openTarget, argumentTypes, explicitTypeArguments);
        if (closedTarget?.SpecializationName is null)
        {
            throw new CompilationErrorException(
                $"Cannot close forwarded generic call to '{openTarget.FullName}'.",
                invocation.SourceSpan);
        }
        var target = SelectGenericFunctionSpecialization(closedTarget);
        if (target.Declaration is { IsStatic: false } targetDeclaration &&
            !IsSupportedGenericForwardInstanceReceiver(
                invocation, declaration, targetDeclaration))
        {
            throw new CompilationErrorException(
                "Generic forwarding to an instance method requires the current instance " +
                "or a class receiver expression whose type matches the method's " +
                "non-generic owner.",
                invocation.SourceSpan);
        }
        invocation.BindClosedGenericTarget(callerName, target);
        _project!.AddGenericFunctionInstance(target);
        RegisterClosedGenericForwardCall(target);
    }

    private bool IsSupportedGenericForwardInstanceReceiver(
        InvocationExpression invocation,
        FunctionDeclaration caller,
        FunctionDeclaration target)
    {
        var targetClass = target.ParentClassDeclaration;
        if (targetClass is not { ClassType: ClassType.Class or ClassType.Struct } ||
            targetClass.GenericTypeNames.Length != 0)
        {
            return false;
        }

        if (invocation.Receiver is null or ThisExpression)
        {
            return !caller.IsStatic &&
                ReferenceEquals(caller.ParentClassDeclaration, targetClass);
        }

        if (invocation.Receiver.InferredType is not { } receiverType ||
            !ReferenceEquals(GetMemberClassDeclaration(receiverType), targetClass))
        {
            return false;
        }
        if (targetClass.ClassType == ClassType.Struct)
        {
            return receiverType is NamedType { ClassType: ClassType.Struct };
        }
        return targetClass.ClassType == ClassType.Class &&
            receiverType is NamedType { ClassType: ClassType.Class };
    }

    private FunctionSymbol? CloseGenericFunction(
        FunctionSymbol symbol,
        IReadOnlyList<TypeBase> argumentTypes,
        IReadOnlyList<TypeBase> explicitTypeArguments,
        TypeBase? expectedReturnType = null)
    {
        var declaration = symbol.Declaration;
        if (declaration is null || declaration.GenericTypeNames.Length == 0)
        {
            return explicitTypeArguments.Count == 0 ? symbol : null;
        }
        if (symbol.ParameterTypes.Count != argumentTypes.Count)
        {
            return null;
        }

        var inferred = new Dictionary<string, TypeBase>(StringComparer.Ordinal);
        if (explicitTypeArguments.Count > 0)
        {
            if (explicitTypeArguments.Count != declaration.GenericTypeNames.Length)
            {
                return null;
            }
            foreach (var pair in declaration.GenericTypeNames.Zip(explicitTypeArguments))
            {
                inferred.Add(pair.First, pair.Second);
            }
        }
        else
        {
            foreach (var pair in symbol.ParameterTypes.Zip(argumentTypes))
            {
                if (!InferGenericArguments(pair.First, pair.Second, inferred))
                {
                    return null;
                }
            }
            if (expectedReturnType is not null &&
                !InferGenericArguments(symbol.ReturnType, expectedReturnType, inferred))
            {
                return null;
            }
        }
        if (declaration.GenericTypeNames.Any(name => !inferred.ContainsKey(name)))
        {
            return null;
        }

        var parameterTypes = symbol.ParameterTypes
            .Select(type => GenericTypeSubstitution.Substitute(type, inferred))
            .ToArray();
        var returnType = GenericTypeSubstitution.Substitute(symbol.ReturnType, inferred);
        var typeArguments = declaration.GenericTypeNames.Select(name => inferred[name]).ToArray();
        if (typeArguments.Any(ContainsOpenTypeParameter))
        {
            return new FunctionSymbol(
                symbol.ModuleName,
                symbol.FullName,
                parameterTypes,
                returnType,
                symbol.OverloadIndex,
                declaration,
                closedContainingType: symbol.ClosedContainingType,
                genericTypeArguments: inferred);
        }
        foreach (var constraint in declaration.GenericConstraints)
        {
            var argumentType = inferred[constraint.ParameterName];
            if (!SatisfiesSpecialConstraint(argumentType, constraint))
            {
                _genericConstraintFailure =
                    $"Type '{GetTypeName(argumentType)}' does not satisfy the " +
                    $"{GetConstraintKindName(constraint.Kind)} constraint for parameter " +
                    $"'{constraint.ParameterName}' on '{declaration.FullName}'.";
                return null;
            }
            foreach (var requiredType in constraint.Types)
            {
                if (!SatisfiesGenericTypeConstraint(requiredType, argumentType))
                {
                    _genericConstraintFailure =
                        $"Type '{GetTypeName(argumentType)}' does not satisfy generic " +
                        $"constraint '{GetTypeName(requiredType)}' for parameter " +
                        $"'{constraint.ParameterName}' on '{declaration.FullName}'.";
                    return null;
                }
            }
        }
        if (_resolvedConceptConstraints.TryGetValue(declaration, out var conceptConstraints))
        {
            foreach (var conceptConstraint in conceptConstraints)
            {
                var argumentType = inferred[conceptConstraint.ParameterName];
                if (!SatisfiesConcept(argumentType, conceptConstraint.Declaration))
                {
                    _genericConstraintFailure =
                        $"Type '{GetTypeName(argumentType)}' does not satisfy concept " +
                        $"'{conceptConstraint.Declaration.FullName}' for parameter " +
                        $"'{conceptConstraint.ParameterName}' on '{declaration.FullName}'.";
                    return null;
                }
            }
        }
        var functionIdentityName = symbol.FullName.ToString() +
            (declaration.IsStatic ? string.Empty : "#instance") +
            (symbol.ClosedContainingType?.ConstructedIdentity is { } containingIdentity
                ? $"@{containingIdentity.CanonicalName}"
                : string.Empty);
        var definingIdentity = GenericTypeIdentity.CreateFunction(
            symbol.ModuleName,
            functionIdentityName,
            typeArguments,
            parameterTypes,
            returnType);
        var specializationModule = IsReferencedModule(symbol.ModuleName) &&
            !_genericFunctionSpecializations.ContainsKey(definingIdentity.CanonicalName)
                ? _project!.Name
                : symbol.ModuleName;
        var identity = specializationModule == symbol.ModuleName
            ? definingIdentity
            : GenericTypeIdentity.CreateFunction(
                specializationModule,
                functionIdentityName,
                typeArguments,
                parameterTypes,
                returnType);
        var conceptOperatorBindings = new Dictionary<string, FunctionSymbol>(StringComparer.Ordinal);
        if (_resolvedConceptConstraints.TryGetValue(declaration, out conceptConstraints))
        {
            foreach (var conceptConstraint in conceptConstraints)
            {
                var argumentType = inferred[conceptConstraint.ParameterName];
                foreach (var requirement in conceptConstraint.Declaration.OperatorRequirements)
                {
                    var requiredParameters = requirement.Parameters
                        .Select(parameter => SubstituteConceptSelfType(
                            parameter.ParameterType, argumentType))
                        .ToArray();
                    if (FindOperator(
                            argumentType, requirement.OperatorToken!, requiredParameters) is
                        { } operatorMatch)
                    {
                        conceptOperatorBindings.TryAdd(
                            requirement.OperatorToken!, operatorMatch.Symbol);
                    }
                }
                foreach (var requirement in conceptConstraint.Declaration.MethodRequirements)
                {
                    if (requirement.GenericTypeNames.Length == 0)
                    {
                        if (FindConceptMethodImplementation(argumentType, requirement) is
                            { } methodImplementation)
                        {
                            conceptOperatorBindings.TryAdd(
                                ConceptDeclaration.GetMethodBindingKey(requirement),
                                methodImplementation);
                        }
                        continue;
                    }

                    foreach (var invocation in EnumerateDirectConceptMethodInvocations(declaration)
                        .Where(invocation => ReferenceEquals(
                            invocation.TargetSymbol?.Declaration, requirement)))
                    {
                        var methodTypeArguments = invocation.ConceptMethodTypeArguments
                            .Select(type => GenericTypeSubstitution.Substitute(type, inferred))
                            .ToArray();
                        if (!ConceptMethodTypeArgumentsSatisfyConstraints(
                                requirement, methodTypeArguments))
                        {
                            return null;
                        }
                        if (FindConceptMethodImplementation(
                                argumentType, requirement, methodTypeArguments) is
                            { } methodImplementation)
                        {
                            conceptOperatorBindings.TryAdd(
                                ConceptDeclaration.GetMethodBindingKey(
                                    requirement, methodTypeArguments),
                                methodImplementation);
                        }
                    }
                }
                foreach (var requirement in conceptConstraint.Declaration.PropertyRequirements)
                {
                    if (requirement.HasGetter &&
                        FindConceptPropertyGetterImplementation(argumentType, requirement) is
                            { } getterImplementation)
                    {
                        conceptOperatorBindings.TryAdd(
                            GetConceptPropertyBindingKey(requirement),
                            getterImplementation);
                    }
                    if (requirement.HasSetter &&
                        FindConceptPropertySetterImplementation(argumentType, requirement) is
                            { } setterImplementation)
                    {
                        conceptOperatorBindings.TryAdd(
                            GetConceptPropertySetterBindingKey(requirement),
                            setterImplementation);
                    }
                }
            }
        }
        return new FunctionSymbol(
            specializationModule,
            symbol.FullName,
            parameterTypes,
            returnType,
            symbol.OverloadIndex,
            declaration,
            identity.CIdentifier,
            symbol.ClosedContainingType,
            conceptOperatorBindings,
            inferred);
    }

    private FunctionSymbol CloseContainingClassFunction(
        FunctionSymbol symbol,
        TypeBase? receiverType)
    {
        receiverType = receiverType is null ? null : UnwrapConst(receiverType);
        if (symbol.Declaration?.ParentClassDeclaration is not { } parent ||
            receiverType is null)
        {
            return symbol;
        }

        if (receiverType is ArrayType && IsPublicArrayClass(parent))
        {
            receiverType = CreateClosedPublicArrayType(
                receiverType,
                parent,
                symbol.ModuleName);
        }

        NamedType closedType;
        Dictionary<string, TypeBase> arguments;
        var isGenericArrayExtension = symbol.Declaration is
            { IsExtension: true } && parent.GenericTypeNames.Length > 0 &&
            symbol.Declaration.ExtensionTargetType is { } extensionPattern &&
            UnwrapConst(extensionPattern) is ArrayType &&
            receiverType is ArrayType;
        if (receiverType is NamedType namedReceiver &&
            namedReceiver.ResolvedTypeFullName ==
                new QualifiedIdentifier(parent.ProjectName ?? symbol.ModuleName, parent.FullName) &&
            parent.GenericTypeNames.Length == namedReceiver.TypeArguments.Count)
        {
            closedType = namedReceiver;
            arguments = parent.GenericTypeNames.Zip(closedType.TypeArguments)
                .ToDictionary(pair => pair.First, pair => pair.Second);
        }
        else if (isGenericArrayExtension &&
            symbol.Declaration!.ExtensionTargetType is { } arrayPattern)
        {
            arguments = new Dictionary<string, TypeBase>(StringComparer.Ordinal);
            if (!InferGenericArguments(arrayPattern, receiverType, arguments) ||
                parent.GenericTypeNames.Any(name => !arguments.ContainsKey(name)))
            {
                return symbol;
            }
            var typeArguments = parent.GenericTypeNames
                .Select(name => arguments[name]).ToArray();
            closedType = NamedType.CreateWithTypeArguments(
                parent.FullName.ToString(), typeArguments);
            closedType.SetResolvedType(
                parent.FullName,
                parent.ProjectName ?? symbol.ModuleName,
                parent.ClassType);
            var typeParts = closedType.ResolvedTypeFullName.Parts;
            closedType.ConstructedIdentity = GenericTypeIdentity.Create(
                typeParts[0], string.Join('.', typeParts.Skip(1)), typeArguments);
        }
        else
        {
            return symbol;
        }

        var parameterTypes = symbol.ParameterTypes.Select(parameter =>
            CloseConstructedTypes(GenericTypeSubstitution.Substitute(parameter, arguments))).ToArray();
        var returnType = CloseConstructedTypes(
            GenericTypeSubstitution.Substitute(symbol.ReturnType, arguments));
        var canCreateSpecialization = !ContainsOpenTypeParameter(closedType) &&
            parameterTypes.All(parameter => !ContainsOpenTypeParameter(parameter)) &&
            !ContainsOpenTypeParameter(returnType);
        var specializationName = canCreateSpecialization
            ? symbol.SpecializationName
            : null;
        var specializationModule = symbol.ModuleName;
        var conceptBindings = symbol.ConceptOperatorBindings;
        var genericTypeArguments = symbol.GenericTypeArguments;
        if (canCreateSpecialization && closedType.ConstructedIdentity is not null &&
            (parent.GenericTypeNames.Length > 0 ||
                isGenericArrayExtension || parent.ClassType == ClassType.Struct ||
                parent.ConceptConstraints.Count > 0) &&
            symbol.Declaration is { GenericTypeNames.Length: 0 } declaration)
        {
            string GetSpecializationName(string moduleName) =>
                GenericTypeIdentity.CreateFunction(
                    moduleName,
                    symbol.FullName.ToString() + "@" + closedType.ConstructedIdentity!.CanonicalName,
                    closedType.TypeArguments,
                    parameterTypes,
                    returnType).CIdentifier;

            specializationName = GetSpecializationName(symbol.ModuleName);
            if (IsReferencedModule(symbol.ModuleName) &&
                !_project!.HasReferencedGenericFunctionSpecialization(specializationName))
            {
                specializationModule = _project.Name;
                specializationName = GetSpecializationName(specializationModule);
            }
            genericTypeArguments = arguments;
            conceptBindings = GetClosedClassConceptBindings(parent, arguments);
        }
        return new FunctionSymbol(
            specializationModule,
            symbol.FullName,
            parameterTypes,
            returnType,
            symbol.OverloadIndex,
            symbol.Declaration,
            specializationName,
            closedType,
            conceptBindings,
            genericTypeArguments);
    }

    private IReadOnlyDictionary<string, FunctionSymbol> GetClosedClassConceptBindings(
        ClassDeclaration declaration,
        IReadOnlyDictionary<string, TypeBase> typeArguments)
    {
        var bindings = new Dictionary<string, FunctionSymbol>(StringComparer.Ordinal);
        if (!_resolvedClassConceptConstraints.TryGetValue(
            declaration, out var constraints))
        {
            return bindings;
        }

        foreach (var constraint in constraints)
        {
            var argumentType = typeArguments[constraint.ParameterName];
            foreach (var requirement in constraint.Declaration.OperatorRequirements)
            {
                var parameters = requirement.Parameters.Select(parameter =>
                    SubstituteConceptSelfType(parameter.ParameterType, argumentType)).ToArray();
                if (FindOperator(argumentType, requirement.OperatorToken!, parameters) is
                    { } operation)
                {
                    bindings.TryAdd(requirement.OperatorToken!, operation.Symbol);
                }
            }
            foreach (var requirement in constraint.Declaration.MethodRequirements
                .Where(requirement => requirement.GenericTypeNames.Length == 0))
            {
                if (FindConceptMethodImplementation(argumentType, requirement) is
                    { } method)
                {
                    bindings.TryAdd(
                        ConceptDeclaration.GetMethodBindingKey(requirement), method);
                }
            }
            foreach (var requirement in constraint.Declaration.PropertyRequirements)
            {
                if (FindConceptPropertyGetterImplementation(argumentType, requirement) is
                    { } getter)
                {
                    bindings.TryAdd(
                        $"property:{requirement.Name}", getter);
                }
                if (FindConceptPropertySetterImplementation(argumentType, requirement) is
                    { } setter)
                {
                    bindings.TryAdd(
                        $"property:{requirement.Name}:set", setter);
                }
            }
        }
        return bindings;
    }

    private TypeBase CloseConstructedTypes(TypeBase type)
    {
        switch (type)
        {
            case ConstType constant:
                CloseConstructedTypes(constant.UnderlyingType);
                break;
            case ArrayType array:
                CloseConstructedTypes(array.ElementType);
                SetConstructedIdentity(array);
                break;
            case NullableType nullable:
                CloseConstructedTypes(nullable.UnderlyingType);
                break;
            case FunctionType function:
                CloseConstructedTypes(function.ReturnType);
                foreach (var parameter in function.ParameterTypes)
                {
                    CloseConstructedTypes(parameter);
                }
                break;
            case NamedType named when named.TypeArguments.Count > 0:
                foreach (var argument in named.TypeArguments)
                {
                    CloseConstructedTypes(argument);
                }
                SetConstructedIdentity(named);
                break;
        }
        return type;
    }

    private static bool InferGenericArguments(
        TypeBase pattern,
        TypeBase actual,
        IDictionary<string, TypeBase> inferred)
    {
        pattern = UnwrapConst(pattern);
        actual = UnwrapConst(actual);
        if (pattern is GenericType parameter)
        {
            if (actual is NullType)
            {
                return false;
            }
            if (inferred.TryGetValue(parameter.Name, out var previous))
            {
                return IsType(previous, actual);
            }
            inferred.Add(parameter.Name, actual);
            return true;
        }
        if (ArrayType.TryGetElementType(pattern, out var patternArrayElement))
        {
            return ArrayType.TryGetElementType(actual, out var actualArrayElement) &&
                InferGenericArguments(patternArrayElement, actualArrayElement, inferred);
        }
        if (pattern is NullableType patternNullable)
        {
            return actual is NullableType actualNullable &&
                InferGenericArguments(patternNullable.UnderlyingType,
                    actualNullable.UnderlyingType, inferred);
        }
        if (pattern is NamedType { TypeArguments: { Count: > 0 } } patternNamed)
        {
            return actual is NamedType actualNamed &&
                GetTypeName(patternNamed) == GetTypeName(actualNamed) &&
                patternNamed.TypeArguments.Count == actualNamed.TypeArguments.Count &&
                patternNamed.TypeArguments.Zip(actualNamed.TypeArguments)
                    .All(pair => InferGenericArguments(pair.First, pair.Second, inferred));
        }
        return true;
    }
}

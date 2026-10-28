using CxCompiler.Model;
using CxCompiler.Model.Common;
using CxCompiler.Model.Errors;
using CxCompiler.Model.Expressions;
using CxCompiler.Model.Project;
using CxCompiler.Model.Statements;
using CxCompiler.Model.Types;
using CxCompiler.Model.Types.BuiltInTypes;

namespace CxCompiler.Semantics;

public sealed partial class SemanticBinder
{

    private void BindGenericVirtualDispatchTargets(CxProject project)
    {
        foreach (var context in project.CompilationContexts)
        {
            foreach (var function in EnumerateFunctions(context.DeclarationScope.Declarations))
            {
                foreach (var invocation in CoroutineSyntaxAnalysis.EnumerateBodyExpressions(
                    function.Body ?? []).OfType<InvocationExpression>())
                {
                    if (invocation.TargetSymbol?.Declaration is not
                        {
                            GenericTypeNames.Length: > 0,
                            VirtualSlotIndex: { } slotIndex,
                            ParentClassDeclaration.ClassType: ClassType.Class or ClassType.Interface,
                        } contract)
                    {
                        continue;
                    }

                    if (invocation.TargetSymbol.SpecializationName is null &&
                        function.GenericTypeNames.Length > 0 &&
                        (IsGenericForwardCallReturnBody(function) ||
                            IsGenericVoidForwardCallBody(function)))
                    {
                        continue;
                    }
                    if (invocation.TargetSymbol.SpecializationName is null)
                    {
                        throw new CompilationErrorException(
                            "Generic virtual calls from open generic bodies require a " +
                            "closed caller specialization.",
                            invocation.SourceSpan);
                    }

                    var receiverType = invocation.Receiver?.InferredType;
                    var methodTypeArguments = contract.GenericTypeNames
                        .Select(name => invocation.TargetSymbol.GenericTypeArguments
                            .GetValueOrDefault(name))
                        .ToArray();
                    if (receiverType is null || methodTypeArguments.Any(type =>
                        type is null || ContainsOpenTypeParameter(type)))
                    {
                        throw new CompilationErrorException(
                            "Generic virtual calls from open generic bodies require a " +
                            "closed caller specialization.",
                            invocation.SourceSpan);
                    }

                    var closedMethodTypeArguments = methodTypeArguments
                        .Cast<TypeBase>().ToArray();
                    var closedReceiverRuntimeType = receiverType is NamedType namedReceiverType &&
                        namedReceiverType.TypeArguments.Count > 0 &&
                        GetClassDeclarationIncludingConstructed(namedReceiverType) is
                            { ClassType: ClassType.Class, IsAbstract: false } receiverDeclaration
                        ? (RuntimeType: namedReceiverType, Declaration: receiverDeclaration)
                        : ((NamedType RuntimeType, ClassDeclaration Declaration)?)null;
                    var candidateRuntimeTypes = _types
                        .Where(type => type.Declaration.ClassType == ClassType.Class &&
                            !type.Declaration.IsAbstract &&
                            type.Declaration.GenericTypeNames.Length == 0 &&
                            type.Type is NamedType)
                        .Select(type => (
                            RuntimeType: (NamedType)type.Type,
                            type.Declaration))
                        .Concat(project.GenericTypeInstances
                            .Where(instance =>
                                instance.Declaration.ClassType == ClassType.Class &&
                                !instance.Declaration.IsAbstract)
                            .Select(instance => (
                                RuntimeType: instance.Type,
                                instance.Declaration)))
                        .Concat(closedReceiverRuntimeType is { } closedReceiver
                            ? [closedReceiver]
                            : [])
                        .DistinctBy(candidate =>
                            candidate.RuntimeType.ConstructedIdentity?.CanonicalName ??
                            candidate.RuntimeType.ResolvedTypeFullName.ToString());
                    foreach (var candidate in candidateRuntimeTypes.Where(candidate =>
                        CanAssign(receiverType, candidate.RuntimeType)))
                    {
                        var implementation = FindGenericVirtualRuntimeImplementation(
                            candidate.Declaration, contract.ParentClassDeclaration!, slotIndex);
                        if (implementation is null)
                        {
                            continue;
                        }

                        var implementationSymbol = _symbols.SingleOrDefault(symbol =>
                            ReferenceEquals(symbol.Declaration, implementation));
                        if (implementationSymbol is null)
                        {
                            throw new CompilationErrorException(
                                $"Generic virtual implementation '{implementation.FullName}' " +
                                "cannot be specialized from this project.",
                                invocation.SourceSpan);
                        }

                        var closedOwnerImplementation = CloseContainingClassFunction(
                            implementationSymbol,
                            candidate.RuntimeType);
                        var closedImplementation = CloseGenericFunction(
                            closedOwnerImplementation,
                            invocation.Arguments.Select(argument =>
                                argument.InferredType ?? BuiltInSystemTypes.Void).ToArray(),
                            closedMethodTypeArguments);
                        if (closedImplementation?.SpecializationName is null)
                        {
                            throw new CompilationErrorException(
                                $"Generic virtual implementation '{implementation.FullName}' " +
                                "could not be closed for the call's method type arguments.",
                                invocation.SourceSpan);
                        }

                        var selectedImplementation = SelectGenericFunctionSpecialization(
                            closedImplementation);
                        project.AddGenericFunctionInstance(selectedImplementation);
                        invocation.BindGenericVirtualDispatchTarget(
                            candidate.RuntimeType,
                            selectedImplementation);
                    }
                }
            }
        }

        BindGenericVirtualDispatchTargetsFromGenericCallers(project);
        BindGenericVirtualMethodValueTargets(project);
    }

    private void BindGenericVirtualMethodValueTargets(CxProject project)
    {
        foreach (var context in project.CompilationContexts)
        {
            foreach (var function in EnumerateFunctions(context.DeclarationScope.Declarations))
            {
                foreach (var methodValue in CoroutineSyntaxAnalysis.EnumerateBodyExpressions(
                    function.Body ?? []).OfType<MemberAccessExpression>())
                {
                    if (methodValue.FunctionValueSymbol is not
                        { Declaration: { GenericTypeNames.Length: > 0 } method } symbol ||
                        method.VirtualSlotIndex is null ||
                        method.ParentClassDeclaration?.ClassType is not
                            (ClassType.Class or ClassType.Interface))
                    {
                        continue;
                    }

                    var contract = method.VirtualContract ?? method;
                    var slotIndex = contract.VirtualSlotIndex ?? method.VirtualSlotIndex.Value;
                    var methodTypeArguments = method.GenericTypeNames.Select(name =>
                        symbol.GenericTypeArguments.GetValueOrDefault(name)).ToArray();
                    var receiverType = methodValue.Target.InferredType;
                    if (receiverType is null || methodTypeArguments.Any(type =>
                        type is null || ContainsOpenTypeParameter(type)))
                    {
                        throw new CompilationErrorException(
                            "Generic virtual method values require a known receiver type and " +
                            "closed method type arguments.", methodValue.SourceSpan);
                    }

                    var closedMethodTypeArguments = methodTypeArguments
                        .Cast<TypeBase>().ToArray();
                    var candidateRuntimeTypes = _types
                        .Where(type => type.Declaration.ClassType == ClassType.Class &&
                            !type.Declaration.IsAbstract &&
                            type.Declaration.GenericTypeNames.Length == 0 &&
                            type.Type is NamedType)
                        .Select(type => ((NamedType)type.Type, type.Declaration))
                        .Concat(project.GenericTypeInstances
                            .Where(instance =>
                                instance.Declaration.ClassType == ClassType.Class &&
                                !instance.Declaration.IsAbstract)
                            .Select(instance => (instance.Type, instance.Declaration)));
                    foreach (var (runtimeType, runtimeDeclaration) in candidateRuntimeTypes
                        .Where(candidate => CanAssign(receiverType, candidate.Item1)))
                    {
                        var implementation = FindGenericVirtualRuntimeImplementation(
                            runtimeDeclaration, contract.ParentClassDeclaration!, slotIndex);
                        if (implementation is null)
                        {
                            continue;
                        }
                        var implementationSymbol = _symbols.SingleOrDefault(candidate =>
                            ReferenceEquals(candidate.Declaration, implementation));
                        if (implementationSymbol is null)
                        {
                            throw new CompilationErrorException(
                                $"Generic virtual implementation '{implementation.FullName}' " +
                                "cannot be specialized from this project.", methodValue.SourceSpan);
                        }
                        var closedOwnerImplementation = CloseContainingClassFunction(
                            implementationSymbol, runtimeType);
                        var closedImplementation = CloseGenericFunction(
                            closedOwnerImplementation, symbol.ParameterTypes,
                            closedMethodTypeArguments);
                        if (closedImplementation?.SpecializationName is null)
                        {
                            throw new CompilationErrorException(
                                $"Generic virtual implementation '{implementation.FullName}' " +
                                "could not be closed for this method value.", methodValue.SourceSpan);
                        }
                        var selectedImplementation = SelectGenericFunctionSpecialization(
                            closedImplementation);
                        project.AddGenericFunctionInstance(selectedImplementation);
                        methodValue.BindGenericVirtualDispatchTarget(
                            runtimeType, selectedImplementation);
                    }
                }
            }
        }
    }

    private void BindGenericVirtualDispatchTargetsFromGenericCallers(CxProject project)
    {
        foreach (var context in project.CompilationContexts)
        {
            foreach (var function in EnumerateFunctions(context.DeclarationScope.Declarations)
                .Where(function => function.GenericTypeNames.Length > 0 &&
                    (IsGenericForwardCallReturnBody(function) ||
                        IsGenericVoidForwardCallBody(function))))
            {
                foreach (var caller in project.GenericFunctionInstances.Where(instance =>
                    ReferenceEquals(instance.Declaration, function) &&
                    instance.SpecializationName is not null).ToArray())
                {
                    var callerSpecialization = caller.SpecializationName!;
                    var substitutions = new Dictionary<string, TypeBase>(
                        caller.GenericTypeArguments, StringComparer.Ordinal);
                    if (function.ParentClassDeclaration is
                        { GenericTypeNames.Length: > 0 } owner &&
                        caller.ClosedContainingType is { } closedOwner)
                    {
                        foreach (var pair in owner.GenericTypeNames.Zip(
                            closedOwner.TypeArguments))
                        {
                            substitutions[pair.First] = pair.Second;
                        }
                    }

                    var invocation = GetGenericForwardCallInvocation(function) ??
                        throw new InternalCompilerException(
                            "Generic virtual forwarding body has no invocation.");
                    if (invocation.TargetSymbol is not
                        { Declaration: { GenericTypeNames.Length: > 0,
                            VirtualSlotIndex: { } slotIndex,
                            ParentClassDeclaration.ClassType: ClassType.Class or ClassType.Interface } contract,
                            } contractSymbol)
                    {
                        continue;
                    }

                    var methodTypeArguments = contract.GenericTypeNames.Select(name =>
                        contractSymbol.GenericTypeArguments.TryGetValue(name, out var type)
                            ? CloseConstructedTypes(
                                GenericTypeSubstitution.Substitute(type, substitutions))
                            : null).ToArray();
                    if (methodTypeArguments.Any(type => type is null ||
                        ContainsOpenTypeParameter(type)))
                    {
                        throw new CompilationErrorException(
                            "Generic virtual forwarding requires closed method type arguments " +
                            "at each generic caller specialization.", invocation.SourceSpan);
                    }
                    var closedMethodTypeArguments = methodTypeArguments
                        .Cast<TypeBase>().ToArray();
                    var receiverType = invocation.Receiver?.InferredType;
                    if (receiverType is null)
                    {
                        throw new CompilationErrorException(
                            "Generic virtual forwarding requires a receiver with a known " +
                            "closed class type.", invocation.SourceSpan);
                    }
                    receiverType = CloseConstructedTypes(
                        GenericTypeSubstitution.Substitute(receiverType, substitutions));
                    if (ContainsOpenTypeParameter(receiverType))
                    {
                        throw new CompilationErrorException(
                            "Generic virtual forwarding through an open receiver type is not " +
                            "supported.", invocation.SourceSpan);
                    }
                    var argumentTypes = invocation.Arguments.Select(argument =>
                    {
                        var inferredType = argument.InferredType ?? BuiltInSystemTypes.Void;
                        return CloseConstructedTypes(
                            GenericTypeSubstitution.Substitute(inferredType, substitutions));
                    }).ToArray();
                    if (argumentTypes.Any(ContainsOpenTypeParameter))
                    {
                        throw new CompilationErrorException(
                            "Generic virtual forwarding requires closed argument types at " +
                            "each generic caller specialization.", invocation.SourceSpan);
                    }
                    var closedContractSymbol = SubstituteFunctionSymbol(
                        CloseContainingClassFunction(contractSymbol, receiverType),
                        substitutions);
                    var closedContract = CloseGenericFunction(
                        closedContractSymbol, argumentTypes, closedMethodTypeArguments);
                    if (closedContract?.SpecializationName is null)
                    {
                        throw new CompilationErrorException(
                            $"Generic virtual contract '{contract.FullName}' could not be " +
                            "closed for this generic caller specialization.", invocation.SourceSpan);
                    }
                    invocation.BindClosedGenericTarget(callerSpecialization, closedContract);

                    var candidateRuntimeTypes = _types
                        .Where(type => type.Declaration.ClassType == ClassType.Class &&
                            !type.Declaration.IsAbstract &&
                            type.Declaration.GenericTypeNames.Length == 0 &&
                            type.Type is NamedType)
                        .Select(type => ((NamedType)type.Type, type.Declaration))
                        .Concat(project.GenericTypeInstances
                            .Where(instance =>
                                instance.Declaration.ClassType == ClassType.Class &&
                                !instance.Declaration.IsAbstract)
                            .Select(instance => (instance.Type, instance.Declaration)));
                    foreach (var (runtimeType, runtimeDeclaration) in candidateRuntimeTypes
                        .Where(candidate => CanAssign(receiverType, candidate.Item1)))
                    {
                        var implementation = FindGenericVirtualRuntimeImplementation(
                            runtimeDeclaration, contract.ParentClassDeclaration!, slotIndex);
                        if (implementation is null)
                        {
                            continue;
                        }
                        var implementationSymbol = _symbols.SingleOrDefault(symbol =>
                            ReferenceEquals(symbol.Declaration, implementation));
                        if (implementationSymbol is null)
                        {
                            throw new CompilationErrorException(
                                $"Generic virtual implementation '{implementation.FullName}' " +
                                "cannot be specialized from this project.", invocation.SourceSpan);
                        }
                        var closedOwnerImplementation = CloseContainingClassFunction(
                            implementationSymbol, runtimeType);
                        var closedImplementation = CloseGenericFunction(
                            closedOwnerImplementation, argumentTypes,
                            closedMethodTypeArguments);
                        if (closedImplementation?.SpecializationName is null)
                        {
                            throw new CompilationErrorException(
                                $"Generic virtual implementation '{implementation.FullName}' " +
                                "could not be closed for this caller specialization.",
                                invocation.SourceSpan);
                        }
                        var selectedImplementation = SelectGenericFunctionSpecialization(
                            closedImplementation);
                        project.AddGenericFunctionInstance(selectedImplementation);
                        invocation.BindGenericVirtualDispatchTarget(
                            runtimeType, selectedImplementation);
                    }
                }
            }
        }
    }

    private static FunctionDeclaration? FindGenericVirtualRuntimeImplementation(
        ClassDeclaration runtimeDeclaration,
        ClassDeclaration contractOwner,
        int slotIndex)
    {
        if (contractOwner.ClassType == ClassType.Interface)
        {
            return runtimeDeclaration.InterfaceDispatchTables
                .Where(table => ReferenceEquals(table.Interface, contractOwner))
                .SelectMany(table => table.Slots)
                .SingleOrDefault(slot => slot.Index == slotIndex)
                ?.ImplementationFunction;
        }

        return runtimeDeclaration.VirtualMethodSlots
            .SingleOrDefault(slot => slot.Index == slotIndex)
            ?.Implementation;
    }

    private void RegisterClosedGenericClassFunctionInstances()
    {
        foreach (var (type, declaration) in _project!.GenericTypeInstances.ToArray())
        {
            if (declaration.GenericTypeNames.Length == 0)
            {
                continue;
            }

            var interfaceImplementations = declaration.InterfaceDispatchTables
                .SelectMany(table => table.Slots)
                .Select(slot => slot.ImplementationFunction)
                .OfType<FunctionDeclaration>()
                .ToHashSet();
            var ownerFunctions = declaration.MemberDeclarations.Declarations
                .OfType<FunctionDeclaration>()
                .Concat(interfaceImplementations)
                .Concat(declaration.InterfaceDispatchTables
                    .SelectMany(table => table.Slots)
                    .Select(slot => slot.ImplementationAccessor?.BodyFunction)
                    .OfType<FunctionDeclaration>()
                    .Where(IsClosedGenericConstructedResultAccessor));
            foreach (var method in ownerFunctions
                .Where(method => method is not ConstructorDeclaration &&
                    method.GenericTypeNames.Length == 0 &&
                    !method.IsStatic &&
                    (method.VirtualSlotIndex is null ||
                        interfaceImplementations.Contains(method))))
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
                .Where(instance => instance.SpecializationName is { } specialization &&
                    instance.Declaration is { } declaration &&
                    (instance.ClosedContainingType is { } containingType &&
                        containingType.TypeArguments.All(argument =>
                            !ContainsOpenTypeParameter(argument)) ||
                     declaration.GenericTypeNames.Length > 0 &&
                        declaration.GenericTypeNames.All(
                            instance.GenericTypeArguments.ContainsKey) &&
                        declaration.GenericTypeNames.All(name =>
                            !ContainsOpenTypeParameter(instance.GenericTypeArguments[name]))) &&
                    processed.Add(specialization))
                .ToArray();
            if (callers.Length == 0)
            {
                break;
            }

            foreach (var caller in callers)
            {
                var declaration = caller.Declaration!;
                var owner = declaration.ParentClassDeclaration;
                var closedOwner = caller.ClosedContainingType;
                var ownerArguments = caller.GenericTypeArguments.ToDictionary(
                    pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
                if (owner is { GenericTypeNames.Length: > 0 } genericOwner &&
                    closedOwner is { } closedContainingType &&
                    closedContainingType.TypeArguments.Count == genericOwner.GenericTypeNames.Length)
                {
                    foreach (var pair in genericOwner.GenericTypeNames.Zip(
                        closedContainingType.TypeArguments))
                    {
                        ownerArguments[pair.First] = pair.Second;
                    }
                }
                foreach (var invocation in EnumerateGenericCallerExpressions(
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
                    if (receiverType is null && owner is not null &&
                        ReferenceEquals(targetOwner, owner) && closedOwner is not null)
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

    private void RegisterClosedGenericMethodFunctionTargets()
    {
        var processed = new HashSet<string>(StringComparer.Ordinal);
        while (true)
        {
            var callers = _project!.GenericFunctionInstances
                .Where(instance => instance.SpecializationName is { } specialization &&
                    instance.Declaration is { GenericTypeNames.Length: > 0 } declaration &&
                    declaration.IsAsync &&
                    declaration.GenericTypeNames.All(
                        instance.GenericTypeArguments.ContainsKey) &&
                    declaration.GenericTypeNames.All(name =>
                        !ContainsOpenTypeParameter(instance.GenericTypeArguments[name])) &&
                    processed.Add(specialization))
                .ToArray();
            if (callers.Length == 0)
            {
                break;
            }

            foreach (var caller in callers)
            {
                var declaration = caller.Declaration!;
                var callerName = caller.SpecializationName!;
                var typeArguments = caller.GenericTypeArguments.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value,
                    StringComparer.Ordinal);
                if (declaration.ParentClassDeclaration is
                        { GenericTypeNames.Length: > 0 } owner &&
                    caller.ClosedContainingType is { } closedOwner &&
                    closedOwner.TypeArguments.Count == owner.GenericTypeNames.Length)
                {
                    foreach (var pair in owner.GenericTypeNames.Zip(
                        closedOwner.TypeArguments))
                    {
                        typeArguments[pair.First] = pair.Second;
                    }
                }

                foreach (var invocation in EnumerateGenericCallerExpressions(
                    declaration.Body ?? []).OfType<InvocationExpression>())
                {
                    if (invocation.TargetSymbol is not
                        { Declaration: { GenericTypeNames.Length: > 0 } targetDeclaration } openTarget ||
                        targetDeclaration.ParentClassDeclaration is not null)
                    {
                        continue;
                    }

                    var argumentTypes = invocation.Arguments
                        .Select(argument => argument.InferredType is { } argumentType
                            ? CloseConstructedTypes(
                                GenericTypeSubstitution.Substitute(
                                    argumentType, typeArguments))
                            : throw new InternalCompilerException(
                                "Generic method call argument has no inferred type."))
                        .ToArray();
                    var explicitTypeArguments = invocation.ExplicitTypeArguments
                        .Select(type => CloseConstructedTypes(
                            GenericTypeSubstitution.Substitute(type, typeArguments)))
                        .ToArray();
                    if (explicitTypeArguments.Length == 0 &&
                        targetDeclaration.GenericTypeNames.All(
                            openTarget.GenericTypeArguments.ContainsKey))
                    {
                        explicitTypeArguments = targetDeclaration.GenericTypeNames
                            .Select(name => CloseConstructedTypes(
                                GenericTypeSubstitution.Substitute(
                                    openTarget.GenericTypeArguments[name], typeArguments)))
                            .ToArray();
                    }
                    if (argumentTypes.Any(ContainsOpenTypeParameter) ||
                        explicitTypeArguments.Any(ContainsOpenTypeParameter))
                    {
                        continue;
                    }

                    var closedTarget = CloseGenericFunction(
                        openTarget, argumentTypes, explicitTypeArguments);
                    if (closedTarget?.SpecializationName is null)
                    {
                        throw new CompilationErrorException(
                            $"Cannot close generic method call to '{openTarget.FullName}' " +
                            $"from '{declaration.FullName}'.",
                            invocation.SourceSpan);
                    }

                    invocation.BindClosedGenericTarget(callerName, closedTarget);
                    _project.AddGenericFunctionInstance(closedTarget);
                }
            }
        }
    }

    private static IEnumerable<ExpressionBase> EnumerateGenericCallerExpressions(
        IEnumerable<StatementBase> body)
    {
        var expressions = CoroutineSyntaxAnalysis.EnumerateBodyExpressions(body)
            .ToArray();
        foreach (var expression in expressions)
        {
            yield return expression;
            if (expression is LambdaExpression
                { GeneratedFunction.Body: { } lambdaBody })
            {
                foreach (var nested in EnumerateGenericCallerExpressions(lambdaBody))
                {
                    yield return nested;
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
                    caller.Declaration is not { Body: { } body } declaration)
                {
                    continue;
                }

                var owner = declaration.ParentClassDeclaration;
                var hasClosedOwner = owner is { GenericTypeNames.Length: > 0 } &&
                    caller.ClosedContainingType is { } closedContainingType &&
                    closedContainingType.TypeArguments.All(argument =>
                        !ContainsOpenTypeParameter(argument));
                var hasClosedMethod = declaration.GenericTypeNames.Length > 0 &&
                    declaration.GenericTypeNames.All(caller.GenericTypeArguments.ContainsKey) &&
                    declaration.GenericTypeNames.All(name =>
                        !ContainsOpenTypeParameter(caller.GenericTypeArguments[name]));
                if (!hasClosedOwner && !hasClosedMethod)
                {
                    continue;
                }

                var ownerArguments = caller.GenericTypeArguments.ToDictionary(
                    pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
                if (hasClosedOwner && owner is { } closedGenericOwner &&
                    caller.ClosedContainingType is { } closedOwner)
                {
                    foreach (var pair in closedGenericOwner.GenericTypeNames.Zip(
                        closedOwner.TypeArguments))
                    {
                        ownerArguments[pair.First] = pair.Second;
                    }
                }
                var objectCreations = CoroutineSyntaxAnalysis.EnumerateBodyExpressions(body)
                    .OfType<ObjectCreationExpression>();
                if (declaration is ConstructorDeclaration && owner is not null)
                {
                    objectCreations = objectCreations.Concat(
                        owner.MemberDeclarations.Declarations.OfType<FieldDeclaration>()
                            .Where(field => !field.IsStatic && field.Initializer is not null)
                            .SelectMany(field => CoroutineSyntaxAnalysis.EnumerateBodyExpressions(
                                [new ExpressionStatement(field.Initializer!)]))
                            .OfType<ObjectCreationExpression>());
                }

                foreach (var creation in objectCreations)
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
            !IsGenericForwardCallReturnBody(declaration) &&
                !IsGenericVoidForwardCallBody(declaration) ||
            !_expandedGenericForwardCalls.Add(callerName))
        {
            return;
        }

        var isVoidForward = IsGenericVoidForwardCallBody(declaration);
        var invocation = GetGenericForwardCallInvocation(declaration) ??
            throw new InternalCompilerException(
                "Generic forwarding body has no supported invocation.");
        var hasMatchingTypeParameter = invocation.Arguments
            .OfType<IdentifierExpression>()
            .Any(argument =>
            {
                var parameterType = declaration.Parameters.SingleOrDefault(parameter =>
                    parameter.Name == argument.Identifier.ToString())?.ParameterType
                    as GenericType;
                var forwardedTypeParameter = parameterType?.Name ??
                    GetGenericForwardAliasTypeParameter(
                        declaration, argument.Identifier.ToString());
                return forwardedTypeParameter is not null &&
                    declaration.GenericTypeNames.Contains(
                        forwardedTypeParameter, StringComparer.Ordinal) &&
                    (isVoidForward || declaration.ReturnType is GenericType returnType &&
                        forwardedTypeParameter == returnType.Name);
            });
        if (!hasMatchingTypeParameter || invocation.Arguments.Any(argument =>
                !IsSupportedGenericForwardArgument(argument, declaration)))
        {
            throw new CompilationErrorException(
                "Generic forwarding arguments must include a matching type-parameter parameter " +
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

        if (openTarget.Declaration.VirtualSlotIndex is not null &&
            openTarget.Declaration.ParentClassDeclaration?.ClassType is
                ClassType.Class or ClassType.Interface)
        {
            // Generic virtual forwarding is closed with the caller and runtime
            // receiver together in BindGenericVirtualDispatchTargetsFromGenericCallers.
            return;
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
        if (targetClass?.ClassType == ClassType.Interface)
        {
            return invocation.Receiver is not null;
        }
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
            .Select(type => CloseConstructedGenericTypeArguments(
                GenericTypeSubstitution.Substitute(type, inferred)))
            .ToArray();
        var returnType = CloseConstructedGenericTypeArguments(
            GenericTypeSubstitution.Substitute(symbol.ReturnType, inferred));
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
        var projectIdentity = GenericTypeIdentity.CreateFunction(
            _project!.Name,
            functionIdentityName,
            typeArguments,
            parameterTypes,
            returnType);
        var hasProjectReplacement = projectIdentity.CanonicalName !=
                definingIdentity.CanonicalName &&
            _genericFunctionSpecializations.ContainsKey(projectIdentity.CanonicalName);
        var specializationModule = IsReferencedModule(symbol.ModuleName) &&
            (hasProjectReplacement ||
             !_genericFunctionSpecializations.ContainsKey(definingIdentity.CanonicalName))
                ? _project.Name
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

    private static FunctionSymbol SubstituteFunctionSymbol(
        FunctionSymbol symbol,
        IReadOnlyDictionary<string, TypeBase> substitutions)
    {
        var closedContainingType = symbol.ClosedContainingType is { } owner &&
            GenericTypeSubstitution.Substitute(owner, substitutions) is NamedType closedOwner
                ? closedOwner
                : symbol.ClosedContainingType;
        var genericArguments = symbol.GenericTypeArguments.ToDictionary(
            pair => pair.Key,
            pair => GenericTypeSubstitution.Substitute(pair.Value, substitutions),
            StringComparer.Ordinal);
        return new FunctionSymbol(
            symbol.ModuleName,
            symbol.FullName,
            symbol.ParameterTypes.Select(type =>
                GenericTypeSubstitution.Substitute(type, substitutions)).ToArray(),
            GenericTypeSubstitution.Substitute(symbol.ReturnType, substitutions),
            symbol.OverloadIndex,
            symbol.Declaration,
            symbol.SpecializationName,
            closedContainingType,
            symbol.ConceptOperatorBindings,
            genericArguments,
            symbol.ImplementationDeclaration);
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

    private TypeBase CloseConstructedGenericTypeArguments(TypeBase type)
    {
        switch (type)
        {
            case ConstType constant:
                CloseConstructedGenericTypeArguments(constant.UnderlyingType);
                break;
            case ArrayType array:
                CloseConstructedGenericTypeArguments(array.ElementType);
                break;
            case NullableType nullable:
                CloseConstructedGenericTypeArguments(nullable.UnderlyingType);
                break;
            case FunctionType function:
                CloseConstructedGenericTypeArguments(function.ReturnType);
                foreach (var parameter in function.ParameterTypes)
                {
                    CloseConstructedGenericTypeArguments(parameter);
                }
                break;
            case NamedType named when named.TypeArguments.Count > 0:
                foreach (var argument in named.TypeArguments)
                {
                    CloseConstructedGenericTypeArguments(argument);
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

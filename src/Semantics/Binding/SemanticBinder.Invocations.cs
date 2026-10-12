using CxCompiler.Model;
using CxCompiler.Model.Common;
using CxCompiler.Model.Errors;
using CxCompiler.Model.Expressions;
using CxCompiler.Model.Types;
using CxCompiler.Model.Types.BuiltInTypes;

namespace CxCompiler.Semantics;

public sealed partial class SemanticBinder
{

    private TypeBase BindInvocation(
        InvocationExpression invocation,
        FunctionDeclaration function,
        IReadOnlyList<QualifiedIdentifier> imports,
        LocalScope scope)
    {
        var currentNamespace = GetCurrentNamespace(function);
        if (invocation.Target is MemberAccessExpression delegateMember &&
            delegateMember.MemberName == "GetInvocationList")
        {
            var delegateReceiverType = BindExpression(
                delegateMember.Target,
                function,
                imports,
                scope);
            if (UnwrapConst(delegateReceiverType) is NamedType
                {
                    IsDelegate: true,
                    DelegateSignature: { } delegateSignature,
                } delegateType)
            {
                if (invocation.Arguments.Count != 0 ||
                    invocation.ExplicitTypeArguments.Count != 0)
                {
                    throw new CompilationErrorException(
                        "Delegate.GetInvocationList does not accept arguments.",
                        invocation.SourceSpan);
                }

                var location = invocation.SourceSpan ?? throw new InternalCompilerException(
                    "Delegate invocation-list access has no source location.");
                invocation.BindDelegateInvocationList(
                    delegateSignature,
                    delegateMember.Target,
                    $"__cx_delegate_list_{location.StartLine}_{location.StartColumn}");
                return new ArrayType(delegateType);
            }
        }

        FunctionType? functionValueType = null;
        if (invocation.Target is IdentifierExpression functionValue &&
            functionValue.Identifier.Parts.Length == 1 &&
            scope.TryLookup(functionValue.Identifier.Parts[0], out _))
        {
            functionValueType = GetFunctionSignature(BindExpression(
                functionValue,
                function,
                imports,
                scope));
        }
        else if (invocation.Target is ArrayAccessExpression)
        {
            functionValueType = GetFunctionSignature(BindExpression(
                invocation.Target,
                function,
                imports,
                scope));
            if (functionValueType is null)
            {
                throw new CompilationErrorException(
                    $"Expression '{invocation.Target.GetType().Name}' cannot be invoked.",
                    invocation.SourceSpan);
            }
        }

        if (functionValueType is not null)
        {
            var functionValueArgumentTypes = new List<TypeBase>();
            for (var index = 0; index < invocation.Arguments.Count; index++)
            {
                var expectedType = index < functionValueType.ParameterTypes.Count
                    ? functionValueType.ParameterTypes[index]
                    : null;
                functionValueArgumentTypes.Add(BindExpression(
                    invocation.Arguments[index],
                    function,
                    imports,
                    scope,
                    expectedType));
            }

            if (functionValueType.ParameterTypes.Count != functionValueArgumentTypes.Count ||
                !functionValueType.ParameterTypes.Zip(functionValueArgumentTypes)
                    .All(pair => CanAssign(pair.First, pair.Second)))
            {
                throw new CompilationErrorException(
                    $"Function value '{invocation.Target.GetType().Name}' cannot be called with " +
                    $"({string.Join(", ", functionValueArgumentTypes.Select(GetTypeName))}).",
                    invocation.SourceSpan);
            }

            var location = invocation.SourceSpan ?? throw new InternalCompilerException(
                "Function value invocation has no source location.");
            invocation.BindFunctionType(
                functionValueType,
                $"__cx_function_value_{location.StartLine}_{location.StartColumn}");
            return functionValueType.ReturnType;
        }

        var argumentTypes = new List<TypeBase>();
        for (var index = 0; index < invocation.Arguments.Count; index++)
        {
            var argument = invocation.Arguments[index];
            var expectedType = argument is LambdaExpression or MemberAccessExpression
                ? GetExpectedFunctionParameterType(
                    invocation, index, function, imports, scope, currentNamespace)
                : null;
            argumentTypes.Add(BindExpression(
                argument, function, imports, scope, expectedType));
        }

        foreach (var typeArgument in invocation.ExplicitTypeArguments)
        {
            ResolveTypeReference(typeArgument, currentNamespace, imports);
        }
        ExpressionBase? receiver = null;
        TypeBase? receiverType = null;
        IReadOnlyList<(FunctionSymbol Symbol, int BaseDepth)> namedCandidates;
        string sourceDisplay;

        if (invocation.Target is IdentifierExpression localFunctionTarget &&
            localFunctionTarget.Identifier.Parts.Length == 1 &&
            scope.TryLookupFunctions(localFunctionTarget.Identifier.Parts[0], out var localFunctions))
        {
            sourceDisplay = localFunctionTarget.Identifier.ToString();
            namedCandidates = localFunctions.Select(symbol => (symbol, 0)).ToArray();
        }
        else if (invocation.Target is IdentifierExpression identifier &&
            identifier.Identifier.Parts.Length == 1 &&
            (function.ParentClassDeclaration ??
                function.LambdaOwner?.EnclosingClassDeclaration) is { } currentClass)
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
                if (memberAccess.StaticContainingTypeArguments is { } containingTypeArguments)
                {
                    if (staticClass.GenericTypeNames.Length != containingTypeArguments.Count)
                    {
                        throw new CompilationErrorException(
                            $"Type '{staticClass.Name}' expects {staticClass.GenericTypeNames.Length} generic arguments, " +
                            $"but got {containingTypeArguments.Count}.",
                            memberAccess.SourceSpan);
                    }
                    var closedStaticType = NamedType.CreateWithTypeArguments(
                        staticClass.FullName.ToString(), containingTypeArguments);
                    closedStaticType.SetResolvedType(
                        staticClass.FullName,
                        staticClass.ProjectName ?? staticTarget.ModuleName,
                        staticClass.ClassType);
                    foreach (var typeArgument in containingTypeArguments)
                    {
                        ResolveTypeReference(typeArgument, currentNamespace, imports);
                    }
                    ValidateGenericTypeConstraints(staticClass, closedStaticType);
                    if (containingTypeArguments.All(argument => argument is not GenericType))
                    {
                        var typeParts = closedStaticType.ResolvedTypeFullName.Parts;
                        closedStaticType.ConstructedIdentity = GenericTypeIdentity.Create(
                            typeParts[0],
                            string.Join('.', typeParts.Skip(1)),
                            containingTypeArguments);
                    }
                    receiverType = closedStaticType;
                }
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
                namedCandidates = GetMemberClassDeclarations(receiverType)
                    .SelectMany(receiverClass => FindFunctions(
                        receiverClass, memberAccess.MemberName))
                    .Where(candidate => candidate.Symbol.Declaration is null ||
                        candidate.Symbol.Declaration.IsStatic == false)
                    .DistinctBy(candidate => candidate.Symbol)
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
            if (receiver is not null && receiverType is not null &&
                invocation.Target is MemberAccessExpression conceptMember &&
                TryBindConceptMethodInvocation(
                    invocation,
                    conceptMember,
                    receiver,
                    receiverType,
                    argumentTypes,
                    function,
                    imports,
                    scope,
                    out var conceptMethodReturnType))
            {
                return conceptMethodReturnType;
            }
            throw new CompilationErrorException(
                $"Cannot resolve function '{sourceDisplay}' with {invocation.Arguments.Count} argument(s).");
        }

        _genericConstraintFailure = null;
        var candidates = namedCandidates
            .Select(candidate => (
                Symbol: CloseGenericFunction(
                    CloseContainingClassFunction(
                        candidate.Symbol,
                        receiverType is not null && receiver is null
                            ? receiverType
                            : GetConstrainedMemberReceiverType(
                                receiverType, candidate.Symbol)),
                    argumentTypes,
                    invocation.ExplicitTypeArguments),
                candidate.BaseDepth))
            .Where(candidate => candidate.Symbol is not null)
            .Select(candidate => (Symbol: candidate.Symbol!, candidate.BaseDepth))
            .Where(candidate => ParametersMatch(candidate.Symbol, argumentTypes))
            .ToArray();

        if (function.IsStatic && receiver is null &&
            function.LambdaOwner?.ThisSymbol is null)
        {
            candidates = candidates
                .Where(candidate => candidate.Symbol.Declaration?.IsStatic != false)
                .ToArray();
        }

        if (receiverType is ConstType || receiver is null && IsFunctionThisConst(function))
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
            candidates = SelectBestConversionCandidates(
                candidates,
                argumentTypes,
                candidate => candidate.Symbol.ParameterTypes);
            var concreteCandidates = candidates
                .Where(candidate => candidate.Symbol.SpecializationName is null)
                .ToArray();
            candidates = candidates
                .Where(candidate => candidate.Symbol.SpecializationName is null ||
                    !concreteCandidates.Any(concrete =>
                        concrete.Symbol.ParameterTypes.Count == candidate.Symbol.ParameterTypes.Count &&
                        concrete.Symbol.ParameterTypes.Zip(candidate.Symbol.ParameterTypes)
                            .All(pair => IsType(pair.First, pair.Second))))
                .ToArray();
        }

        if (candidates.Length == 0)
        {
            if (_genericConstraintFailure is { } constraintFailure)
            {
                throw new CompilationErrorException(constraintFailure, invocation.SourceSpan);
            }
            throw new CompilationErrorException(
                $"No overload of '{sourceDisplay}' accepts ({string.Join(", ", argumentTypes.Select(GetTypeName))}).");
        }

        if (candidates.Length > 1)
        {
            throw new CompilationErrorException(
                $"Function call '{sourceDisplay}' with {invocation.Arguments.Count} argument(s) is ambiguous. Candidates:\n" +
                string.Join("\n", candidates.Select(candidate =>
                    $"  {GetTypeName(candidate.Symbol.ReturnType)} {candidate.Symbol.FullName}(" +
                    string.Join(", ", candidate.Symbol.ParameterTypes.Select(GetTypeName)) + ")")),
                invocation.SourceSpan);
        }

        var target = candidates[0];
        target = (SelectGenericFunctionSpecialization(target.Symbol), target.BaseDepth);
        _project!.AddGenericFunctionInstance(target.Symbol);
        int? dispatchSlotIndex = null;
        NamedType? dispatchInterfaceType = receiverType is not null &&
            UnwrapConst(receiverType) is NamedType { ClassType: ClassType.Interface } directInterfaceType
                ? directInterfaceType
                : null;
        var constrainedInterfaceTypes = receiverType is not null &&
            UnwrapConst(receiverType) is GenericType constrainedParameter
                ? GetActiveInterfaceConstraintTypes(constrainedParameter)
                : [];
        if (receiverType is not null &&
            target.Symbol.Declaration is { IsExtension: false } targetDeclaration)
        {
            var receiverInterface = dispatchInterfaceType is not null
                ? GetClassDeclarationIncludingConstructed(dispatchInterfaceType)
                : null;
            if (receiverInterface is null)
            {
                dispatchInterfaceType = constrainedInterfaceTypes.FirstOrDefault(type =>
                    GetClassDeclarationIncludingConstructed(type)?.VirtualMethodSlots.Any(slot =>
                        FunctionSignaturesMatch(slot.Contract, targetDeclaration)) == true);
                receiverInterface = dispatchInterfaceType is null
                    ? null
                    : GetClassDeclarationIncludingConstructed(dispatchInterfaceType);
            }
            if (receiverInterface is not null)
            {
                dispatchSlotIndex = receiverInterface.VirtualMethodSlots
                    .Single(slot => FunctionSignaturesMatch(slot.Contract, targetDeclaration))
                    .Index;
            }
        }
        invocation.BindTarget(
            target.Symbol,
            receiver,
            target.BaseDepth,
            dispatchSlotIndex,
            dispatchSlotIndex is null
                ? null
                : $"__cx_iface_receiver_{_interfaceReceiverIndex++}",
            dispatchInterfaceType);
        if (target.Symbol.SpecializationName is not null)
        {
            RegisterClosedGenericForwardCall(target.Symbol);
        }
        invocation.AddDefaultArguments(BindDefaultArguments(
            target.Symbol,
            invocation.Arguments.Count,
            function,
            imports,
            scope));
        foreach (var pair in invocation.Arguments.Zip(target.Symbol.ParameterTypes))
        {
            ApplyContextualType(pair.First, pair.Second);
        }
        return target.Symbol.ReturnType;
    }

    private bool TryBindConceptMethodInvocation(
        InvocationExpression invocation,
        MemberAccessExpression memberAccess,
        ExpressionBase receiver,
        TypeBase receiverType,
        IReadOnlyList<TypeBase> argumentTypes,
        FunctionDeclaration function,
        IReadOnlyList<QualifiedIdentifier> imports,
        LocalScope scope,
        out TypeBase returnType)
    {
        returnType = BuiltInSystemTypes.Void;
        var receiverTypeIsConst = receiverType is ConstType;
        if (UnwrapConst(receiverType) is not GenericType genericType ||
            !IsGenericTypeParameterInScope(function, genericType.Name))
        {
            return false;
        }

        var matches = new List<(
            string ModuleName,
            ConceptDeclaration Concept,
            FunctionDeclaration Requirement,
            IReadOnlyList<TypeBase> MethodTypeArguments,
            IReadOnlyList<TypeBase> ParameterTypes,
            TypeBase ReturnType)>();
        foreach (var constraint in GetConceptConstraints(function)
            .Where(constraint => constraint.ParameterName == genericType.Name))
        {
            foreach (var conceptName in constraint.Concepts)
            {
                var concept = FindConcept(
                    conceptName.ToString(), function.Namespace, imports);
                if (concept is null)
                {
                    continue;
                }

                foreach (var requirement in concept.Value.Declaration.MethodRequirements
                    .Where(candidate => candidate.Name == memberAccess.MemberName &&
                        (!receiverTypeIsConst || candidate.Const)))
                {
                    if (!TryCloseConceptMethodRequirement(
                            requirement,
                            invocation,
                            argumentTypes,
                            out var methodTypeArguments,
                            out var parameterTypes,
                            out var methodReturnType))
                    {
                        continue;
                    }

                    var substitutedParameterTypes = parameterTypes
                        .Select(parameterType =>
                            SubstituteConceptSelfType(parameterType, receiverType))
                        .ToArray();
                    var substitutedReturnType =
                        SubstituteConceptSelfType(methodReturnType, receiverType);
                    if (argumentTypes.Count > substitutedParameterTypes.Length ||
                        !HasDefaultsForOmittedParameters(
                            requirement, argumentTypes.Count) ||
                        !substitutedParameterTypes.Zip(argumentTypes)
                            .All(pair => IsType(pair.First, pair.Second)))
                    {
                        continue;
                    }
                    matches.Add((
                        concept.Value.ModuleName,
                        concept.Value.Declaration,
                        requirement,
                        methodTypeArguments,
                        substitutedParameterTypes,
                        substitutedReturnType));
                }
            }
        }
        if (matches.Count == 0)
        {
            return false;
        }
        if (matches.Count > 1)
        {
            throw new CompilationErrorException(
                $"Concept method invocation '{memberAccess.MemberName}' is ambiguous.",
                invocation.SourceSpan);
        }

        var match = matches[0];
        var symbol = new FunctionSymbol(
            match.ModuleName,
            new QualifiedIdentifier(
                match.Concept.FullName,
                new QualifiedIdentifier(match.Requirement.Name)),
            match.ParameterTypes,
            match.ReturnType,
            declaration: match.Requirement);
        invocation.BindTarget(symbol, receiver);
        invocation.BindConceptMethodTypeArguments(match.MethodTypeArguments);
        invocation.BindAsConceptMethod();
        invocation.AddDefaultArguments(BindDefaultArguments(
            symbol,
            invocation.Arguments.Count,
            function,
            imports,
            scope));
        foreach (var pair in invocation.Arguments.Zip(symbol.ParameterTypes))
        {
            ApplyContextualType(pair.First, pair.Second);
        }
        returnType = match.ReturnType;
        return true;
    }

    private static bool HasDefaultsForOmittedParameters(
        FunctionDeclaration requirement,
        int suppliedArgumentCount) =>
        requirement.Parameters.Skip(suppliedArgumentCount)
            .All(parameter => parameter.DefaultValueText is not null);

    private static bool TryCloseConceptMethodRequirement(
        FunctionDeclaration requirement,
        InvocationExpression invocation,
        IReadOnlyList<TypeBase> argumentTypes,
        out IReadOnlyList<TypeBase> methodTypeArguments,
        out IReadOnlyList<TypeBase> parameterTypes,
        out TypeBase returnType)
    {
        methodTypeArguments = [];
        parameterTypes = [];
        returnType = requirement.ReturnType;
        if (requirement.GenericTypeNames.Length == 0)
        {
            if (invocation.ExplicitTypeArguments.Count != 0)
            {
                return false;
            }
            parameterTypes = requirement.Parameters
                .Select(parameter => parameter.ParameterType).ToArray();
            return true;
        }

        var inferred = new Dictionary<string, TypeBase>(StringComparer.Ordinal);
        if (invocation.ExplicitTypeArguments.Count > 0)
        {
            if (invocation.ExplicitTypeArguments.Count != requirement.GenericTypeNames.Length)
            {
                return false;
            }
            foreach (var pair in requirement.GenericTypeNames.Zip(
                invocation.ExplicitTypeArguments))
            {
                inferred.Add(pair.First, pair.Second);
            }
        }
        else
        {
            if (argumentTypes.Count > requirement.Parameters.Count ||
                !requirement.Parameters.Zip(argumentTypes).All(pair =>
                    InferGenericArguments(
                        pair.First.ParameterType, pair.Second, inferred)))
            {
                return false;
            }
        }
        if (requirement.GenericTypeNames.Any(name => !inferred.ContainsKey(name)))
        {
            return false;
        }

        methodTypeArguments = requirement.GenericTypeNames
            .Select(name => inferred[name]).ToArray();
        parameterTypes = requirement.Parameters.Select(parameter =>
            GenericTypeSubstitution.Substitute(parameter.ParameterType, inferred)).ToArray();
        returnType = GenericTypeSubstitution.Substitute(requirement.ReturnType, inferred);
        return true;
    }

    private TypeBase? GetExpectedFunctionParameterType(
        InvocationExpression invocation,
        int parameterIndex,
        FunctionDeclaration function,
        IReadOnlyList<QualifiedIdentifier> imports,
        LocalScope scope,
        QualifiedIdentifier currentNamespace)
    {
        if (invocation.Target is not IdentifierExpression identifier ||
            identifier.Identifier.Parts.Length != 1)
        {
            return null;
        }
        var name = identifier.Identifier.Parts[0];
        if (scope.TryLookup(name, out var localType) &&
            GetFunctionSignature(localType) is { } valueType &&
            valueType.ParameterTypes.Count == invocation.Arguments.Count)
        {
            return GetFunctionSignature(valueType.ParameterTypes[parameterIndex]) is not null
                ? valueType.ParameterTypes[parameterIndex]
                : null;
        }

        IReadOnlyList<FunctionSymbol> candidates;
        if (scope.TryLookupFunctions(name, out var localFunctions))
        {
            candidates = localFunctions;
        }
        else if (function.ParentClassDeclaration is { } parentClass)
        {
            var functions = FindFunctions(parentClass, name);
            candidates = functions.Count > 0
                ? functions.Select(item => item.Symbol).ToArray()
                : FindNamedFunctions(identifier.Identifier, currentNamespace, imports)
                    .Select(item => item.Symbol).ToArray();
        }
        else
        {
            candidates = FindNamedFunctions(identifier.Identifier, currentNamespace, imports)
                .Select(item => item.Symbol).ToArray();
        }
        var matches = candidates
            .Where(candidate =>
                candidate.ParameterTypes.Count == invocation.Arguments.Count &&
                GetFunctionSignature(candidate.ParameterTypes[parameterIndex]) is not null)
            .ToArray();
        if (matches.Length == 0)
        {
            return null;
        }
        var expectedTypes = matches
            .Select(candidate => candidate.ParameterTypes[parameterIndex])
            .DistinctBy(GetTypeName)
            .ToArray();
        return expectedTypes.Length == 1 ? expectedTypes[0] : null;
    }

    private TypeBase BindObjectCreation(
        ObjectCreationExpression creation,
        FunctionDeclaration function,
        IReadOnlyList<QualifiedIdentifier> imports,
        LocalScope scope)
    {
        var currentNamespace = GetCurrentNamespace(function);
        creation.BindGenericTypeParameters(
            (function.ParentClassDeclaration?.GenericTypeNames ?? [])
                .Concat(function.GenericTypeNames)
                .Distinct(StringComparer.Ordinal)
                .ToArray());
        ResolveTypeReference(creation.RequestedType, currentNamespace, imports);

        var argumentTypes = creation.Arguments
            .Select(argument => BindExpression(argument, function, imports, scope))
            .ToArray();
        var declaredType = _types.SingleOrDefault(candidate =>
            MatchesConstructedDeclaration(candidate, creation.RequestedType));
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
            .Where(candidate => IsType(candidate.ConstructedType, creation.RequestedType) ||
                declaredType is not null &&
                ReferenceEquals(candidate.Constructor.Declaration?.ParentClassDeclaration,
                    declaredType.Declaration))
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

        var resolvedCandidates = typeCandidates
            .Select(candidate => creation.RequestedType is NamedType
            {
                ConstructedIdentity: not null,
            } closedType &&
                candidate.Constructor.Declaration?.ParentClassDeclaration is { } parent &&
                parent.GenericTypeNames.Length == closedType.TypeArguments.Count
                    ? candidate with
                    {
                        Constructor = new FunctionSymbol(
                            candidate.Constructor.ModuleName,
                            candidate.Constructor.FullName,
                            candidate.Constructor.ParameterTypes.Select(parameter =>
                                CloseConstructedTypes(GenericTypeSubstitution.Substitute(
                                    parameter,
                                    parent.GenericTypeNames.Zip(closedType.TypeArguments)
                                        .ToDictionary(pair => pair.First, pair => pair.Second))))
                                .ToArray(),
                            candidate.Constructor.ReturnType,
                            candidate.Constructor.OverloadIndex,
                            candidate.Constructor.Declaration)
                    }
                    : candidate)
            .ToArray();
        var matchingConstructors = resolvedCandidates
            .Where(candidate => ParametersMatch(candidate.Constructor, argumentTypes))
            .ToArray();
        matchingConstructors = SelectBestConversionCandidates(
            matchingConstructors,
            argumentTypes,
            candidate => candidate.Constructor.ParameterTypes);
        if (matchingConstructors.Length == 0)
        {
            throw new CompilationErrorException(
                $"No constructor for '{GetTypeName(creation.RequestedType)}' accepts " +
                $"({string.Join(", ", argumentTypes.Select(GetTypeName))}).");
        }
        if (matchingConstructors.Length > 1)
        {
            throw new CompilationErrorException(
                $"Constructor call for '{GetTypeName(creation.RequestedType)}' is ambiguous. Candidates:\n" +
                string.Join("\n", matchingConstructors.Select(candidate =>
                    $"  {candidate.Constructor.FullName}(" +
                    string.Join(", ", candidate.Constructor.ParameterTypes.Select(GetTypeName)) + ")")),
                creation.SourceSpan);
        }

        var constructor = matchingConstructors[0].Constructor;
        if (creation.RequestedType is NamedType
            { ConstructedIdentity: { } closedIdentity } closedType &&
            (constructor.Declaration?.ParentClassDeclaration?.GenericTypeNames.Length > 0 ||
                constructor.Declaration?.ParentClassDeclaration?.ClassType == ClassType.Struct))
        {
            var declarationName = constructor.FullName.ToString() +
                $"#constructor@{closedIdentity.CanonicalName}";
            string GetSpecializationName(string moduleName) =>
                GenericTypeIdentity.CreateFunction(
                    moduleName,
                    declarationName,
                    closedType.TypeArguments,
                    constructor.ParameterTypes,
                    constructor.ReturnType).CIdentifier;

            var specializationModule = constructor.ModuleName;
            var specializationName = GetSpecializationName(specializationModule);
            if (IsReferencedModule(constructor.ModuleName) &&
                !_project!.HasReferencedGenericFunctionSpecialization(specializationName))
            {
                specializationModule = _project.Name;
                specializationName = GetSpecializationName(specializationModule);
            }
            constructor = new FunctionSymbol(
                specializationModule,
                constructor.FullName,
                constructor.ParameterTypes,
                constructor.ReturnType,
                constructor.OverloadIndex,
                constructor.Declaration,
                specializationName,
                closedType);
            _project!.AddGenericFunctionInstance(constructor);
        }
        creation.AddDefaultArguments(BindDefaultArguments(
            constructor,
            creation.Arguments.Count,
            function,
            imports,
            scope));
        foreach (var pair in creation.Arguments.Zip(constructor.ParameterTypes))
        {
            ApplyContextualType(pair.First, pair.Second);
        }
        creation.BindConstructor(
            constructor,
            matchingConstructors[0].ClassType,
            $"__cx_new_{_objectCreationIndex++}");
        return creation.RequestedType;
    }
}

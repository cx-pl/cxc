using CxCompiler.Model;
using CxCompiler.Model.Common;
using CxCompiler.Model.Errors;
using CxCompiler.Model.Expressions;
using CxCompiler.Model.Types;
using CxCompiler.Model.Types.BuiltInTypes;

namespace CxCompiler.Semantics;

public sealed partial class SemanticBinder
{

    private TypeBase BindIdentifier(
        IdentifierExpression identifier,
        FunctionDeclaration function,
        IReadOnlyList<QualifiedIdentifier> imports,
        LocalScope scope,
        TypeBase? expectedType)
    {
        if (identifier.Identifier.Parts.Length == 1 &&
            scope.TryLookupSymbol(identifier.Identifier.Parts[0], out var resolvedLocal))
        {
            if (scope.CurrentLambda is { } currentLambda &&
                !ReferenceEquals(resolvedLocal.OwnerLambda, currentLambda))
            {
                identifier.BindCapture(CaptureThroughEnclosingLambdas(
                    currentLambda,
                    resolvedLocal));
            }
            else
            {
                identifier.BindLocal(resolvedLocal);
            }
            return resolvedLocal.Type;
        }

        if (identifier.Identifier.Parts.Length == 1 &&
            scope.TryLookupFunctions(identifier.Identifier.Parts[0], out var localFunctions))
        {
            var expectedLocalFunctionType = expectedType is not null
                ? GetFunctionSignature(expectedType)
                : null;
            var matchingLocalFunctions = localFunctions
                .Select(symbol => (
                    Symbol: expectedLocalFunctionType is null
                        ? symbol.Declaration?.GenericTypeNames.Length is null or 0
                            ? symbol
                            : null
                        : CloseGenericFunction(
                            symbol,
                            expectedLocalFunctionType.ParameterTypes,
                            []),
                    BaseDepth: 0))
                .Where(candidate => candidate.Symbol is not null)
                .Select(candidate => candidate.Symbol!)
                .Where(symbol => expectedLocalFunctionType is null ||
                    symbol.ParameterTypes.Count == expectedLocalFunctionType.ParameterTypes.Count &&
                    symbol.ParameterTypes.Zip(expectedLocalFunctionType.ParameterTypes)
                        .All(pair => IsType(pair.First, pair.Second)) &&
                    CanAssign(expectedLocalFunctionType.ReturnType, symbol.ReturnType))
                .Select(SelectGenericFunctionSpecialization)
                .DistinctBy(symbol => symbol.SpecializationName ??
                    symbol.Declaration?.LocalCName ?? symbol.FullName.ToString())
                .ToArray();
            if (matchingLocalFunctions.Length == 1)
            {
                var localSymbol = matchingLocalFunctions[0];
                if (localSymbol.SpecializationName is not null)
                {
                    _project!.AddGenericFunctionInstance(localSymbol);
                }
                identifier.BindFunctionValue(localSymbol);
                return new FunctionType(
                    QualifiedIdentifier.Empty,
                    localSymbol.ReturnType,
                    localSymbol.ParameterTypes);
            }
            if (matchingLocalFunctions.Length > 1)
            {
                throw new CompilationErrorException(
                    $"Function value '{identifier.Identifier}' is ambiguous.");
            }
            if (expectedLocalFunctionType is not null)
            {
                throw new CompilationErrorException(
                    $"No overload of function value '{identifier.Identifier}' matches the expected " +
                    $"type '{GetTypeName(expectedLocalFunctionType)}'.",
                    identifier.SourceSpan);
            }
            throw new CompilationErrorException(
                $"Function value '{identifier.Identifier}' requires an expected function type.",
                identifier.SourceSpan);
        }

        var lexicalClass = function.ParentClassDeclaration ??
            function.LambdaOwner?.EnclosingClassDeclaration;
        if (identifier.Identifier.Parts.Length == 1 && lexicalClass is { } parentClass)
        {
            var fieldMatch = FindField(
                parentClass,
                identifier.Identifier.Parts[0],
                staticOnly: null);
            if (fieldMatch is not null)
            {
                var field = fieldMatch.Value.Symbol;
                if (!field.Declaration.IsStatic && function.IsStatic &&
                    function.LambdaOwner?.ThisSymbol is null)
                {
                    throw new CompilationErrorException(
                        $"Instance field '{field.Declaration.Name}' cannot be used from a static function.");
                }
                if (!field.Declaration.IsStatic && function.LambdaOwner is { } lambdaOwner)
                {
                    CaptureThroughEnclosingLambdas(
                        lambdaOwner,
                        lambdaOwner.ThisSymbol ?? throw new CompilationErrorException(
                            "An instance member cannot be used from a lambda without an instance context."));
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
                if (!property.IsStatic && function.IsStatic &&
                    function.LambdaOwner?.ThisSymbol is null)
                {
                    throw new CompilationErrorException(
                        $"Instance property '{identifier.Identifier.Parts[0]}' cannot be used from a static function.");
                }
                if (!property.IsStatic && function.LambdaOwner is { } lambdaOwner)
                {
                    CaptureThroughEnclosingLambdas(
                        lambdaOwner,
                        lambdaOwner.ThisSymbol ?? throw new CompilationErrorException(
                            "An instance member cannot be used from a lambda without an instance context."));
                }
                EnsurePropertyValueIsSupported(property);
                var getter = SelectPropertyAccessor(
                    property,
                    "get",
                    [],
                    !property.IsStatic && IsFunctionThisConst(function));
                identifier.BindProperty(
                    property,
                    getter,
                    property.IsStatic ? 0 : propertyMatch.Value.BaseDepth);
                return property.Type;
            }
        }

        var currentNamespace = lexicalClass?.Namespace ?? function.Namespace;
        if (identifier.Identifier.Parts.Length == 1 && lexicalClass is { } enclosingClass)
        {
            var hasInstanceContext = !function.IsStatic ||
                function.LambdaOwner?.ThisSymbol is not null;
            var instanceMethods = FindFunctions(enclosingClass, identifier.Identifier.Parts[0])
                .Where(candidate => candidate.Symbol.Declaration?.IsStatic == false)
                .ToArray();
            if (hasInstanceContext && instanceMethods.Length > 0)
            {
                var receiver = new ThisExpression { SourceSpan = identifier.SourceSpan };
                receiver.SetInferredType(BindThis(receiver, function));
                var methodValue = new MemberAccessExpression(
                    receiver,
                    identifier.Identifier.Parts[0])
                {
                    SourceSpan = identifier.SourceSpan,
                };
                var methodType = BindMemberAccess(
                    methodValue,
                    function,
                    imports,
                    scope,
                    expectedType);
                methodValue.SetInferredType(methodType);
                identifier.BindMethodValue(methodValue);
                return methodType;
            }
        }

        var functions = identifier.Identifier.Parts.Length == 1 &&
            lexicalClass is { } declaringClass
            ? FindFunctions(declaringClass, identifier.Identifier.Parts[0])
                .Where(candidate => candidate.Symbol.Declaration?.IsStatic != false)
                .ToArray()
            : FindNamedFunctions(identifier.Identifier, currentNamespace, imports)
                .Where(candidate => candidate.Symbol.Declaration?.IsStatic != false)
                .ToArray();
        var expectedFunctionType = expectedType is not null
            ? GetFunctionSignature(expectedType)
            : null;
        functions = functions
            .Select(candidate => (
                Symbol: expectedFunctionType is null
                    ? candidate.Symbol.Declaration?.GenericTypeNames.Length is null or 0
                        ? candidate.Symbol
                        : null
                    : CloseGenericFunction(
                        candidate.Symbol,
                        expectedFunctionType.ParameterTypes,
                        []),
                candidate.BaseDepth))
            .Where(candidate => candidate.Symbol is not null)
            .Select(candidate => (Symbol: candidate.Symbol!, candidate.BaseDepth))
            .Where(candidate => expectedFunctionType is null ||
                candidate.Symbol.ParameterTypes.Count == expectedFunctionType.ParameterTypes.Count &&
                candidate.Symbol.ParameterTypes.Zip(expectedFunctionType.ParameterTypes)
                    .All(pair => IsType(pair.First, pair.Second)) &&
                CanAssign(expectedFunctionType.ReturnType, candidate.Symbol.ReturnType))
            .ToArray();
        if (functions.Length == 1)
        {
            var symbol = SelectGenericFunctionSpecialization(functions[0].Symbol);
            if (symbol.SpecializationName is not null)
            {
                _project!.AddGenericFunctionInstance(symbol);
            }
            identifier.BindFunctionValue(symbol);
            return new FunctionType(
                QualifiedIdentifier.Empty,
                symbol.ReturnType,
                symbol.ParameterTypes);
        }
        if (functions.Length > 1)
        {
            throw new CompilationErrorException(
                $"Function value '{identifier.Identifier}' is ambiguous; select a function " +
                "with an unambiguous name.");
        }
        if (expectedFunctionType is not null &&
            (identifier.Identifier.Parts.Length == 1 && lexicalClass is { } classForName
                ? FindFunctions(classForName, identifier.Identifier.Parts[0])
                    .Any(candidate => candidate.Symbol.Declaration?.IsStatic != false)
                : FindNamedFunctions(identifier.Identifier, currentNamespace, imports).Count > 0))
        {
            throw new CompilationErrorException(
                $"No overload of function value '{identifier.Identifier}' matches the expected " +
                $"type '{GetTypeName(expectedFunctionType)}'.",
                identifier.SourceSpan);
        }

        throw new CompilationErrorException($"Cannot resolve value '{identifier.Identifier}'.");
    }

    private TypeBase BindThis(ThisExpression expression, FunctionDeclaration function)
    {
        if (function.LambdaOwner is { ThisSymbol: { } thisSymbol } lambdaOwner)
        {
            expression.BindCapture(CaptureThroughEnclosingLambdas(lambdaOwner, thisSymbol));
            return thisSymbol.Type;
        }
        if (function.IsStatic || function.ParentClassDeclaration is null)
        {
            throw new CompilationErrorException(
                "The 'this' expression is only available in instance functions and constructors.");
        }

        if (function.ExtensionTargetType is { } extensionTargetType)
        {
            return function.Const ? new ConstType(extensionTargetType) : extensionTargetType;
        }

        var type = _types.Single(type =>
            ReferenceEquals(type.Declaration, function.ParentClassDeclaration)).Type;
        if (function.ParentClassDeclaration is
            {
                GenericTypeNames.Length: 1,
                FullName.Parts: ["System", "Collections", "Array"],
            } arrayDeclaration)
        {
            var arrayType = NamedType.CreateWithTypeArguments(
                arrayDeclaration.FullName.ToString(),
                [new GenericType(arrayDeclaration.GenericTypeNames[0])]);
            arrayType.SetResolvedType(
                arrayDeclaration.FullName,
                arrayDeclaration.ProjectName ?? _project!.Name,
                arrayDeclaration.ClassType);
            type = arrayType;
        }
        return function.Const ? new ConstType(type) : type;
    }

    private static bool IsFunctionThisConst(FunctionDeclaration function) =>
        function.Const || function.LambdaOwner?.ThisSymbol?.Type is ConstType;

    private TypeBase BindMemberAccess(
        MemberAccessExpression memberAccess,
        FunctionDeclaration function,
        IReadOnlyList<QualifiedIdentifier> imports,
        LocalScope scope,
        TypeBase? expectedType)
    {
        var currentNamespace = GetCurrentNamespace(function);
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
                    var expectedFunctionType = expectedType is null
                        ? null
                        : GetFunctionSignature(expectedType)
                            ?? throw new CompilationErrorException(
                                $"Method value '{memberAccess.MemberName}' requires a function type, " +
                                $"but '{GetTypeName(expectedType)}' was expected.",
                                memberAccess.SourceSpan);
                    var methodCandidates = staticClass is null
                        ? []
                        : FindFunctions(staticClass, memberAccess.MemberName)
                            .Where(candidate => candidate.Symbol.Declaration?.IsStatic == true)
                            .Select(candidate => (
                                Symbol: CloseContainingClassFunction(
                                    candidate.Symbol,
                                    staticTarget.Type),
                                candidate.BaseDepth))
                            .Where(candidate => candidate.Symbol is not null)
                            .Select(candidate => (Symbol: candidate.Symbol!, candidate.BaseDepth))
                            .Select(candidate => (
                                Symbol: expectedFunctionType is null
                                    ? candidate.Symbol.Declaration?.GenericTypeNames.Length is null or 0
                                        ? candidate.Symbol
                                        : null
                                    : CloseGenericFunction(
                                        candidate.Symbol,
                                        expectedFunctionType.ParameterTypes,
                                        []),
                                candidate.BaseDepth))
                            .Where(candidate => candidate.Symbol is not null)
                            .Select(candidate => (Symbol: candidate.Symbol!, candidate.BaseDepth))
                            .Where(candidate => expectedFunctionType is null ||
                                candidate.Symbol.ParameterTypes.Count == expectedFunctionType.ParameterTypes.Count &&
                                candidate.Symbol.ParameterTypes.Zip(expectedFunctionType.ParameterTypes)
                                    .All(pair => IsType(pair.First, pair.Second)) &&
                                CanAssign(expectedFunctionType.ReturnType, candidate.Symbol.ReturnType))
                            .ToArray();
                    if (methodCandidates.Length == 1)
                    {
                        var symbol = SelectGenericFunctionSpecialization(
                            methodCandidates[0].Symbol);
                        if (symbol.SpecializationName is not null)
                        {
                            _project!.AddGenericFunctionInstance(symbol);
                        }
                        memberAccess.BindStaticFunctionValue(symbol);
                        return new FunctionType(
                            QualifiedIdentifier.Empty,
                            symbol.ReturnType,
                            symbol.ParameterTypes);
                    }
                    if (methodCandidates.Length > 1)
                    {
                        throw new CompilationErrorException(
                            $"Static method value '{staticTarget.Declaration.FullName}." +
                            $"{memberAccess.MemberName}' is ambiguous; provide a target function type.",
                            memberAccess.SourceSpan);
                    }
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
        if (ArrayType.TryGetElementType(targetType, out _) &&
            memberAccess.MemberName == "Length")
        {
            var arrayLength = new PropertySymbol(
                "cxcore",
                new QualifiedIdentifier("System", "Collections", "Array", "Length"),
                targetType,
                ClassType.Class,
                BuiltInSystemTypes.UInt,
                false,
                [new PropertyAccessorSymbol("get", true, [])]);
            memberAccess.BindProperty(arrayLength, arrayLength.Accessors[0]);
            return arrayLength.Type;
        }

        if (TryBindConceptPropertyAccess(
                memberAccess,
                targetType,
                function,
                imports) is { } conceptPropertyType)
        {
            if (memberAccess.PropertyGetter is null)
            {
                throw new CompilationErrorException(
                    $"Concept property '{memberAccess.MemberName}' is not readable.",
                    memberAccess.SourceSpan);
            }
            return conceptPropertyType;
        }

        var targetClass = GetMemberClassDeclaration(targetType);
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
            var propertyClass = GetMemberClassDeclaration(targetType);
            (PropertySymbol Symbol, int BaseDepth)? property = null;
            if (ArrayType.TryGetElementType(targetType, out _))
            {
                var propertyMatches = GetMemberClassDeclarations(targetType)
                    .Select(owner => (
                        Owner: owner,
                        Match: FindProperty(owner, memberAccess.MemberName, staticOnly: false)))
                    .Where(candidate => candidate.Match is not null)
                    .GroupBy(candidate => candidate.Match!.Value.Symbol.FullName)
                    .Select(group => group.First())
                    .ToArray();
                if (propertyMatches.Length > 1)
                {
                    throw new CompilationErrorException(
                        $"Array property '{memberAccess.MemberName}' is ambiguous for " +
                        $"'{GetTypeName(targetType)}'.",
                        memberAccess.SourceSpan);
                }
                if (propertyMatches.Length == 1)
                {
                    propertyClass = propertyMatches[0].Owner;
                    property = propertyMatches[0].Match;
                }
            }
            else if (propertyClass is not null)
            {
                property = FindProperty(
                    propertyClass, memberAccess.MemberName, staticOnly: false);
            }
            else if (UnwrapConst(targetType) is GenericType)
            {
                var propertyMatches = GetMemberClassDeclarations(targetType)
                    .Select(owner => (
                        Owner: owner,
                        Match: FindProperty(owner, memberAccess.MemberName, staticOnly: false)))
                    .Where(candidate => candidate.Match is not null)
                    .ToArray();
                if (propertyMatches.Length > 0)
                {
                    propertyClass = propertyMatches[0].Owner;
                    property = propertyMatches[0].Match;
                }
            }
            else
            {
                property = _properties
                    .Where(candidate =>
                        IsType(candidate.ContainingType, targetType) &&
                        candidate.FullName.Parts[^1] == memberAccess.MemberName &&
                        !candidate.IsStatic)
                    .Select(candidate => ((PropertySymbol Symbol, int BaseDepth)?)(candidate, 0))
                    .SingleOrDefault();
            }
            if (property is null)
            {
                var expectedFunctionType = expectedType is null
                    ? null
                    : GetFunctionSignature(expectedType)
                        ?? throw new CompilationErrorException(
                            $"Method value '{memberAccess.MemberName}' requires a function type, " +
                            $"but '{GetTypeName(expectedType)}' was expected.",
                            memberAccess.SourceSpan);
                var methodCandidates = propertyClass is null
                    ? []
                    : FindFunctions(propertyClass, memberAccess.MemberName)
                        .Where(candidate => candidate.Symbol.Declaration?.IsStatic == false)
                        .ToArray();
                methodCandidates = methodCandidates
                    .Select(candidate => (
                        Symbol: CloseContainingClassFunction(candidate.Symbol, targetType),
                        candidate.BaseDepth))
                    .Where(candidate => candidate.Symbol is not null)
                    .Select(candidate => (Symbol: candidate.Symbol!, candidate.BaseDepth))
                    .Select(candidate => (
                        Symbol: expectedFunctionType is null
                            ? candidate.Symbol.Declaration?.GenericTypeNames.Length is null or 0
                                ? candidate.Symbol
                                : null
                            : CloseGenericFunction(
                                candidate.Symbol,
                                expectedFunctionType.ParameterTypes,
                                []),
                        candidate.BaseDepth))
                    .Where(candidate => candidate.Symbol is not null)
                    .Select(candidate => (Symbol: candidate.Symbol!, candidate.BaseDepth))
                    .Where(candidate =>
                        (targetType is not ConstType ||
                            candidate.Symbol.Declaration?.Const == true) &&
                        (expectedFunctionType is null ||
                            candidate.Symbol.ParameterTypes.Count == expectedFunctionType.ParameterTypes.Count &&
                            candidate.Symbol.ParameterTypes.Zip(expectedFunctionType.ParameterTypes)
                                .All(pair => IsType(pair.First, pair.Second)) &&
                            CanAssign(expectedFunctionType.ReturnType, candidate.Symbol.ReturnType)))
                    .ToArray();
                if (methodCandidates.Length == 1)
                {
                    var candidate = methodCandidates[0];
                    var symbol = SelectGenericFunctionSpecialization(candidate.Symbol);
                    if (symbol.SpecializationName is not null)
                    {
                        _project!.AddGenericFunctionInstance(symbol);
                    }
                    var functionType = new FunctionType(
                        QualifiedIdentifier.Empty,
                        symbol.ReturnType,
                        symbol.ParameterTypes);
                    var dispatchSlot = symbol.Declaration?.IsExtension != true &&
                        GetClassDeclaration(targetType) is
                        { ClassType: ClassType.Interface } receiverInterface &&
                        symbol.Declaration is { } methodDeclaration
                            ? receiverInterface.VirtualMethodSlots
                                .Single(slot => FunctionSignaturesMatch(
                                    slot.Contract,
                                    methodDeclaration))
                                .Index
                            : (int?)null;
                    var methodValueName = GetMethodValueName(memberAccess);
                    memberAccess.BindFunctionValue(
                        symbol,
                        $"{methodValueName}_adapter",
                        $"{methodValueName}_environment",
                        $"{methodValueName}_receiver",
                        candidate.BaseDepth,
                        dispatchSlot);
                    return functionType;
                }
                if (methodCandidates.Length > 1)
                {
                    throw new CompilationErrorException(
                        $"Instance method value '{GetTypeName(targetType)}.{memberAccess.MemberName}' " +
                        "is ambiguous; select a method with an unambiguous name.",
                        memberAccess.SourceSpan);
                }
                throw new CompilationErrorException(
                    $"Instance member '{GetTypeName(targetType)}.{memberAccess.MemberName}' does not exist.");
            }
            var closedProperty = CloseContainingClassProperty(
                property.Value.Symbol, propertyClass, targetType);
            EnsurePropertyValueIsSupported(closedProperty);
            var getter = SelectPropertyAccessor(
                closedProperty,
                "get",
                [],
                UnwrapConst(targetType) != targetType);
            var dispatch = GetInterfacePropertyDispatch(
                targetType,
                closedProperty,
                getter);
            memberAccess.BindProperty(
                closedProperty,
                getter,
                property.Value.BaseDepth,
                dispatch.SlotIndex,
                dispatch.TemporaryName,
                dispatch.InterfaceType);
            return closedProperty.Type;
        }

        memberAccess.BindField(field.Value.Symbol, field.Value.BaseDepth);
        return CloseContainingClassFieldType(
            field.Value.Symbol, targetClass, targetType);
    }

    private static bool IsGenericTypeParameterInScope(
        FunctionDeclaration function,
        string name) => function.GenericTypeNames.Contains(name, StringComparer.Ordinal) ||
            function.ParentClassDeclaration?.GenericTypeNames.Contains(
                name, StringComparer.Ordinal) == true;

    private static IReadOnlyList<GenericConceptConstraint> GetConceptConstraints(
        FunctionDeclaration function) =>
        (function.ParentClassDeclaration?.ConceptConstraints ?? [])
            .Concat(function.ConceptConstraints).ToArray();

    private TypeBase? TryBindConceptPropertyAccess(
        MemberAccessExpression memberAccess,
        TypeBase receiverType,
        FunctionDeclaration function,
        IReadOnlyList<QualifiedIdentifier> imports)
    {
        if (UnwrapConst(receiverType) is not GenericType genericType ||
            !IsGenericTypeParameterInScope(function, genericType.Name))
        {
            return null;
        }

        foreach (var constraint in GetConceptConstraints(function)
            .Where(constraint => constraint.ParameterName == genericType.Name))
        {
            foreach (var conceptName in constraint.Concepts)
            {
                var concept = FindConcept(
                    conceptName.ToString(), function.Namespace, imports);
                var requirement = concept?.Declaration.PropertyRequirements
                    .SingleOrDefault(candidate => candidate.Name == memberAccess.MemberName);
                if (concept is null || requirement is null)
                {
                    continue;
                }

                var property = new PropertySymbol(
                    concept.Value.ModuleName,
                    new QualifiedIdentifier(
                        concept.Value.Declaration.FullName,
                        requirement.Name),
                    receiverType,
                    ClassType.Class,
                    requirement.Type,
                    false,
                    (requirement.HasGetter
                            ? new[]
                            {
                                new PropertyAccessorSymbol(
                                    "get", requirement.GetterConst, Array.Empty<TypeBase>()),
                            }
                            : Array.Empty<PropertyAccessorSymbol>())
                        .Concat(requirement.HasSetter
                            ? new[]
                            {
                                new PropertyAccessorSymbol(
                                    "set", false, Array.Empty<TypeBase>()),
                            }
                            : Array.Empty<PropertyAccessorSymbol>())
                        .ToArray());
                memberAccess.BindPropertyTarget(property);
                if (requirement.HasGetter)
                {
                    memberAccess.BindProperty(property, property.Accessors[0]);
                }
                return requirement.Type;
            }
        }
        return null;
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
        var delegateCandidates = _delegates
            .Where(candidate => candidateNames.Contains(candidate.Declaration.FullName))
            .ToArray();
        var aliasCandidates = _aliases
            .Where(candidate => candidateNames.Contains(candidate.Declaration.FullName))
            .ToArray();
        if (classCandidates.Length + enumCandidates.Length + delegateCandidates.Length +
            aliasCandidates.Length > 1)
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
        if (aliasCandidates.SingleOrDefault() is { } aliasCandidate)
        {
            var aliasedType = new NamedType(aliasCandidate.Declaration.Name, []);
            ResolveAlias(aliasCandidate, aliasedType);
            var moduleName = aliasedType.ResolvedTypeFullName.Parts[0];
            var fullName = new QualifiedIdentifier(
                aliasedType.ResolvedTypeFullName.Parts.Skip(1).ToArray());
            var aliasedClass = _types.SingleOrDefault(candidate =>
                candidate.ModuleName == moduleName && candidate.Declaration.FullName == fullName);
            if (aliasedClass is not null)
            {
                return new ResolvedTypeSymbol(
                    aliasedClass.ModuleName,
                    aliasedClass.Declaration,
                    aliasedClass.Type);
            }
            var aliasedEnum = _enums.SingleOrDefault(candidate =>
                candidate.ModuleName == moduleName && candidate.Declaration.FullName == fullName);
            if (aliasedEnum is not null)
            {
                return new ResolvedTypeSymbol(
                    aliasedEnum.ModuleName,
                    aliasedEnum.Declaration,
                    aliasedEnum.Type);
            }
        }
        return null;
    }

    private static bool ContainsUnboundGenericParameter(TypeBase type) => type switch
    {
        ConstType constant => ContainsUnboundGenericParameter(constant.UnderlyingType),
        ArrayType array => ContainsUnboundGenericParameter(array.ElementType),
        NullableType nullable => ContainsUnboundGenericParameter(nullable.UnderlyingType),
        FunctionType function => ContainsUnboundGenericParameter(function.ReturnType) ||
            function.ParameterTypes.Any(ContainsUnboundGenericParameter),
        GenericType => true,
        NamedType named => named.TypeArguments.Any(ContainsUnboundGenericParameter),
        _ => false,
    };

    private static bool ContainsUndeclaredConceptMethodParameter(
        TypeBase type,
        IReadOnlyCollection<string> genericTypeNames) => type switch
    {
        ConstType constant => ContainsUndeclaredConceptMethodParameter(
            constant.UnderlyingType, genericTypeNames),
        GenericType generic => !genericTypeNames.Contains(generic.Name),
        ArrayType array => ContainsUndeclaredConceptMethodParameter(
            array.ElementType, genericTypeNames),
        NullableType nullable => ContainsUndeclaredConceptMethodParameter(
            nullable.UnderlyingType, genericTypeNames),
        NamedType named => named.TypeArguments.Any(argument =>
            ContainsUndeclaredConceptMethodParameter(argument, genericTypeNames)),
        FunctionType function => ContainsUndeclaredConceptMethodParameter(
                function.ReturnType, genericTypeNames) ||
            function.ParameterTypes.Any(parameter =>
                ContainsUndeclaredConceptMethodParameter(parameter, genericTypeNames)),
        _ => false,
    };

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
        var functions = hierarchy
            .SelectMany(type => _symbols
                .Where(symbol =>
                    symbol.Declaration is { } function &&
                    SameGenericSpecializationOwner(
                        function.ParentClassDeclaration, type.Declaration) &&
                    (_extensionFunctionOwners.ContainsKey(function) ||
                        type.Declaration.ProjectName is null ||
                        symbol.ModuleName == type.Declaration.ProjectName) &&
                    function.Name == name &&
                    (!_extensionFunctionOwners.TryGetValue(function, out var owner) ||
                        IsExtensionInScope(owner)))
                .Select(symbol => (symbol, type.BaseDepth)))
            .ToArray();
        if (functions.Length == 0 && declaration.ClassType == ClassType.Class &&
            !declaration.IsStatic && _symbols.Where(symbol =>
                symbol.FullName == new QualifiedIdentifier("System", "Object", name))
            .ToArray() is { Length: > 0 } objectMethods)
        {
            return objectMethods.Select(symbol =>
                (symbol, EnumerateBaseClasses(declaration).Count())).ToArray();
        }
        return functions;
    }

    private (FunctionSymbol Symbol, int BaseDepth)? FindOperator(
        TypeBase receiverType,
        string token,
        IReadOnlyList<TypeBase> argumentTypes,
        TypeBase? expectedReturnType = null)
    {
        var isConstReceiver = receiverType is ConstType;
        var receiverClass = GetMemberClassDeclaration(receiverType);
        var receiverClasses = UnwrapConst(receiverType) is ArrayType
            ? GetMemberClassDeclarations(receiverType)
            : receiverClass is null ? [] : [receiverClass];
        if (receiverClasses.Count == 0)
        {
            return null;
        }

        var candidates = receiverClasses
            .SelectMany(owner => FindFunctions(
                owner, OperatorNames.GetDeclarationName(token)))
            .Select(candidate =>
            {
                var symbol = CloseContainingClassFunction(candidate.Symbol, receiverType);
                var closedSymbol =
                    CloseGenericFunction(
                        symbol, argumentTypes, [], expectedReturnType);
                return (
                    Symbol: closedSymbol is null
                        ? null
                        : SelectGenericFunctionSpecialization(closedSymbol),
                    candidate.BaseDepth);
            })
            .Where(candidate => candidate.Symbol is not null)
            .Select(candidate => (Symbol: candidate.Symbol!, candidate.BaseDepth))
            .Where(candidate =>
                candidate.Symbol.Declaration is { } declaration &&
                declaration.OperatorToken == token &&
                !declaration.IsStatic &&
                (!isConstReceiver || declaration.Const) &&
                candidate.Symbol.ParameterTypes.Count == argumentTypes.Count &&
                candidate.Symbol.ParameterTypes.Zip(argumentTypes)
                    .All(pair => IsType(pair.First, pair.Second)))
            .OrderBy(candidate => candidate.BaseDepth)
            .ThenBy(candidate => candidate.Symbol.Declaration!.Const ? 1 : 0)
            .ToArray();
        if (candidates.Length == 0)
        {
            return null;
        }
        _project!.AddGenericFunctionInstance(candidates[0].Symbol);
        return candidates[0];
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

    private static IEnumerable<ClassDeclaration> EnumerateClasses(
        IEnumerable<DeclarationBase> declarations)
    {
        foreach (var declaration in declarations.OfType<ClassDeclaration>())
        {
            yield return declaration;
            foreach (var nested in EnumerateClasses(declaration.MemberDeclarations.Declarations))
            {
                yield return nested;
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
        var coreTypeName = type switch
        {
            ObjectType => "System.Object",
            StringType => "System.String",
            NamedType { ResolvedTypeFullName: var resolvedName }
                when resolvedName.ToString() == "cxcore.System.Object" => "System.Object",
            NamedType { ResolvedTypeFullName: var resolvedName }
                when resolvedName.ToString() == "cxcore.System.String" => "System.String",
            _ => null,
        };
        if (coreTypeName is not null && FindCoreClass(coreTypeName) is { } coreDeclaration)
        {
            return coreDeclaration;
        }
        return _types.SingleOrDefault(candidate => IsType(candidate.Type, type))?.Declaration;
    }

    private ClassDeclaration? GetClassDeclarationIncludingConstructed(TypeBase type)
    {
        type = UnwrapConst(type);
        if (ArrayType.TryGetElementType(type, out _))
        {
            return GetPublicArrayClassDeclaration();
        }
        return GetClassDeclaration(type) ??
            (type is NamedType namedType
                ? _types.SingleOrDefault(candidate =>
                    MatchesConstructedDeclaration(candidate, namedType))?.Declaration
                : null);
    }

    private ClassDeclaration? GetMemberClassDeclaration(TypeBase type)
    {
        type = UnwrapConst(type);
        if (type is GenericType genericParameter &&
            GetActiveClassConstraint(genericParameter) is { } constrainedClass)
        {
            return constrainedClass;
        }
        var declaration = GetClassDeclaration(type) ?? _types.SingleOrDefault(candidate =>
            MatchesConstructedDeclaration(candidate, type))?.Declaration ??
            _primitiveExtensionTargets.SingleOrDefault(candidate =>
                IsType(candidate.Type, type)).Declaration;
        if (declaration is not null || type is not ArrayType)
        {
            return declaration;
        }

        var matchingArrayExtensions = _primitiveExtensionTargets
            .Where(candidate => candidate.Type is ArrayType pattern &&
                candidate.Declaration.GenericTypeNames.Length > 0)
            .Select(candidate =>
            {
                var inferred = new Dictionary<string, TypeBase>(StringComparer.Ordinal);
                return InferGenericArguments(candidate.Type, type, inferred) &&
                    candidate.Declaration.GenericTypeNames.All(inferred.ContainsKey)
                        ? candidate.Declaration
                        : null;
            })
            .Where(candidate => candidate is not null)
            .Cast<ClassDeclaration>()
            .Distinct()
            .ToArray();
        return matchingArrayExtensions.Length == 1
            ? matchingArrayExtensions[0]
            : null;
    }

    private IReadOnlyList<ClassDeclaration> GetMemberClassDeclarations(TypeBase type)
    {
        type = UnwrapConst(type);
        if (type is ArrayType arrayType)
        {
            var arrayDeclarations = new List<ClassDeclaration>();
            if (GetPublicArrayClassDeclaration() is { } publicArrayDeclaration)
            {
                arrayDeclarations.Add(publicArrayDeclaration);
            }
            if (GetMemberClassDeclaration(type) is { } exactDeclaration)
            {
                arrayDeclarations.Add(exactDeclaration);
            }
            foreach (var candidate in _primitiveExtensionTargets.Where(candidate =>
                candidate.Type is ArrayType))
            {
                if (candidate.Declaration.GenericTypeNames.Length == 0)
                {
                    if (IsType(candidate.Type, arrayType))
                    {
                        arrayDeclarations.Add(candidate.Declaration);
                    }
                    continue;
                }
                var inferred = new Dictionary<string, TypeBase>(StringComparer.Ordinal);
                if (InferGenericArguments(candidate.Type, arrayType, inferred) &&
                    candidate.Declaration.GenericTypeNames.All(inferred.ContainsKey))
                {
                    arrayDeclarations.Add(candidate.Declaration);
                }
            }
            return arrayDeclarations.Distinct().ToArray();
        }
        if (type is not GenericType genericParameter)
        {
            return GetMemberClassDeclaration(type) is { } declaration
                ? [declaration]
                : [];
        }

        var declarations = new List<ClassDeclaration>();
        if (GetActiveClassConstraint(genericParameter) is { } classConstraint)
        {
            declarations.Add(classConstraint);
        }
        declarations.AddRange(GetActiveInterfaceConstraints(genericParameter));
        return declarations.Distinct().ToArray();
    }

    private ClassDeclaration? GetPublicArrayClassDeclaration() =>
        _types.Select(candidate => candidate.Declaration)
            .FirstOrDefault(IsPublicArrayClass);

    private static bool IsPublicArrayClass(ClassDeclaration declaration) =>
        declaration.GenericTypeNames.Length == 1 &&
        declaration.FullName.ToString() == "System.Collections.Array";

    private static NamedType CreateClosedPublicArrayType(
        TypeBase arrayType,
        ClassDeclaration declaration,
        string fallbackModuleName)
    {
        if (!ArrayType.TryGetElementType(arrayType, out var elementType))
        {
            throw new InternalCompilerException(
                $"Type '{arrayType.FullName}' is not an array type.");
        }

        var moduleName = declaration.ProjectName ?? fallbackModuleName;
        var closedType = NamedType.CreateWithTypeArguments(
            declaration.FullName.ToString(),
            [elementType]);
        closedType.SetResolvedType(
            declaration.FullName,
            moduleName,
            declaration.ClassType);
        if (!ContainsOpenTypeParameter(elementType))
        {
            closedType.ConstructedIdentity = GenericTypeIdentity.Create(
                moduleName,
                declaration.FullName.ToString(),
                [elementType]);
        }
        return closedType;
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

            var extensions = _properties.Where(candidate =>
                _extensionPropertyOwners.TryGetValue(candidate, out var owner) &&
                IsExtensionInScope(owner) &&
                candidate.FullName.Parts[^1] == name &&
                _extensionPropertyTargets.TryGetValue(candidate, out var extensionTarget) &&
                ReferenceEquals(extensionTarget, type.Declaration) &&
                (staticOnly is null || candidate.IsStatic == staticOnly))
                .ToArray();
            if (extensions.Length > 1)
            {
                throw new CompilationErrorException(
                    $"Extension property '{name}' is ambiguous for type '{type.Declaration.FullName}'.");
            }
            if (extensions.SingleOrDefault() is { } extensionProperty)
            {
                return (extensionProperty, type.BaseDepth);
            }
        }

        if (declaration.ClassType == ClassType.Class && !declaration.IsStatic &&
            staticOnly != true &&
            _properties.SingleOrDefault(candidate =>
                IsType(candidate.ContainingType, BuiltInSystemTypes.Object) &&
                candidate.FullName == new QualifiedIdentifier("System", "Object", name) &&
                !candidate.IsStatic) is { } objectProperty)
        {
            var objectBaseDepth = EnumerateTypeHierarchy(declaration)
                .Select(type => type.BaseDepth)
                .DefaultIfEmpty(0)
                .Max() + 1;
            return (objectProperty, objectBaseDepth);
        }
        return null;
    }
}

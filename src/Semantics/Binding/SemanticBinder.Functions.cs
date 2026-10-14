using CxCompiler.Model;
using CxCompiler.Model.Common;
using CxCompiler.Model.Errors;
using CxCompiler.Model.Expressions;
using CxCompiler.Model.Statements;
using CxCompiler.Model.Types;
using CxCompiler.Model.Types.BuiltInTypes;

namespace CxCompiler.Semantics;

public sealed partial class SemanticBinder
{

    private void BindFunction(
        FunctionDeclaration function,
        IReadOnlyList<QualifiedIdentifier> imports,
        FunctionSymbol? localFunctionSymbol = null,
        IReadOnlyList<(string Name, FunctionSymbol Symbol)>? visibleLocalFunctions = null,
        LocalScope? captureScope = null,
        LambdaExpression? lambdaOwner = null)
    {
        var previousImports = _activeImports;
        var previousFunction = _activeFunction;
        var previousUnsafeDepth = _unsafeDepth;
        _unsafeDepth = 0;
        var previousObjectCreationIndex = _objectCreationIndex;
        var previousFunctionState = (_loopDepth, _breakableDepth, _catchDepth,
            _propertyAssignmentIndex, _interfaceReceiverIndex, _foreachEnumeratorIndex);
        _activeImports = imports;
        _activeFunction = function;
        try
        {
            BindFunctionCore(
                function,
                imports,
                localFunctionSymbol,
                visibleLocalFunctions,
                captureScope,
                lambdaOwner);
        }
        catch (CompilationErrorException exception) when (
            exception.SourceSpan is null && function.SourceSpan is not null)
        {
            throw exception.WithSourceSpan(function.SourceSpan);
        }
        finally
        {
            _objectCreationIndex = previousObjectCreationIndex;
            (_loopDepth, _breakableDepth, _catchDepth, _propertyAssignmentIndex,
                _interfaceReceiverIndex, _foreachEnumeratorIndex) = previousFunctionState;
            _activeImports = previousImports;
            _activeFunction = previousFunction;
            _unsafeDepth = previousUnsafeDepth;
        }
    }

    private void BindFunctionCore(
        FunctionDeclaration function,
        IReadOnlyList<QualifiedIdentifier> imports,
        FunctionSymbol? localFunctionSymbol,
        IReadOnlyList<(string Name, FunctionSymbol Symbol)>? visibleLocalFunctions,
        LocalScope? captureScope,
        LambdaExpression? lambdaOwner)
    {
        var hasDefaultParameter = false;
        foreach (var parameter in function.Parameters)
        {
            if (parameter.DefaultValueText is not null)
            {
                hasDefaultParameter = true;
            }
            else if (hasDefaultParameter)
            {
                throw new CompilationErrorException(
                    $"Required parameter '{parameter.Name}' cannot follow a parameter with a default value.",
                    parameter.SourceSpan);
            }
        }

        var defaultScope = new LocalScope();
        foreach (var parameter in function.Parameters.Where(parameter =>
            parameter.DefaultValueText is not null))
        {
            var defaultExpression = CreateDefaultParameterExpression(parameter);
            var actualType = BindExpression(
                defaultExpression,
                function,
                imports,
                defaultScope,
                parameter.ParameterType);
            if (!CanAssign(parameter.ParameterType, actualType) &&
                !IsIntegralConstantAssignable(parameter.ParameterType, defaultExpression))
            {
                throw new CompilationErrorException(
                    $"Default value '{parameter.DefaultValueText}' cannot be assigned to parameter " +
                    $"'{parameter.Name}' of type '{GetTypeName(parameter.ParameterType)}'.",
                    parameter.SourceSpan);
            }
        }

        if (function.Body is null)
        {
            return;
        }

        _loopDepth = 0;
        _breakableDepth = 0;
        _catchDepth = 0;
        _propertyAssignmentIndex = 0;
        _interfaceReceiverIndex = 0;
        _foreachEnumeratorIndex = 0;

        var scope = new LocalScope(captureScope, lambdaOwner);
        foreach (var parameter in function.Parameters)
        {
            if (!scope.TryDeclare(
                parameter.Name,
                parameter.ParameterType,
                parameter.LocalSymbol,
                out var localSymbol))
            {
                throw new CompilationErrorException(
                    $"Parameter '{parameter.Name}' is already declared in function '{function.FullName}'.");
            }
            parameter.BindLocalSymbol(localSymbol);
        }
        if (localFunctionSymbol is not null &&
            !scope.TryDeclareFunction(function.Name, localFunctionSymbol))
        {
            throw new CompilationErrorException(
                $"Local function '{function.Name}' conflicts with a parameter in its body.");
        }
        foreach (var (name, symbol) in visibleLocalFunctions ?? [])
        {
            if (name != function.Name && !scope.TryDeclareFunction(name, symbol))
            {
                throw new CompilationErrorException(
                    $"Local function '{name}' conflicts with a parameter in '{function.Name}'.");
            }
        }

        if (function is ConstructorDeclaration { IsStatic: false } constructor)
        {
            BindConstructorInitializer(constructor, imports, scope);
        }

        BindStatements(function.Body, function, imports, scope);

        if (IsGenericConstrainedVoidMethodCallBody(function) &&
            function.Body.Any(statement =>
                ContainsNonVoidGenericConstrainedCall(statement, function)))
        {
            throw new CompilationErrorException(
                $"Generic function '{function.Name}' supports only statement-form calls " +
                "to void methods on a constrained parameter.",
                function.SourceSpan);
        }

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

        var targetDeclaration = GetClassDeclarationIncludingConstructed(targetType);
        var candidates = _constructors
            .Where(candidate => IsType(candidate.ConstructedType, targetType) ||
                targetDeclaration is not null &&
                ReferenceEquals(
                    candidate.Constructor.Declaration?.ParentClassDeclaration,
                    targetDeclaration))
            .Select(candidate => new ConstructorSymbol(
                targetType,
                candidate.ClassType,
                CloseContainingClassFunction(candidate.Constructor, targetType)))
            .Where(candidate => ParametersMatch(candidate.Constructor, argumentTypes))
            .ToArray();
        candidates = SelectBestConversionCandidates(
            candidates,
            argumentTypes,
            candidate => candidate.Constructor.ParameterTypes);
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
        if (target.SpecializationName is not null &&
            target.Declaration is ConstructorDeclaration)
        {
            _project!.AddGenericFunctionInstance(target);
        }
        var initializerDefaults = BindDefaultArguments(
            target,
            initializer.Arguments.Count,
            constructor,
            imports,
            scope);
        initializer.AddDefaultArguments(initializerDefaults);
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
                        $"Constructor initializer cycle detected in '{constructor.ParentClassDeclaration!.FullName}'.",
                        constructor.SourceSpan);
                }
                current = next;
            }
        }
    }

    private void BindPropertyAccessor(
        PropertyAccessorDeclaration accessor,
        IReadOnlyList<QualifiedIdentifier> imports)
    {
        try
        {
            BindPropertyAccessorCore(accessor, imports);
        }
        catch (CompilationErrorException exception) when (
            exception.SourceSpan is null && accessor.SourceSpan is not null)
        {
            throw exception.WithSourceSpan(accessor.SourceSpan);
        }
    }

    private void BindPropertyAccessorCore(
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
        var genericTypeNames =
            accessor.ParentPropertyDeclaration.ParentClassDeclaration?.GenericTypeNames ?? [];
        if (IsPropertyGenericTypePattern(propertyType, genericTypeNames) &&
            !IsGenericFieldGetter(accessor) &&
            !IsGenericArrayGetter(accessor) &&
            !IsGenericFieldLocalGetter(accessor) &&
            !IsGenericConditionalFieldGetter(accessor) &&
            !IsGenericFieldSetter(accessor))
        {
            throw new CompilationErrorException(
                $"Generic property accessor bodies are not supported yet: '{accessor.FullName}'.");
        }

        BindFunction(accessor.BodyFunction, imports);
    }

    private void DeclareLocalFunction(
        LocalFunctionDeclarationStatement statement,
        FunctionDeclaration enclosingFunction,
        IReadOnlyList<QualifiedIdentifier> imports,
        LocalScope scope)
    {
        var declaration = statement.Function;
        var currentNamespace = GetCurrentNamespace(enclosingFunction);
        if (declaration.GenericTypeNames.Length != 0)
        {
            declaration.ReturnType = GenericTypeSubstitution.BindParameters(
                declaration.ReturnType, declaration.GenericTypeNames);
            foreach (var parameter in declaration.Parameters)
            {
                parameter.ParameterType = GenericTypeSubstitution.BindParameters(
                    parameter.ParameterType, declaration.GenericTypeNames);
            }
            var constraints = declaration.GenericConstraints.Select(constraint =>
                constraint with
                {
                    Types = constraint.Types.Select(type =>
                        GenericTypeSubstitution.BindParameters(
                            type, declaration.GenericTypeNames)).ToArray(),
                }).ToArray();
            declaration.SetGenericConstraints(constraints);
            foreach (var constraint in declaration.GenericConstraints)
            {
                if (!declaration.GenericTypeNames.Contains(
                        constraint.ParameterName, StringComparer.Ordinal))
                {
                    throw new CompilationErrorException(
                        $"Constraint refers to unknown generic parameter " +
                        $"'{constraint.ParameterName}'.",
                        declaration.SourceSpan);
                }
                foreach (var type in constraint.Types)
                {
                    ResolveTypeReference(type, currentNamespace, imports);
                }
                ValidateGenericConstraintTypes(constraint);
            }
            ValidateGenericConstraintSet(declaration.GenericConstraints);
            ValidateGenericFunctionShape(declaration);
        }
        ResolveTypeReference(declaration.ReturnType, currentNamespace, imports);
        foreach (var parameter in declaration.Parameters)
        {
            ResolveTypeReference(parameter.ParameterType, currentNamespace, imports);
        }
        if (declaration.GenericTypeNames.Length != 0)
        {
            var availableConstraints = (enclosingFunction.ParentClassDeclaration?.GenericConstraints ?? [])
                .Concat(enclosingFunction.GenericConstraints)
                .Where(constraint => !declaration.GenericTypeNames.Contains(
                    constraint.ParameterName, StringComparer.Ordinal))
                .Concat(declaration.GenericConstraints)
                .ToArray();
            var availableConceptConstraints = GetAvailableConceptConstraints(
                    enclosingFunction.ParentClassDeclaration, enclosingFunction)
                .Where(constraint => !declaration.GenericTypeNames.Contains(
                    constraint.ParameterName, StringComparer.Ordinal))
                .ToArray();
            ValidateOpenGenericTypeConstraints(
                declaration.ReturnType, availableConstraints, availableConceptConstraints);
            foreach (var parameter in declaration.Parameters)
            {
                ValidateOpenGenericTypeConstraints(
                    parameter.ParameterType, availableConstraints,
                    availableConceptConstraints);
            }
        }
        if (declaration.MemberModifiers.Length != 0)
        {
            throw new CompilationErrorException(
                $"Local function '{declaration.Name}' cannot have member modifiers.",
                declaration.SourceSpan);
        }
        var cName = declaration.LocalCName ?? throw new InternalCompilerException(
            "Local function has no generated C name.");
        var symbol = new FunctionSymbol(
            enclosingFunction.ParentClassDeclaration?.ProjectName ??
                enclosingFunction.Namespace.Parts.FirstOrDefault() ?? _project!.Name,
            new QualifiedIdentifier(cName),
            declaration.Parameters.Select(parameter => parameter.ParameterType).ToArray(),
            declaration.ReturnType,
            declaration: declaration);
        if (!scope.TryDeclareFunction(declaration.Name, symbol))
        {
            throw new CompilationErrorException(
                $"Local function '{declaration.Name}' is already declared in this scope.",
                declaration.SourceSpan);
        }
        statement.BindSymbol(symbol);

        BindLocalFunctionClosureOwner(declaration, enclosingFunction);
    }

    private void DeclareLocalFunctionSpecialization(
        LocalFunctionDeclarationStatement statement,
        FunctionDeclaration enclosingFunction,
        IReadOnlyList<QualifiedIdentifier> imports,
        LocalScope scope)
    {
        var specialization = statement.Function;
        var currentNamespace = GetCurrentNamespace(enclosingFunction);
        if (specialization.Body is null)
        {
            throw new CompilationErrorException(
                "A generic local function specialization must provide a CX body.",
                specialization.SourceSpan);
        }
        if (specialization.GenericTypeNames.Length != 0 ||
            specialization.GenericConstraints.Count != 0 ||
            specialization.ConceptConstraints.Count != 0)
        {
            throw new CompilationErrorException(
                "A generic local function specialization cannot declare generic parameters or constraints.",
                specialization.SourceSpan);
        }
        if (specialization.SpecializationTypeArguments.Any(ContainsOpenTypeParameter))
        {
            throw new CompilationErrorException(
                "Generic local function specialization type arguments must be closed.",
                specialization.SourceSpan);
        }
        if (specialization.Parameters.Any(parameter =>
            parameter.DefaultValueText is not null))
        {
            throw new CompilationErrorException(
                "A generic local function specialization inherits default arguments from its primary declaration.",
                specialization.SourceSpan);
        }
        if (specialization.MemberModifiers.Length != 0)
        {
            throw new CompilationErrorException(
                $"Local function '{specialization.Name}' cannot have member modifiers.",
                specialization.SourceSpan);
        }

        ResolveTypeReference(specialization.ReturnType, currentNamespace, imports);
        foreach (var parameter in specialization.Parameters)
        {
            ResolveTypeReference(parameter.ParameterType, currentNamespace, imports);
        }

        var exactPrimaries = new List<(FunctionSymbol Symbol, TypeBase[] Parameters,
            TypeBase ReturnType)>();
        foreach (var candidate in scope.GetDeclaredFunctions(specialization.Name)
            .Where(symbol => symbol.Declaration is
                { GenericTypeNames.Length: > 0, IsGenericSpecialization: false }))
        {
            var primary = candidate.Declaration!;
            if (primary.GenericTypeNames.Length !=
                specialization.SpecializationTypeArguments.Count)
            {
                continue;
            }
            var mapping = primary.GenericTypeNames.Zip(
                    specialization.SpecializationTypeArguments)
                .ToDictionary(pair => pair.First, pair => pair.Second,
                    StringComparer.Ordinal);
            var parameterTypes = primary.Parameters.Select(parameter =>
                GenericTypeSubstitution.Substitute(parameter.ParameterType, mapping))
                .ToArray();
            var returnType = GenericTypeSubstitution.Substitute(
                primary.ReturnType, mapping);
            if (parameterTypes.Length == specialization.Parameters.Count &&
                parameterTypes.Zip(specialization.Parameters).All(pair =>
                    IsType(pair.First, pair.Second.ParameterType)) &&
                IsType(returnType, specialization.ReturnType))
            {
                exactPrimaries.Add((candidate, parameterTypes, returnType));
            }
        }

        if (exactPrimaries.Count == 0)
        {
            throw new CompilationErrorException(
                $"No generic local function matches specialization '{specialization.Name}'.",
                specialization.SourceSpan);
        }
        if (exactPrimaries.Count > 1)
        {
            throw new CompilationErrorException(
                $"Generic local function specialization '{specialization.Name}' is ambiguous.",
                specialization.SourceSpan);
        }

        var (primarySymbol, closedParameters, closedReturnType) = exactPrimaries[0];
        var primaryDeclaration = primarySymbol.Declaration!;
        if (!primaryDeclaration.MemberModifiers.SequenceEqual(
                specialization.MemberModifiers) ||
            primaryDeclaration.Const != specialization.Const)
        {
            throw new CompilationErrorException(
                $"Generic local function specialization '{specialization.Name}' must preserve the primary declaration's modifiers and signature.",
                specialization.SourceSpan);
        }

        if (CloseGenericFunction(
                primarySymbol,
                closedParameters,
                specialization.SpecializationTypeArguments) is null)
        {
            throw new CompilationErrorException(
                _genericConstraintFailure ??
                    $"The type arguments do not satisfy '{primaryDeclaration.FullName}'.",
                specialization.SourceSpan);
        }

        var identity = GenericTypeIdentity.CreateFunction(
            primarySymbol.ModuleName,
            primarySymbol.FullName.ToString(),
            specialization.SpecializationTypeArguments,
            closedParameters,
            closedReturnType).CanonicalName;
        if (!_genericFunctionSpecializations.TryAdd(identity, specialization))
        {
            throw new CompilationErrorException(
                $"Duplicate generic local function specialization for '{primaryDeclaration.Name}'.",
                specialization.SourceSpan);
        }

        var cName = specialization.LocalCName ?? throw new InternalCompilerException(
            "Local function specialization has no generated C name.");
        statement.BindSymbol(new FunctionSymbol(
            primarySymbol.ModuleName,
            new QualifiedIdentifier(cName),
            specialization.Parameters.Select(parameter => parameter.ParameterType).ToArray(),
            specialization.ReturnType,
            declaration: specialization));
        BindLocalFunctionClosureOwner(specialization, enclosingFunction);
    }

    private void BindLocalFunctionClosureOwner(
        FunctionDeclaration declaration,
        FunctionDeclaration enclosingFunction)
    {

        var enclosingClass = enclosingFunction.ParentClassDeclaration ??
            enclosingFunction.LambdaOwner?.EnclosingClassDeclaration;
        var thisSymbol = enclosingFunction.LambdaOwner?.ThisSymbol;
        if (thisSymbol is null && enclosingFunction.ParentClassDeclaration is { } thisClass &&
            !enclosingFunction.IsStatic)
        {
            var thisType = _types.Single(type => ReferenceEquals(type.Declaration, thisClass)).Type;
            if (enclosingFunction.Const)
            {
                thisType = new ConstType(thisType);
            }
            thisSymbol = new LocalVariableSymbol("this", thisType, isThis: true);
        }
        var owner = new LambdaExpression([], null, null);
        owner.SetEnclosingContext(enclosingClass, thisSymbol, enclosingFunction.LambdaOwner);
        var functionType = new FunctionType(
            QualifiedIdentifier.Empty,
            declaration.ReturnType,
            declaration.Parameters.Select(parameter => parameter.ParameterType).ToArray());
        owner.Bind(
            declaration,
            functionType,
            $"__cx_local_function_environment_{++_operatorTemporaryIndex}");
    }

    private void BindLocalFunction(
        LocalFunctionDeclarationStatement statement,
        FunctionDeclaration enclosingFunction,
        IReadOnlyList<QualifiedIdentifier> imports,
        LocalScope scope)
    {
        var localFunction = statement.Function;
        BindFunction(
            localFunction,
            imports,
            statement.Symbol ?? throw new InternalCompilerException(
                "Local function symbol was not declared before binding."),
            scope.GetVisibleFunctions(),
            captureScope: scope,
            lambdaOwner: localFunction.LambdaOwner);
    }
}

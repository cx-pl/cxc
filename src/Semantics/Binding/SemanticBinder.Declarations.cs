using CxCompiler.Model;
using CxCompiler.Model.Common;
using CxCompiler.Model.Errors;
using CxCompiler.Model.Types;
using CxCompiler.Model.Types.BuiltInTypes;

namespace CxCompiler.Semantics;

public sealed partial class SemanticBinder
{

    private void AddAliases(
        string moduleName,
        IReadOnlyList<DeclarationBase> declarations,
        IReadOnlyList<QualifiedIdentifier> imports)
    {
        foreach (var declaration in declarations.OfType<AliasDeclaration>())
        {
            _aliases.Add(new AliasSymbol(moduleName, declaration, imports));
        }
    }

    private void ValidateAliasNames()
    {
        var duplicateAlias = _aliases
            .GroupBy(symbol => (symbol.ModuleName, symbol.Declaration.FullName))
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateAlias is not null)
        {
            throw new CompilationErrorException(
                $"Alias '{duplicateAlias.Key.FullName}' is declared more than once.");
        }

        foreach (var alias in _aliases)
        {
            if (_types.Any(symbol => symbol.ModuleName == alias.ModuleName &&
                    symbol.Declaration.FullName == alias.Declaration.FullName) ||
                _enums.Any(symbol => symbol.ModuleName == alias.ModuleName &&
                    symbol.Declaration.FullName == alias.Declaration.FullName) ||
                _delegates.Any(symbol => symbol.ModuleName == alias.ModuleName &&
                    symbol.Declaration.FullName == alias.Declaration.FullName))
            {
                throw new CompilationErrorException(
                    $"Type name '{alias.Declaration.FullName}' is declared more than once.");
            }
        }
    }

    private void MergePartialTypeDeclarations(
        IReadOnlyList<CompilationContext> contexts)
    {
        // Merge from the outermost types inward. Merging an outer partial declaration can
        // expose nested partial declarations that were previously in separate member scopes.
        while (true)
        {
            var duplicateTypes = contexts
                .SelectMany(context => EnumerateClasses(context.DeclarationScope.Declarations)
                    .Select(declaration => (Module: context.ProjectName, Declaration: declaration)))
                .GroupBy(item => (item.Module, item.Declaration.FullName))
                .Where(group => group.Count() > 1)
                .ToArray();
            if (duplicateTypes.Length == 0)
            {
                return;
            }

            foreach (var group in duplicateTypes)
            {
                var declarations = group.Select(item => item.Declaration).ToArray();
                if (declarations.Any(declaration => !declaration.IsPartial))
                {
                    throw new CompilationErrorException(
                        $"Type '{group.Key.FullName}' is declared more than once; every declaration " +
                        "must be partial to share a type name.",
                        declarations[^1].SourceSpan);
                }

                var first = declarations[0];
                _canonicalPartialTypeDeclarations[first] = first;
                if (declarations.Any(declaration => declaration.ClassType != first.ClassType ||
                    !declaration.GenericTypeNames.SequenceEqual(
                        first.GenericTypeNames, StringComparer.Ordinal) ||
                    !HaveSameConceptConstraints(
                        declaration.ConceptConstraints, first.ConceptConstraints)))
                {
                    throw new CompilationErrorException(
                        $"Partial declarations of type '{group.Key.FullName}' have conflicting " +
                        "kind, generic parameters, or concept constraints.",
                        declarations[^1].SourceSpan);
                }

                foreach (var declaration in declarations.Skip(1))
                {
                    try
                    {
                        first.MergePartialDeclaration(declaration);
                    }
                    catch (ArgumentException)
                    {
                        throw new CompilationErrorException(
                            $"Partial declarations of type '{group.Key.FullName}' have conflicting " +
                            "modifiers or base type declarations.",
                            declaration.SourceSpan);
                    }
                    _canonicalPartialTypeDeclarations[declaration] = first;
                    declaration.MemberDeclarations.Parent?.RemoveDeclaration(declaration);
                }
            }
        }
    }

    private static bool HaveSameConceptConstraints(
        IReadOnlyList<GenericConceptConstraint> first,
        IReadOnlyList<GenericConceptConstraint> second)
    {
        static HashSet<string> Flatten(IEnumerable<GenericConceptConstraint> constraints) =>
            constraints.SelectMany(constraint => constraint.Concepts.Select(concept =>
                $"{constraint.ParameterName}={concept}"))
                .ToHashSet(StringComparer.Ordinal);

        return Flatten(first).SetEquals(Flatten(second));
    }

    private static void AddImplicitParameterlessConstructors(
        IReadOnlyList<CompilationContext> contexts)
    {
        foreach (var declaration in contexts
            .SelectMany(context => EnumerateClasses(context.DeclarationScope.Declarations)))
        {
            if (declaration.ClassType != ClassType.Class || declaration.IsStatic ||
                declaration.MemberDeclarations.Declarations.OfType<ConstructorDeclaration>()
                    .Any(constructor => !constructor.IsStatic))
            {
                continue;
            }

            MemberModifier[] modifiers = declaration.IsAbstract
                ? [MemberModifier.Protected]
                : declaration.Visibility switch
                {
                    Visibility.Public => [MemberModifier.Public],
                    Visibility.Protected => [MemberModifier.Protected],
                    Visibility.Internal => [MemberModifier.Internal],
                    _ => [MemberModifier.Private],
                };
            var constructor = new ConstructorDeclaration(
                declaration.FullName,
                modifiers,
                declaration)
            {
                SourceSpan = declaration.SourceSpan,
            };
            constructor.SetBody([]);
            declaration.MemberDeclarations.AddDeclaration(constructor);
        }
    }

    private static void ValidateStaticConstructors(
        IReadOnlyList<CompilationContext> contexts)
    {
        foreach (var declaration in contexts
            .SelectMany(context => EnumerateClasses(context.DeclarationScope.Declarations)))
        {
            var constructors = declaration.MemberDeclarations.Declarations
                .OfType<ConstructorDeclaration>()
                .Where(constructor => constructor.IsStatic)
                .ToArray();
            if (constructors.Length > 1)
            {
                throw new CompilationErrorException(
                    $"Type '{declaration.FullName}' can declare only one static constructor.",
                    constructors[1].SourceSpan);
            }

            foreach (var constructor in constructors)
            {
                if (declaration.ClassType is not (ClassType.Class or ClassType.Struct) ||
                    constructor.MemberModifiers is not [MemberModifier.Static] ||
                    constructor.Parameters.Count != 0 ||
                    constructor.Initializer is not null ||
                    constructor.Body is null)
                {
                    throw new CompilationErrorException(
                        $"Static constructor on '{declaration.FullName}' must be the only " +
                        "static constructor, have no access modifier, parameters, " +
                        "initializer, or missing body, and belong to a class or struct.",
                        constructor.SourceSpan);
                }
            }
        }
    }

    private static void ValidateProjectReferenceTypeConflicts(
        IReadOnlyList<CompilationContext> contexts)
    {
        var duplicate = contexts
            .SelectMany(context => EnumerateProjectTypes(context.DeclarationScope.Declarations)
                .Select(type => (Context: context, Type: type)))
            .GroupBy(item => item.Type.FullName)
            .FirstOrDefault(group => group.Select(item => item.Context.ProjectName)
                .Distinct(StringComparer.Ordinal).Skip(1).Any());
        if (duplicate is not null)
        {
            var owners = duplicate.Select(item => item.Context.ProjectName)
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
            throw new CompilationErrorException(
                $"Type '{duplicate.Key}' is declared by multiple CX projects: " +
                string.Join(", ", owners) + ".",
                duplicate.Last().Type.SourceSpan);
        }
    }

    private static IEnumerable<DeclarationBase> EnumerateProjectTypes(
        IEnumerable<DeclarationBase> declarations)
    {
        foreach (var declaration in declarations)
        {
            if (declaration is ClassDeclaration classDeclaration)
            {
                yield return classDeclaration;
                foreach (var nested in EnumerateProjectTypes(
                    classDeclaration.MemberDeclarations.Declarations))
                    yield return nested;
            }
            else if (declaration is EnumDeclaration enumDeclaration)
            {
                yield return enumDeclaration;
            }
            else if (declaration is DelegateDeclaration delegateDeclaration)
            {
                yield return delegateDeclaration;
            }
        }
    }

    private static void BindDeclarationParameters(
        IEnumerable<DeclarationBase> declarations,
        IReadOnlyCollection<string> inheritedParameters,
        bool inExtension = false,
        bool validateFunctions = true,
        bool bindFieldAndPropertyTypes = true)
    {
        foreach (var declaration in declarations)
        {
            if (declaration is ExtensionDeclaration extensionDeclaration)
            {
                var parameters = inheritedParameters
                    .Concat(extensionDeclaration.GenericTypeNames).ToArray();
                BindDeclarationParameters(
                    extensionDeclaration.Members.MemberDeclarations.Declarations,
                    parameters,
                    inExtension: true,
                    validateFunctions: validateFunctions,
                    bindFieldAndPropertyTypes: bindFieldAndPropertyTypes);
                continue;
            }
            if (declaration is ClassDeclaration classDeclaration)
            {
                var parameters = inheritedParameters.Concat(classDeclaration.GenericTypeNames).ToArray();
                classDeclaration.BindGenericConstraintParameters(parameters);
                classDeclaration.BindBaseTypeParameters(parameters);
                BindDeclarationParameters(
                    classDeclaration.MemberDeclarations.Declarations,
                    parameters,
                    validateFunctions: validateFunctions,
                    bindFieldAndPropertyTypes: bindFieldAndPropertyTypes);
                continue;
            }

            switch (declaration)
            {
                case FieldDeclaration field when bindFieldAndPropertyTypes:
                    field.Type = GenericTypeSubstitution.BindParameters(field.Type, inheritedParameters);
                    break;
                case PropertyDeclaration property when bindFieldAndPropertyTypes:
                    property.Type = GenericTypeSubstitution.BindParameters(property.Type, inheritedParameters);
                    foreach (var accessor in property.PropertyAccessorDeclarations)
                    {
                        foreach (var parameter in accessor.Parameters)
                        {
                            parameter.ParameterType = GenericTypeSubstitution.BindParameters(
                                parameter.ParameterType, inheritedParameters);
                        }
                        if (accessor.BodyFunction is { } body)
                        {
                            body.ReturnType = GenericTypeSubstitution.BindParameters(
                                body.ReturnType, inheritedParameters);
                            foreach (var parameter in body.Parameters)
                            {
                                parameter.ParameterType = GenericTypeSubstitution.BindParameters(
                                    parameter.ParameterType, inheritedParameters);
                            }
                        }
                    }
                    break;
                case FunctionDeclaration function:
                    var functionParameters = inheritedParameters
                        .Concat(function.GenericTypeNames).ToArray();
                    function.ReturnType = GenericTypeSubstitution.BindParameters(
                        function.ReturnType, functionParameters);
                    foreach (var parameter in function.Parameters)
                    {
                        parameter.ParameterType = GenericTypeSubstitution.BindParameters(
                            parameter.ParameterType, functionParameters);
                    }
                    if (validateFunctions && function.OperatorToken is not null && !inExtension)
                    {
                        ValidateOperatorDeclaration(function);
                    }
                    if (validateFunctions)
                    {
                        ValidateGenericFunctionShape(function);
                    }
                    break;
            }
        }
    }

    private static void ValidateOperatorDeclaration(FunctionDeclaration function)
    {
        var token = function.OperatorToken!;
        var typeName = function.ParentClassDeclaration?.FullName.ToString() ?? "<global>";
        var memberName = $"operator {token}";
        var isExtern = function.MemberModifiers.Contains(MemberModifier.Extern);
        if (!OperatorNames.IsSupported(token) ||
            function.ParentClassDeclaration is not { ClassType: ClassType.Class or ClassType.Struct } ||
            function.IsStatic ||
            function.MemberModifiers.Any(modifier =>
                modifier is MemberModifier.Virtual or MemberModifier.Override or MemberModifier.Abstract) ||
            function.Body is null && !isExtern)
        {
            throw new CompilationErrorException(
                $"Operator '{memberName}' on '{typeName}' must be a non-static " +
                "member of a class or struct with a body.");
        }

        var isIncrement = token is "++" or "--";
        var isCompound = token.EndsWith('=') && token is not ("==" or "!=" or "<=" or ">=");
        var isPrimitiveExtension = function.ExtensionTargetType is { } extensionTarget &&
            IsPrimitiveExtensionTarget(extensionTarget);
        var isArrayReturnedMutationExtension = token is
                "++" or "--" or "+=" or "-=" or "*=" or "/=" or "%=" &&
            function.ExtensionTargetType is ArrayType arrayTarget &&
            IsType(function.ReturnType, arrayTarget);
        var isBinary = token is "+" or "-" or "*" or "/" or "%" or "&" or "|" or "^" or
            "<<" or ">>" or "==" or "!=" or "<" or "<=" or ">" or ">=";
        var expectedParameters = isBinary || isCompound ? 1 : 0;
        var allowsUnaryOrBinaryForm = token is "+" or "-";
        if ((!allowsUnaryOrBinaryForm && function.Parameters.Count != expectedParameters) ||
            allowsUnaryOrBinaryForm && function.Parameters.Count is not (0 or 1) ||
            isIncrement && (isPrimitiveExtension || isArrayReturnedMutationExtension
                ? function.ReturnType is VoidType ||
                    !IsType(function.ReturnType, function.ExtensionTargetType!)
                : function.ReturnType is not VoidType || function.Const) ||
            isCompound && (isPrimitiveExtension || isArrayReturnedMutationExtension
                ? function.ReturnType is VoidType ||
                    !IsType(function.ReturnType, function.ExtensionTargetType!)
                : function.ReturnType is not VoidType || function.Const) ||
            !isIncrement && !isCompound && function.ReturnType is VoidType ||
            (token is "==" or "!=" or "<" or "<=" or ">" or ">=") &&
                !IsType(function.ReturnType, BuiltInSystemTypes.Bool))
        {
            throw new CompilationErrorException(
                $"Operator '{memberName}' on '{typeName}' has an invalid signature.");
        }
    }

    private void AddProjectTypes(
        string moduleName,
        IEnumerable<DeclarationBase> declarations,
        IReadOnlyList<QualifiedIdentifier> imports)
    {
        foreach (var delegateDeclaration in declarations.OfType<DelegateDeclaration>()
            .Where(declaration => !IsReferencedModule(moduleName) ||
                declaration.Visibility == Visibility.Public))
        {
            _delegates.Add(new DelegateTypeSymbol(moduleName, delegateDeclaration));
        }

        foreach (var enumDeclaration in declarations.OfType<EnumDeclaration>()
            .Where(declaration => !IsReferencedModule(moduleName) ||
                declaration.Visibility == Visibility.Public))
        {
            var type = new NamedType(enumDeclaration.FullName.ToString(), []);
            type.SetResolvedType(
                enumDeclaration.FullName,
                moduleName,
                ClassType.Enum);
            _enums.Add(new EnumTypeSymbol(moduleName, enumDeclaration, type));
        }

        foreach (var classDeclaration in declarations.OfType<ClassDeclaration>()
            .Where(declaration => !IsReferencedModule(moduleName) ||
                declaration.Visibility == Visibility.Public))
        {
            classDeclaration.ProjectName = moduleName;
            var type = new NamedType(classDeclaration.FullName.ToString(), []);
            type.SetResolvedType(
                classDeclaration.FullName,
                moduleName,
                classDeclaration.ClassType);
            _types.Add(new TypeSymbol(moduleName, classDeclaration, type));
            foreach (var field in classDeclaration.MemberDeclarations.Declarations.OfType<FieldDeclaration>()
                .Where(field => !IsReferencedModule(moduleName) ||
                    field.MemberModifiers.Contains(MemberModifier.Public)))
            {
                _fields.Add(new FieldSymbol(
                    moduleName,
                    type,
                    classDeclaration.ClassType,
                    field,
                    imports));
            }
            foreach (var property in classDeclaration.MemberDeclarations.Declarations.OfType<PropertyDeclaration>()
                .Where(property => !IsReferencedModule(moduleName) ||
                    property.MemberModifiers.Contains(MemberModifier.Public)))
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
                                .ToArray(),
                            accessor))
                        .ToArray()));
            }
            AddProjectTypes(
                moduleName,
                classDeclaration.MemberDeclarations.Declarations,
                imports);
        }
    }

    private void ResolveDeclarationTypes(
        IEnumerable<DeclarationBase> declarations,
        QualifiedIdentifier currentNamespace,
        IReadOnlyList<QualifiedIdentifier> imports,
        bool publicApiOnly = false)
    {
        foreach (var declaration in declarations)
        {
            try
            {
                switch (declaration)
                {
                    case ClassDeclaration classDeclaration:
                        if (publicApiOnly && classDeclaration.Visibility != Visibility.Public)
                        {
                            break;
                        }
                        var classConceptConstraints = GetAvailableConceptConstraints(
                            classDeclaration, null);
                        foreach (var baseType in classDeclaration.BaseTypes)
                        {
                            BindAtSourceSpan(classDeclaration, () =>
                                ValidateOpenGenericTypeConstraints(
                                    baseType, classDeclaration.GenericConstraints,
                                    classConceptConstraints));
                        }
                        foreach (var field in classDeclaration.MemberDeclarations.Declarations.OfType<FieldDeclaration>()
                            .Where(field => !publicApiOnly || field.MemberModifiers.Contains(MemberModifier.Public)))
                        {
                            BindAtSourceSpan(field, () =>
                            {
                                ResolveTypeReference(field.Type, currentNamespace, imports);
                                field.Type = ArrayType.Canonicalize(field.Type);
                                ValidateOpenGenericTypeConstraints(
                                    field.Type, classDeclaration.GenericConstraints,
                                    classConceptConstraints);
                            });
                        }
                        foreach (var property in classDeclaration.MemberDeclarations.Declarations.OfType<PropertyDeclaration>()
                            .Where(property => !publicApiOnly || property.MemberModifiers.Contains(MemberModifier.Public)))
                        {
                            BindAtSourceSpan(property, () =>
                            {
                                ResolveTypeReference(property.Type, currentNamespace, imports);
                                property.Type = ArrayType.Canonicalize(property.Type);
                                ValidateOpenGenericTypeConstraints(
                                    property.Type, classDeclaration.GenericConstraints,
                                    classConceptConstraints);
                            });
                            foreach (var accessor in property.PropertyAccessorDeclarations)
                            {
                                foreach (var parameter in accessor.Parameters)
                                {
                                    BindAtSourceSpan(parameter, () =>
                                    {
                                        ResolveTypeReference(parameter.ParameterType, currentNamespace, imports);
                                        parameter.ParameterType = ArrayType.Canonicalize(
                                            parameter.ParameterType);
                                        ValidateOpenGenericTypeConstraints(
                                            parameter.ParameterType,
                                            classDeclaration.GenericConstraints,
                                            classConceptConstraints);
                                    });
                                }
                                if (accessor.BodyFunction is { } bodyFunction)
                                {
                                    BindAtSourceSpan(bodyFunction, () =>
                                    {
                                        ResolveTypeReference(
                                            bodyFunction.ReturnType, currentNamespace, imports);
                                        bodyFunction.ReturnType = ArrayType.Canonicalize(
                                            bodyFunction.ReturnType);
                                        ValidateOpenGenericTypeConstraints(
                                            bodyFunction.ReturnType,
                                            classDeclaration.GenericConstraints,
                                            classConceptConstraints);
                                    });
                                    foreach (var parameter in bodyFunction.Parameters)
                                    {
                                        BindAtSourceSpan(parameter, () =>
                                        {
                                            ResolveTypeReference(
                                                parameter.ParameterType, currentNamespace, imports);
                                            parameter.ParameterType = ArrayType.Canonicalize(
                                                parameter.ParameterType);
                                            ValidateOpenGenericTypeConstraints(
                                                parameter.ParameterType,
                                                classDeclaration.GenericConstraints,
                                                classConceptConstraints);
                                        });
                                    }
                                }
                            }
                        }
                        ResolveDeclarationTypes(
                            classDeclaration.MemberDeclarations.Declarations,
                            classDeclaration.Namespace,
                            imports,
                            publicApiOnly);
                        break;

                    case FunctionDeclaration function:
                        if (publicApiOnly && !IsPublicApiFunction(function))
                        {
                            break;
                        }
                        foreach (var typeArgument in function.SpecializationTypeArguments)
                        {
                            BindAtSourceSpan(function, () =>
                                ResolveTypeReference(
                                    typeArgument, currentNamespace, imports));
                        }
                        ResolveConceptConstraints(function, currentNamespace, imports);
                        var genericConstraints =
                            (function.ParentClassDeclaration?.GenericConstraints ?? [])
                            .Where(constraint => !function.GenericTypeNames.Contains(
                                constraint.ParameterName, StringComparer.Ordinal))
                            .Concat(function.GenericConstraints)
                            .ToArray();
                        var genericConceptConstraints = GetAvailableConceptConstraints(
                            function.ParentClassDeclaration, function);
                        BindAtSourceSpan(function, () =>
                        {
                            ResolveTypeReference(function.ReturnType, currentNamespace, imports);
                            function.ReturnType = ArrayType.Canonicalize(function.ReturnType);
                        });
                        foreach (var parameter in function.Parameters)
                        {
                            BindAtSourceSpan(parameter, () =>
                            {
                                ResolveTypeReference(parameter.ParameterType, currentNamespace, imports);
                                parameter.ParameterType = ArrayType.Canonicalize(
                                    parameter.ParameterType);
                            });
                        }
                        foreach (var constraint in function.GenericConstraints)
                        {
                            BindAtSourceSpan(function, () =>
                            {
                                if (!function.GenericTypeNames.Contains(
                                        constraint.ParameterName, StringComparer.Ordinal))
                                {
                                    throw new CompilationErrorException(
                                        $"Constraint refers to unknown generic parameter " +
                                        $"'{constraint.ParameterName}'.");
                                }
                                foreach (var type in constraint.Types)
                                {
                                    ResolveTypeReference(type, currentNamespace, imports);
                                }
                                ValidateGenericConstraintTypes(constraint);
                            });
                        }
                        BindAtSourceSpan(function, () =>
                            ValidateGenericConstraintSet(function.GenericConstraints));
                        ValidateOpenGenericTypeConstraints(
                            function.ReturnType, genericConstraints,
                            genericConceptConstraints);
                        foreach (var parameter in function.Parameters)
                        {
                            ValidateOpenGenericTypeConstraints(
                                parameter.ParameterType, genericConstraints,
                                genericConceptConstraints);
                        }
                        break;

                    case DelegateDeclaration delegateDeclaration:
                        if (publicApiOnly && delegateDeclaration.Visibility != Visibility.Public)
                        {
                            break;
                        }
                        BindAtSourceSpan(delegateDeclaration, () =>
                        {
                            ResolveTypeReference(
                                delegateDeclaration.Signature.ReturnType,
                                currentNamespace,
                                imports);
                            foreach (var parameterType in delegateDeclaration.Signature.ParameterTypes)
                            {
                                ResolveTypeReference(parameterType, currentNamespace, imports);
                            }
                            delegateDeclaration.Signature = (FunctionType)ArrayType.Canonicalize(
                                delegateDeclaration.Signature);
                        });
                        break;
                }
            }
            catch (CompilationErrorException exception) when (
                exception.SourceSpan is null && declaration.SourceSpan is not null)
            {
                throw exception.WithSourceSpan(declaration.SourceSpan);
            }
        }
    }

    private void AddProjectSymbols(
        string moduleName,
        IEnumerable<DeclarationBase> declarations)
    {
        var overloadIndexes = new Dictionary<QualifiedIdentifier, int>();
        foreach (var function in EnumerateFunctions(declarations)
            .Where(function => !function.IsGenericSpecialization)
            .Where(function => !IsReferencedModule(moduleName) || IsPublicApiFunction(function)))
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

            if (function is ConstructorDeclaration { IsStatic: false } constructor)
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

    private void BindFieldInitializers()
    {
        foreach (var field in _fields.Where(field => field.Declaration.Initializer is not null))
        {
            BindAtSourceSpan(field.Declaration, () =>
            {
                var initializer = field.Declaration.Initializer!;
                var context = new FunctionDeclaration(
                    "__cx_field_initializer",
                    field.Declaration.Namespace,
                    field.Declaration.Type,
                    [],
                    null);
                var initializerType = BindExpression(
                    initializer,
                    context,
                    field.Imports,
                    new LocalScope(),
                    field.Declaration.Type);
                if (!CanAssign(field.Declaration.Type, initializerType) &&
                    !(initializerType is NullType &&
                        IsNullAssignableToGenericField(field.Declaration)))
                {
                    throw new CompilationErrorException(
                        $"Cannot initialize field '{field.Declaration.FullName}' of type " +
                        $"'{GetTypeName(field.Declaration.Type)}' with '{GetTypeName(initializerType)}'.");
                }
                ApplyContextualType(initializer, field.Declaration.Type);
            });
        }
    }

    private static bool IsNullAssignableToGenericField(FieldDeclaration field)
    {
        var owner = field.ParentClassDeclaration;
        if (owner is null)
        {
            return false;
        }
        var type = UnwrapConst(field.Type);
        if (type is ArrayType or NamedType
            { ClassType: ClassType.Class or ClassType.Interface })
        {
            return true;
        }
        if (type is not GenericType parameter)
        {
            return false;
        }
        return owner.GenericConstraints.Any(constraint =>
            constraint.ParameterName == parameter.Name &&
            (constraint.Kind == GenericConstraintKind.ReferenceType ||
                constraint.Types.Any(required => UnwrapConst(required) is NamedType
                {
                    ClassType: ClassType.Class,
                })));
    }

    private void BindEnumMembers()
    {
        foreach (var enumType in _enums)
        {
            foreach (var member in enumType.Declaration.Members
                .Where(member => member.Value is not null))
            {
                BindAtSourceSpan(member, () =>
                {
                    var valueType = BindLiteral(member.Value!);
                    member.Value!.SetInferredType(valueType);
                    if (!IsInteger(valueType))
                    {
                        throw new CompilationErrorException(
                            $"Enum member '{member.FullName}' must have an integer value.");
                    }
                });
            }
        }
    }

    private static void BindAtSourceSpan(IHasSourceSpan source, Action bind)
    {
        try
        {
            bind();
        }
        catch (CompilationErrorException exception) when (
            exception.SourceSpan is null && source.SourceSpan is not null)
        {
            throw exception.WithSourceSpan(source.SourceSpan);
        }
    }
}

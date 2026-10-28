using CxCompiler.Model;
using CxCompiler.Model.Common;
using CxCompiler.Model.Errors;
using CxCompiler.Model.Types;
using CxCompiler.Model.Types.BuiltInTypes;

namespace CxCompiler.Semantics;

public sealed partial class SemanticBinder
{

    private void AddExtensionMembers(CompilationContext context)
    {
        foreach (var extension in context.DeclarationScope.Declarations
            .OfType<ExtensionDeclaration>())
        {
            BindAtSourceSpan(extension, () => ResolveTypeReference(
                extension.TargetType, context.Namespace, context.Imports));
            var primitiveTarget = IsPrimitiveExtensionTarget(extension.TargetType);
            var arrayTarget = UnwrapConst(extension.TargetType) is ArrayType;
            var specialTarget = primitiveTarget || arrayTarget;
            var target = GetClassDeclaration(extension.TargetType);
            if (target is null && extension.TargetType is NamedType namedTarget)
            {
                target = _types.SingleOrDefault(candidate =>
                    new QualifiedIdentifier(candidate.ModuleName, candidate.Declaration.FullName) ==
                    namedTarget.ResolvedTypeFullName)?.Declaration;
            }
            if (target is null && specialTarget)
            {
                target = GetOrCreatePrimitiveExtensionTarget(extension.TargetType);
            }
            if (target is null || (!specialTarget && target.ClassType is not
                    (ClassType.Class or ClassType.Struct or ClassType.Interface) ||
                (target.GenericTypeNames.Length == 0
                    ? extension.GenericTypeNames.Length != 0
                    : !IsSupportedGenericExtensionTarget(extension, target))) ||
                primitiveTarget && extension.GenericTypeNames.Length != 0)
            {
                throw new CompilationErrorException(
                    $"Extension '{extension.Name}' must target a class, struct, interface, " +
                    "supported primitive, or concrete array " +
                    "using each target type parameter exactly once in declaration order " +
                    $"(resolved target '{GetTypeName(extension.TargetType)}'; available types: " +
                    string.Join(", ", _types.Select(type =>
                        $"{type.ModuleName}:{type.Declaration.FullName}")) + ").",
                    extension.SourceSpan);
            }

            var sourceMembers = extension.Members.MemberDeclarations.Declarations.ToArray();
            foreach (var member in sourceMembers)
            {
                if (arrayTarget && member is PropertyDeclaration arrayProperty)
                {
                    if (extension.GenericTypeNames.Length > 0 &&
                        (ContainsFunctionTypeParameter(
                                arrayProperty.Type, extension.GenericTypeNames) ||
                         arrayProperty.PropertyAccessorDeclarations.Any(accessor =>
                            accessor.Parameters.Any(parameter =>
                                ContainsFunctionTypeParameter(
                                    parameter.ParameterType,
                                    extension.GenericTypeNames)))))
                    {
                        throw new CompilationErrorException(
                            "Generic array-target extension properties require concrete signatures.",
                            arrayProperty.SourceSpan ?? extension.SourceSpan);
                    }
                    if (arrayProperty.PropertyAccessorDeclarations.Any(accessor =>
                        accessor.Name == "set"))
                    {
                        throw new CompilationErrorException(
                            "Array-target extension properties must be read-only.",
                            arrayProperty.SourceSpan ?? extension.SourceSpan);
                    }
                }
                if (arrayTarget && member is FunctionDeclaration
                    { OperatorToken: { } arrayOperator } arrayOperatorFunction)
                {
                    if (arrayOperator is "++" or "--" or "+=" or "-=" or "*=" or "/=" or "%=")
                    {
                        var supportsReturnedArray = arrayOperator is
                                "++" or "--" or "+=" or "-=" or "*=" or "/=" or "%=" &&
                            IsType(arrayOperatorFunction.ReturnType, extension.TargetType);
                        if (!supportsReturnedArray)
                        {
                            throw new CompilationErrorException(
                                "Array-target extension operators cannot mutate the array receiver.",
                                arrayOperatorFunction.SourceSpan ?? extension.SourceSpan);
                        }
                    }
                }
                if (primitiveTarget && member is PropertyDeclaration primitiveProperty &&
                    primitiveProperty.PropertyAccessorDeclarations.Any(accessor =>
                        accessor.Name == "set"))
                {
                    throw new CompilationErrorException(
                        "Primitive-target extension properties must be read-only.",
                        member.SourceSpan ?? extension.SourceSpan);
                }
                DeclarationBase boundMember;
                switch (member)
                {
                    case FunctionDeclaration function when member is not ConstructorDeclaration:
                        boundMember = CloneExtensionFunction(
                            function, target, extension.Members.FullName, extension.TargetType);
                        break;
                    case PropertyDeclaration property:
                        boundMember = CloneExtensionProperty(
                            property, target, extension.Members.FullName, extension.TargetType);
                        break;
                    default:
                        throw new CompilationErrorException(
                            $"Extension '{extension.Name}' can contain only methods and properties.",
                            member.SourceSpan ?? extension.SourceSpan);
                }
                extension.Members.MemberDeclarations.RemoveDeclaration(member);
                extension.Members.MemberDeclarations.AddDeclaration(boundMember);
                if (boundMember is FunctionDeclaration boundFunction)
                {
                    _extensionFunctionOwners[boundFunction] = extension;
                }
                else if (boundMember is PropertyDeclaration boundProperty)
                {
                    foreach (var accessor in boundProperty.PropertyAccessorDeclarations)
                    {
                        if (accessor.BodyFunction is { } body)
                        {
                            _extensionFunctionOwners[body] = extension;
                        }
                    }
                }
            }
        }
    }

    private static bool IsPrimitiveExtensionTarget(TypeBase type)
    {
        type = UnwrapConst(type);
        return type is BoolType or CharType or IntTypeBase or FloatTypeBase or DecimalType or PtrType or StringType ||
            type is NamedType { ClassType: ClassType.Enum };
    }

    private QualifiedIdentifier GetCurrentNamespace(FunctionDeclaration function)
    {
        if (function.IsExtension && _extensionFunctionOwners.TryGetValue(function, out var owner))
        {
            return owner.Namespace;
        }

        return function.ParentClassDeclaration?.Namespace ?? function.Namespace;
    }

    private ClassDeclaration GetOrCreatePrimitiveExtensionTarget(TypeBase type)
    {
        type = UnwrapConst(type);
        var existing = _primitiveExtensionTargets.SingleOrDefault(candidate => IsType(candidate.Type, type));
        if (existing.Declaration is not null)
        {
            return existing.Declaration;
        }

        var scope = new DeclarationScope(@namespace: new QualifiedIdentifier(
            "System", "__PrimitiveExtensionTargets"));
        var declaration = new ClassDeclaration(
            [ClassModifier.Public],
            partial: false,
            ClassType.Class,
            customClassTypeName: null,
            name: type is ArrayType ? "ArrayExtensionTarget" : $"{type.Name}ExtensionTarget",
            genericTypeNames: GetGenericArrayExtensionParameters(type),
            scope);
        _primitiveExtensionTargets.Add((type, declaration));
        return declaration;
    }

    private static bool IsSupportedGenericExtensionTarget(
        ExtensionDeclaration extension,
        ClassDeclaration target)
    {
        if (extension.GenericTypeNames.Length != target.GenericTypeNames.Length)
        {
            return false;
        }

        if (UnwrapConst(extension.TargetType) is ArrayType arrayTarget)
        {
            var targetParameters = GetGenericTypeParameterNames(arrayTarget);
            return targetParameters.SequenceEqual(target.GenericTypeNames) &&
                extension.GenericTypeNames.SequenceEqual(target.GenericTypeNames);
        }

        if (extension.TargetType is not NamedType namedTarget ||
            namedTarget.TypeArguments.Count != target.GenericTypeNames.Length)
        {
            return false;
        }

        return extension.GenericTypeNames.SequenceEqual(target.GenericTypeNames) &&
            namedTarget.TypeArguments.Select((argument, index) =>
                argument is GenericType parameter &&
                parameter.Name == target.GenericTypeNames[index]).All(matches => matches);
    }

    private static string[] GetGenericArrayExtensionParameters(TypeBase type)
    {
        return UnwrapConst(type) is ArrayType arrayType
            ? GetGenericTypeParameterNames(arrayType).ToArray()
            : [];
    }

    private static IEnumerable<string> GetGenericTypeParameterNames(TypeBase type)
    {
        var names = type switch
        {
            ConstType constant => GetGenericTypeParameterNames(constant.UnderlyingType),
            NullableType nullable => GetGenericTypeParameterNames(nullable.UnderlyingType),
            ArrayType array => GetGenericTypeParameterNames(array.ElementType),
            GenericType generic => [generic.Name],
            NamedType named => named.TypeArguments
                .SelectMany(GetGenericTypeParameterNames),
            _ => [],
        };
        return names.Distinct(StringComparer.Ordinal);
    }

    private static FunctionDeclaration CloneExtensionFunction(
        FunctionDeclaration source,
        ClassDeclaration target,
        QualifiedIdentifier extensionName,
        TypeBase extensionTargetType)
    {
        var function = new FunctionDeclaration(
            source.Name,
            extensionName,
            source.ReturnType,
            source.MemberModifiers,
            target,
            source.Const,
            source.GenericTypeNames,
            source.OperatorToken,
            source.GenericConstraints,
            source.ConceptConstraints,
            source.SpecializationTypeArguments,
            source.UnclassifiedGenericTypeArguments)
        {
            SourceSpan = source.SourceSpan,
            IsExtension = true,
            ExtensionTargetType = extensionTargetType,
        };
        foreach (var parameter in source.Parameters)
        {
            function.AddParameter(parameter);
        }
        if (source.Body is not null)
        {
            function.SetBody(source.Body);
        }
        return function;
    }

    private static PropertyDeclaration CloneExtensionProperty(
        PropertyDeclaration source,
        ClassDeclaration target,
        QualifiedIdentifier extensionName,
        TypeBase extensionTargetType)
    {
        var property = new PropertyDeclaration(
            source.Name,
            extensionName,
            source.Type,
            source.MemberModifiers,
            target)
        {
            SourceSpan = source.SourceSpan,
            IsExtension = true,
            ExtensionTargetType = extensionTargetType,
        };
        foreach (var sourceAccessor in source.PropertyAccessorDeclarations)
        {
            var accessor = new PropertyAccessorDeclaration(
                property, sourceAccessor.Name, sourceAccessor.Extern, sourceAccessor.Const)
            {
                SourceSpan = sourceAccessor.SourceSpan,
            };
            foreach (var parameter in sourceAccessor.Parameters)
            {
                accessor.AddParameter(parameter);
            }
            if (sourceAccessor.Body is not null)
            {
                accessor.SetBody(sourceAccessor.Body);
                if (accessor.BodyFunction is { } bodyFunction)
                {
                    bodyFunction.IsExtension = true;
                        bodyFunction.ExtensionTargetType = extensionTargetType;
                }
            }
            property.AddAccessor(accessor);
        }
        return property;
    }

    private void AddExtensionPropertySymbols(CompilationContext context)
    {
        var moduleName = context.ProjectName ?? _project!.Name;
        foreach (var extension in context.DeclarationScope.Declarations
            .OfType<ExtensionDeclaration>())
        {
            if (IsExternalReference(context) && extension.Visibility != Visibility.Public)
            {
                continue;
            }
            var target = GetMemberClassDeclaration(extension.TargetType)!;
            foreach (var property in extension.Members.MemberDeclarations.Declarations
                .OfType<PropertyDeclaration>()
                .Where(property => !IsExternalReference(context) ||
                    property.MemberModifiers.Contains(MemberModifier.Public)))
            {
                var propertyTarget = property.ParentClassDeclaration ?? target;
                var propertySymbol = new PropertySymbol(
                    moduleName,
                    property.FullName,
                    extension.TargetType,
                    propertyTarget.ClassType,
                    property.Type,
                    property.IsStatic,
                    property.PropertyAccessorDeclarations.Select(accessor =>
                        new PropertyAccessorSymbol(
                            accessor.Name,
                            accessor.Const,
                            accessor.Parameters.Select(parameter => parameter.ParameterType).ToArray()))
                        .ToArray());
                _properties.Add(propertySymbol);
                _extensionPropertyOwners[propertySymbol] = extension;
                _extensionPropertyTargets[propertySymbol] = propertyTarget;
            }
        }
    }

    private bool IsExtensionInScope(ExtensionDeclaration extension)
    {
        if (_activeImports?.Contains(extension.Namespace) == true)
        {
            return true;
        }
        if (_activeFunction is null)
        {
            return false;
        }
        if (_extensionFunctionOwners.TryGetValue(_activeFunction, out var activeOwner) &&
            ReferenceEquals(activeOwner, extension))
        {
            return true;
        }
        return _activeFunction.Namespace == extension.Namespace ||
            _activeFunction.ParentClassDeclaration is { } parent &&
            parent.Namespace == extension.Namespace;
    }
}

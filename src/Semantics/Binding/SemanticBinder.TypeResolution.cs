using CxCompiler.Model.Common;
using CxCompiler.Model.Errors;
using CxCompiler.Model.Types;
using CxCompiler.Model.Types.BuiltInTypes;

namespace CxCompiler.Semantics;

public sealed partial class SemanticBinder
{

    private static bool ContainsOpenTypeParameter(TypeBase type) => type switch
    {
        GenericType => true,
        ConstType item => ContainsOpenTypeParameter(item.UnderlyingType),
        ArrayType item => ContainsOpenTypeParameter(item.ElementType),
        NullableType item => ContainsOpenTypeParameter(item.UnderlyingType),
        NamedType item => item.TypeArguments.Any(ContainsOpenTypeParameter),
        _ => false,
    };

    private static bool IsValueTypeGenericArgument(TypeBase type) => type switch
    {
        ConstType constant => IsValueTypeGenericArgument(constant.UnderlyingType),
        ArrayType or NullableType or StringType or ObjectType => false,
        NamedType named => named.ClassType is ClassType.Struct or ClassType.Enum,
        _ => type is not GenericType,
    };

    private static bool MatchesConstructedDeclaration(
        TypeSymbol candidate,
        TypeBase requestedType)
    {
        if (IsType(candidate.Type, requestedType))
        {
            return true;
        }
        return requestedType is NamedType named &&
            named.ResolvedTypeFullName ==
                new QualifiedIdentifier(candidate.ModuleName, candidate.Declaration.FullName) &&
            named.TypeArguments.Count == candidate.Declaration.GenericTypeNames.Length;
    }

    private void ResolveTypeReference(
        TypeBase type,
        QualifiedIdentifier currentNamespace,
        IReadOnlyList<QualifiedIdentifier> imports)
    {
        switch (type)
        {
            case FixedArrayType array:
                ResolveTypeReference(array.ElementType, currentNamespace, imports);
                if (!IsNativeScalar(array.ElementType))
                    throw new CompilationErrorException("Fixed arrays require native elements.", type.SourceSpan);
                return;
            case PtrType { Pointee: { } pointee }:
                ResolveTypeReference(pointee, currentNamespace, imports);
                if (!IsNativeScalar(pointee) && UnwrapConst(pointee) is not VoidType and not NamedType { NativeType: not null })
                    throw new CompilationErrorException("Typed pointers require a native scalar, pointer, or native SDK pointee.", type.SourceSpan);
                return;
            case ConstType constType:
                ResolveTypeReference(constType.UnderlyingType, currentNamespace, imports);
                return;
            case ArrayType arrayType:
                ResolveTypeReference(arrayType.ElementType, currentNamespace, imports);
                if (UnwrapConst(arrayType.ElementType) is NamedType { NativeType.Opaque: true })
                    throw new CompilationErrorException("Opaque native types are legal through pointers only, not managed arrays.", type.SourceSpan);
                return;
            case NullableType nullableType:
                ResolveTypeReference(nullableType.UnderlyingType, currentNamespace, imports);
                if (UnwrapConst(nullableType.UnderlyingType) is NamedType { NativeType: not null })
                    throw new CompilationErrorException("Native types do not use managed nullable storage; use a pointer or an explicit native representation.", type.SourceSpan);
                return;
            case FunctionType functionType:
                ResolveTypeReference(functionType.ReturnType, currentNamespace, imports);
                foreach (var parameterType in functionType.ParameterTypes)
                {
                    ResolveTypeReference(parameterType, currentNamespace, imports);
                }
                return;
            case NamedType namedType:
                ResolveNamedTypeReference(namedType, currentNamespace, imports);
                return;
        }
    }

    private void ResolveNamedTypeReference(
        NamedType namedType,
        QualifiedIdentifier currentNamespace,
        IReadOnlyList<QualifiedIdentifier> imports)
    {
        foreach (var argument in namedType.TypeArguments)
        {
            ResolveTypeReference(argument, currentNamespace, imports);
            if (argument is NamedType unresolved &&
                unresolved.ResolvedTypeFullName.ToString() == "void")
            {
                throw new CompilationErrorException(
                    $"Cannot resolve generic argument '{unresolved.Name}' for '{namedType.Name}'.");
            }
        }
        if (namedType.ResolvedTypeFullName.ToString() != "void")
        {
            SetConstructedIdentity(namedType);
            return;
        }
        var sourceName = new QualifiedIdentifier(
            namedType.Name.Split('.', StringSplitOptions.RemoveEmptyEntries));
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
            throw new CompilationErrorException(
                $"Type name '{namedType.Name}' is ambiguous.");
        }
        if (classCandidates.SingleOrDefault() is { } classCandidate)
        {
            if (namedType.TypeArguments.Count != classCandidate.Declaration.GenericTypeNames.Length)
            {
                throw new CompilationErrorException(
                    $"Type '{namedType.Name}' expects " +
                    $"{classCandidate.Declaration.GenericTypeNames.Length} generic arguments, " +
                    $"but got {namedType.TypeArguments.Count}.");
            }
            namedType.SetResolvedType(
                classCandidate.Declaration.FullName,
                classCandidate.ModuleName,
                classCandidate.Declaration.ClassType);
            namedType.NativeType = classCandidate.Declaration.NativeType;
            ValidateGenericTypeConstraints(classCandidate.Declaration, namedType);
        }
        else if (enumCandidates.SingleOrDefault() is { } enumCandidate)
        {
            if (namedType.TypeArguments.Count != 0)
            {
                throw new CompilationErrorException(
                    $"Enum '{namedType.Name}' cannot have generic arguments.");
            }
            namedType.SetResolvedType(
                enumCandidate.Declaration.FullName,
                enumCandidate.ModuleName,
                ClassType.Enum);
            namedType.EnumUnderlyingType = enumCandidate.Declaration.UnderlyingType;
        }
        else if (delegateCandidates.SingleOrDefault() is { } delegateCandidate)
        {
            if (namedType.TypeArguments.Count != 0)
            {
                throw new CompilationErrorException(
                    $"Delegate '{namedType.Name}' cannot have generic arguments.");
            }
            namedType.SetDelegateType(
                delegateCandidate.Declaration.FullName,
                delegateCandidate.ModuleName,
                delegateCandidate.Declaration.Signature);
        }
        else if (aliasCandidates.SingleOrDefault() is { } aliasCandidate)
        {
            if (namedType.TypeArguments.Count !=
                aliasCandidate.Declaration.GenericTypeNames.Length)
            {
                throw new CompilationErrorException(
                    $"Alias '{namedType.Name}' expects " +
                    $"{aliasCandidate.Declaration.GenericTypeNames.Length} generic arguments, " +
                    $"but got {namedType.TypeArguments.Count}.");
            }
            ResolveAlias(aliasCandidate, namedType);
        }
        SetConstructedIdentity(namedType);
    }

    private void ResolveAlias(AliasSymbol symbol, NamedType destination)
    {
        if (destination.TypeArguments.Count !=
            symbol.Declaration.GenericTypeNames.Length)
        {
            throw new CompilationErrorException(
                $"Alias '{destination.Name}' expects " +
                $"{symbol.Declaration.GenericTypeNames.Length} generic arguments, " +
                $"but got {destination.TypeArguments.Count}.");
        }

        var key = (symbol.ModuleName, symbol.Declaration.FullName);
        if (!_resolvingAliases.Add(key))
        {
            throw new CompilationErrorException(
                $"Cyclic alias involving '{symbol.Declaration.FullName}'.");
        }

        try
        {
            var arguments = symbol.Declaration.GenericTypeNames
                .Zip(destination.TypeArguments)
                .ToDictionary(pair => pair.First, pair => pair.Second, StringComparer.Ordinal);
            var substitutedTarget = GenericTypeSubstitution.Substitute(
                symbol.Declaration.TargetType,
                arguments);
            if (substitutedTarget is not NamedType namedTarget)
            {
                ResolveTypeReference(substitutedTarget, symbol.Declaration.Namespace, symbol.Imports);
                destination.SetPrimitiveAliasTarget(substitutedTarget);
                return;
            }
            var target = NamedType.CreateWithTypeArguments(
                namedTarget.Name,
                namedTarget.TypeArguments);
            ResolveNamedTypeReference(
                target,
                symbol.Declaration.Namespace,
                symbol.Imports);
            if (target.ResolvedTypeFullName.ToString() == "void" && target.AliasTargetType is null)
            {
                throw new CompilationErrorException(
                    $"Cannot resolve alias target '{symbol.Declaration.TargetType.Name}' " +
                    $"for '{symbol.Declaration.FullName}'.");
            }
            destination.SetAliasTarget(target);
        }
        finally
        {
            _resolvingAliases.Remove(key);
        }
    }

    private void SetConstructedIdentity(NamedType type)
    {
        if (type.TypeArguments.Count == 0 ||
            type.ResolvedTypeFullName.ToString() == "void" ||
            type.TypeArguments.Any(ContainsOpenTypeParameter))
        {
            return;
        }
        var parts = type.ResolvedTypeFullName.Parts;
        type.ConstructedIdentity = GenericTypeIdentity.Create(
            parts[0], string.Join('.', parts.Skip(1)), type.TypeArguments);
        var identity = type.ConstructedIdentity.CanonicalName;
        if (_types.SingleOrDefault(candidate =>
            candidate.ModuleName == parts[0] &&
            candidate.Declaration.FullName == new QualifiedIdentifier(parts.Skip(1).ToArray()))
            is { } localType &&
            !_project!.ReferencedGenericTypeIdentities.Contains(identity))
        {
            _project!.AddGenericTypeInstance(type, localType.Declaration);
            if (!_expandedGenericTypeInstances.Contains(identity) &&
                _expandingGenericTypeInstances.Add(identity))
            {
                try
                {
                    var arguments = localType.Declaration.GenericTypeNames
                        .Zip(type.TypeArguments)
                        .ToDictionary(pair => pair.First, pair => pair.Second,
                            StringComparer.Ordinal);
                    foreach (var field in localType.Declaration.MemberDeclarations.Declarations
                        .OfType<FieldDeclaration>().Where(field => !field.IsStatic))
                    {
                        RegisterConstructedGenericTypes(
                            GenericTypeSubstitution.Substitute(field.Type, arguments));
                    }
                    if (localType.Declaration.BaseClassType is { } baseType)
                    {
                        RegisterConstructedGenericTypes(
                            GenericTypeSubstitution.Substitute(baseType, arguments));
                    }
                    _expandedGenericTypeInstances.Add(identity);
                }
                finally
                {
                    _expandingGenericTypeInstances.Remove(identity);
                }
            }
        }
    }

    private void RegisterConstructedGenericTypes(TypeBase type)
    {
        switch (type)
        {
            case ConstType constant:
                RegisterConstructedGenericTypes(constant.UnderlyingType);
                break;
            case ArrayType array:
                RegisterConstructedGenericTypes(array.ElementType);
                break;
            case NullableType nullable:
                RegisterConstructedGenericTypes(nullable.UnderlyingType);
                break;
            case NamedType namedType:
                foreach (var argument in namedType.TypeArguments)
                {
                    RegisterConstructedGenericTypes(argument);
                }
                SetConstructedIdentity(namedType);
                break;
        }
    }
}

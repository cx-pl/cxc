namespace CxCompiler.Model.Types;

/// <summary>Substitutes type parameters without changing the declaration template.</summary>
public static class GenericTypeSubstitution
{
    public static TypeBase BindParameters(TypeBase type, IReadOnlyCollection<string> parameterNames)
    {
        return Replace(type, named =>
            named.GenericParams.Length == 0 && parameterNames.Contains(named.Name)
                ? new GenericType(named.Name)
                : null);
    }

    public static TypeBase Substitute(TypeBase type, IReadOnlyDictionary<string, TypeBase> arguments)
    {
        return Replace(type, named =>
            named.GenericParams.Length == 0 && arguments.TryGetValue(named.Name, out var argument)
                ? argument
                : null,
            generic => arguments.TryGetValue(generic.Name, out var argument) ? argument : null);
    }

    private static TypeBase Replace(
        TypeBase type,
        Func<NamedType, TypeBase?> replaceNamed,
        Func<GenericType, TypeBase?>? replaceGeneric = null)
    {
        return type switch
        {
            ConstType item => new ConstType(Replace(item.UnderlyingType, replaceNamed, replaceGeneric)),
            ArrayType item => new ArrayType(Replace(item.ElementType, replaceNamed, replaceGeneric)),
            NullableType item => new NullableType(Replace(item.UnderlyingType, replaceNamed, replaceGeneric)),
            BuiltInTypes.FunctionType item => new BuiltInTypes.FunctionType(
                item.Namespace,
                Replace(item.ReturnType, replaceNamed, replaceGeneric),
                item.ParameterTypes.Select(parameter =>
                    Replace(parameter, replaceNamed, replaceGeneric)).ToArray()),
            NamedType item => ReplaceNamed(item, replaceNamed, replaceGeneric),
            GenericType item => replaceGeneric?.Invoke(item) ?? item,
            _ => type,
        };
    }

    private static TypeBase ReplaceNamed(
        NamedType type,
        Func<NamedType, TypeBase?> replaceNamed,
        Func<GenericType, TypeBase?>? replaceGeneric)
    {
        if (replaceNamed(type) is { } replacement)
        {
            return replacement;
        }
        if (type.TypeArguments.Count == 0)
        {
            return type;
        }
        var result = new NamedType(type.Name, type.GenericParams);
        if (type.ResolvedTypeFullName.ToString() != "void")
        {
            var parts = type.ResolvedTypeFullName.Parts;
            result.SetResolvedType(new Common.QualifiedIdentifier(parts.Skip(1).ToArray()),
                parts[0], type.ClassType);
        }
        result.SetTypeArguments(type.TypeArguments
            .Select(item => Replace(item, replaceNamed, replaceGeneric)).ToArray());
        return result;
    }
}

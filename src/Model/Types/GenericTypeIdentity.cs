using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using CxCompiler.Model.Common;
using CxCompiler.Model.Types.BuiltInTypes;

namespace CxCompiler.Model.Types;

/// <summary>Stable identity for one closed generic type.</summary>
public sealed class GenericTypeIdentity
{
    public string CanonicalName { get; }
    public string CIdentifier { get; }
    public ulong RuntimeHash { get; }

    private GenericTypeIdentity(string canonicalName)
    {
        CanonicalName = canonicalName;
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(canonicalName));
        CIdentifier = "cx_generic_" + Convert.ToHexString(digest).ToLowerInvariant();
        RuntimeHash = BinaryPrimitives.ReadUInt64LittleEndian(digest.AsSpan(0, 8)) ^
            BinaryPrimitives.ReadUInt64LittleEndian(digest.AsSpan(8, 8)) ^
            BinaryPrimitives.ReadUInt64LittleEndian(digest.AsSpan(16, 8)) ^
            BinaryPrimitives.ReadUInt64LittleEndian(digest.AsSpan(24, 8));
    }

    public static GenericTypeIdentity Create(
        string moduleName,
        string declarationName,
        IReadOnlyList<TypeBase> arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleName);
        ArgumentException.ThrowIfNullOrWhiteSpace(declarationName);
        if (arguments.Count == 0)
        {
            throw new ArgumentException("A constructed generic type needs arguments.", nameof(arguments));
        }
        return new GenericTypeIdentity(Encode(["type", moduleName, declarationName,
            arguments.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
            .. arguments.Select(GetTypeKey)]));
    }

    public static GenericTypeIdentity CreateFunction(
        string moduleName,
        string functionName,
        IReadOnlyList<TypeBase> arguments,
        IReadOnlyList<TypeBase> parameterTypes,
        TypeBase returnType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleName);
        ArgumentException.ThrowIfNullOrWhiteSpace(functionName);
        if (arguments.Count == 0)
        {
            throw new ArgumentException("A generic function needs type arguments.", nameof(arguments));
        }
        return new GenericTypeIdentity(Encode(["function", moduleName, functionName,
            arguments.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
            .. arguments.Select(GetTypeKey),
            parameterTypes.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
            .. parameterTypes.Select(GetTypeKey), GetTypeKey(returnType)]));
    }

    private static string GetTypeKey(TypeBase type)
    {
        return type switch
        {
            ConstType item => GetTypeKey(item.UnderlyingType),
            ArrayType item => Encode("array", GetTypeKey(item.ElementType)),
            NullableType item => Encode("nullable", GetTypeKey(item.UnderlyingType)),
            GenericType item => throw new ArgumentException(
                $"Unsubstituted type parameter '{item.Name}' has no runtime identity."),
            StringType => Encode("named", "cxcore.System.String", "0"),
            ObjectType => Encode("named", "cxcore.System.Object", "0"),
            NamedType item when item.ResolvedTypeFullName.ToString() != "void" =>
                Encode(["named", item.ResolvedTypeFullName.ToString(),
                    item.TypeArguments.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    .. item.TypeArguments.Select(GetTypeKey)]),
            NamedType item => throw new ArgumentException(
                $"Unresolved or constructed type '{item.Name}' has no canonical identity yet."),
            _ => Encode("builtin", type.FullName.ToString()),
        };
    }

    private static string Encode(params string[] parts) =>
        string.Concat(parts.Select(part => $"{part.Length}:{part}"));
}

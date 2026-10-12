using CxCompiler.Model.Common;
using CxCompiler.Model.Types.BuiltInTypes;

namespace CxCompiler.Model.Types;

/// <summary>
/// Compiler-special representation of the public System.Collections.Array&lt;T&gt; type.
/// The parser creates this type for T[] syntax, while named Array&lt;T&gt; syntax is
/// canonicalized to the same representation after resolution.
/// </summary>
public class ArrayType : NamedType
{
    public TypeBase ElementType { get; }

    public ArrayType(TypeBase elementType)
        : base($"{elementType.Name}[]", ["T"])
    {
        ElementType = elementType;
        SetTypeArguments([elementType]);
        SetResolvedType(
            new QualifiedIdentifier("System", "Collections", "Array"),
            "cxcore",
            ClassType.Class);
    }

    public static TypeBase Canonicalize(TypeBase type)
    {
        switch (type)
        {
            case ConstType constant:
            {
                var underlyingType = Canonicalize(constant.UnderlyingType);
                return ReferenceEquals(underlyingType, constant.UnderlyingType)
                    ? type
                    : new ConstType(underlyingType);
            }
            case ArrayType array:
            {
                var elementType = Canonicalize(array.ElementType);
                return ReferenceEquals(elementType, array.ElementType)
                    ? array
                    : new ArrayType(elementType);
            }
            case NullableType nullable:
            {
                var underlyingType = Canonicalize(nullable.UnderlyingType);
                return ReferenceEquals(underlyingType, nullable.UnderlyingType)
                    ? nullable
                    : new NullableType(underlyingType);
            }
            case FunctionType function:
            {
                var returnType = Canonicalize(function.ReturnType);
                var parameterTypes = function.ParameterTypes
                    .Select(Canonicalize)
                    .ToArray();
                return ReferenceEquals(returnType, function.ReturnType) &&
                    parameterTypes.Zip(function.ParameterTypes)
                        .All(pair => ReferenceEquals(pair.First, pair.Second))
                    ? function
                    : new FunctionType(
                        function.Namespace,
                        returnType,
                        parameterTypes);
            }
            case NamedType named when TryGetElementType(named, out var elementType):
                return new ArrayType(Canonicalize(elementType));
            case NamedType named when named.TypeArguments.Count > 0:
            {
                var arguments = named.TypeArguments.Select(Canonicalize).ToArray();
                if (arguments.Zip(named.TypeArguments)
                    .All(pair => ReferenceEquals(pair.First, pair.Second)))
                {
                    return named;
                }

                var result = NamedType.CreateWithTypeArguments(named.Name, arguments);
                if (named.ResolvedTypeFullName.ToString() != "void")
                {
                    var parts = named.ResolvedTypeFullName.Parts;
                    result.SetResolvedType(
                        new QualifiedIdentifier(parts.Skip(1).ToArray()),
                        parts[0],
                        named.ClassType);
                }
                result.ConstructedIdentity = named.ConstructedIdentity;
                return result;
            }
            default:
                return type;
        }
    }

    public static bool TryGetElementType(TypeBase type, out TypeBase elementType)
    {
        type = type is ConstType constant ? constant.UnderlyingType : type;
        if (type is ArrayType array)
        {
            elementType = array.ElementType;
            return true;
        }

        if (type is NamedType
            {
                TypeArguments.Count: 1,
            } namedArray &&
            namedArray.ResolvedTypeFullName.Parts is
                [_, "System", "Collections", "Array"])
        {
            elementType = namedArray.TypeArguments[0];
            return true;
        }

        elementType = null!;
        return false;
    }

    public static bool AreEquivalent(TypeBase left, TypeBase right)
    {
        var leftIsArray = TryGetElementType(left, out var leftElement);
        var rightIsArray = TryGetElementType(right, out var rightElement);
        return leftIsArray || rightIsArray
            ? leftIsArray && rightIsArray && AreEquivalent(leftElement, rightElement)
            : SameNonArrayType(left, right);
    }

    private static bool SameNonArrayType(TypeBase left, TypeBase right)
    {
        left = left is ConstType leftConst ? leftConst.UnderlyingType : left;
        right = right is ConstType rightConst ? rightConst.UnderlyingType : right;
        if (left is NamedType leftNamed && right is NamedType rightNamed)
        {
            return leftNamed.ResolvedTypeFullName == rightNamed.ResolvedTypeFullName &&
                leftNamed.TypeArguments.Count == rightNamed.TypeArguments.Count &&
                leftNamed.TypeArguments.Zip(rightNamed.TypeArguments)
                    .All(pair => AreEquivalent(pair.First, pair.Second));
        }

        return left.FullName == right.FullName;
    }
}

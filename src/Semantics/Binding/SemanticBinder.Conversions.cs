using System.Numerics;
using CxCompiler.Model.Common;
using CxCompiler.Model.Errors;
using CxCompiler.Model.Expressions;
using CxCompiler.Model.Types;
using CxCompiler.Model.Types.BuiltInTypes;

namespace CxCompiler.Semantics;

public sealed partial class SemanticBinder
{

    private static bool IsIntegralConstantAssignable(TypeBase targetType, ExpressionBase expression)
    {
        if (expression is not LiteralExpression literal)
        {
            return false;
        }
        var source = literal.SourceText;
        var isNegative = source.StartsWith('-');
        if (isNegative || source.StartsWith('+'))
        {
            source = source[1..];
        }
        if (!TryParseNonNegativeInteger(source, out var value))
        {
            return false;
        }
        if (isNegative)
        {
            value = -value;
        }

        var range = GetIntegralRange(UnwrapConst(targetType));
        return range is { } bounds && value >= bounds.Minimum && value <= bounds.Maximum;
    }

    private static (BigInteger Minimum, BigInteger Maximum)? GetIntegralRange(TypeBase type)
    {
        if (IsType(type, BuiltInSystemTypes.SByte))
        {
            return (sbyte.MinValue, sbyte.MaxValue);
        }
        if (IsType(type, BuiltInSystemTypes.Byte))
        {
            return (byte.MinValue, byte.MaxValue);
        }
        if (IsType(type, BuiltInSystemTypes.Short))
        {
            return (short.MinValue, short.MaxValue);
        }
        if (IsType(type, BuiltInSystemTypes.UShort))
        {
            return (ushort.MinValue, ushort.MaxValue);
        }
        if (IsType(type, BuiltInSystemTypes.Int))
        {
            return (int.MinValue, int.MaxValue);
        }
        if (IsType(type, BuiltInSystemTypes.UInt))
        {
            return (uint.MinValue, uint.MaxValue);
        }
        if (IsType(type, BuiltInSystemTypes.Long))
        {
            return (long.MinValue, long.MaxValue);
        }
        if (IsType(type, BuiltInSystemTypes.ULong))
        {
            return (ulong.MinValue, ulong.MaxValue);
        }
        return null;
    }

    private static bool TryParseNonNegativeInteger(string source, out BigInteger value)
    {
        var digits = source.TrimEnd('u', 'U', 'l', 'L');
        var prefixLength = 0;
        var radix = 10;
        if (digits.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            radix = 16;
            prefixLength = 2;
        }
        else if (digits.StartsWith("0b", StringComparison.OrdinalIgnoreCase))
        {
            radix = 2;
            prefixLength = 2;
        }
        else if (digits.Length > 1 && digits[0] == '0')
        {
            radix = 8;
            prefixLength = 1;
        }
        digits = digits[prefixLength..];
        if (digits.Length == 0)
        {
            value = BigInteger.Zero;
            return prefixLength == 2;
        }

        value = BigInteger.Zero;
        foreach (var digit in digits)
        {
            var digitValue = digit switch
            {
                >= '0' and <= '9' => digit - '0',
                >= 'a' and <= 'f' => digit - 'a' + 10,
                >= 'A' and <= 'F' => digit - 'A' + 10,
                _ => int.MaxValue,
            };
            if (digitValue >= radix)
            {
                return false;
            }
            value = value * radix + digitValue;
        }
        return true;
    }

    private bool ParametersMatch(
        IReadOnlyList<TypeBase> parameterTypes,
        IReadOnlyList<TypeBase> argumentTypes)
    {
        return parameterTypes.Count == argumentTypes.Count &&
            parameterTypes.Zip(argumentTypes).All(pair => CanAssign(pair.First, pair.Second));
    }

    private bool ParametersMatch(
        FunctionSymbol function,
        IReadOnlyList<TypeBase> argumentTypes)
    {
        if (argumentTypes.Count > function.ParameterTypes.Count ||
            !function.ParameterTypes.Zip(argumentTypes)
                .All(pair => CanAssign(pair.First, pair.Second)))
        {
            return false;
        }

        var parameters = function.Declaration?.Parameters;
        if (parameters is null || parameters.Count != function.ParameterTypes.Count)
        {
            return argumentTypes.Count == function.ParameterTypes.Count;
        }

        return parameters.Skip(argumentTypes.Count).All(parameter =>
            parameter.DefaultValueText is not null);
    }

    private IReadOnlyList<ExpressionBase> BindDefaultArguments(
        FunctionSymbol function,
        int suppliedArgumentCount,
        FunctionDeclaration context,
        IReadOnlyList<QualifiedIdentifier> imports,
        LocalScope scope)
    {
        var parameters = function.Declaration?.Parameters;
        if (parameters is null || suppliedArgumentCount >= parameters.Count)
        {
            return [];
        }

        var result = new List<ExpressionBase>();
        for (var index = suppliedArgumentCount; index < parameters.Count; index++)
        {
            var parameter = parameters[index];
            var defaultText = parameter.DefaultValueText ?? throw new CompilationErrorException(
                $"Required parameter '{parameter.Name}' cannot be omitted.", parameter.SourceSpan);
            var expression = CreateDefaultParameterExpression(parameter);
            var expectedType = function.ParameterTypes[index];
            var actualType = BindExpression(
                expression,
                context,
                imports,
                scope,
                expectedType);
            if (!CanAssign(expectedType, actualType) &&
                !IsIntegralConstantAssignable(expectedType, expression))
            {
                throw new CompilationErrorException(
                    $"Default value '{defaultText}' cannot be assigned to parameter " +
                    $"'{parameter.Name}' of type '{GetTypeName(expectedType)}'.",
                    parameter.SourceSpan);
            }
            ApplyContextualType(expression, expectedType);
            result.Add(expression);
        }
        return result;
    }

    private static ExpressionBase CreateDefaultParameterExpression(FunctionParameter parameter)
    {
        var defaultText = parameter.DefaultValueText ?? throw new ArgumentException(
            "The parameter does not have a default value.", nameof(parameter));
        if (!parameter.DefaultValueIsIdentifier)
        {
            return new LiteralExpression(defaultText);
        }

        var parts = defaultText.Split('.');
        return parts.Length > 1
            ? new MemberAccessExpression(
                new IdentifierExpression(new QualifiedIdentifier(parts[..^1])),
                parts[^1])
            : new IdentifierExpression(new QualifiedIdentifier(parts));
    }

    private static T[] SelectBestConversionCandidates<T>(
        IReadOnlyList<T> candidates,
        IReadOnlyList<TypeBase> argumentTypes,
        Func<T, IReadOnlyList<TypeBase>> getParameterTypes)
    {
        if (candidates.Count < 2)
        {
            return candidates.ToArray();
        }

        return candidates
            .Select((candidate, index) => (candidate, index))
            .Where(current => !candidates
                .Select((candidate, index) => (candidate, index))
                .Any(other => other.index != current.index &&
                    IsBetterConversionTarget(
                        argumentTypes,
                        getParameterTypes(other.candidate),
                        getParameterTypes(current.candidate))))
            .Select(item => item.candidate)
            .ToArray();
    }

    private static bool IsBetterConversionTarget(
        IReadOnlyList<TypeBase> sourceTypes,
        IReadOnlyList<TypeBase> firstTargetTypes,
        IReadOnlyList<TypeBase> secondTargetTypes)
    {
        var foundBetterConversion = false;
        for (var index = 0; index < sourceTypes.Count; index++)
        {
            var source = sourceTypes[index];
            var first = firstTargetTypes[index];
            var second = secondTargetTypes[index];
            if (IsType(first, second))
            {
                continue;
            }
            if (IsType(source, first))
            {
                foundBetterConversion = true;
                continue;
            }
            if (IsType(source, second))
            {
                return false;
            }

            var firstConvertsToSecond = IsImplicitNumericConversion(first, second);
            var secondConvertsToFirst = IsImplicitNumericConversion(second, first);
            if (firstConvertsToSecond == secondConvertsToFirst)
            {
                return false;
            }
            if (secondConvertsToFirst)
            {
                return false;
            }
            foundBetterConversion = true;
        }

        return foundBetterConversion;
    }

    private bool CanAssign(TypeBase targetType, TypeBase valueType)
    {
        if (IsType(targetType, valueType) ||
            valueType is NullType && IsNullAssignable(targetType) ||
            IsImplicitNumericConversion(valueType, targetType))
        {
            return true;
        }

        var unwrappedTarget = UnwrapConst(targetType);
        var unwrappedValue = UnwrapConst(valueType);
        if (unwrappedTarget is NamedType { ClassType: ClassType.Interface } &&
            unwrappedValue is ArrayType arrayValue)
        {
            CloseConstructedTypes(arrayValue);
        }
        if (unwrappedTarget is ObjectType &&
            ArrayType.TryGetElementType(unwrappedValue, out _))
        {
            return true;
        }
        if (unwrappedTarget is ObjectType && IsBoxableValueType(unwrappedValue))
        {
            return true;
        }
        if (unwrappedTarget is ObjectType &&
            unwrappedValue is GenericType genericReference &&
            _activeFunction is { } activeFunction &&
            IsReferenceConstrainedGenericType(genericReference, activeFunction))
        {
            return true;
        }
        var targetDelegateType = unwrappedTarget as NamedType;
        var valueDelegateType = unwrappedValue as NamedType;
        if (targetDelegateType?.IsDelegate == true && valueDelegateType?.IsDelegate == true)
        {
            return IsType(targetDelegateType, valueDelegateType);
        }
        if ((targetDelegateType?.IsDelegate == true && unwrappedValue is FunctionType) ||
            (valueDelegateType?.IsDelegate == true && unwrappedTarget is FunctionType))
        {
            return GetFunctionSignature(targetType) is { } targetSignature &&
                GetFunctionSignature(valueType) is { } valueSignature &&
                IsType(targetSignature, valueSignature);
        }
        if (unwrappedTarget is NullableType nullableTarget)
        {
            return unwrappedValue is NullableType nullableValue
                ? CanAssign(nullableTarget.UnderlyingType, nullableValue.UnderlyingType)
                : CanAssign(nullableTarget.UnderlyingType, unwrappedValue);
        }
        var valueClass = GetClassDeclarationIncludingConstructed(unwrappedValue);
        var targetClass = GetClassDeclarationIncludingConstructed(unwrappedTarget);
        if (valueClass?.ClassType is not
            (ClassType.Class or ClassType.Interface or ClassType.Struct))
        {
            return false;
        }
        if (unwrappedTarget is ObjectType)
        {
            return valueClass.ClassType == ClassType.Class;
        }

        return targetClass?.ClassType switch
        {
            ClassType.Class when valueClass.ClassType == ClassType.Class &&
                unwrappedTarget is NamedType targetNamed &&
                unwrappedValue is NamedType valueNamed =>
                    IsAssignableToClass(targetNamed, valueNamed),
            ClassType.Interface when valueClass.ClassType is
                ClassType.Class or ClassType.Struct or ClassType.Interface &&
                unwrappedValue is NamedType namedValue =>
                    FindConstructedInterfaceType(namedValue, targetClass, []) is
                        { } constructedInterface &&
                    IsType(unwrappedTarget, constructedInterface),
            _ => false,
        };
    }

    private static bool IsBoxableValueType(TypeBase type) =>
        type is ValueTypeBase and not PtrType ||
        type is NamedType { ClassType: ClassType.Struct or ClassType.Enum };

    private bool IsAssignableToClass(NamedType targetType, NamedType valueType)
    {
        var currentType = valueType;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (true)
        {
            if (IsType(targetType, currentType))
            {
                return true;
            }

            var currentKey = currentType.ConstructedIdentity?.CanonicalName ??
                $"{currentType.ResolvedTypeFullName}<" +
                $"{string.Join(",", currentType.TypeArguments.Select(argument => GetTypeName(argument)))}" +
                ">";
            if (!visited.Add(currentKey))
            {
                return false;
            }

            var declaration = _types.SingleOrDefault(candidate =>
                MatchesConstructedDeclaration(candidate, currentType))?.Declaration;
            if (declaration?.BaseClassType is not { } baseType)
            {
                return false;
            }

            if (declaration.GenericTypeNames.Length > 0 &&
                declaration.GenericTypeNames.Length == currentType.TypeArguments.Count)
            {
                var arguments = declaration.GenericTypeNames.Zip(currentType.TypeArguments)
                    .ToDictionary(pair => pair.First, pair => pair.Second);
                baseType = GenericTypeSubstitution.Substitute(baseType, arguments);
            }
            baseType = CloseConstructedTypes(baseType);
            if (UnwrapConst(baseType) is not NamedType nextType)
            {
                return false;
            }
            currentType = nextType;
        }
    }

    private static bool IsImplicitNumericConversion(TypeBase sourceType, TypeBase targetType)
    {
        sourceType = UnwrapConst(sourceType);
        targetType = UnwrapConst(targetType);

        return sourceType switch
        {
            SByteType => targetType is ShortType or IntType or LongType or FloatType or DoubleType,
            ByteType => targetType is ShortType or UShortType or IntType or UIntType or LongType or ULongType or FloatType or DoubleType,
            ShortType => targetType is IntType or LongType or FloatType or DoubleType,
            UShortType => targetType is IntType or UIntType or LongType or ULongType or FloatType or DoubleType,
            IntType => targetType is LongType or FloatType or DoubleType,
            UIntType => targetType is LongType or ULongType or FloatType or DoubleType,
            LongType => targetType is FloatType or DoubleType,
            ULongType => targetType is FloatType or DoubleType,
            CharType => targetType is UShortType or IntType or UIntType or LongType or ULongType or FloatType or DoubleType,
            FloatType => targetType is DoubleType,
            _ => false,
        };
    }

    private static bool IsNullAssignable(TypeBase type)
    {
        type = UnwrapConst(type);
        return type is NullType or ReferenceTypeBase or ArrayType or NullableType or PtrType or FunctionType ||
            type is NamedType { ClassType: ClassType.Class or ClassType.Interface } ||
            type is NamedType { IsDelegate: true };
    }

    private void ApplyContextualType(ExpressionBase expression, TypeBase type)
    {
        if (expression.InferredType is { } sourceType &&
            GetClassDeclaration(sourceType) is { ClassType: ClassType.Interface } sourceInterface &&
            GetClassDeclaration(type) is { ClassType: ClassType.Interface } targetInterface &&
            !ReferenceEquals(sourceInterface, targetInterface))
        {
            var upcastIndex = sourceInterface.InterfaceUpcastTargets
                .Select((candidate, index) => (candidate, index))
                .Single(pair => ReferenceEquals(pair.candidate, targetInterface))
                .index;
            expression.BindInterfaceUpcast(
                sourceInterface.InterfaceDispatchSlots.Count + 1 + upcastIndex);
        }

        switch (expression)
        {
            case LiteralExpression { SourceText: "null" }:
                expression.SetInferredType(type);
                break;
            case ConditionalExpression conditional:
                ApplyContextualType(conditional.WhenTrue, type);
                ApplyContextualType(conditional.WhenFalse, type);
                break;
            case SwitchExpression switchExpression:
                foreach (var arm in switchExpression.Arms)
                {
                    ApplyContextualType(arm.Value, type);
                }
                break;
        }
    }

    private static bool IsType(TypeBase left, TypeBase right)
    {
        left = UnwrapConst(left);
        right = UnwrapConst(right);

        if (ArrayType.TryGetElementType(left, out var leftArrayElement) &&
            ArrayType.TryGetElementType(right, out var rightArrayElement))
        {
            return IsType(leftArrayElement, rightArrayElement);
        }

        if (left is FunctionType leftFunction && right is FunctionType rightFunction)
        {
            return IsType(leftFunction.ReturnType, rightFunction.ReturnType) &&
                leftFunction.ParameterTypes.Count == rightFunction.ParameterTypes.Count &&
                leftFunction.ParameterTypes.Zip(rightFunction.ParameterTypes)
                    .All(pair => IsType(pair.First, pair.Second));
        }

        if (left is NamedType leftNamed && right is NamedType rightNamed)
        {
            return GetTypeName(leftNamed) == GetTypeName(rightNamed) &&
                leftNamed.TypeArguments.Count == rightNamed.TypeArguments.Count &&
                leftNamed.TypeArguments.Zip(rightNamed.TypeArguments)
                    .All(pair => IsType(pair.First, pair.Second));
        }

        return GetTypeName(left) == GetTypeName(right);
    }

    private static QualifiedIdentifier GetTypeName(TypeBase type)
    {
        type = UnwrapConst(type);
        return type is NamedType namedType && namedType.ResolvedTypeFullName.ToString() != "void"
            ? namedType.ResolvedTypeFullName
            : type.FullName;
    }

    private static string GetTypeDisplayName(TypeBase type)
    {
        type = UnwrapConst(type);
        if (type is NamedType { TypeArguments.Count: > 0 } namedType)
        {
            return $"{GetTypeName(namedType)}<" +
                string.Join(", ", namedType.TypeArguments.Select(GetTypeDisplayName)) + ">";
        }
        return GetTypeName(type).ToString();
    }

    private static TypeBase UnwrapConst(TypeBase type)
    {
        return type is ConstType constType ? constType.UnderlyingType : type;
    }

    private static FunctionType? GetFunctionSignature(TypeBase type)
    {
        type = UnwrapConst(type);
        return type switch
        {
            FunctionType functionType => functionType,
            NamedType { IsDelegate: true, DelegateSignature: { } signature } => signature,
            _ => null,
        };
    }

    private static bool AreCompatibleDelegateValues(TypeBase left, TypeBase right)
    {
        var leftType = UnwrapConst(left);
        var rightType = UnwrapConst(right);
        var leftSignature = GetFunctionSignature(leftType);
        var rightSignature = GetFunctionSignature(rightType);
        if (leftType is NullType)
        {
            return rightSignature is not null;
        }
        if (rightType is NullType)
        {
            return leftSignature is not null;
        }
        if (leftType is NamedType { IsDelegate: true } leftDelegate &&
            rightType is NamedType { IsDelegate: true } rightDelegate &&
            !IsType(leftDelegate, rightDelegate))
        {
            return false;
        }
        return leftSignature is not null && rightSignature is not null &&
            IsType(leftSignature, rightSignature);
    }
}

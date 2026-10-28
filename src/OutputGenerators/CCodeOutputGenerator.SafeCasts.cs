using CxCompiler.Model.Expressions;
using CxCompiler.Model.Types;
using CxCompiler.Model.Types.BuiltInTypes;

namespace CxCompiler.OutputGenerators;

public static partial class CCodeOutputGenerator
{
    private static string ToCSafeCastExpression(TypeBase target, TypeBase source, string value)
    {
        if (target is GenericType || source is GenericType) return "CX_NULL";
        if (source is NullableType && target is NullableType) return value;
        if (source is NullType) return ToCDefaultValue(target);
        if (source is NullableType sourceNullable && IsBoxableValueType(UnwrapConst(sourceNullable.UnderlyingType)))
        {
            var underlying = UnwrapConst(sourceNullable.UnderlyingType);
            var boxed = $"cx_box_nullable_value(({value})._obj, sizeof({underlying.ToCIdentifier(false)}), {ToTypeInfoPointer(underlying)})";
            return ToCSafeCastExpression(target, BuiltInSystemTypes.Object, boxed);
        }
        if (IsBoxableValueType(source) && target is not NullableType)
        {
            var boxed = $"cx_box_value(&(({source.ToCIdentifier(false)}[]){{ {value} }})[0], " +
                $"sizeof({source.ToCIdentifier(false)}), {ToTypeInfoPointer(source)})";
            return ToCSafeCastExpression(target, BuiltInSystemTypes.Object, boxed);
        }
        if (ArrayType.TryGetElementType(target, out var elementType))
        {
            if (ArrayType.TryGetElementType(source, out var sourceElement) &&
                ArrayTypeKey(sourceElement) == ArrayTypeKey(elementType)) return value;
            return GetReferenceClassType(source) == ClassType.Interface
                ? $"({target.ToCIdentifier(false)})cx_safe_cast_interface_to_array({value}, {ArrayTypeKey(elementType)})"
                : $"({target.ToCIdentifier(false)})cx_safe_cast_array((cx_ptr)({value}), {ArrayTypeKey(elementType)})";
        }
        if (target is NullableType nullable)
        {
            var underlying = UnwrapConst(nullable.UnderlyingType);
            if (IsBoxableValueType(underlying))
            {
                if (IsBoxableValueType(source))
                    return $"({target.ToCIdentifier(false)}){{ cxcore_System_Nullable_CreateValueStorage(" +
                        $"(cx_ptr)&(({underlying.ToCIdentifier(false)}[]){{ {value} }})[0], sizeof({underlying.ToCIdentifier(false)})) }}";
                var boxed = GetReferenceClassType(source) == ClassType.Interface
                    ? $"({value}).owner" : $"(cx_ptr)({value})";
                return $"({target.ToCIdentifier(false)}){{ cx_safe_unbox_nullable({boxed}, {ToTypeInfoPointer(underlying)}) }}";
            }
            var converted = ToCSafeCastExpression(underlying, source, value);
            return IsCReferenceType(underlying)
                ? $"({target.ToCIdentifier(false)}){{ (cx_ptr)({converted}) }}"
                : $"({target.ToCIdentifier(false)}){{ cxcore_System_Nullable_CreateValueStorage(" +
                    $"(cx_ptr)&(({underlying.ToCIdentifier(false)}[]){{ {converted} }})[0], " +
                    $"sizeof({underlying.ToCIdentifier(false)})) }}";
        }
        var info = ToTypeInfoPointer(target);
        return (GetReferenceClassType(source), GetReferenceClassType(target)) switch
        {
            (ClassType.Class, ClassType.Class) =>
                $"({target.ToCIdentifier(false)})cx_safe_cast_object((cx_ptr)({value}), {info})",
            (ClassType.Interface, ClassType.Class) =>
                $"({target.ToCIdentifier(false)})cx_safe_cast_interface({value}, {info})",
            (ClassType.Class, ClassType.Interface) =>
                $"cx_safe_cast_object_to_interface((cx_ptr)({value}), {info})",
            _ => $"cx_safe_cast_interface_to_interface({value}, {info})",
        };
    }

    private static string ArrayTypeKey(TypeBase element) =>
        $"\"{GenericTypeIdentity.Create("cxcore", "System.Collections.Array", [element]).CIdentifier}\"";

    private static string ToCTypedArrayCreation(TypeBase element, string length)
    {
        var elementCType = element is GenericType ? "cx_ptr" : element.ToCIdentifier(false);
        var allocation = $"cx_array_new((cx_uint)({length}), (cx_uint)sizeof({elementCType}))";
        // Erased generic bodies have no closed element identity to attach.
        return ContainsOpenGenericParameter(element)
            ? allocation
            : $"cx_array_with_type({allocation}, {ArrayTypeKey(element)})";
    }
}

using CxCompiler.Model.Types;

namespace CxCompiler.OutputGenerators;

public static partial class CCodeOutputGenerator
{
    private static string ToCTypeOf(TypeBase type)
    {
        type = UnwrapConst(type);
        if (ArrayType.TryGetElementType(type, out var element))
            return $"cx_typeof_array({ToCTypeOf(element)})";
        if (type is NullableType nullable)
            return $"cx_typeof_nullable({ToCTypeOf(nullable.UnderlyingType)})";
        // Open templates are emitted as placeholders; executable bodies close
        // generic parameters before producing their type metadata expressions.
        if (type is GenericType)
            return "(struct cxcore_System_Reflection_TypeInfo){ 0 }";
        return $"(*{ToTypeInfoPointer(type)})";
    }

    private static bool IsRuntimeTypeInfo(TypeBase type) => UnwrapConst(type) is NamedType named &&
        named.ResolvedTypeFullName.ToString() == "cxcore.System.Reflection.TypeInfo";
}

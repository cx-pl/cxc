using System.Globalization;
using CxCompiler.Model.Expressions;
using CxCompiler.Model.Types;
using CxCompiler.Model.Types.BuiltInTypes;

namespace CxCompiler.OutputGenerators;

public static partial class CCodeOutputGenerator
{
    private static bool HasStaticLiteralInitializer(FieldDeclaration field) =>
        field.Initializer is LiteralExpression literal &&
        (UnwrapConst(field.Type) is not DecimalType || IsDecimalLiteral(literal));

    private static bool IsDecimalLiteral(LiteralExpression literal) =>
        literal.SourceText.EndsWith('m') || literal.SourceText.EndsWith('M');

    private static string ToCDecimalLiteral(LiteralExpression literal)
    {
        var value = decimal.Parse(literal.SourceText[..^1], NumberStyles.Float, CultureInfo.InvariantCulture);
        var bits = decimal.GetBits(value);
        return $"(cx_decimal){{ {(uint)bits[0]}u, {(uint)bits[1]}u, {(uint)bits[2]}u, {(uint)bits[3]}u }}";
    }

    private static string ToCDecimalConversion(string value, TypeBase source, TypeBase target)
    {
        if (source is DecimalType && target is DecimalType) return value;
        if (target is DecimalType)
        {
            if (source is FloatTypeBase)
                return $"cx_decimal_from_double({value}, {(source is FloatType ? 7 : 15)})";
            return $"cx_decimal_from_{(source is IntTypeBase { Signed: false } ? "u64" : "i64")}({value})";
        }
        if (target is FloatTypeBase)
            return $"(({target.ToCIdentifier(false)})cx_decimal_to_double({value}))";
        var integer = target as IntTypeBase;
        var size = integer?.Size ?? 4;
        var signed = integer?.Signed ?? true;
        var max = size == 8 ? signed ? "9223372036854775807ULL" : "18446744073709551615ULL"
            : ((1UL << (size * 8 - (signed ? 1 : 0))) - 1).ToString(CultureInfo.InvariantCulture) + "ULL";
        return $"(({target.ToCIdentifier(false)})cx_decimal_to_{(signed ? "i64" : "u64")}({value}, {max}))";
    }

    private static string DecimalOperation(string op) => op switch
    {
        "+" => "add", "-" => "subtract", "*" => "multiply", "/" => "divide", "%" => "remainder",
        _ => throw new InvalidOperationException($"Unknown decimal operator '{op}'."),
    };

    private static string ToCDecimalBinary(string op, string left, string right) =>
        op is "==" or "!=" or "<" or "<=" or ">" or ">="
            ? $"(cx_decimal_compare({left}, {right}) {op} 0)"
            : $"cx_decimal_{DecimalOperation(op)}({left}, {right})";

    private static string ToCDecimalUnary(UnaryExpression expression, FunctionDeclaration function, string module)
    {
        var value = ToCExpression(expression.Operand, function, module);
        return expression.Operator switch
        {
            "+" => value,
            "-" => $"cx_decimal_negate({value})",
            "++" or "--" => $"cx_decimal_increment(&({value}), {(expression.Operator == "++" ? 1 : -1)}, " +
                $"{(expression.Postfix ? "CX_TRUE" : "CX_FALSE")})",
            _ => throw new InvalidOperationException("Unsupported decimal unary operator."),
        };
    }
}

namespace CxCompiler.Model.Types;

public static class OperatorNames
{
    private static readonly IReadOnlyDictionary<string, string> Encodings =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["+"] = "plus",
            ["-"] = "minus",
            ["*"] = "multiply",
            ["/"] = "divide",
            ["%"] = "remainder",
            ["!"] = "logical_not",
            ["~"] = "bitwise_not",
            ["&"] = "bitwise_and",
            ["|"] = "bitwise_or",
            ["^"] = "bitwise_xor",
            ["<<"] = "left_shift",
            [">>"] = "right_shift",
            ["=="] = "equal",
            ["!="] = "not_equal",
            ["<"] = "less",
            ["<="] = "less_equal",
            [">"] = "greater",
            [">="] = "greater_equal",
            ["++"] = "increment",
            ["--"] = "decrement",
            ["+="] = "plus_assign",
            ["-="] = "minus_assign",
            ["*="] = "multiply_assign",
            ["/="] = "divide_assign",
            ["%="] = "remainder_assign",
            ["&="] = "bitwise_and_assign",
            ["|="] = "bitwise_or_assign",
            ["^="] = "bitwise_xor_assign",
            ["<<="] = "left_shift_assign",
            [">>="] = "right_shift_assign",
        };

    private static readonly IReadOnlyDictionary<string, string> TokensByName =
        Encodings.ToDictionary(pair => $"__cx_operator_{pair.Value}", pair => pair.Key,
            StringComparer.Ordinal);

    public static bool IsSupported(string token) => Encodings.ContainsKey(token);

    public static string GetDeclarationName(string token) =>
        Encodings.TryGetValue(token, out var name)
            ? $"__cx_operator_{name}"
            : throw new ArgumentOutOfRangeException(nameof(token), token,
                "Unsupported operator token.");

    public static string? GetToken(string declarationName) =>
        TokensByName.GetValueOrDefault(declarationName);
}

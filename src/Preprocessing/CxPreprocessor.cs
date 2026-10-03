using System.Text;
using System.Text.RegularExpressions;

namespace CxCompiler.Preprocessing;

internal sealed record PreprocessorDiagnostic(string FilePath, int Line, int Column, string Severity, string Message)
{
    public override string ToString() => $"{FilePath}({Line},{Column}): {Severity}: {Message}";
}

internal sealed record PreprocessorResult(string Source, IReadOnlyList<PreprocessorDiagnostic> Diagnostics);

internal static class CxPreprocessor
{
    private static readonly Regex DirectivePattern = new("^[ \\t]*#([A-Za-z]+)(.*)$", RegexOptions.Compiled);
    private static readonly Regex SymbolPattern = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);

    public static PreprocessorResult Process(string source, string filePath, IEnumerable<string> predefinedSymbols)
    {
        var symbols = new HashSet<string>(predefinedSymbols, StringComparer.Ordinal);
        var diagnostics = new List<PreprocessorDiagnostic>();
        var output = new StringBuilder(source.Length);
        var conditions = new Stack<ConditionalFrame>();
        var active = true;
        var inBlockComment = false;
        var lineNumber = 1;

        for (var position = 0; position < source.Length;)
        {
            var lineEnd = source.IndexOf('\n', position);
            var hasNewline = lineEnd >= 0;
            if (!hasNewline) lineEnd = source.Length;
            var contentEnd = hasNewline && lineEnd > position && source[lineEnd - 1] == '\r'
                ? lineEnd - 1
                : lineEnd;
            var line = source[position..contentEnd];
            var ending = source[contentEnd..(hasNewline ? lineEnd + 1 : lineEnd)];
            var match = inBlockComment ? Match.Empty : DirectivePattern.Match(line);

            if (!match.Success)
            {
                output.Append(active ? line : Blank(line)).Append(ending);
                if (active) UpdateBlockCommentState(line, ref inBlockComment);
            }
            else
            {
                var directive = match.Groups[1].Value;
                var argument = match.Groups[2].Value.Trim();
                var column = line.IndexOf('#') + 1;
                output.Append(Blank(line)).Append(ending);

                switch (directive)
                {
                    case "if":
                    {
                        var parentActive = active;
                        var branchMatches = TryEvaluate(argument, symbols, filePath, lineNumber, column, diagnostics, out var value);
                        var branchActive = parentActive && branchMatches && value;
                        conditions.Push(new ConditionalFrame(parentActive, branchActive, branchActive, false, lineNumber));
                        active = branchActive;
                        break;
                    }
                    case "elif":
                    {
                        if (!TryGetFrame(conditions, filePath, lineNumber, column, diagnostics, out var frame)) break;
                        if (frame.SawElse)
                        {
                            AddError(diagnostics, filePath, lineNumber, column, "#elif cannot follow #else.");
                            active = false;
                            break;
                        }
                        var canMatch = frame.ParentActive && !frame.AnyBranchTaken;
                        var valid = TryEvaluate(argument, symbols, filePath, lineNumber, column, diagnostics, out var value);
                        frame.CurrentBranchActive = canMatch && valid && value;
                        frame.AnyBranchTaken |= frame.CurrentBranchActive;
                        active = frame.CurrentBranchActive;
                        break;
                    }
                    case "else":
                    {
                        if (argument.Length != 0)
                        {
                            AddError(diagnostics, filePath, lineNumber, column, "#else does not accept an expression.");
                        }
                        if (!TryGetFrame(conditions, filePath, lineNumber, column, diagnostics, out var frame)) break;
                        if (frame.SawElse)
                        {
                            AddError(diagnostics, filePath, lineNumber, column, "Only one #else is allowed in a conditional block.");
                            active = false;
                            break;
                        }
                        frame.SawElse = true;
                        frame.CurrentBranchActive = frame.ParentActive && !frame.AnyBranchTaken;
                        frame.AnyBranchTaken |= frame.CurrentBranchActive;
                        active = frame.CurrentBranchActive;
                        break;
                    }
                    case "endif":
                        if (argument.Length != 0)
                            AddError(diagnostics, filePath, lineNumber, column, "#endif does not accept an expression.");
                        if (conditions.Count == 0)
                            AddError(diagnostics, filePath, lineNumber, column, "#endif has no matching #if.");
                        else
                        {
                            var frame = conditions.Pop();
                            active = frame.ParentActive;
                        }
                        break;
                    case "define":
                    case "undef":
                        if (active)
                        {
                            if (!SymbolPattern.IsMatch(argument))
                                AddError(diagnostics, filePath, lineNumber, column, $"#{directive} requires one symbol name.");
                            else if (directive == "define") symbols.Add(argument);
                            else symbols.Remove(argument);
                        }
                        break;
                    case "error":
                        if (active) AddError(diagnostics, filePath, lineNumber, column,
                            argument.Length == 0 ? "#error" : argument);
                        break;
                    case "warning":
                        if (active) diagnostics.Add(new(filePath, lineNumber, column, "warning",
                            argument.Length == 0 ? "#warning" : argument));
                        break;
                    default:
                        AddError(diagnostics, filePath, lineNumber, column, $"Unknown preprocessor directive '#{directive}'.");
                        break;
                }
            }

            position = hasNewline ? lineEnd + 1 : source.Length;
            lineNumber++;
        }

        while (conditions.Count > 0)
        {
            var frame = conditions.Pop();
            AddError(diagnostics, filePath, frame.StartLine, 1, "Conditional block is missing #endif.");
        }
        return new PreprocessorResult(output.ToString(), diagnostics);
    }

    private static bool TryEvaluate(string expression, HashSet<string> symbols, string filePath, int line,
        int column, List<PreprocessorDiagnostic> diagnostics, out bool value)
    {
        try
        {
            value = new ExpressionParser(expression, symbols).Parse();
            return true;
        }
        catch (FormatException exception)
        {
            AddError(diagnostics, filePath, line, column, exception.Message);
            value = false;
            return false;
        }
    }

    private static bool TryGetFrame(Stack<ConditionalFrame> conditions, string filePath, int line, int column,
        List<PreprocessorDiagnostic> diagnostics, out ConditionalFrame frame)
    {
        if (conditions.TryPeek(out frame!)) return true;
        AddError(diagnostics, filePath, line, column, "Directive has no matching #if.");
        return false;
    }

    private static void AddError(List<PreprocessorDiagnostic> diagnostics, string filePath, int line, int column,
        string message) => diagnostics.Add(new(filePath, line, column, "error", message));

    private static string Blank(string line) => string.Create(line.Length, line,
        static (span, original) =>
        {
            for (var i = 0; i < original.Length; i++) span[i] = original[i] == '\t' ? '\t' : ' ';
        });

    private static void UpdateBlockCommentState(string line, ref bool inBlockComment)
    {
        for (var index = 0; index < line.Length; index++)
        {
            if (inBlockComment)
            {
                if (line.AsSpan(index).StartsWith("*/", StringComparison.Ordinal))
                {
                    inBlockComment = false;
                    index++;
                }
                continue;
            }

            if (line.AsSpan(index).StartsWith("//", StringComparison.Ordinal)) return;
            if (line.AsSpan(index).StartsWith("/*", StringComparison.Ordinal))
            {
                inBlockComment = true;
                index++;
                continue;
            }

            if (line[index] is '"' or '\'')
            {
                var quote = line[index++];
                while (index < line.Length)
                {
                    if (line[index] == '\\') index++;
                    else if (line[index] == quote) break;
                    index++;
                }
            }
        }
    }

    private sealed class ConditionalFrame(bool parentActive, bool branchActive, bool anyBranchTaken, bool sawElse, int startLine)
    {
        public bool ParentActive { get; } = parentActive;
        public bool CurrentBranchActive { get; set; } = branchActive;
        public bool AnyBranchTaken { get; set; } = anyBranchTaken;
        public bool SawElse { get; set; } = sawElse;
        public int StartLine { get; } = startLine;
    }

    private sealed class ExpressionParser(string expression, HashSet<string> symbols)
    {
        private int _position;

        public bool Parse()
        {
            var value = ParseOr();
            SkipWhitespace();
            if (_position != expression.Length) throw Error("Unexpected token in conditional expression.");
            return value;
        }

        private bool ParseOr()
        {
            var value = ParseAnd();
            while (Take("||"))
            {
                var right = ParseAnd();
                value |= right;
            }
            return value;
        }

        private bool ParseAnd()
        {
            var value = ParseEquality();
            while (Take("&&"))
            {
                var right = ParseEquality();
                value &= right;
            }
            return value;
        }

        private bool ParseEquality()
        {
            var value = ParseUnary();
            while (true)
            {
                if (Take("==")) value = value == ParseUnary();
                else if (Take("!=")) value = value != ParseUnary();
                else return value;
            }
        }

        private bool ParseUnary()
        {
            if (Take("!")) return !ParseUnary();
            if (Take("("))
            {
                var value = ParseOr();
                if (!Take(")")) throw Error("Expected ')' in conditional expression.");
                return value;
            }

            SkipWhitespace();
            var start = _position;
            while (_position < expression.Length && (char.IsLetterOrDigit(expression[_position]) || expression[_position] == '_'))
                _position++;
            if (start == _position) throw Error("Expected a symbol or boolean literal in conditional expression.");
            var identifier = expression[start.._position];
            return identifier switch
            {
                "true" => true,
                "false" => false,
                _ when SymbolPattern.IsMatch(identifier) => symbols.Contains(identifier),
                _ => throw Error("Invalid symbol in conditional expression.")
            };
        }

        private bool Take(string token)
        {
            SkipWhitespace();
            if (!expression.AsSpan(_position).StartsWith(token, StringComparison.Ordinal)) return false;
            _position += token.Length;
            return true;
        }

        private void SkipWhitespace()
        {
            while (_position < expression.Length && char.IsWhiteSpace(expression[_position])) _position++;
        }

        private FormatException Error(string message) => new($"{message} (column {_position + 1}).");
    }
}

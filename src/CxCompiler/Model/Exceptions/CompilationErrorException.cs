using CxCompiler.Model.Common;

namespace CxCompiler.Model.Errors;

public class CompilationErrorException : Exception
{
    public CompilationErrorException(string message)
        : base(message)
    {
        DiagnosticText = message;
    }

    public CompilationErrorException(string message, Exception inner)
        : base(message, inner)
    {
        DiagnosticText = message;
    }

    public CompilationErrorException(string message, SourceSpan? sourceSpan)
        : this(message, sourceSpan, null)
    {
    }

    private CompilationErrorException(string message, SourceSpan? sourceSpan, Exception? inner)
        : base(Format(message, sourceSpan), inner)
    {
        DiagnosticText = message;
        SourceSpan = sourceSpan;
    }

    public string DiagnosticText { get; }

    public SourceSpan? SourceSpan { get; }

    public CompilationErrorException WithSourceSpan(SourceSpan? sourceSpan)
    {
        if (SourceSpan is not null || sourceSpan is null)
        {
            return this;
        }

        return new CompilationErrorException(DiagnosticText, sourceSpan, this);
    }

    private static string Format(string message, SourceSpan? sourceSpan) =>
        sourceSpan is { } span
            ? $"{span.FilePath}({span.StartLine},{span.StartColumn}): error: {message}"
            : message;
}

namespace CxCompiler.Model.Errors;

public class CompilationErrorException : Exception
{
    public CompilationErrorException(string message)
    : base(message)
    {
    }

    public CompilationErrorException(string message, Exception inner)
    : base(message, inner)
    {
    }
}

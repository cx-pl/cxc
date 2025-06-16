namespace CxCompiler.Model;

public class InternalCompilerException : Exception
{
    public InternalCompilerException(string message)
    : base(message)
    {
    }

    public InternalCompilerException(string message, Exception inner)
    : base(message, inner)
    {
    }
}

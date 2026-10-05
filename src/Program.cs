using CxCompiler;
using CxCompiler.Model.Errors;
using System.Text.Json;

var jsonDiagnostics = args.Any(argument => argument is "--diagnostics-format=json");
for (var index = 0; index < args.Length - 1; index++)
    jsonDiagnostics |= args[index] == "--diagnostics-format" && args[index + 1] == "json";

if (args.Length == 1 && args[0] is "--help" or "-h" or "help")
{
    Console.WriteLine(Compiler.Usage);
    return 0;
}

if (args.Length == 1 && args[0] is "--version" or "-V")
{
    var version = typeof(Compiler).Assembly.GetName().Version?.ToString(3) ?? "unknown";
    Console.WriteLine($"cxc {version}");
    return 0;
}

try
{
    new Compiler().Compile(args);
    return 0;
}
catch (CommandLineException exception)
{
    Console.Error.WriteLine(exception.Message);
    Console.Error.WriteLine($"Run 'cxc --help' for usage.");
    return 2;
}
catch (CompilationErrorException exception)
{
    if (jsonDiagnostics)
    {
        foreach (var message in exception.Message.Split(Environment.NewLine,
            StringSplitOptions.RemoveEmptyEntries))
            Console.Error.WriteLine(JsonSerializer.Serialize(new { severity = "error", message }));
    }
    else
    {
        Console.Error.WriteLine(exception.Message);
    }
    return 1;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"Compiler failure: {exception.Message}");
    return 1;
}

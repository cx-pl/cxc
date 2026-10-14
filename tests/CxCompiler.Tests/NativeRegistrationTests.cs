using CxCompiler.Semantics;

namespace CxCompiler.Tests;

public sealed class NativeRegistrationTests
{
    [Fact]
    public void RetainedCallbacksCannotRootADeadStackBorrow()
    {
        var project = NativeInteropTests.Project("""
            import System.Interop;
            @NativeFunction(contextParameter = 0) public delegate int Callback(ptr context);
            public unsafe NativeCallbackRegistration Bad() {
                int value = 42; ptr<int> borrow = &value;
                delegate() => int callback = () => *borrow;
                return NativeCallback.Register<Callback>(callback);
            }
            """);
        Assert.Contains("borrowed stack storage", Assert.Throws<CxCompiler.Model.Errors.CompilationErrorException>(() => new SemanticBinder().Bind(project)).Message);
    }
    [Fact]
    public void CapturedCallbacksUseExplicitRootedContextsAndSeparateNativeEntryPoints()
    {
        var project = NativeInteropTests.Project("""
            import System.Interop;
            @NativeFunction(contextParameter = 1, failureResult = "-1")
            public delegate int Callback(int number, ptr context);
            public unsafe void Run() {
                int captured = 42;
                NativeCallbackRegistration registration = NativeCallback.Register<Callback>((int number) => number + captured);
                Callback function = registration.Function<Callback>();
                ptr context = registration.Context;
                int value = function(0, context);
                registration.Unregister(() => {});
            }
            """);
        new SemanticBinder().Bind(project);
        var generated = NativeInteropTests.Generate(project).Source;
        Assert.Contains("cx_interop_registration_create", generated);
        Assert.Contains("cx_interop_registration_acquire", generated);
        Assert.Contains("cx_interop_registration_release(&registrationScope)", generated);
        Assert.Contains("cx_interop_registration_failed", generated);
    }
}

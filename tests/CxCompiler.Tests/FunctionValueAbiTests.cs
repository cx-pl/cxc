using System.Text.RegularExpressions;
using CxCompiler.Model.Project;
using CxCompiler.OutputGenerators;
using CxCompiler.Semantics;

namespace CxCompiler.Tests;

public sealed class FunctionValueAbiTests
{
    [Fact]
    public void ClosedGenericLambdaUsesClosedCapturedDelegateSignature()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_callback");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            namespace System.DependencyInjection;
            public delegate int IntFactory();
            public delegate object ObjectFactory();
            public ObjectFactory Wrap<T>(delegate() => T factory) {
                return () => (object)factory();
            }
            public void Run() {
                IntFactory input = () => 1;
                ObjectFactory factory = Wrap<int>(input);
                factory();
            }
            """));
        new SemanticBinder().Bind(project);
        var directory = Path.Combine(Path.GetTempPath(), $"cxc-function-abi-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(Path.Combine(directory, "generic_callback.c"));
            var closedInvoke = Regex.Match(source,
                @"CX_FN_(?<captured>[A-F0-9]+) __cx_function_value_[^;]+;\s*return[^;]*__cx_delegate_invoke_CX_FN_(?<invoked>[A-F0-9]+)\(__cx_function_value_");

            Assert.True(closedInvoke.Success, "Expected a captured generic delegate invocation.");
            Assert.Equal(closedInvoke.Groups["captured"].Value,
                closedInvoke.Groups["invoked"].Value);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ManagedVoidCallbackReusesRuntimeStructsWithoutDefinitionMarkers()
    {
        var project = CxProject.CreateDefaultApplicationProject("callbacks");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public delegate void Signal();
            public delegate void Receiver(int value);
            public delegate int Reader();
            public void Run(Signal signal, Receiver receiver, Reader reader) {
                signal();
                receiver(reader());
            }
            """));
        new SemanticBinder().Bind(project);
        var directory = Path.Combine(Path.GetTempPath(), $"cxc-function-abi-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var header = File.ReadAllText(Path.Combine(directory, "callbacks.internal.h"));
            var source = File.ReadAllText(Path.Combine(directory, "callbacks.c"));
            const string actionType = "CX_FN_136FFEE706DF35B2";

            Assert.Contains($"typedef {actionType} callbacks_Signal;", header);
            Assert.DoesNotContain($"CX_FUNCTION_TYPE_{actionType}", header);
            Assert.DoesNotContain($"typedef struct {actionType}", header);
            Assert.DoesNotContain($"CX_FUNCTION_NODE_{actionType}", source);
            Assert.DoesNotContain($"struct {actionType}_invocation {{", source);
            Assert.Contains($"__cx_delegate_invoke_{actionType}", source);
            // Other signatures still need definitions shared across generated headers.
            Assert.Equal(2, Regex.Matches(header, "#define CX_FUNCTION_TYPE_CX_FN_").Count);
            Assert.Equal(2, Regex.Matches(source, "#define CX_FUNCTION_NODE_CX_FN_").Count);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void InvocationListResultsUseCheckedReferenceStores()
    {
        var project = CxProject.CreateDefaultApplicationProject("callback_lists");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public delegate void Signal();
            public Signal Combine(Signal left, Signal right) { return left + right; }
            public Signal Remove(Signal source, Signal value) { return source - value; }
            """));
        new SemanticBinder().Bind(project);
        var directory = Path.Combine(Path.GetTempPath(), $"cxc-function-abi-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(Path.Combine(directory, "callback_lists.c"));

            Assert.Contains(
                "cx_gc_store_reference((cx_ptr*)&result.__invocation_list, (cx_ptr)head)",
                source);
            Assert.DoesNotContain("result.__invocation_list = head;", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}

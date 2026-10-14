using CxCompiler.Model.Errors;
using CxCompiler.Model.Expressions;
using CxCompiler.Model.Project;
using CxCompiler.Model.Statements;
using CxCompiler.Model.Types;
using CxCompiler.Model.Types.BuiltInTypes;
using CxCompiler.OutputGenerators;
using CxCompiler.Semantics;

namespace CxCompiler.Tests;

public sealed class GenericFunctionBodyTests
{
    private const string Source = """
        public T Identity<T>(T value) { return value; }
        public int RunInt() { return Identity(7); }
        public long RunLong() { return Identity(9L); }
        public int RunIntAgain() { return Identity(11); }
        """;

    [Fact]
    public void ForwardsGenericCallThroughMatchingLocal()
    {
        var project = CxProject.CreateDefaultApplicationProject(
            "generic_forward_call_local");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public T Transform<T>(T value) { return value; }
            public int Transform<int>(int value) { return value + 1; }
            public T Forward<T>(T value) {
                T result = Transform(value);
                return result;
            }
            public T ForwardInferred<T>(T value) {
                var result = Transform(value);
                return result;
            }
            public int Run() {
                return Forward(41) + (Forward(7L) == 7L ? 1 : 0) +
                    ForwardInferred(40) +
                    (ForwardInferred(8L) == 8L ? 1 : 0);
            }
            """));

        new SemanticBinder().Bind(project);

        var forward = project.CompilationContexts.Single()
            .DeclarationScope.Declarations.OfType<FunctionDeclaration>()
            .Single(function => function.Name == "Forward");
        var local = Assert.IsType<LocalVariableDeclarationStatement>(forward.Body![0]);
        var forwardedCall = Assert.IsType<InvocationExpression>(
            local.Declarators.Single().Initializer);
        var callers = project.GenericFunctionInstances
            .Where(instance => instance.Declaration?.Name == "Forward")
            .ToArray();
        Assert.Equal(2, callers.Length);
        Assert.Contains(callers, caller =>
            forwardedCall.GetClosedGenericTarget(caller.SpecializationName!)?
                .ImplementationDeclaration?.IsGenericSpecialization == true);
        Assert.Contains(callers, caller =>
            forwardedCall.GetClosedGenericTarget(caller.SpecializationName!)?
                .ImplementationDeclaration is null);
        var inferredForward = project.CompilationContexts.Single()
            .DeclarationScope.Declarations.OfType<FunctionDeclaration>()
            .Single(function => function.Name == "ForwardInferred");
        var inferredLocal = Assert.IsType<LocalVariableDeclarationStatement>(
            inferredForward.Body![0]);
        Assert.IsType<AutoType>(inferredLocal.DeclaredType);
        var inferredCall = Assert.IsType<InvocationExpression>(
            inferredLocal.Declarators.Single().Initializer);
        var inferredCallers = project.GenericFunctionInstances
            .Where(instance => instance.Declaration?.Name == "ForwardInferred")
            .ToArray();
        Assert.True(inferredCallers.Length == 2,
            string.Join("\n", project.GenericFunctionInstances.Select(instance =>
                $"{instance.Declaration?.Name}: {instance.SpecializationName}")));
        Assert.Contains(inferredCallers, caller =>
            inferredCall.GetClosedGenericTarget(caller.SpecializationName!)?
                .ImplementationDeclaration?.IsGenericSpecialization == true);
        Assert.Contains(inferredCallers, caller =>
            inferredCall.GetClosedGenericTarget(caller.SpecializationName!)?
                .ImplementationDeclaration is null);

        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-generic-forward-call-local-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project,
                Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(Path.Combine(directory,
                "generic_forward_call_local.c"));
            Assert.DoesNotContain("_unknowntype_", source);
            foreach (var caller in callers)
            {
                var target = forwardedCall.GetClosedGenericTarget(
                    caller.SpecializationName!)!;
                Assert.Contains($"{target.SpecializationName}(", source);
            }
            foreach (var caller in inferredCallers)
            {
                var target = inferredCall.GetClosedGenericTarget(
                    caller.SpecializationName!)!;
                Assert.Contains($"{target.SpecializationName}(", source);
            }
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void RejectsInferredForwardingLocalWithMismatchedGenericResult()
    {
        var project = CxProject.CreateDefaultApplicationProject(
            "generic_forward_inferred_local_mismatch");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public U Choose<T, U>(T value, U result) { return result; }
            public T Invalid<T>(T value) {
                var result = Choose(value, "not the caller type");
                return result;
            }
            """));

        var exception = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(project));

        Assert.Contains("returns 'System.String', but 'T' is required",
            exception.Message);
    }

    [Fact]
    public void ForwardsVoidGenericCallsForClosedCallerTypes()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_void_forward");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public interface IStore { void Touch(); }
            public class Store : IStore {
                public void Touch() {}
            }
            public class OtherStore : IStore {
                public void Touch() {}
            }
            public void Write<T>(T value) where T : IStore {
                value.Touch();
            }
            public void Write<Store>(Store value) {
                value.Touch();
            }
            public void Forward<T>(T value) where T : IStore {
                Write(value);
            }
            public void Run() {
                Store value = new Store();
                Forward(value);
                OtherStore other = new OtherStore();
                Forward(other);
            }
            """));

        new SemanticBinder().Bind(project);

        Assert.Equal(4, project.GenericFunctionInstances.Count);
        var forwardDeclaration = project.CompilationContexts.Single()
            .DeclarationScope.Declarations.OfType<FunctionDeclaration>()
            .Single(function => function.Name == "Forward");
        var forwardedCall = Assert.IsType<InvocationExpression>(
            Assert.IsType<ExpressionStatement>(forwardDeclaration.Body!.Single()).Expression);
        var forwards = project.GenericFunctionInstances
            .Where(instance => instance.Declaration?.Name == "Forward").ToArray();
        Assert.Equal(2, forwards.Length);
        var targets = forwards.Select(instance => forwardedCall.GetClosedGenericTarget(
            instance.SpecializationName!)).ToArray();
        Assert.Contains(targets, target =>
            target?.ImplementationDeclaration?.IsGenericSpecialization == true);
        Assert.Contains(targets, target =>
            target?.ImplementationDeclaration is null);
        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-generic-void-forward-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(Path.Combine(directory, "generic_void_forward.c"));
            foreach (var target in targets)
            {
                var call = $"{target!.SpecializationName}(value);";
                Assert.Contains(call, source);
                Assert.DoesNotContain($"return {call}", source);
            }
            Assert.DoesNotContain("_unknowntype_", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ForwardsVoidGenericCallsFromInstanceMethods()
    {
        var project = CxProject.CreateDefaultApplicationProject(
            "generic_void_method_forward");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public interface IStore { void Touch(); }
            public class Store : IStore {
                public void Touch() {}
            }
            public void Write<T>(T value) where T : IStore {
                value.Touch();
            }
            public class Forwarder {
                public void Forward<T>(T value) where T : IStore {
                    Write(value);
                }
            }
            public void Run() {
                Forwarder forwarder = new Forwarder();
                Store value = new Store();
                forwarder.Forward(value);
            }
            """));

        new SemanticBinder().Bind(project);

        Assert.Equal(2, project.GenericFunctionInstances.Count);
        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-generic-void-method-forward-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(Path.Combine(
                directory, "generic_void_method_forward.c"));
            Assert.DoesNotContain("_unknowntype_", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void BindsIdentityBodyForDistinctConcreteCalls()
    {
        var project = CreateProject();
        var functions = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<FunctionDeclaration>().ToArray();
        Assert.IsType<GenericType>(functions[0].ReturnType);
        Assert.IsType<GenericType>(Assert.Single(functions[0].Parameters).ParameterType);
        var intCall = Assert.IsType<InvocationExpression>(
            Assert.IsType<ReturnStatement>(functions[1].Body![0]).Expression);
        var longCall = Assert.IsType<InvocationExpression>(
            Assert.IsType<ReturnStatement>(functions[2].Body![0]).Expression);
        Assert.Same(BuiltInSystemTypes.Int, intCall.TargetSymbol!.ReturnType);
        Assert.Same(BuiltInSystemTypes.Long, longCall.TargetSymbol!.ReturnType);
        Assert.Equal(2, project.GenericFunctionInstances.Count);
    }

    [Fact]
    public void EmitsOneConcreteBodyPerInstantiation()
    {
        var project = CreateProject();
        var directory = Path.Combine(Path.GetTempPath(), $"cxc-generic-body-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var header = File.ReadAllText(Path.Combine(directory, "generic_body.h"));
            var source = File.ReadAllText(Path.Combine(directory, "generic_body.c"));
            foreach (var instance in project.GenericFunctionInstances)
            {
                Assert.Equal(1, header.Split(instance.SpecializationName!).Length - 1);
                Assert.Contains(instance.SpecializationName!, source);
            }
            Assert.Equal(2, source.Split("return value;").Length - 1);
            Assert.DoesNotContain("_unknowntype_", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void SupportsNullCoalescingBetweenMatchingReferenceConstrainedParameters()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_null_coalescing");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public class Box {}
            public T FirstNonNull<T>(T first, T second, T fallback) where T : class {
                return first ?? second ?? fallback;
            }
            public Box Select(Box first, Box second, Box fallback) {
                return FirstNonNull(first, second, fallback);
            }
            """));
        new SemanticBinder().Bind(project);

        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-generic-null-coalescing-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(Path.Combine(directory, "generic_null_coalescing.c"));
            var instance = Assert.Single(project.GenericFunctionInstances);
            var specializedStart = source.IndexOf(instance.SpecializationName!, StringComparison.Ordinal);

            Assert.Contains("?", source[specializedStart..]);
            Assert.DoesNotContain("_unknowntype_", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void SupportsNullCoalescingBetweenMatchingGenericArrayParameters()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_array_null_coalescing");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public T[] FirstNonNull<T>(T[] first, T[] fallback) {
                return first ?? fallback;
            }
            public int[] Select(int[] first, int[] fallback) {
                return FirstNonNull(first, fallback);
            }
            """));
        new SemanticBinder().Bind(project);

        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-generic-array-null-coalescing-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(Path.Combine(directory, "generic_array_null_coalescing.c"));
            var instance = Assert.Single(project.GenericFunctionInstances);
            var specializedStart = source.IndexOf(instance.SpecializationName!, StringComparison.Ordinal);

            Assert.Contains("?", source[specializedStart..]);
            Assert.DoesNotContain("_unknowntype_", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void SupportsDefaultValuesInGenericReturnBodies()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_default_value");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public struct Point { public int X; }
            public class Box { }

            public T DefaultValue<T>() { return default; }
            public T DefaultLocal<T>() {
                T value = default;
                return value;
            }
            public T[] DefaultArray<T>() { return default; }
            public T[] DefaultArrayLocal<T>() {
                T[] values = default;
                return values;
            }

            public int RunInt() { return DefaultValue<int>(); }
            public long RunLong() { return DefaultLocal<long>(); }
            public Box RunReference() { return DefaultValue<Box>(); }
            public Point RunStruct() { return DefaultLocal<Point>(); }
            public int RunArray() {
                int[] direct = DefaultArray<int>();
                int[] local = DefaultArrayLocal<int>();
                return direct == null && local == null ? 0 : 1;
            }
            """));
        new SemanticBinder().Bind(project);

        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-generic-default-value-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(Path.Combine(directory, "generic_default_value.c"));
            var defaultInt = Assert.Single(project.GenericFunctionInstances, instance =>
                instance.Declaration!.Name == "DefaultValue" &&
                instance.GenericTypeArguments["T"].FullName.ToString() == "System.Int");
            var defaultBox = Assert.Single(project.GenericFunctionInstances, instance =>
                instance.Declaration!.Name == "DefaultValue" &&
                instance.GenericTypeArguments["T"].FullName.ToString() == "Box");
            var localLong = Assert.Single(project.GenericFunctionInstances, instance =>
                instance.Declaration!.Name == "DefaultLocal" &&
                instance.GenericTypeArguments["T"].FullName.ToString() == "System.Long");
            var localPoint = Assert.Single(project.GenericFunctionInstances, instance =>
                instance.Declaration!.Name == "DefaultLocal" &&
                instance.GenericTypeArguments["T"].FullName.ToString() == "Point");
            var defaultArray = Assert.Single(project.GenericFunctionInstances, instance =>
                instance.Declaration!.Name == "DefaultArray");
            var defaultArrayLocal = Assert.Single(project.GenericFunctionInstances, instance =>
                instance.Declaration!.Name == "DefaultArrayLocal");

            string GetSpecializedBody(CxCompiler.Semantics.FunctionSymbol instance)
            {
                var start = source.LastIndexOf(instance.SpecializationName!, StringComparison.Ordinal);
                var end = source.IndexOf("\n}", start, StringComparison.Ordinal);
                return source[start..end];
            }

            Assert.Contains("return ((cx_int){ 0 });", GetSpecializedBody(defaultInt));
            Assert.Contains("return ((struct ", GetSpecializedBody(defaultBox));
            Assert.Contains("cx_long value = ((cx_long){ 0 });", GetSpecializedBody(localLong));
            Assert.Contains("value = ((struct ", GetSpecializedBody(localPoint));
            Assert.Contains("return ((struct ", GetSpecializedBody(defaultArray));
            Assert.Contains("values = ((struct ", GetSpecializedBody(defaultArrayLocal));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void SupportsReturningAnElementFromAMatchingGenericArrayParameter()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_array_first");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public T First<T>(T[] values) { return values[0]; }
            public int RunInt() {
                int[] values = new int[1];
                values[0] = 37;
                return First(values);
            }
            public long RunLong() {
                long[] values = new long[1];
                values[0] = 41L;
                return First(values);
            }
            """));
        new SemanticBinder().Bind(project);

        var instances = project.GenericFunctionInstances
            .OrderBy(instance => instance.ReturnType.FullName.ToString(), StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(2, instances.Length);
        Assert.Equal(["System.Int", "System.Long"],
            instances.Select(instance => instance.ReturnType.FullName.ToString()).ToArray());

        var directory = Path.Combine(Path.GetTempPath(), $"cxc-generic-array-first-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(Path.Combine(directory, "generic_array_first.c"));
            Assert.Contains("return (*", source);
            Assert.Contains("cx_array_at(values", source);
            Assert.Contains("sizeof(cx_int)", source);
            Assert.Contains("sizeof(cx_long)", source);
            Assert.DoesNotContain("_unknowntype_", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void SupportsReturningAnArrayElementThroughATypedGenericLocal()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_array_element_local");
        var context = CompilerTestHelper.Parse("""
            public T At<T>(T[] values, uint index) {
                T item = values[index];
                return item;
            }
            public int RunInt() {
                int[] values = new int[1];
                values[0] = 43;
                return At(values, 0u);
            }
            public long RunLong() {
                long[] values = new long[1];
                values[0] = 47L;
                return At(values, 0u);
            }
            """);
        project.AddCompilationContext(context);
        new SemanticBinder().Bind(project);

        Assert.Equal(2, project.GenericFunctionInstances.Count);
        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-generic-array-element-local-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(
                Path.Combine(directory, "generic_array_element_local.c"));
            Assert.Contains("cx_int item = (*", source);
            Assert.Contains("cx_long item = (*", source);
            Assert.Equal(2, source.Split("return item;", StringSplitOptions.None).Length - 1);
            Assert.DoesNotContain("_unknowntype_", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void SupportsCopyingAnArrayElementThroughSeveralTypedGenericLocals()
    {
        var project = CxProject.CreateDefaultApplicationProject(
            "generic_array_element_local_sequence");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public T AtThroughLocals<T>(T[] values, uint index) {
                T item = values[index];
                T copy = item;
                T result = copy;
                copy = result;
                return copy;
            }
            public int RunInt() {
                int[] values = new int[1];
                values[0] = 53;
                return AtThroughLocals(values, 0u);
            }
            public long RunLong() {
                long[] values = new long[1];
                values[0] = 59L;
                return AtThroughLocals(values, 0u);
            }
            """));

        new SemanticBinder().Bind(project);

        Assert.Equal(2, project.GenericFunctionInstances.Count);
        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-generic-array-element-local-sequence-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(
                Path.Combine(directory, "generic_array_element_local_sequence.c"));
            Assert.Contains("cx_int item = (*", source);
            Assert.Contains("cx_int copy = item;", source);
            Assert.Contains("cx_int result = copy;", source);
            Assert.Contains("copy = result;", source);
            Assert.Contains("cx_long item = (*", source);
            Assert.Contains("cx_long copy = item;", source);
            Assert.DoesNotContain("_unknowntype_", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void InfersVarTypesWhenReturningArrayElementsThroughGenericLocalCopies()
    {
        var project = CxProject.CreateDefaultApplicationProject(
            "generic_array_element_var_sequence");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public T AtInferred<T>(T[] values, uint index) {
                var item = values[index];
                var copy = item;
                var result = copy;
                return result;
            }
            public int RunInt() {
                int[] values = new int[1];
                values[0] = 71;
                return AtInferred(values, 0u);
            }
            public long RunLong() {
                long[] values = new long[1];
                values[0] = 73L;
                return AtInferred(values, 0u);
            }
            """));

        new SemanticBinder().Bind(project);

        Assert.Equal(2, project.GenericFunctionInstances.Count);
        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-generic-array-element-var-sequence-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(
                Path.Combine(directory, "generic_array_element_var_sequence.c"));
            Assert.Contains("cx_int item = (*", source);
            Assert.Contains("cx_int copy = item;", source);
            Assert.Contains("cx_int result = copy;", source);
            Assert.Contains("cx_long item = (*", source);
            Assert.Contains("cx_long copy = item;", source);
            Assert.DoesNotContain("_unknowntype_", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void SupportsJaggedArrayCreationAndNestedGenericElementReturns()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_jagged_array_first");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public T FirstNested<T>(T[][] values) { return values[0][0]; }
            public uint FirstRowLength<T>(T[][] values) { return values[0].Length; }
            public bool HasNonEmptyFirstRow<T>(T[][] values) {
                return values[0].Length > 0u;
            }
            public void SetNested<T>(T[][] values, int row, int column, T value) {
                values[row][column] = value;
            }
            public void CopyNested<T>(T[][] source, T[][] target, int row, int column) {
                target[row][column] = source[row][column];
            }
            public uint Run() {
                int[][] values = new int[1][];
                values[0] = new int[3];
                SetNested(values, 0, 0, 53);
                int[][] copy = new int[1][];
                copy[0] = new int[3];
                CopyNested(values, copy, 0, 0);
                if (FirstNested(copy) != 53) return 0u;
                if (!HasNonEmptyFirstRow(copy)) return 0u;
                return FirstRowLength(copy);
            }
            """));
        new SemanticBinder().Bind(project);

        Assert.Equal(5, project.GenericFunctionInstances.Count);
        Assert.Contains(project.GenericFunctionInstances,
            instance => ReferenceEquals(BuiltInSystemTypes.Int, instance.ReturnType));
        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-generic-jagged-array-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(Path.Combine(directory, "generic_jagged_array_first.c"));
            Assert.Contains("cx_array_new((cx_uint)(3)", source);
            Assert.Contains("cx_array_at(values", source);
            Assert.True(source.Split("cx_array_at(").Length - 1 >= 8);
            Assert.Contains("sizeof(cx_int)", source);
            Assert.DoesNotContain("_unknowntype_", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void SupportsReturningLengthOfAMatchingGenericArrayParameter()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_array_count");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public uint Count<T>(T[] values) { return values.Length; }
            public uint Run() {
                int[] values = new int[3];
                return Count(values);
            }
            """));
        new SemanticBinder().Bind(project);

        Assert.Single(project.GenericFunctionInstances);
        var directory = Path.Combine(Path.GetTempPath(), $"cxc-generic-array-count-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(Path.Combine(directory, "generic_array_count.c"));
            Assert.Contains(
                "CX_ID_6(cxcore, System, Collections, Array, Length, __const_get)(values)",
                source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void SupportsComposedGenericArrayLengthComparisons()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_array_length_predicates");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public bool IsInRange<T>(T[] values, uint minimum, uint maximum) {
                return values.Length >= minimum && values.Length <= maximum;
            }
            public bool HasNonEmptyNestedArray<T>(T[][] values, uint minimum) {
                return values[0].Length > 0u && values[0].Length >= minimum;
            }
            public bool IsEmptyOrLongEnough<T>(T[] values, uint minimum) {
                return values.Length == 0u ? values.Length < minimum : values.Length >= minimum;
            }
            public bool HasLengthIfPresent<T>(T[] values, uint minimum) {
                return values != null && values.Length >= minimum;
            }
            public bool IsNullOrLongEnough<T>(T[] values, uint minimum) {
                return values == null || values.Length >= minimum;
            }
            public int Run() {
                int[] values = new int[3];
                int[] empty = new int[0];
                int[] missing = null;
                int[][] nested = new int[1][];
                nested[0] = new int[2];
                if (!IsInRange(values, 2u, 4u)) return 0;
                if (!HasNonEmptyNestedArray(nested, 2u)) return 0;
                if (!IsEmptyOrLongEnough(values, 3u)) return 0;
                if (!IsEmptyOrLongEnough(empty, 1u)) return 0;
                if (!HasLengthIfPresent(values, 3u) || HasLengthIfPresent(missing, 0u)) return 0;
                if (!IsNullOrLongEnough(missing, 4u) || IsNullOrLongEnough(values, 4u)) return 0;
                return 1;
            }
            """));
        new SemanticBinder().Bind(project);

        Assert.Equal(5, project.GenericFunctionInstances.Count);
        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-generic-array-length-predicates-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(Path.Combine(
                directory, "generic_array_length_predicates.c"));
            Assert.Contains("&&", source);
            Assert.Contains("?", source);
            Assert.Contains("cx_array_at(values", source);
            Assert.DoesNotContain("_unknowntype_", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void SupportsComparingLengthsOfMatchingGenericArrays()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_array_length_equality");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            namespace System.Collections;
            public final class Array<T> {
                public constructor(uint length) {}
                public uint Length { const get { return this.Length; } }
                public T Get(uint index) const { return this[index]; }
                public void Set(uint index, T value) { this[index] = value; }
            }
            """));
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public bool SameLength<T>(T[] first, System.Collections.Array<T> second) {
                return first.Length == second.Length;
            }
            public bool DifferentLength<T>(T[] first, T[] second) {
                return first.Length != second.Length;
            }
            public bool SameFirstRowLength<T>(T[][] first, T[][] second) {
                return first[0].Length == second[0].Length;
            }
            public bool SameLengthIfPresent<T>(T[] first, T[] second) {
                return first != null && second != null &&
                    first.Length == second.Length;
            }
            public int Run() {
                int[] first = new int[2];
                int[] same = new int[2];
                int[] different = new int[3];
                long[] longFirst = new long[1];
                long[] longSame = new long[1];
                int[][] firstRows = new int[1][];
                int[][] secondRows = new int[1][];
                firstRows[0] = new int[2];
                secondRows[0] = new int[2];
                int[] missing = null;
                if (!SameLength(first, same) || !DifferentLength(first, different)) return 0;
                if (!SameLength(longFirst, longSame)) return 0;
                if (!SameFirstRowLength(firstRows, secondRows)) return 0;
                if (!SameLengthIfPresent(first, same) ||
                    SameLengthIfPresent(missing, same)) return 0;
                return 1;
            }
            """));
        new SemanticBinder().Bind(project);

        Assert.Equal(5, project.GenericFunctionInstances.Count);
        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-generic-array-length-equality-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(Path.Combine(directory,
                "generic_array_length_equality.c"));
            Assert.True(source.Split(
                "CX_ID_6(cxcore, System, Collections, Array, Length, __const_get)(").Length - 1 >= 8);
            Assert.DoesNotContain("_unknowntype_", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void SupportsUnsignedArithmeticInGenericArrayLengthComparisons()
    {
        var project = CxProject.CreateDefaultApplicationProject(
            "generic_array_length_arithmetic");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            namespace System.Collections;
            public final class Array<T> {
                public constructor(uint length) {}
                public uint Length { const get { return this.Length; } }
                public T Get(uint index) const { return this[index]; }
                public void Set(uint index, T value) { this[index] = value; }
            }
            """));
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public bool HasOffsetLength<T>(T[] values, uint minimum, uint offset) {
                return values.Length >= minimum + offset;
            }
            public bool HasScaledLength<T>(T[] values, uint baseline, uint scale) {
                return values.Length == baseline * scale;
            }
            public int Run() {
                int[] values = new int[5];
                if (!HasOffsetLength(values, 2u, 3u)) return 0;
                if (!HasScaledLength(values, 1u, 5u)) return 0;
                return 1;
            }
            """));
        new SemanticBinder().Bind(project);

        Assert.Equal(2, project.GenericFunctionInstances.Count);
        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-generic-array-length-arithmetic-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project,
                Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(Path.Combine(directory,
                "generic_array_length_arithmetic.c"));
            Assert.Contains("minimum + offset", source);
            Assert.Contains("baseline * scale", source);
            Assert.DoesNotContain("_unknowntype_", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void SupportsNullChecksForGenericReferenceValues()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_reference_null_check");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public interface IMarker {}
            public class Marker : IMarker {}
            public bool IsNull<T>(T value) where T : class { return value == null; }
            public bool IsNotNull<T>(T value) where T : IMarker { return null != value; }
            public bool IsArrayNull<T>(T[] values) { return values == null; }
            public bool HasNull<T>(T first, T second) where T : class {
                return first == null || second == null;
            }
            public bool HasNoNull<T>(T first, T second) where T : class {
                return first != null && second != null;
            }
            public bool HasNullArray<T>(T[] first, T[] second) {
                return first == null || second == null;
            }
            public bool HasNullWithNegation<T>(T value) where T : class {
                return !(value != null);
            }
            public bool HasNullWithConditional<T>(T first, T second) where T : class {
                return first == null ? second == null : second != null;
            }
            public bool HasNullArrayWithConditional<T>(T[] first, T[] second) {
                return first == null ? second == null : second != null;
            }
            public bool Run() {
                Marker value = new Marker();
                Marker missing = null;
                int[] values = new int[0];
                int[] missingValues = null;
                return !IsNull(value) && IsNull(missing) && IsNotNull(value) &&
                    !IsNotNull(missing) && !IsArrayNull(values) && IsArrayNull(missingValues) &&
                    !HasNull(value, value) && HasNull(value, missing) &&
                    HasNoNull(value, value) && !HasNoNull(value, missing) &&
                    !HasNullArray(values, values) && HasNullArray(values, missingValues) &&
                    HasNullWithNegation(missing) && !HasNullWithNegation(value) &&
                    HasNullWithConditional(value, value) &&
                    !HasNullWithConditional(value, missing) &&
                    HasNullArrayWithConditional(values, values) &&
                    !HasNullArrayWithConditional(values, missingValues);
            }
            """));
        new SemanticBinder().Bind(project);

        Assert.Equal(9, project.GenericFunctionInstances.Count);
        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-generic-reference-null-check-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(Path.Combine(directory, "generic_reference_null_check.c"));
            Assert.Contains("value ==", source);
            Assert.Contains("!= value", source);
            Assert.Contains("values ==", source);
            Assert.Contains("first ==", source);
            Assert.Contains("second !=", source);
            Assert.Contains("?", source);
            Assert.DoesNotContain("_unknowntype_", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void RejectsNullChecksForUnconstrainedGenericValues()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_unconstrained_null_check");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public bool IsNull<T>(T value) { return value == null; }
            """));

        var error = Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
        Assert.Contains("currently supports", error.Message);
    }

    [Fact]
    public void RejectsNullChecksForValueConstrainedGenericValues()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_value_null_check");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public bool IsNull<T>(T value) where T : struct { return value == null; }
            """));

        var error = Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
        Assert.Contains("currently supports", error.Message);
    }

    [Fact]
    public void SupportsAssigningAValueToAMatchingGenericArrayElement()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_array_set");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public void SetAt<T>(T[] values, int index, T value) {
                values[index] = value;
            }
            public int Run() {
                int[] values = new int[1];
                SetAt(values, 0, 53);
                return values[0];
            }
            """));
        new SemanticBinder().Bind(project);

        var instance = Assert.Single(project.GenericFunctionInstances);
        Assert.Same(BuiltInSystemTypes.Void, instance.ReturnType);
        Assert.Same(BuiltInSystemTypes.Int, instance.ParameterTypes[0] is ArrayType array
            ? array.ElementType
            : null);
        var directory = Path.Combine(Path.GetTempPath(), $"cxc-generic-array-set-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(Path.Combine(directory, "generic_array_set.c"));
            Assert.Contains("cx_array_at(values, (cx_uint)(index), (cx_uint)sizeof(cx_int))) = value;", source);
            Assert.DoesNotContain("_unknowntype_", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void SupportsCopyingBetweenMatchingGenericArrayElements()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_array_element_copy");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public void CopyAt<T>(T[] source, T[] target, int sourceIndex, int targetIndex) {
                target[targetIndex] = source[sourceIndex];
            }
            public int Run() {
                int[] source = new int[1];
                int[] target = new int[1];
                source[0] = 59;
                CopyAt(source, target, 0, 0);
                return target[0];
            }
            """));
        new SemanticBinder().Bind(project);

        var instance = Assert.Single(project.GenericFunctionInstances);
        Assert.Same(BuiltInSystemTypes.Void, instance.ReturnType);
        var directory = Path.Combine(Path.GetTempPath(), $"cxc-generic-array-element-copy-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(Path.Combine(directory, "generic_array_element_copy.c"));
            Assert.Contains("cx_array_at(target, (cx_uint)(targetIndex)", source);
            Assert.Contains("cx_array_at(source, (cx_uint)(sourceIndex)", source);
            Assert.DoesNotContain("_unknowntype_", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void SupportsCreatingAndFillingAMatchingGenericArray()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_array_create");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public T[] Create<T>(uint length, uint index, T value) {
                T[] values = new T[length];
                values[index] = value;
                return values;
            }
            public int RunInt() { return Create(2u, 1u, 61)[1]; }
            public long RunLong() { return Create(3u, 2u, 73L)[2]; }
            """));
        new SemanticBinder().Bind(project);

        Assert.Equal(2, project.GenericFunctionInstances.Count);
        Assert.All(project.GenericFunctionInstances, instance =>
            Assert.IsType<ArrayType>(instance.ReturnType));

        var directory = Path.Combine(Path.GetTempPath(), $"cxc-generic-array-create-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(Path.Combine(directory, "generic_array_create.c"));
            Assert.Equal(2, source.Split("cx_array_new(").Length - 1);
            Assert.Contains("sizeof(cx_int)", source);
            Assert.Contains("sizeof(cx_long)", source);
            Assert.Contains("cx_array_at(values", source);
            Assert.DoesNotContain("_unknowntype_", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void RejectsGenericArrayConstructionWithMismatchedElementValue()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_array_create_mismatch");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public T[] Create<T>(uint length, uint index, long value) {
                T[] values = new T[length];
                values[index] = value;
                return values;
            }
            """));

        var exception = Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
        Assert.Contains("Generic function 'Create' currently supports only", exception.Message);
    }

    [Fact]
    public void RejectsGenericArrayElementAssignmentWithMismatchedValueType()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_array_set_invalid");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public void SetAt<T>(T[] values, int index, long value) {
                values[index] = value;
            }
            """));

        var exception = Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
        Assert.Contains("Generic function 'SetAt' currently supports only", exception.Message);
    }

    [Fact]
    public void BindsGenericConditionalReturnForDistinctInstantiations()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_select");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public T Select<T>(bool chooseFirst, T first, T second) {
                if (chooseFirst) return first; else return second;
            }
            public int RunInt() { return Select(true, 7, 9); }
            public long RunLong() { return Select(false, 11L, 13L); }
            """));
        new SemanticBinder().Bind(project);

        var calls = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<FunctionDeclaration>()
            .Where(function => function.Name is "RunInt" or "RunLong")
            .Select(function => Assert.IsType<InvocationExpression>(
                Assert.IsType<ReturnStatement>(Assert.Single(function.Body!)).Expression))
            .ToArray();
        Assert.Same(BuiltInSystemTypes.Int, calls[0].TargetSymbol!.ReturnType);
        Assert.Same(BuiltInSystemTypes.Long, calls[1].TargetSymbol!.ReturnType);
        Assert.Equal(2, project.GenericFunctionInstances.Count);
        Assert.Equal(2, project.GenericFunctionInstances
            .Select(instance => instance.SpecializationName).Distinct().Count());
    }

    [Fact]
    public void EmitsConcreteConditionalBranchesForEachInstantiation()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_select");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public T Select<T>(bool chooseFirst, T first, T second) {
                if (chooseFirst) return first; else return second;
            }
            public int RunInt() { return Select(true, 7, 9); }
            public long RunLong() { return Select(false, 11L, 13L); }
            """));
        new SemanticBinder().Bind(project);
        var directory = Path.Combine(Path.GetTempPath(), $"cxc-generic-select-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var header = File.ReadAllText(Path.Combine(directory, "generic_select.h"));
            var source = File.ReadAllText(Path.Combine(directory, "generic_select.c"));

            foreach (var instance in project.GenericFunctionInstances)
            {
                Assert.Contains(instance.SpecializationName!, header);
                Assert.Contains(instance.SpecializationName!, source);
            }
            Assert.Equal(2, source.Split("if (chooseFirst)").Length - 1);
            Assert.Equal(2, source.Split("return first;").Length - 1);
            Assert.Equal(2, source.Split("return second;").Length - 1);
            Assert.DoesNotContain("_unknowntype_", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void BindsAndEmitsGenericConditionalReturnUsingBooleanLocal()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_select_local_condition");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public T Select<T>(bool chooseFirst, T first, T second) {
                bool shouldChooseFirst = !(!chooseFirst);
                if (shouldChooseFirst) return first; else return second;
            }
            public int RunInt(bool chooseFirst) {
                return Select(chooseFirst, 7, 9);
            }
            public long RunLong(bool chooseFirst) {
                return Select(chooseFirst, 11L, 13L);
            }
            """));

        new SemanticBinder().Bind(project);

        Assert.Equal(2, project.GenericFunctionInstances.Count);
        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-generic-select-local-condition-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(
                Path.Combine(directory, "generic_select_local_condition.c"));

            Assert.Equal(2, source.Split("shouldChooseFirst =").Length - 1);
            Assert.Equal(2, source.Split("if (shouldChooseFirst)").Length - 1);
            Assert.Equal(2, source.Split("return first;").Length - 1);
            Assert.Equal(2, source.Split("return second;").Length - 1);
            Assert.DoesNotContain("_unknowntype_", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void EmitsConditionalArrayReturnForConcreteElementTypes()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_select_array");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public T[] Select<T>(bool chooseFirst, T[] first, T[] second) {
                if (chooseFirst) return first; else return second;
            }
            public int[] RunInt(bool chooseFirst, int[] first, int[] second) {
                return Select(chooseFirst, first, second);
            }
            public long[] RunLong(bool chooseFirst, long[] first, long[] second) {
                return Select(chooseFirst, first, second);
            }
            """));
        new SemanticBinder().Bind(project);

        Assert.Equal(2, project.GenericFunctionInstances.Count);
        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-generic-select-array-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var header = File.ReadAllText(Path.Combine(directory, "generic_select_array.h"));
            var source = File.ReadAllText(Path.Combine(directory, "generic_select_array.c"));
            foreach (var instance in project.GenericFunctionInstances)
            {
                Assert.Contains(instance.SpecializationName!, header);
            }
            Assert.Equal(2, source.Split("if (chooseFirst)").Length - 1);
            Assert.Equal(2, source.Split("return first;").Length - 1);
            Assert.Equal(2, source.Split("return second;").Length - 1);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void EmitsGenericConditionalValueExpressionsForConcreteTypes()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_select_expression");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public T Select<T>(bool chooseFirst, T first, T second) {
                return chooseFirst ? first : second;
            }
            public int RunInt(bool chooseFirst) { return Select(chooseFirst, 7, 9); }
            public long RunLong(bool chooseFirst) { return Select(chooseFirst, 11L, 13L); }
            """));
        new SemanticBinder().Bind(project);

        Assert.Equal(2, project.GenericFunctionInstances.Count);
        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-generic-select-expression-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(
                Path.Combine(directory, "generic_select_expression.c"));
            Assert.Equal(2, source.Split("? first : second", StringSplitOptions.None).Length - 1);
            Assert.DoesNotContain("_unknowntype_", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void EmitsGenericConditionalValuesThroughAnInferredLocal()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_select_local_value");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public T Select<T>(bool chooseFirst, T first, T second) {
                var selected = chooseFirst ? first : second;
                return selected;
            }
            public int RunInt(bool chooseFirst) { return Select(chooseFirst, 7, 9); }
            public long RunLong(bool chooseFirst) { return Select(chooseFirst, 11L, 13L); }
            """));
        new SemanticBinder().Bind(project);

        Assert.Equal(2, project.GenericFunctionInstances.Count);
        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-generic-select-local-value-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(
                Path.Combine(directory, "generic_select_local_value.c"));
            Assert.Contains("cx_int selected = ", source);
            Assert.Contains("cx_long selected = ", source);
            Assert.Equal(2, source.Split("return selected;", StringSplitOptions.None).Length - 1);
            Assert.DoesNotContain("_unknowntype_", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void BindsAndEmitsGenericLocalCopyWithOneAssignment()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_replace");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public T Replace<T>(T initial, T replacement) {
                T copy = initial;
                copy = replacement;
                return copy;
            }
            public int RunInt() { return Replace(5, 7); }
            public long RunLong() { return Replace(11L, 13L); }
            """));
        new SemanticBinder().Bind(project);
        Assert.Equal(2, project.GenericFunctionInstances.Count);

        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-generic-replace-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(Path.Combine(directory, "generic_replace.c"));
            Assert.Contains("copy = replacement;", source);
            Assert.Equal(2, source.Split("return copy;").Length - 1);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void InfersGenericLocalCopyTypeForEachInstantiation()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_inferred_copy");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public T Copy<T>(T value) { var copy = value; return copy; }
            public int RunInt() { return Copy(5); }
            public long RunLong() { return Copy(11L); }
            """));

        new SemanticBinder().Bind(project);

        Assert.Equal(2, project.GenericFunctionInstances.Count);
        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-generic-inferred-copy-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(Path.Combine(directory, "generic_inferred_copy.c"));
            Assert.Contains("cx_int copy = value;", source);
            Assert.Contains("cx_long copy = value;", source);
            Assert.Equal(2, source.Split("return copy;").Length - 1);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void InfersArrayLocalCopyTypeForEachInstantiation()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_inferred_array_copy");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public T[] Copy<T>(T[] value) { var copy = value; return copy; }
            public int[] RunInt(int[] value) { return Copy(value); }
            public long[] RunLong(long[] value) { return Copy(value); }
            """));

        new SemanticBinder().Bind(project);

        Assert.Equal(2, project.GenericFunctionInstances.Count);
        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-generic-inferred-array-copy-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(
                Path.Combine(directory, "generic_inferred_array_copy.c"));
            Assert.Equal(2, source.Split("return copy;").Length - 1);
            Assert.DoesNotContain("_unknowntype_", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void InfersNestedConstructedLocalCopyTypeAtInstantiation()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_inferred_constructed_copy");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public class Box<T> {}
            public Box<T> Copy<T>(Box<T> value) { var copy = value; return copy; }
            public Box<int> Run(Box<int> value) { return Copy(value); }
            public Box<long> RunLong(Box<long> value) { return Copy(value); }
            """));

        new SemanticBinder().Bind(project);

        Assert.Equal(2, project.GenericFunctionInstances.Count);
        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-generic-inferred-constructed-copy-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(
                Path.Combine(directory, "generic_inferred_constructed_copy.c"));
            Assert.Equal(2, source.Split("return copy;").Length - 1);
            Assert.DoesNotContain("_unknowntype_", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void InfersMultipleGenericLocalDeclaratorsAtInstantiation()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_inferred_local_list");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public T Copy<T, U>(T value, U ignored) {
                var first = value, second = first;
                return second;
            }
            public int RunInt() { return Copy(5, true); }
            public long RunLong() { return Copy(11L, false); }
            """));

        new SemanticBinder().Bind(project);

        Assert.Equal(2, project.GenericFunctionInstances.Count);
        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-generic-inferred-local-list-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(
                Path.Combine(directory, "generic_inferred_local_list.c"));
            Assert.Contains("first = value;", source);
            Assert.Contains("second = first;", source);
            Assert.Equal(2, source.Split("return second;").Length - 1);
            Assert.DoesNotContain("_unknowntype_", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void RejectsInferredLocalCopyWhenReturnParameterDoesNotMatch()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_inferred_copy_mismatch");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public U Copy<T, U>(T value) { var copy = value; return copy; }
            """));

        var error = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(project));

        Assert.Contains("Generic function", error.Message);
        Assert.Contains("currently supports only", error.Message);
    }

    [Fact]
    public void BindsAndEmitsIdentityForNestedConstructedGenericType()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_identity_box");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public class Box<T> {}
            public Box<T> IdentityBox<T>(Box<T> value) { return value; }
            public Box<int> Run(Box<int> value) { return IdentityBox(value); }
            """));
        new SemanticBinder().Bind(project);
        Assert.Single(project.GenericFunctionInstances);
        Assert.Single(project.GenericTypeInstances);

        var directory = Path.Combine(Path.GetTempPath(),
            $"cxc-generic-identity-box-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var header = File.ReadAllText(Path.Combine(directory, "generic_identity_box.h"));
            var source = File.ReadAllText(Path.Combine(directory, "generic_identity_box.c"));
            Assert.Contains(project.GenericFunctionInstances.Single().SpecializationName!, header);
            Assert.Contains("return value;", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void RejectsBodyThatNeedsPerInstantiationBinding()
    {
        var project = CxProject.CreateDefaultApplicationProject();
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public T Identity<T>(T value) { T copy = value; return value; }
            """));
        Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
    }

    [Fact]
    public void BindsOneGenericLocalFromMatchingParameter()
    {
        var project = CreateCopyProject();
        var function = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<FunctionDeclaration>().First();
        var local = Assert.IsType<LocalVariableDeclarationStatement>(function.Body![0]);
        Assert.IsType<GenericType>(local.DeclaredType);
        Assert.IsType<GenericType>(Assert.Single(local.Declarators).Type);
        Assert.Equal(2, project.GenericFunctionInstances.Count);
    }

    [Fact]
    public void EmitsConcreteLocalForEveryUsedCopyInstantiation()
    {
        var project = CreateCopyProject();
        var directory = Path.Combine(Path.GetTempPath(), $"cxc-generic-copy-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(Path.Combine(directory, "generic_copy.c"));
            Assert.Contains("cx_int copy = value;", source);
            Assert.Contains("cx_long copy = value;", source);
            Assert.Equal(2, source.Split("return copy;").Length - 1);
            Assert.DoesNotContain("_unknowntype_", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void InfersTwoParametersForLocalCopyBody()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_pair_copy");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public T Pick<T, U>(T first, U second) { T copy = first; return copy; }
            public int RunInt() { return Pick(7, true); }
            public long RunLong() { return Pick(9L, false); }
            """));
        new SemanticBinder().Bind(project);
        Assert.Equal(2, project.GenericFunctionInstances.Count);
        var directory = Path.Combine(Path.GetTempPath(), $"cxc-generic-pair-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(Path.Combine(directory, "generic_pair_copy.c"));
            Assert.Contains("cx_int copy = first;", source);
            Assert.Contains("cx_long copy = first;", source);
            Assert.Equal(2, source.Split("return copy;").Length - 1);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void InfersArrayElementTypesForIdentityBody()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_array_identity");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public class First {}
            public class Second {}
            public T[] IdentityArray<T>(T[] value) { return value; }
            public First[] RunFirst(First[] input) { return IdentityArray(input); }
            public Second[] RunSecond(Second[] input) { return IdentityArray(input); }
            """));
        new SemanticBinder().Bind(project);
        var calls = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<FunctionDeclaration>()
            .Where(function => function.Name is "RunFirst" or "RunSecond")
            .Select(function => Assert.IsType<InvocationExpression>(
                Assert.IsType<ReturnStatement>(Assert.Single(function.Body!)).Expression))
            .ToArray();
        Assert.Equal("First", Assert.IsType<NamedType>(
            Assert.IsType<ArrayType>(calls[0].TargetSymbol!.ReturnType).ElementType).Name);
        Assert.Equal("Second", Assert.IsType<NamedType>(
            Assert.IsType<ArrayType>(calls[1].TargetSymbol!.ReturnType).ElementType).Name);
        Assert.Equal(2, project.GenericFunctionInstances.Count);
    }

    [Fact]
    public void EmitsSeparateArrayIdentitySpecializations()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_array_identity");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public class First {}
            public class Second {}
            public T[] IdentityArray<T>(T[] value) { return value; }
            public First[] RunFirst(First[] input) { return IdentityArray(input); }
            public Second[] RunSecond(Second[] input) { return IdentityArray(input); }
            """));
        new SemanticBinder().Bind(project);
        var directory = Path.Combine(Path.GetTempPath(), $"cxc-generic-array-identity-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var header = File.ReadAllText(Path.Combine(directory, "generic_array_identity.h"));
            var source = File.ReadAllText(Path.Combine(directory, "generic_array_identity.c"));
            foreach (var instance in project.GenericFunctionInstances)
            {
                Assert.Contains(instance.SpecializationName!, header);
                Assert.Contains(instance.SpecializationName!, source);
            }
            Assert.Equal(2, project.GenericFunctionInstances.Select(instance =>
                instance.SpecializationName).Distinct().Count());
            Assert.Equal(2, source.Split("return value;").Length - 1);
            Assert.DoesNotContain("_unknowntype_", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void RepeatedArrayParameterRejectsDifferentElementTypes()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_array_mismatch");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public class First {}
            public class Second {}
            public T[] PickSame<T>(T[] first, T[] second) { return first; }
            public First[] Run(First[] first, Second[] second) {
                return PickSame(first, second);
            }
            """));
        var error = Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
        Assert.Contains("No overload", error.Message);
    }

    [Fact]
    public void CopiesMatchingGenericArraysWithAnIndexedForLoop()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_array_copy");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public T[] Copy<T>(T[] source) {
                T[] result = new T[source.Length];
                for (uint i = 0u; i < source.Length; i++) {
                    result[i] = source[i];
                }
                return result;
            }
            public int[] RunInt(int[] source) { return Copy(source); }
            public long[] RunLong(long[] source) { return Copy(source); }
            """));
        new SemanticBinder().Bind(project);

        Assert.Equal(2, project.GenericFunctionInstances.Count);
        var directory = Path.Combine(Path.GetTempPath(), $"cxc-generic-array-copy-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(Path.Combine(directory, "generic_array_copy.c"));
            Assert.Equal(2, source.Split("cx_array_new((cx_uint)(", StringSplitOptions.None).Length - 1);
            Assert.Contains("for (cx_uint i = 0u;", source);
            Assert.Contains("sizeof(cx_int)", source);
            Assert.Contains("sizeof(cx_long)", source);
            Assert.Contains("cx_array_at(source", source);
            Assert.DoesNotContain("_unknowntype_", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void CopiesMatchingGenericArraysWithAnIndexedWhileLoop()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_array_copy_while");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public T[] Copy<T>(T[] source) {
                T[] result = new T[source.Length];
                uint i = 0u;
                while (i < source.Length) {
                    result[i] = source[i];
                    i = i + 1u;
                }
                return result;
            }
            public int[] RunInt(int[] source) { return Copy(source); }
            public long[] RunLong(long[] source) { return Copy(source); }
            """));
        new SemanticBinder().Bind(project);

        Assert.Equal(2, project.GenericFunctionInstances.Count);
        var directory = Path.Combine(Path.GetTempPath(), $"cxc-generic-array-copy-while-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(Path.Combine(directory, "generic_array_copy_while.c"));
            Assert.Equal(2, source.Split("cx_array_new((cx_uint)(", StringSplitOptions.None).Length - 1);
            Assert.Contains("while (", source);
            Assert.Contains("i = (i + 1u)", source);
            Assert.Contains("sizeof(cx_int)", source);
            Assert.Contains("sizeof(cx_long)", source);
            Assert.DoesNotContain("_unknowntype_", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void CopiesMatchingGenericArraysWithForeach()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_array_copy_foreach");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public T[] Copy<T>(T[] source) {
                T[] result = new T[source.Length];
                uint index = 0u;
                foreach (T item in source) {
                    result[index] = item;
                    index++;
                }
                return result;
            }
            public int[] RunInt(int[] source) { return Copy(source); }
            public long[] RunLong(long[] source) { return Copy(source); }
            """));
        new SemanticBinder().Bind(project);

        Assert.Equal(2, project.GenericFunctionInstances.Count);
        var directory = Path.Combine(Path.GetTempPath(), $"cxc-generic-array-copy-foreach-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            CCodeOutputGenerator.GenerateOutput(project, Path.Combine(directory, "input.cx"));
            var source = File.ReadAllText(Path.Combine(directory, "generic_array_copy_foreach.c"));
            Assert.Equal(2, source.Split("cx_array_new((cx_uint)(", StringSplitOptions.None).Length - 1);
            Assert.Contains("__cx_concept_foreach_collection_item", source);
            Assert.Contains("cx_array_at", source);
            Assert.Contains("sizeof(cx_int)", source);
            Assert.Contains("sizeof(cx_long)", source);
            Assert.DoesNotContain("_unknowntype_", source);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void RejectsGenericArrayLoopsOutsideTheCopyPattern()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_array_copy_reject");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public T[] CopyEveryOther<T>(T[] source) {
                T[] result = new T[source.Length];
                for (uint i = 0u; i < source.Length; i += 2u) {
                    result[i] = source[i];
                }
                return result;
            }
            public T[] CopyEveryOtherWhile<T>(T[] source) {
                T[] result = new T[source.Length];
                uint i = 0u;
                while (i < source.Length) {
                    result[i] = source[i];
                    i += 2u;
                }
                return result;
            }
            """));

        var error = Assert.Throws<CompilationErrorException>(() => new SemanticBinder().Bind(project));
        Assert.Contains("currently supports only", error.Message);
    }

    [Fact]
    public void RejectsGenericArrayForeachCopyWithSkippedIndices()
    {
        var project = CxProject.CreateDefaultApplicationProject(
            "generic_array_copy_foreach_reject");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public T[] CopyEveryOther<T>(T[] source) {
                T[] result = new T[source.Length];
                uint index = 0u;
                foreach (T item in source) {
                    result[index] = item;
                    index = index + 2u;
                }
                return result;
            }
            """));

        var error = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(project));
        Assert.Contains("currently supports only", error.Message);
    }

    private static CxProject CreateProject()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_body");
        project.AddCompilationContext(CompilerTestHelper.Parse(Source));
        new SemanticBinder().Bind(project);
        return project;
    }

    private static CxProject CreateCopyProject()
    {
        var project = CxProject.CreateDefaultApplicationProject("generic_copy");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            public T Copy<T>(T value) { T copy = value; return copy; }
            public int RunInt() { return Copy(7); }
            public long RunLong() { return Copy(9L); }
            public int RunIntAgain() { return Copy(11); }
            """));
        new SemanticBinder().Bind(project);
        return project;
    }
}

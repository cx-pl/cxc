using CxCompiler.Model.Errors;
using CxCompiler.Model.Expressions;
using CxCompiler.Model.Project;
using CxCompiler.Model.Statements;
using CxCompiler.Model.Types;
using CxCompiler.OutputGenerators;
using CxCompiler.Semantics;

namespace CxCompiler.Tests;

public sealed class AwaitExpressionTests
{
    [Fact]
    public void ParserPreservesAwaitAsAnExpressionNode()
    {
        var project = CreateProject("""
            async System.Threading.Tasks.Task<int> Run() {
                await Work();
                return 0;
            }
            """);

        var function = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>().Single();
        var expression = Assert.IsType<ExpressionStatement>(function.Body![0]).Expression;
        var awaitExpression = Assert.IsType<AwaitExpression>(expression);
        Assert.IsType<InvocationExpression>(awaitExpression.Operand);
    }

    [Fact]
    public void AwaitOutsideAsyncFunctionHasSourceDiagnostic()
    {
        var project = CreateProject("""
            void Run() {
                await Work();
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(project));

        Assert.Contains("only valid inside an async function", error.Message);
    }

    [Fact]
    public void AwaitForeachIsPreservedAndRequiresAsyncFunction()
    {
        var project = CreateProject("""
            import System.Collections;
            void Run(IAsyncEnumerable<int> source) {
                await foreach (int value in source) { }
            }
            """);

        var function = project.CompilationContexts
            .SelectMany(context => context.DeclarationScope.Declarations)
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(candidate => candidate.Name == "Run");
        var loop = Assert.IsType<ForeachStatement>(Assert.Single(function.Body!));
        Assert.True(loop.IsAwait);

        var error = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(project));

        Assert.Contains("await foreach", error.Message);
        Assert.Contains("only valid inside an async function", error.Message);
    }

    [Fact]
    public void LowersSimpleAwaitForeachInAsyncTaskConsumer()
    {
        var project = CxProject.CreateDefaultApplicationProject();
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            namespace System;
            public interface IAsyncDisposable {
                System.Threading.Tasks.ValueTaskHandle DisposeAsync();
            }
            """));
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            namespace System.Collections;
            public interface IAsyncEnumerable<T> {
                IAsyncEnumerator<T> GetAsyncEnumerator(
                    System.Threading.Tasks.CancellationToken cancellationToken);
            }
            public interface IAsyncEnumerator<T> : System.IAsyncDisposable {
                T Current { const get; }
                System.Threading.Tasks.ValueTask<bool> MoveNextAsync();
            }
            """));
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            import System.Collections;
            import System.Threading.Tasks;
            namespace App;
            async Task Run(IAsyncEnumerable<int> source) {
                await foreach (int value in source) { }
            }
            async Task<int> Sum(IAsyncEnumerable<int> source) {
                int total = 0;
                await foreach (int value in source) { total += value; }
                return total;
            }
            """));

        new SemanticBinder().Bind(project);

        var functions = project.CompilationContexts
            .SelectMany(context => context.DeclarationScope.Declarations)
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Where(candidate => candidate.Name is "Run" or "Sum")
            .ToArray();
        Assert.Equal(2, functions.Length);
        foreach (var function in functions)
        {
            Assert.True(function.IsPendingAwaitRelay);
            Assert.DoesNotContain(CoroutineSyntaxAnalysis.EnumerateBodyStatements(function.Body!),
                statement => statement is ForeachStatement { IsAwait: true });
            Assert.DoesNotContain(CoroutineSyntaxAnalysis.EnumerateBodyExpressions(function.Body!),
                expression => expression is AwaitExpression);
            var outerTry = Assert.IsType<TryStatement>(Assert.Single(function.Body!));
            var loweredBody = Assert.IsType<BlockStatement>(outerTry.Body);
            Assert.Contains(loweredBody.Statements, statement => statement is WhileStatement);
            Assert.DoesNotContain(loweredBody.Statements
                .OfType<LocalVariableDeclarationStatement>(), declaration =>
                declaration.DeclaredType is NamedType { ResolvedTypeFullName.Parts: var parts } &&
                parts.Length >= 4 && parts[^2] == "Tasks" &&
                parts[^1] == "TaskCompletionSource");
        }
    }

    [Fact]
    public void AwaitForeachControlBodyDefersEscapedStateUntilMoveSuspends()
    {
        var project = CxProject.CreateDefaultApplicationProject();
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            namespace System;
            public interface IAsyncDisposable {
                System.Threading.Tasks.ValueTaskHandle DisposeAsync();
            }
            """));
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            namespace System.Collections;
            public interface IAsyncEnumerable<T> {
                IAsyncEnumerator<T> GetAsyncEnumerator(
                    System.Threading.Tasks.CancellationToken cancellationToken);
            }
            public interface IAsyncEnumerator<T> : System.IAsyncDisposable {
                T Current { const get; }
                System.Threading.Tasks.ValueTask<bool> MoveNextAsync();
            }
            """));
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            import System.Collections;
            import System.Threading.Tasks;
            namespace App;
            async Task<int> SumWithControl(IAsyncEnumerable<int> source) {
                int total = 0;
                await foreach (int value in source) {
                    if (value == 1) continue;
                    if (value == 3) break;
                    total += value;
                }
                return total;
            }
            """));

        new SemanticBinder().Bind(project);

        var function = project.CompilationContexts
            .SelectMany(context => context.DeclarationScope.Declarations)
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(candidate => candidate.Name == "SumWithControl");
        Assert.True(function.IsPendingAwaitRelay);
        var outerTry = Assert.IsType<TryStatement>(Assert.Single(function.Body!));
        var loweredBody = Assert.IsType<BlockStatement>(outerTry.Body);
        var inlineLoopIndex = Array.FindIndex(
            loweredBody.Statements.ToArray(), statement => statement is WhileStatement);
        Assert.True(inlineLoopIndex >= 0,
            "Expected a top-level completed-move loop before creating escaped state.");

        var pendingBranch = loweredBody.Statements
            .Select((statement, index) => (statement, index))
            .FirstOrDefault(item => item.index > inlineLoopIndex &&
                item.statement is IfStatement);
        var pendingBranchIf = Assert.IsType<IfStatement>(pendingBranch.statement);
        Assert.Contains(CoroutineSyntaxAnalysis
            .EnumerateBodyStatements([pendingBranchIf.ThenStatement])
            .OfType<LocalVariableDeclarationStatement>(), declaration =>
                declaration.DeclaredType is NamedType
                {
                    ResolvedTypeFullName.Parts: var parts,
                } && parts.Length >= 4 && parts[^2] == "Tasks" &&
                    parts[^1] == "TaskCompletionSource");
        Assert.True(inlineLoopIndex < pendingBranch.index,
            "The completion source must be created only after inline moves stop completing.");
        Assert.DoesNotContain(loweredBody.Statements.Take(inlineLoopIndex)
            .OfType<LocalVariableDeclarationStatement>(), declaration =>
            declaration.DeclaredType is NamedType
            {
                ResolvedTypeFullName.Parts: var parts,
            } && parts.Length >= 4 && parts[^2] == "Tasks" &&
                parts[^1] == "TaskCompletionSource");
    }

    [Fact]
    public void LowersSimpleAwaitForeachTaskEnumeratorInAsyncTaskConsumer()
    {
        var project = CxProject.CreateDefaultApplicationProject();
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            namespace System;
            public interface IAsyncDisposable {
                System.Threading.Tasks.TaskHandle DisposeAsync();
            }
            """));
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            namespace System.Collections;
            public interface IAsyncEnumerable<T> {
                IAsyncEnumerator<T> GetAsyncEnumerator(
                    System.Threading.Tasks.CancellationToken cancellationToken);
            }
            public interface IAsyncEnumerator<T> : System.IAsyncDisposable {
                T Current { const get; }
                System.Threading.Tasks.Task<bool> MoveNextAsync();
            }
            """));
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            import System.Collections;
            import System.Threading.Tasks;
            namespace App;
            async Task Run(IAsyncEnumerable<int> source) {
                await foreach (int value in source) { }
            }
            """));

        new SemanticBinder().Bind(project);

        var function = project.CompilationContexts
            .SelectMany(context => context.DeclarationScope.Declarations)
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(candidate => candidate.Name == "Run");
        Assert.True(function.IsPendingAwaitRelay);
        Assert.DoesNotContain(CoroutineSyntaxAnalysis.EnumerateBodyStatements(function.Body!),
            statement => statement is ForeachStatement { IsAwait: true });
        Assert.DoesNotContain(CoroutineSyntaxAnalysis.EnumerateBodyExpressions(function.Body!),
            expression => expression is AwaitExpression);
    }

    [Fact]
    public void AsyncIteratorCreatesPullBasedAsyncEnumeratorAndDisposalMethods()
    {
        var project = CxProject.CreateDefaultApplicationProject();
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            namespace System;
            public interface IAsyncDisposable {
                System.Threading.Tasks.ValueTaskHandle DisposeAsync();
            }
            """));
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            namespace System.Collections;
            public interface IAsyncEnumerable<T> {
                IAsyncEnumerator<T> GetAsyncEnumerator(
                    System.Threading.Tasks.CancellationToken cancellationToken);
            }
            public interface IAsyncEnumerator<T> : System.IAsyncDisposable {
                T Current { const get; }
                System.Threading.Tasks.ValueTask<bool> MoveNextAsync();
            }
            """));
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            import System.Collections;
            import System.Threading.Tasks;
            namespace App;
            async IAsyncEnumerable<int> Values(int start) {
                yield return start;
                yield return start + 1;
            }
            """));

        new SemanticBinder().Bind(project);

        var appContext = project.CompilationContexts.Single(context =>
            context.DeclarationScope.Declarations.OfType<
                CxCompiler.Model.Types.FunctionDeclaration>()
                .Any(candidate => candidate.Name == "Values"));
        var function = appContext.DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(candidate => candidate.Name == "Values");
        Assert.True(function.IsIteratorFactory);
        var iterator = Assert.Single(appContext.DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.ClassDeclaration>(), declaration =>
                declaration.Name.StartsWith("__CxIterator_Values_", StringComparison.Ordinal));
        var methods = iterator.MemberDeclarations.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .ToDictionary(candidate => candidate.Name);
        Assert.Contains("GetAsyncEnumerator", methods.Keys);
        Assert.Contains("MoveNextAsync", methods.Keys);
        Assert.Contains("DisposeAsync", methods.Keys);
    }

    [Fact]
    public void AsyncIteratorMatchesTaskBasedAsyncEnumeratorContract()
    {
        var project = CxProject.CreateDefaultApplicationProject();
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            namespace System;
            public interface IAsyncDisposable {
                System.Threading.Tasks.TaskHandle DisposeAsync();
            }
            """));
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            namespace System.Collections;
            public interface IAsyncEnumerable<T> {
                IAsyncEnumerator<T> GetAsyncEnumerator(
                    System.Threading.Tasks.CancellationToken cancellationToken);
            }
            public interface IAsyncEnumerator<T> : System.IAsyncDisposable {
                T Current { const get; }
                System.Threading.Tasks.Task<bool> MoveNextAsync();
            }
            """));
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            import System.Collections;
            async IAsyncEnumerable<int> Values() { yield return 1; }
            async IAsyncEnumerable<int> Forward(IAsyncEnumerable<int> source) {
                await foreach (int value in source) { yield return value; }
            }
            """));

        new SemanticBinder().Bind(project);

        var iterator = Assert.Single(project.CompilationContexts
            .SelectMany(context => context.DeclarationScope.Declarations)
            .OfType<CxCompiler.Model.Types.ClassDeclaration>(), declaration =>
                declaration.Name.StartsWith("__CxIterator_Values_", StringComparison.Ordinal));
        var methods = iterator.MemberDeclarations.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .ToDictionary(candidate => candidate.Name);
        var moveReturnType = Assert.IsType<NamedType>(methods["MoveNextAsync"].ReturnType);
        var disposeReturnType = Assert.IsType<NamedType>(methods["DisposeAsync"].ReturnType);
        Assert.Equal("Task", moveReturnType.ResolvedTypeFullName.Parts[^1]);
        Assert.Equal("TaskHandle", disposeReturnType.ResolvedTypeFullName.Parts[^1]);

        var forwardIterator = Assert.Single(project.CompilationContexts
            .SelectMany(context => context.DeclarationScope.Declarations)
            .OfType<CxCompiler.Model.Types.ClassDeclaration>(), declaration =>
                declaration.Name.StartsWith("__CxIterator_Forward_", StringComparison.Ordinal));
        var moveNextCore = forwardIterator.MemberDeclarations.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "MoveNextCore");
        var onCompleted = CoroutineSyntaxAnalysis
            .EnumerateBodyExpressions(moveNextCore.Body!)
            .OfType<InvocationExpression>()
            .Where(invocation => invocation.Target is MemberAccessExpression
                { MemberName: "OnCompleted" })
            .ToArray();
        Assert.NotEmpty(onCompleted);
        Assert.All(onCompleted, invocation => Assert.IsType<MemberAccessExpression>(
            invocation.Target));
    }

    [Fact]
    public void AsyncIteratorUsesProjectProvidedAsyncSequenceContracts()
    {
        var project = CxProject.CreateDefaultApplicationProject();
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            namespace System;
            public interface IAsyncDisposable {
                System.Threading.Tasks.ValueTaskHandle DisposeAsync();
            }
            """));
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            namespace System.Collections;
            public interface IAsyncEnumerable<T> {
                IAsyncEnumerator<T> GetAsyncEnumerator(
                    System.Threading.Tasks.CancellationToken cancellationToken);
            }
            public interface IAsyncEnumerator<T> : System.IAsyncDisposable {
                T Current { const get; }
                System.Threading.Tasks.ValueTask<bool> MoveNextAsync();
            }
            """));
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            import System.Collections;
            async IAsyncEnumerable<int> Values() { yield return 1; }
            """));

        new SemanticBinder().Bind(project);

        var asyncContracts = project.CompilationContexts
            .SelectMany(context => context.DeclarationScope.Declarations
                .OfType<CxCompiler.Model.Types.ClassDeclaration>())
            .Where(declaration => declaration.FullName.ToString() is
                "System.IAsyncDisposable" or
                "System.Collections.IAsyncEnumerable" or
                "System.Collections.IAsyncEnumerator")
            .ToArray();
        Assert.Equal(3, asyncContracts.Length);
    }

    [Fact]
    public void AsyncIteratorCanAwaitEnumerationOfAnotherAsyncSequence()
    {
        var project = CxProject.CreateDefaultApplicationProject();
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            namespace System;
            public interface IAsyncDisposable {
                System.Threading.Tasks.ValueTaskHandle DisposeAsync();
            }
            """));
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            namespace System.Collections;
            public interface IAsyncEnumerable<T> {
                IAsyncEnumerator<T> GetAsyncEnumerator(
                    System.Threading.Tasks.CancellationToken cancellationToken);
            }
            public interface IAsyncEnumerator<T> : System.IAsyncDisposable {
                T Current { const get; }
                System.Threading.Tasks.ValueTask<bool> MoveNextAsync();
            }
            """));
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            import System.Collections;
            namespace App;
            async IAsyncEnumerable<int> Forward(IAsyncEnumerable<int> source) {
                await foreach (int value in source) {
                    await foreach (int nestedValue in source) {
                        yield return value + nestedValue;
                    }
                }
            }
            """));

        new SemanticBinder().Bind(project);

        var context = project.CompilationContexts.Single(candidate =>
            candidate.DeclarationScope.Declarations.OfType<
                CxCompiler.Model.Types.FunctionDeclaration>()
                .Any(function => function.Name == "Forward"));
        var iterator = Assert.Single(context.DeclarationScope.Declarations.OfType<
            CxCompiler.Model.Types.ClassDeclaration>(), declaration =>
                declaration.Name.StartsWith("__CxIterator_Forward_", StringComparison.Ordinal));
        var localCount = iterator.MemberDeclarations.Declarations
            .OfType<CxCompiler.Model.Types.FieldDeclaration>()
            .Count(field => field.Name.StartsWith("__cx_local_", StringComparison.Ordinal));
        Assert.True(localCount >= 5, $"Expected await foreach frame locals; found {localCount}.");
    }

    [Fact]
    public void AsyncIteratorLowersAwaitedTaskLocalIntoAResumableState()
    {
        var project = CxProject.CreateDefaultApplicationProject();
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            namespace System;
            public interface IAsyncDisposable {
                System.Threading.Tasks.ValueTaskHandle DisposeAsync();
            }
            """));
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            namespace System.Collections;
            public interface IAsyncEnumerable<T> {
                IAsyncEnumerator<T> GetAsyncEnumerator(
                    System.Threading.Tasks.CancellationToken cancellationToken);
            }
            public interface IAsyncEnumerator<T> : System.IAsyncDisposable {
                T Current { const get; }
                System.Threading.Tasks.ValueTask<bool> MoveNextAsync();
            }
            """));
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            import System.Collections;
            import System.Threading.Tasks;
            namespace App;
            async IAsyncEnumerable<int> Values(Task<int> pending) {
                int value = await pending;
                yield return value;
            }
            """));

        new SemanticBinder().Bind(project);

        var context = project.CompilationContexts.Single(candidate =>
            candidate.DeclarationScope.Declarations.OfType<
                CxCompiler.Model.Types.FunctionDeclaration>()
                .Any(function => function.Name == "Values"));
        var iterator = Assert.Single(context.DeclarationScope.Declarations.OfType<
            CxCompiler.Model.Types.ClassDeclaration>(), declaration =>
                declaration.Name.StartsWith("__CxIterator_Values_", StringComparison.Ordinal));
        var fields = iterator.MemberDeclarations.Declarations
            .OfType<CxCompiler.Model.Types.FieldDeclaration>()
            .Select(field => field.Name)
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains("__cx_async_move_source", fields);
        Assert.Contains("__cx_async_move_started", fields);
        Assert.DoesNotContain("__cx_async_move_task", fields);
        Assert.Contains("__cx_async_suspended", fields);
        Assert.Contains(fields, name => name.StartsWith("__cx_local_", StringComparison.Ordinal));
        Assert.Contains(iterator.MemberDeclarations.Declarations.OfType<
            CxCompiler.Model.Types.FunctionDeclaration>(), function =>
                function.Name == "ResumeAsyncMove");
    }

    [Fact]
    public void AsyncIteratorSharesOneCallbackDelegateAcrossCancellationAndCompletion()
    {
        var project = CxProject.CreateDefaultApplicationProject();
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            namespace System;
            public interface IAsyncDisposable {
                System.Threading.Tasks.ValueTaskHandle DisposeAsync();
            }
            """));
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            namespace System.Collections;
            public interface IAsyncEnumerable<T> {
                IAsyncEnumerator<T> GetAsyncEnumerator(
                    System.Threading.Tasks.CancellationToken cancellationToken);
            }
            public interface IAsyncEnumerator<T> : System.IAsyncDisposable {
                T Current { const get; }
                System.Threading.Tasks.ValueTask<bool> MoveNextAsync();
            }
            """));
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            import System.Collections;
            import System.Threading.Tasks;
            namespace App;
            async IAsyncEnumerable<int> Values(Task pending) {
                await pending;
                yield return 1;
            }
            """));

        new SemanticBinder().Bind(project);

        var iterator = project.CompilationContexts
            .SelectMany(context => context.DeclarationScope.Declarations)
            .OfType<CxCompiler.Model.Types.ClassDeclaration>()
            .Single(declaration => declaration.Name.StartsWith(
                "__CxIterator_Values_", StringComparison.Ordinal));
        var moveNextCore = iterator.MemberDeclarations.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "MoveNextCore");
        var invocations = CoroutineSyntaxAnalysis
            .EnumerateBodyExpressions(moveNextCore.Body!)
            .OfType<InvocationExpression>().ToArray();
        var registers = invocations.Where(invocation =>
            invocation.Target is MemberAccessExpression { MemberName: "Register" } &&
            invocation.Arguments.Count == 1).ToArray();
        var onCompletedCalls = invocations.Where(invocation =>
            invocation.Target is MemberAccessExpression { MemberName: "OnCompleted" } &&
            invocation.Arguments.Count == 1).ToArray();
        Assert.Equal(2, registers.Length);
        Assert.Equal(2, onCompletedCalls.Length);
        foreach (var register in registers)
        {
            var callbackName = Assert.IsType<IdentifierExpression>(
                Assert.Single(register.Arguments)).Identifier.ToString();
            var completion = Assert.Single(onCompletedCalls, invocation =>
                Assert.IsType<IdentifierExpression>(invocation.Arguments[0])
                    .Identifier.ToString() == callbackName);
            Assert.IsType<IdentifierExpression>(completion.Arguments[0]);
        }
        Assert.Equal(2, CoroutineSyntaxAnalysis
            .EnumerateBodyExpressions(moveNextCore.Body!).OfType<LambdaExpression>().Count());
    }

    [Fact]
    public void AsyncIteratorInfersAwaitedLocalTypeAcrossSuspension()
    {
        var project = CxProject.CreateDefaultApplicationProject();
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            namespace System;
            public interface IAsyncDisposable {
                System.Threading.Tasks.ValueTaskHandle DisposeAsync();
            }
            """));
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            namespace System.Collections;
            public interface IAsyncEnumerable<T> {
                IAsyncEnumerator<T> GetAsyncEnumerator(
                    System.Threading.Tasks.CancellationToken cancellationToken);
            }
            public interface IAsyncEnumerator<T> : System.IAsyncDisposable {
                T Current { const get; }
                System.Threading.Tasks.ValueTask<bool> MoveNextAsync();
            }
            """));
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            import System.Collections;
            import System.Threading.Tasks;
            namespace App;
            async IAsyncEnumerable<int> Values(Task<int> pending) {
                var value = await pending;
                yield return value;
            }
            """));

        new SemanticBinder().Bind(project);

        var context = project.CompilationContexts.Single(candidate =>
            candidate.DeclarationScope.Declarations.OfType<
                CxCompiler.Model.Types.FunctionDeclaration>()
                .Any(function => function.Name == "Values"));
        var iterator = Assert.Single(context.DeclarationScope.Declarations.OfType<
            CxCompiler.Model.Types.ClassDeclaration>(), declaration =>
                declaration.Name.StartsWith("__CxIterator_Values_", StringComparison.Ordinal));
        var fields = iterator.MemberDeclarations.Declarations
            .OfType<CxCompiler.Model.Types.FieldDeclaration>()
            .Select(field => field.Name)
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains(fields, name => name.StartsWith("__cx_local_", StringComparison.Ordinal));
        Assert.Contains(iterator.MemberDeclarations.Declarations.OfType<
            CxCompiler.Model.Types.FunctionDeclaration>(), function =>
                function.Name == "ResumeAsyncMove");
    }

    [Fact]
    public void AsyncIteratorLowersAwaitedYieldExpressionIntoResumableState()
    {
        var project = CxProject.CreateDefaultApplicationProject();
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            namespace System;
            public interface IAsyncDisposable {
                System.Threading.Tasks.ValueTaskHandle DisposeAsync();
            }
            """));
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            namespace System.Collections;
            public interface IAsyncEnumerable<T> {
                IAsyncEnumerator<T> GetAsyncEnumerator(
                    System.Threading.Tasks.CancellationToken cancellationToken);
            }
            public interface IAsyncEnumerator<T> : System.IAsyncDisposable {
                T Current { const get; }
                System.Threading.Tasks.ValueTask<bool> MoveNextAsync();
            }
            """));
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            import System.Collections;
            import System.Threading.Tasks;
            namespace App;
            async IAsyncEnumerable<int> Values(Task<int> pending) {
                yield return await pending;
            }
            """));

        new SemanticBinder().Bind(project);

        var context = project.CompilationContexts.Single(candidate =>
            candidate.DeclarationScope.Declarations.OfType<
                CxCompiler.Model.Types.FunctionDeclaration>()
                .Any(function => function.Name == "Values"));
        var iterator = Assert.Single(context.DeclarationScope.Declarations.OfType<
            CxCompiler.Model.Types.ClassDeclaration>(), declaration =>
                declaration.Name.StartsWith("__CxIterator_Values_", StringComparison.Ordinal));
        var fields = iterator.MemberDeclarations.Declarations
            .OfType<CxCompiler.Model.Types.FieldDeclaration>()
            .Select(field => field.Name)
            .ToHashSet(StringComparer.Ordinal);
        Assert.True(fields.Count(name => name.StartsWith(
            "__cx_local_", StringComparison.Ordinal)) >= 2);
        Assert.Contains(iterator.MemberDeclarations.Declarations.OfType<
            CxCompiler.Model.Types.FunctionDeclaration>(), function =>
                function.Name == "ResumeAsyncMove");
    }

    [Fact]
    public void AsyncIteratorRejectsAwaitedFinallyAroundYieldWithSpecificDiagnostic()
    {
        var project = CxProject.CreateDefaultApplicationProject();
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            namespace System;
            public interface IAsyncDisposable {
                System.Threading.Tasks.ValueTaskHandle DisposeAsync();
            }
            """));
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            namespace System.Collections;
            public interface IAsyncEnumerable<T> {
                IAsyncEnumerator<T> GetAsyncEnumerator(
                    System.Threading.Tasks.CancellationToken cancellationToken);
            }
            public interface IAsyncEnumerator<T> : System.IAsyncDisposable {
                T Current { const get; }
                System.Threading.Tasks.ValueTask<bool> MoveNextAsync();
            }
            """));
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            import System.Collections;
            import System.Threading.Tasks;
            namespace App;
            async IAsyncEnumerable<int> Values(Task inner, Task outer) {
                try {
                    try { yield return 1; }
                    finally { await inner; }
                } finally { await outer; }
            }
            """));

        var error = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(project));

        Assert.Contains(
            "Async iterator finally blocks that await while protecting a yield are not supported yet",
            error.Message);
    }

    [Fact]
    public void AsyncIteratorRejectsNestedAwaitWithSpecificDiagnostic()
    {
        var project = CxProject.CreateDefaultApplicationProject();
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            namespace System;
            public interface IAsyncDisposable {
                System.Threading.Tasks.ValueTaskHandle DisposeAsync();
            }
            """));
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            namespace System.Collections;
            public interface IAsyncEnumerable<T> {
                IAsyncEnumerator<T> GetAsyncEnumerator(
                    System.Threading.Tasks.CancellationToken cancellationToken);
            }
            public interface IAsyncEnumerator<T> : System.IAsyncDisposable {
                T Current { const get; }
                System.Threading.Tasks.ValueTask<bool> MoveNextAsync();
            }
            """));
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            import System.Collections;
            import System.Threading.Tasks;
            namespace App;
            async IAsyncEnumerable<int> Values(Task<int> pending) {
                int value = await pending + 1;
                yield return value;
            }
            """));

        var error = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(project));

        Assert.Contains("Async iterator await must be a standalone statement", error.Message);
    }

    [Fact]
    public void AsyncTaskAwaitBindsToTaskResultAndWrapsAsyncReturn()
    {
        var project = CreateProject("""
            import System.Threading.Tasks;
            namespace App;
            Task<int> Completed() { return new Task<int>(7); }
            async Task<int> Run() {
                return await Completed();
            }
            """);

        new SemanticBinder().Bind(project);

        var functions = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>().ToDictionary(function => function.Name);
        var run = functions["Run"];
        var guarded = Assert.IsType<TryStatement>(Assert.Single(run.Body!));
        var body = Assert.IsType<BlockStatement>(guarded.Body).Statements;
        var completedBranch = Assert.IsType<IfStatement>(body[1]);
        var completedReturn = Assert.IsType<ReturnStatement>(
            Assert.Single(Assert.IsType<BlockStatement>(completedBranch.ThenStatement).Statements));
        var completedTask = Assert.IsType<ObjectCreationExpression>(completedReturn.Expression);
        var awaitExpression = Assert.IsType<AwaitExpression>(
            Assert.Single(completedTask.Arguments));
        Assert.IsType<InvocationExpression>(awaitExpression.LoweredExpression);
    }

    [Fact]
    public void AsyncTaskDirectReturnAwaitBuildsPendingCompletionRelay()
    {
        var project = CreateProject("""
            import System.Threading.Tasks;
            namespace App;
            async Task<int> Relay(Task<int> pending) {
                return await pending;
            }
            """);

        new SemanticBinder().Bind(project);

        var relay = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Relay");
        var guarded = Assert.IsType<TryStatement>(Assert.Single(relay.Body!));
        var body = Assert.IsType<BlockStatement>(guarded.Body).Statements;
        Assert.IsType<LocalVariableDeclarationStatement>(body[0]);
        var completedBranch = Assert.IsType<IfStatement>(body[1]);
        var completedReturn = Assert.IsType<ReturnStatement>(
            Assert.Single(Assert.IsType<BlockStatement>(completedBranch.ThenStatement).Statements));
        var completedTask = Assert.IsType<ObjectCreationExpression>(completedReturn.Expression);
        var completedAwait = Assert.IsType<AwaitExpression>(Assert.Single(completedTask.Arguments));
        Assert.IsType<InvocationExpression>(completedAwait.LoweredExpression);
        Assert.IsType<LocalVariableDeclarationStatement>(body[2]);
        var callbackSource = Assert.IsType<LocalVariableDeclarationStatement>(body[3]);
        Assert.IsType<IdentifierExpression>(
            Assert.Single(callbackSource.Declarators).Initializer);
        var callbackCompletion = Assert.IsType<LocalVariableDeclarationStatement>(body[4]);
        Assert.IsType<IdentifierExpression>(
            Assert.Single(callbackCompletion.Declarators).Initializer);
        var forward = Assert.IsType<InvocationExpression>(
            Assert.IsType<ExpressionStatement>(body[5]).Expression);
        Assert.Equal("OnCompleted", Assert.IsType<MemberAccessExpression>(forward.Target).MemberName);
        Assert.Single(forward.Arguments);
        var pendingReturn = Assert.IsType<IdentifierExpression>(
            Assert.IsType<ReturnStatement>(body[6]).Expression);
        Assert.Contains("__cx_async_completion", pendingReturn.Identifier.ToString());
    }

    [Fact]
    public void AsyncTaskConditionalAwaitReturnSelectsOneBranchBeforeSuspending()
    {
        var project = CreateProject("""
            import System.Threading.Tasks;
            namespace App;
            async Task<int> Pick(Task<int> first, Task<int> second, bool chooseFirst) {
                if (chooseFirst) return await first;
                else return await second;
            }
            """);

        new SemanticBinder().Bind(project);

        var pick = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Pick");
        var guarded = Assert.IsType<TryStatement>(Assert.Single(pick.Body!));
        var body = Assert.IsType<BlockStatement>(guarded.Body).Statements;
        var selectedTask = Assert.IsType<LocalVariableDeclarationStatement>(body[0]);
        var selectedExpression = Assert.IsType<ConditionalExpression>(
            Assert.Single(selectedTask.Declarators).Initializer);
        Assert.IsType<IdentifierExpression>(selectedExpression.Condition);
        Assert.Equal("first", Assert.IsType<IdentifierExpression>(selectedExpression.WhenTrue)
            .Identifier.ToString());
        Assert.Equal("second", Assert.IsType<IdentifierExpression>(selectedExpression.WhenFalse)
            .Identifier.ToString());
        Assert.IsType<IfStatement>(body[1]);
        Assert.IsType<LocalVariableDeclarationStatement>(body[2]);
        Assert.IsType<LocalVariableDeclarationStatement>(body[3]);
        Assert.IsType<LocalVariableDeclarationStatement>(body[4]);
        Assert.IsType<ExpressionStatement>(body[5]);
        Assert.IsType<ReturnStatement>(body[6]);
    }

    [Fact]
    public void AsyncTaskConditionalBranchContinuationResumesInSelectedBranch()
    {
        var project = CreateProject("""
            import System.Threading.Tasks;
            namespace App;
            async Task<int> Pick(Task<int> first, Task<int> second, bool chooseFirst) {
                if (chooseFirst) {
                    var value = await (chooseFirst ? first : second);
                    return value + 10;
                } else {
                    var value = await second;
                    return value + 20;
                }
            }
            """);

        new SemanticBinder().Bind(project);

        var pick = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Pick");
        var guarded = Assert.IsType<TryStatement>(Assert.Single(pick.Body!));
        var body = Assert.IsType<BlockStatement>(guarded.Body).Statements;
        Assert.IsType<LocalVariableDeclarationStatement>(body[0]);
        var selectedTask = Assert.IsType<LocalVariableDeclarationStatement>(body[1]);
        Assert.IsType<ConditionalExpression>(
            Assert.Single(selectedTask.Declarators).Initializer);
        var completionGuard = Assert.IsType<IfStatement>(body[2]);
        Assert.Equal("IsCompleted", Assert.IsType<MemberAccessExpression>(
            completionGuard.Condition).MemberName);
        var pendingStatements = Assert.IsType<BlockStatement>(
            completionGuard.ElseStatement).Statements;
        Assert.Contains(pendingStatements, statement => statement is LocalVariableDeclarationStatement
        {
            Declarators: [{ Initializer: LambdaExpression }],
        });
    }

    [Fact]
    public void AsyncTaskConditionalBranchCallSourcesAreEvaluatedOnceBeforeCompletionCheck()
    {
        var project = CreateProject("""
            import System.Threading.Tasks;
            namespace App;
            Task<int> FirstSource(Task<int> source) { return source; }
            Task<int> SecondSource(Task<int> source) { return source; }
            async Task<int> Pick(
                Task<int> first, Task<int> second, bool chooseFirst) {
                if (chooseFirst) {
                    var value = await FirstSource(first);
                    return value + 10;
                } else {
                    var value = await SecondSource(second);
                    return value + 20;
                }
            }
            """);

        new SemanticBinder().Bind(project);

        var pick = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Pick");
        var guarded = Assert.IsType<TryStatement>(Assert.Single(pick.Body!));
        var body = Assert.IsType<BlockStatement>(guarded.Body).Statements;
        var selectedTask = Assert.IsType<LocalVariableDeclarationStatement>(body[1]);
        var selectedBranch = Assert.IsType<ConditionalExpression>(
            Assert.Single(selectedTask.Declarators).Initializer);
        Assert.IsType<InvocationExpression>(selectedBranch.WhenTrue);
        Assert.IsType<InvocationExpression>(selectedBranch.WhenFalse);

        var completionGuard = Assert.IsType<IfStatement>(body[2]);
        var completionCheck = Assert.IsType<MemberAccessExpression>(
            completionGuard.Condition);
        Assert.Equal("IsCompleted", completionCheck.MemberName);
        Assert.Equal(selectedTask.Declarators.Single().Name,
            Assert.IsType<IdentifierExpression>(completionCheck.Target)
                .Identifier.ToString());
        Assert.Contains(Assert.IsType<BlockStatement>(completionGuard.ThenStatement)
            .Statements, statement => statement is LocalVariableDeclarationStatement
            {
                Declarators: [{ Initializer: InvocationExpression
                {
                    Target: MemberAccessExpression { MemberName: "GetResult" },
                } }],
            });
        Assert.Contains(Assert.IsType<BlockStatement>(completionGuard.ElseStatement)
            .Statements, statement => statement is LocalVariableDeclarationStatement
            {
                Declarators: [{ Initializer: LambdaExpression }],
            });
    }

    [Fact]
    public void AsyncTaskConditionalBranchCallSourcesKeepFallbackWhenOverloaded()
    {
        var project = CreateProject("""
            import System.Threading.Tasks;
            namespace App;
            Task<int> FirstSource(Task<int> source) { return source; }
            Task<int> FirstSource(int value) { return new Task<int>(value); }
            Task<int> SecondSource(Task<int> source) { return source; }
            Task<int> SecondSource(int value) { return new Task<int>(value); }
            async Task<int> Pick(
                Task<int> first, Task<int> second, bool chooseFirst) {
                if (chooseFirst) {
                    var value = await FirstSource(first);
                    return value + 10;
                } else {
                    var value = await SecondSource(second);
                    return value + 20;
                }
            }
            """);

        new SemanticBinder().Bind(project);

        var pick = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Pick");
        var expressions = CoroutineSyntaxAnalysis
            .EnumerateBodyExpressions(pick.Body!).ToArray();
        Assert.DoesNotContain(CoroutineSyntaxAnalysis
            .EnumerateBodyStatements(pick.Body!), statement =>
                statement is IfStatement
                {
                    Condition: MemberAccessExpression { MemberName: "IsCompleted" },
                    ThenStatement: BlockStatement,
                });
        Assert.Contains(expressions.OfType<ConditionalExpression>(), branch =>
            branch.WhenTrue is InvocationExpression &&
            branch.WhenFalse is InvocationExpression);
    }

    [Fact]
    public void AsyncTaskConditionalBranchContinuationSupportsMultipleAwaits()
    {
        var project = CreateProject("""
            import System.Threading.Tasks;
            namespace App;
            async Task<int> Pick(
                Task<int> firstA, Task<int> firstB,
                Task<int> secondA, Task<int> secondB, bool chooseFirst) {
                if (chooseFirst) {
                    var left = await firstA;
                    var right = await firstB;
                    return left + right;
                } else {
                    var left = await secondA;
                    var right = await secondB;
                    return left - right;
                }
            }
            """);

        new SemanticBinder().Bind(project);

        var pick = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Pick");
        var guarded = Assert.IsType<TryStatement>(Assert.Single(pick.Body!));
        var body = Assert.IsType<BlockStatement>(guarded.Body).Statements;
        Assert.IsType<LocalVariableDeclarationStatement>(body[0]);
        var firstSelectedTask = Assert.IsType<LocalVariableDeclarationStatement>(body[1]);
        Assert.IsType<ConditionalExpression>(
            Assert.Single(firstSelectedTask.Declarators).Initializer);
        var completionGuard = Assert.IsType<IfStatement>(body[2]);
        var allCompleted = Assert.IsType<BinaryExpression>(completionGuard.Condition);
        Assert.Equal("&&", allCompleted.Operator);
        Assert.IsType<ConditionalExpression>(
            Assert.IsType<MemberAccessExpression>(allCompleted.Left).Target);
        Assert.IsType<ConditionalExpression>(
            Assert.IsType<MemberAccessExpression>(allCompleted.Right).Target);
        var inlineStatements = Assert.IsType<BlockStatement>(
            completionGuard.ThenStatement).Statements;
        Assert.Equal(2, inlineStatements.OfType<LocalVariableDeclarationStatement>()
            .Count(declaration => declaration.Declarators.Single().Initializer is
                InvocationExpression
                {
                    Target: MemberAccessExpression { MemberName: "GetResult" },
                }));
        Assert.Contains(inlineStatements, statement => statement is LocalVariableDeclarationStatement
        {
            Declarators: [{ Name: "__cx_async_branch_task_1", Initializer: ConditionalExpression }],
        });
        var pendingStatements = Assert.IsType<BlockStatement>(
            completionGuard.ElseStatement).Statements;
        Assert.Contains(pendingStatements, statement => statement is LocalVariableDeclarationStatement
        {
            Declarators: [{ Initializer: LambdaExpression }],
        });
    }

    [Fact]
    public void AsyncTaskConditionalBranchDefersLaterCallUntilEarlierAwaitCompletes()
    {
        var project = CreateProject("""
            import System.Threading.Tasks;
            namespace App;
            Task<int> FirstSource(Task<int> input) { return input; }
            Task<int> SecondSource(Task<int> input) { return input; }
            async Task<int> Pick(
                Task<int> firstA, Task<int> firstB,
                Task<int> secondA, Task<int> secondB, bool chooseFirst) {
                if (chooseFirst) {
                    var left = await firstA;
                    var right = await FirstSource(firstB);
                    return left + right;
                } else {
                    var left = await secondA;
                    var right = await SecondSource(secondB);
                    return left - right;
                }
            }
            """);

        new SemanticBinder().Bind(project);

        var pick = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Pick");
        var guarded = Assert.IsType<TryStatement>(Assert.Single(pick.Body!));
        var body = Assert.IsType<BlockStatement>(guarded.Body).Statements;
        Assert.IsType<ReturnStatement>(body[^1]);
        var sourceOrderedContinuation = CoroutineSyntaxAnalysis
            .EnumerateBodyExpressions(pick.Body!)
            .OfType<LambdaExpression>()
            .Select(lambda => CoroutineSyntaxAnalysis
                .EnumerateBodyStatements(lambda.StatementBody ?? []).ToArray())
            .First(statements =>
                statements.OfType<LocalVariableDeclarationStatement>()
                    .Any(declaration => declaration.Declarators.Any(declarator =>
                        declarator.Name == "__cx_async_branch_task_1" &&
                        declarator.Initializer is ConditionalExpression
                        {
                            WhenTrue: InvocationExpression,
                            WhenFalse: InvocationExpression,
                        })));
        var orderedStatements = sourceOrderedContinuation
            .Select((statement, index) => (statement, index)).ToArray();
        var firstGetResultIndex = Assert.Single(orderedStatements, item =>
            item.statement is LocalVariableDeclarationStatement declaration &&
            declaration.Declarators.Any(declarator =>
                declarator.Initializer is InvocationExpression
                {
                    Target: MemberAccessExpression { MemberName: "GetResult" },
                })).index;
        var laterTaskCallIndex = Assert.Single(orderedStatements, item =>
            item.statement is LocalVariableDeclarationStatement declaration &&
            declaration.Declarators.Any(declarator =>
                declarator.Name == "__cx_async_branch_task_1")).index;
        Assert.True(firstGetResultIndex < laterTaskCallIndex);
    }

    [Fact]
    public void AsyncTaskConditionalBranchContinuationSupportsFourOrderedAwaits()
    {
        var project = CreateProject("""
            import System.Threading.Tasks;
            namespace App;
            async Task<int> Pick(
                Task<int> firstA, Task<int> firstB, Task<int> firstC, Task<int> firstD,
                Task<int> secondA, Task<int> secondB, Task<int> secondC, Task<int> secondD,
                bool chooseFirst) {
                if (chooseFirst) {
                    var a = await firstA;
                    var b = await firstB;
                    var c = await firstC;
                    var d = await firstD;
                    return a + b + c + d;
                } else {
                    var a = await secondA;
                    var b = await secondB;
                    var c = await secondC;
                    var d = await secondD;
                    return a + b + c + d;
                }
            }
            """);

        new SemanticBinder().Bind(project);

        var pick = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Pick");
        var guarded = Assert.IsType<TryStatement>(Assert.Single(pick.Body!));
        var body = Assert.IsType<BlockStatement>(guarded.Body).Statements;
        var completionGuard = Assert.IsType<IfStatement>(body[2]);
        var completionChecks = CoroutineSyntaxAnalysis.EnumerateAllExpressions(
                completionGuard.Condition)
            .OfType<MemberAccessExpression>()
            .Where(access => access.MemberName == "IsCompleted").ToArray();
        Assert.Equal(4, completionChecks.Length);

        var inline = Assert.IsType<BlockStatement>(
            completionGuard.ThenStatement).Statements;
        var inlineResults = inline.OfType<LocalVariableDeclarationStatement>()
            .Where(declaration => declaration.Declarators.Single().Initializer is
                InvocationExpression
                {
                    Target: MemberAccessExpression { MemberName: "GetResult" },
                }).ToArray();
        Assert.Equal(4, inlineResults.Length);
        var inlineResultIndices = inline.Select((statement, index) =>
                (statement, index))
            .Where(item => item.statement is LocalVariableDeclarationStatement
            {
                Declarators: [{ Initializer: InvocationExpression
                {
                    Target: MemberAccessExpression { MemberName: "GetResult" },
                } }],
            })
            .Select(item => item.index).ToArray();
        var secondSelectedTaskIndex = Array.FindIndex(inline.ToArray(), statement =>
            statement is LocalVariableDeclarationStatement
            {
                Declarators: [{ Name: "__cx_async_branch_task_1",
                    Initializer: ConditionalExpression }],
            });
        var thirdSelectedTaskIndex = Array.FindIndex(inline.ToArray(), statement =>
            statement is LocalVariableDeclarationStatement
            {
                Declarators: [{ Name: "__cx_async_branch_task_2",
                    Initializer: ConditionalExpression }],
            });
        var fourthSelectedTaskIndex = Array.FindIndex(inline.ToArray(), statement =>
            statement is LocalVariableDeclarationStatement
            {
                Declarators: [{ Name: "__cx_async_branch_task_3",
                    Initializer: ConditionalExpression }],
            });
        Assert.Equal(4, inlineResultIndices.Length);
        Assert.True(inlineResultIndices[0] < secondSelectedTaskIndex);
        Assert.True(secondSelectedTaskIndex < inlineResultIndices[1]);
        Assert.True(inlineResultIndices[1] < thirdSelectedTaskIndex);
        Assert.True(thirdSelectedTaskIndex < inlineResultIndices[2]);
        Assert.True(inlineResultIndices[2] < fourthSelectedTaskIndex);
        Assert.True(fourthSelectedTaskIndex < inlineResultIndices[3]);
        Assert.Contains(Assert.IsType<BlockStatement>(
            completionGuard.ElseStatement).Statements, statement =>
                statement is LocalVariableDeclarationStatement
                {
                    Declarators: [{ Initializer: LambdaExpression }],
                });
    }

    [Fact]
    public void AsyncTaskAwaitWhileWithNestedLoopIsRejected()
    {
        var project = CreateProject("""
            import System.Threading.Tasks;
            namespace App;
            async Task<int> Count(Task<int> pending, int limit) {
                int count = 0;
                while (count < limit) {
                    var value = await pending;
                    count += value;
                    while (false) count++;
                }
                return count;
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(project));

        Assert.Contains("loop-await shape is not supported", error.Message);
    }

    [Fact]
    public void AsyncTaskAwaitWhileConditionResumesAfterEachIteration()
    {
        var project = CreateProject("""
            import System.Threading.Tasks;
            namespace App;
            async Task<int> Count(Task<int> pending, int limit) {
                int count = 0;
                while (count < limit) {
                    var value = await pending;
                    count += value;
                }
                return count;
            }
            """);

        new SemanticBinder().Bind(project);

        var count = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Count");
        var guarded = Assert.IsType<TryStatement>(Assert.Single(count.Body!));
        var body = Assert.IsType<BlockStatement>(guarded.Body).Statements;
        Assert.Contains(body, statement => statement is WhileStatement);
        Assert.Contains(CoroutineSyntaxAnalysis.EnumerateStatements(body), statement => statement is ExpressionStatement
        {
            Expression: AssignmentExpression { Value: LambdaExpression },
        });
        Assert.Contains(CoroutineSyntaxAnalysis.EnumerateStatements(body), statement =>
            statement is IfStatement
            {
                Condition: MemberAccessExpression { MemberName: "IsCompleted" },
                ElseStatement: BlockStatement,
            });
    }

    [Fact]
    public void AsyncTaskValueTaskLoopDefersEscapedStateUntilPendingIteration()
    {
        var project = CreateProject("""
            import System.Threading.Tasks;
            namespace App;
            async Task<int> Count(ValueTask<int> pending, int limit) {
                int total = 0;
                for (int index = 0; index < limit; index++) {
                    int value = await pending;
                    total += value;
                }
                return total;
            }
            """);

        new SemanticBinder().Bind(project);

        var count = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Count");
        var guarded = Assert.IsType<TryStatement>(Assert.Single(count.Body!));
        var body = Assert.IsType<BlockStatement>(guarded.Body).Statements;
        var loop = Assert.IsType<ForStatement>(body[1]);
        var awaitBody = Assert.IsType<BlockStatement>(loop.Body).Statements;
        Assert.IsType<LocalVariableDeclarationStatement>(awaitBody[0]);
        var completionCheck = Assert.IsType<IfStatement>(awaitBody[1]);
        Assert.Equal("IsCompleted", Assert.IsType<MemberAccessExpression>(
            completionCheck.Condition).MemberName);
        Assert.IsType<BlockStatement>(completionCheck.ThenStatement);
        var pendingBranch = Assert.IsType<BlockStatement>(completionCheck.ElseStatement);
        Assert.Contains(CoroutineSyntaxAnalysis.EnumerateStatements(pendingBranch.Statements),
            statement => statement is LocalVariableDeclarationStatement
            {
                Declarators: [{ Initializer: ObjectCreationExpression }],
            });
        Assert.Contains(CoroutineSyntaxAnalysis.EnumerateStatements(pendingBranch.Statements),
            statement => statement is ExpressionStatement
            {
                Expression: AssignmentExpression { Value: LambdaExpression },
            });
        var deferredAliases = CoroutineSyntaxAnalysis.EnumerateStatements(
                pendingBranch.Statements)
            .OfType<LocalVariableDeclarationStatement>()
            .SelectMany(declaration => declaration.Declarators)
            .Select(declarator => declarator.Name)
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains("__cx_async_saved_pending", deferredAliases);
        Assert.Contains("__cx_async_saved_limit", deferredAliases);
        Assert.Contains("__cx_async_saved_total", deferredAliases);
        Assert.Contains("__cx_async_saved_index", deferredAliases);
        var step = Assert.Single(CoroutineSyntaxAnalysis
            .EnumerateBodyExpressions(pendingBranch.Statements)
            .OfType<LambdaExpression>(), lambda =>
            lambda.GeneratedCName?.EndsWith("_step", StringComparison.Ordinal) == true);
        var resumedAwaitCheck = Assert.Single(CoroutineSyntaxAnalysis
            .EnumerateStatements(step.StatementBody!)
            .OfType<IfStatement>(), conditional =>
            conditional.Condition is MemberAccessExpression
            {
                MemberName: "IsCompleted",
            } && conditional.ElseStatement is BlockStatement);
        Assert.IsType<BlockStatement>(resumedAwaitCheck.ThenStatement);
        Assert.Contains(CoroutineSyntaxAnalysis.EnumerateStatements(
                [resumedAwaitCheck.ElseStatement!]),
            statement => statement is LocalVariableDeclarationStatement
            {
                Declarators: [{ Initializer: LambdaExpression }],
            });
        Assert.DoesNotContain(body, statement => statement is LocalVariableDeclarationStatement
        {
            Declarators: [{ Initializer: ObjectCreationExpression }],
        });
    }

    [Fact]
    public void AsyncTaskAwaitWhileLowersBreakContinueAndEarlyReturn()
    {
        var project = CreateProject("""
            import System.Threading.Tasks;
            namespace App;
            async Task<int> Control(Task<int> pending, int limit) {
                int iterations = 0;
                int result = 0;
                while (iterations < limit) {
                    var value = await pending;
                    iterations++;
                    if (value == 99) break;
                    if (value == 0) continue;
                    if (value == 42) return value;
                    result = value;
                }
                return result;
            }
            """);

        new SemanticBinder().Bind(project);

        var control = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Control");
        var guarded = Assert.IsType<TryStatement>(Assert.Single(control.Body!));
        var body = Assert.IsType<BlockStatement>(guarded.Body).Statements;
        Assert.Contains(body, statement => statement is WhileStatement);
        Assert.Contains(CoroutineSyntaxAnalysis.EnumerateStatements(body), statement => statement is ExpressionStatement
        {
            Expression: AssignmentExpression { Value: LambdaExpression },
        });
    }

    [Fact]
    public void AsyncTaskStandaloneAwaitWhileResumesAndTrampolines()
    {
        var project = CreateProject("""
            import System.Threading.Tasks;
            namespace App;
            async Task Run(Task pending, int limit) {
                int iterations = 0;
                while (iterations < limit) {
                    await pending;
                    iterations++;
                }
                return;
            }
            """);

        new SemanticBinder().Bind(project);

        var run = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Run");
        var guarded = Assert.IsType<TryStatement>(Assert.Single(run.Body!));
        var body = Assert.IsType<BlockStatement>(guarded.Body).Statements;
        Assert.Contains(CoroutineSyntaxAnalysis.EnumerateStatements(body),
            statement => statement is WhileStatement);
        Assert.Contains(CoroutineSyntaxAnalysis.EnumerateStatements(body),
            statement => statement is ExpressionStatement
            {
                Expression: AssignmentExpression { Value: LambdaExpression },
            });
    }

    [Fact]
    public void AsyncTaskAwaitDoWhilePreservesBodyFirstSemantics()
    {
        var project = CreateProject("""
            import System.Threading.Tasks;
            namespace App;
            async Task<int> Count(Task<int> pending, int limit) {
                int count = 0;
                do {
                    var value = await pending;
                    count += value;
                } while (count < limit);
                return count;
            }
            """);

        new SemanticBinder().Bind(project);

        var count = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Count");
        var guarded = Assert.IsType<TryStatement>(Assert.Single(count.Body!));
        var body = Assert.IsType<BlockStatement>(guarded.Body).Statements;
        Assert.Contains(body, statement => statement is DoWhileStatement);
        Assert.Contains(CoroutineSyntaxAnalysis.EnumerateStatements(body), statement => statement is ExpressionStatement
        {
            Expression: AssignmentExpression { Value: LambdaExpression },
        });
    }

    [Fact]
    public void AsyncTaskStandaloneAwaitDoWhileAndForResumeInOrder()
    {
        var project = CreateProject("""
            import System.Threading.Tasks;
            namespace App;
            async Task RunDo(Task pending, int limit) {
                int iterations = 0;
                do {
                    await pending;
                    iterations++;
                } while (iterations < limit);
                return;
            }
            async Task RunFor(Task pending, int limit) {
                for (int index = 0; index < limit; index++) {
                    await pending;
                }
            }
            """);

        new SemanticBinder().Bind(project);

        var functions = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Where(function => function.Name is "RunDo" or "RunFor")
            .ToDictionary(function => function.Name);
        foreach (var function in functions.Values)
        {
            var guarded = Assert.IsType<TryStatement>(Assert.Single(function.Body!));
            var body = Assert.IsType<BlockStatement>(guarded.Body).Statements;
            Assert.Contains(CoroutineSyntaxAnalysis.EnumerateStatements(body),
                statement => statement is DoWhileStatement or ForStatement);
            Assert.Contains(CoroutineSyntaxAnalysis.EnumerateStatements(body),
                statement => statement is ExpressionStatement
                {
                    Expression: AssignmentExpression { Value: LambdaExpression },
                });
        }
    }

    [Fact]
    public void AsyncTaskAwaitForPreservesInitializationAndIteratorOrder()
    {
        var project = CreateProject("""
            import System.Threading.Tasks;
            namespace App;
            async Task<int> Count(Task<int> pending, int limit) {
                int count = 0;
                for (int index = 0; index < limit; index++) {
                    var value = await pending;
                    count += value;
                }
                return count;
            }
            """);

        new SemanticBinder().Bind(project);

        var count = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Count");
        var guarded = Assert.IsType<TryStatement>(Assert.Single(count.Body!));
        var body = Assert.IsType<BlockStatement>(guarded.Body).Statements;
        Assert.Contains(body, statement => statement is ForStatement);
        Assert.Contains(body, statement => statement is ForStatement
        {
            DeclarationInitializer: LocalVariableDeclarationStatement
            {
                Declarators: [{ Name: "index" }],
            },
        });
    }

    [Fact]
    public void AsyncTaskSwitchSelectsOneAwaitedCaseAndDefault()
    {
        var project = CreateProject("""
            import System.Threading.Tasks;
            namespace App;
            async Task<int> Pick(Task<int> first, Task<int> fallback, int selector) {
                switch (selector) {
                    case 1: {
                        var value = await first;
                        return value;
                    }
                    default: {
                        var value = await fallback;
                        return value;
                    }
                }
            }
            """);

        new SemanticBinder().Bind(project);

        var pick = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Pick");
        Assert.DoesNotContain(CoroutineSyntaxAnalysis.EnumerateStatements(pick.Body!),
            statement => statement is SwitchStatement);
        Assert.Contains(CoroutineSyntaxAnalysis.EnumerateBodyExpressions(pick.Body!),
            expression => expression is ConditionalExpression);
    }

    [Fact]
    public void AsyncTaskSwitchRejectsMultipleAwaitsInOneCase()
    {
        var project = CreateProject("""
            import System.Threading.Tasks;
            namespace App;
            async Task<int> Pick(Task<int> first, Task<int> second, int selector) {
                switch (selector) {
                    case 1: {
                        var value = await first;
                        await second;
                        return value;
                    }
                    default: return 0;
                }
            }
            """);

        var error = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(project));

        Assert.Contains("switch-await shape is not supported", error.Message);
    }

    [Fact]
    public void AsyncTaskSwitchResumesSynchronousCaseContinuationAndBreak()
    {
        var project = CreateProject("""
            import System.Threading.Tasks;
            namespace App;
            async Task<int> Pick(Task<int> first, Task<int> fallback, int selector) {
                int result = 0;
                switch (selector) {
                    case 1: {
                        var value = await first;
                        result = value + 1;
                        break;
                    }
                    default: {
                        var value = await fallback;
                        result = value + 2;
                        break;
                    }
                }
                return result;
            }
            """);

        new SemanticBinder().Bind(project);

        var pick = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Pick");
        Assert.Contains(CoroutineSyntaxAnalysis.EnumerateBodyExpressions(pick.Body!),
            expression => expression is ConditionalExpression);
    }

    [Fact]
    public void AsyncVoidTaskSwitchResumesSynchronousCaseContinuationAndBreak()
    {
        var project = CreateProject("""
            import System.Threading.Tasks;
            namespace App;
            async Task Pick(Task first, Task fallback, int selector) {
                switch (selector) {
                    case 1: {
                        await first;
                        Touch();
                        break;
                    }
                    default: {
                        await fallback;
                        Touch();
                        break;
                    }
                }
            }
            void Touch() { }
            """);

        new SemanticBinder().Bind(project);

        var pick = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Pick");
        Assert.DoesNotContain(CoroutineSyntaxAnalysis.EnumerateBodyExpressions(pick.Body!),
            expression => expression is AwaitExpression);
    }

    [Fact]
    public void AsyncTaskConditionalVoidAwaitSelectsOneBranchBeforeSuspending()
    {
        var project = CreateProject("""
            import System.Threading.Tasks;
            namespace App;
            async Task Pick(Task first, Task second, bool chooseFirst) {
                if (chooseFirst) await first;
                else await second;
            }
            """);

        new SemanticBinder().Bind(project);

        var pick = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Pick");
        var guarded = Assert.IsType<TryStatement>(Assert.Single(pick.Body!));
        var body = Assert.IsType<BlockStatement>(guarded.Body).Statements;
        var selectedTask = Assert.IsType<LocalVariableDeclarationStatement>(body[0]);
        var selectedExpression = Assert.IsType<ConditionalExpression>(
            Assert.Single(selectedTask.Declarators).Initializer);
        Assert.IsType<IdentifierExpression>(selectedExpression.Condition);
        Assert.Equal("first", Assert.IsType<IdentifierExpression>(selectedExpression.WhenTrue)
            .Identifier.ToString());
        Assert.Equal("second", Assert.IsType<IdentifierExpression>(selectedExpression.WhenFalse)
            .Identifier.ToString());
        Assert.IsType<IfStatement>(body[1]);
        Assert.IsType<LocalVariableDeclarationStatement>(body[2]);
        Assert.IsType<ExpressionStatement>(body[3]);
        Assert.IsType<ReturnStatement>(body[4]);
    }

    [Fact]
    public void AsyncTaskExplicitTaskLocalChecksInlineBeforeRelayAllocation()
    {
        var project = CreateProject("""
            import System.Threading.Tasks;
            namespace App;
            async Task<int> Relay(Task<int> input) {
                Task<int> selected = input;
                var result = await selected;
                return result + 1;
            }
            """);

        new SemanticBinder().Bind(project);

        var relay = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Relay");
        var guarded = Assert.IsType<TryStatement>(Assert.Single(relay.Body!));
        var loweredStatements = Assert.IsType<BlockStatement>(guarded.Body).Statements;
        Assert.Equal(2, loweredStatements.Count);
        Assert.IsType<LocalVariableDeclarationStatement>(loweredStatements[0]);
        var allInline = Assert.IsType<IfStatement>(loweredStatements[1]);
        Assert.Equal("IsCompleted", Assert.IsType<MemberAccessExpression>(
            allInline.Condition).MemberName);
        Assert.IsType<BlockStatement>(allInline.ThenStatement);
        var pending = Assert.IsType<BlockStatement>(allInline.ElseStatement).Statements;
        Assert.Contains(pending, statement =>
            statement is LocalVariableDeclarationStatement
            {
                Declarators: [{ Initializer: ObjectCreationExpression }],
            });
    }

    [Fact]
    public void AsyncTaskExplicitTaskLocalCallChecksItsMaterializedValue()
    {
        var project = CreateProject("""
            import System.Threading.Tasks;
            namespace App;
            Task<int> Source(Task<int> input) { return input; }
            async Task<int> Relay(Task<int> input) {
                System.Threading.Tasks.Task<int> selected = Source(input);
                var result = await selected;
                return result + 1;
            }
            """);

        new SemanticBinder().Bind(project);

        var relay = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Relay");
        var guarded = Assert.IsType<TryStatement>(Assert.Single(relay.Body!));
        var body = Assert.IsType<BlockStatement>(guarded.Body).Statements;
        Assert.Equal(2, body.Count);
        Assert.IsType<LocalVariableDeclarationStatement>(body[0]);
        var selectedLocal = Assert.IsType<LocalVariableDeclarationStatement>(body[0]);
        Assert.IsType<InvocationExpression>(selectedLocal.Declarators.Single().Initializer);
        var allInline = Assert.IsType<IfStatement>(body[1]);
        Assert.Equal("IsCompleted", Assert.IsType<MemberAccessExpression>(
            allInline.Condition).MemberName);
        Assert.Equal("selected", Assert.IsType<IdentifierExpression>(
            Assert.IsType<MemberAccessExpression>(allInline.Condition).Target)
                .Identifier.ToString());
    }

    [Fact]
    public void AsyncTaskExplicitTaskLocalCapturesReferencePrefixOnlyWhenPending()
    {
        var project = CreateProject("""
            import System.Threading.Tasks;
            namespace App;
            public class Prefix { public int Number; }
            Task<int> Source(Task<int> input) { return input; }
            async Task<int> Relay(
                Task<int> input, Prefix inputPrefix) {
                Prefix prefix = inputPrefix;
                System.Threading.Tasks.Task<int> selected = Source(input);
                var result = await selected;
                if (prefix.Number > 0 && result > 0) {
                    return prefix.Number + result;
                } else {
                    return prefix.Number - result;
                }
            }
            """);

        new SemanticBinder().Bind(project);

        var relay = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Relay");
        var guarded = Assert.IsType<TryStatement>(Assert.Single(relay.Body!));
        var body = Assert.IsType<BlockStatement>(guarded.Body).Statements;
        var selectedLocal = Assert.IsType<LocalVariableDeclarationStatement>(body[1]);
        Assert.IsType<InvocationExpression>(
            selectedLocal.Declarators.Single().Initializer);
        var allInline = Assert.IsType<IfStatement>(body[^1]);
        var inlineBody = Assert.IsType<BlockStatement>(allInline.ThenStatement);
        Assert.DoesNotContain(CoroutineSyntaxAnalysis.EnumerateStatements(
                inlineBody.Statements), statement =>
            statement is LocalVariableDeclarationStatement declaration &&
            declaration.Declarators.Any(declarator => declarator.Name ==
                "__cx_async_callback_prefix_prefix"));

        var pendingBody = Assert.IsType<BlockStatement>(allInline.ElseStatement)
            .Statements;
        Assert.Contains(pendingBody, statement =>
            statement is LocalVariableDeclarationStatement declaration &&
            declaration.Declarators.Any(declarator => declarator.Name ==
                "__cx_async_callback_prefix_prefix"));
        var callback = Assert.Single(CoroutineSyntaxAnalysis
            .EnumerateBodyExpressions(pendingBody).OfType<LambdaExpression>());
        var callbackTry = Assert.IsType<TryStatement>(
            Assert.Single(callback.StatementBody!));
        var callbackStatements = Assert.IsType<BlockStatement>(callbackTry.Body)
            .Statements;
        var callbackConditional = Assert.Single(callbackStatements
            .OfType<IfStatement>());
        Assert.Contains(CoroutineSyntaxAnalysis.EnumerateAllExpressions(
                callbackConditional.Condition).OfType<IdentifierExpression>(),
            identifier => identifier.Identifier.ToString() ==
                "__cx_async_callback_prefix_prefix");
        Assert.Contains(CoroutineSyntaxAnalysis.EnumerateBodyExpressions(
                [callbackConditional.ThenStatement]).OfType<IdentifierExpression>(),
            identifier => identifier.Identifier.ToString() ==
                "__cx_async_callback_prefix_prefix");
        Assert.Contains(CoroutineSyntaxAnalysis.EnumerateBodyExpressions(
                [callbackConditional.ElseStatement!]).OfType<IdentifierExpression>(),
            identifier => identifier.Identifier.ToString() ==
                "__cx_async_callback_prefix_prefix");
    }

    [Fact]
    public void AsyncTaskInferredTaskLocalCapturesReferencePrefixOnlyWhenPending()
    {
        var project = CreateProject("""
            import System.Threading.Tasks;
            namespace App;
            public class Prefix { public int Number; }
            async Task<int> Relay(Task<int> input, Prefix inputPrefix) {
                Prefix prefix = inputPrefix;
                var selected = input;
                var result = await selected;
                return prefix.Number + result;
            }
            """);

        new SemanticBinder().Bind(project);

        var relay = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Relay");
        var guarded = Assert.IsType<TryStatement>(Assert.Single(relay.Body!));
        var body = Assert.IsType<BlockStatement>(guarded.Body).Statements;
        Assert.IsType<LocalVariableDeclarationStatement>(body[0]);
        Assert.IsType<LocalVariableDeclarationStatement>(body[1]);
        var readiness = Assert.Single(body.OfType<IfStatement>());
        var readinessTarget = Assert.IsType<MemberAccessExpression>(readiness.Condition);
        Assert.Equal("IsCompleted", readinessTarget.MemberName);
        Assert.Equal("selected", Assert.IsType<IdentifierExpression>(
            readinessTarget.Target).Identifier.ToString());
        var inlineStatements = Assert.IsType<BlockStatement>(readiness.ThenStatement)
            .Statements;
        Assert.DoesNotContain(CoroutineSyntaxAnalysis.EnumerateStatements(
                inlineStatements), statement =>
            statement is LocalVariableDeclarationStatement declaration &&
            declaration.Declarators.Any(declarator => declarator.Name ==
                "__cx_async_callback_prefix_prefix"));

        var pendingStatements = Assert.IsType<BlockStatement>(readiness.ElseStatement)
            .Statements;
        Assert.Contains(pendingStatements, statement =>
            statement is LocalVariableDeclarationStatement declaration &&
            declaration.Declarators.Any(declarator => declarator.Name ==
                "__cx_async_callback_prefix_prefix"));
    }

    [Fact]
    public void AsyncTaskInferredTaskCallLocalCapturesReferencePrefixOnlyWhenPending()
    {
        var project = CreateProject("""
            import System.Threading.Tasks;
            namespace App;
            public class Prefix { public int Number; }
            Task<int> Source(Task<int> input) { return input; }
            async Task<int> Relay(Task<int> input, Prefix inputPrefix) {
                Prefix prefix = inputPrefix;
                var selected = Source(input);
                var result = await selected;
                return prefix.Number + result;
            }
            """);

        new SemanticBinder().Bind(project);

        var relay = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Relay");
        var guarded = Assert.IsType<TryStatement>(Assert.Single(relay.Body!));
        var body = Assert.IsType<BlockStatement>(guarded.Body).Statements;
        Assert.Equal(2, body.Take(2)
            .OfType<LocalVariableDeclarationStatement>().Count());
        var selected = Assert.IsType<LocalVariableDeclarationStatement>(body[1]);
        Assert.IsType<InvocationExpression>(selected.Declarators.Single().Initializer);
        var readiness = Assert.Single(body.OfType<IfStatement>());
        var readinessTarget = Assert.IsType<MemberAccessExpression>(readiness.Condition);
        Assert.Equal("IsCompleted", readinessTarget.MemberName);
        Assert.Equal("selected", Assert.IsType<IdentifierExpression>(
            readinessTarget.Target).Identifier.ToString());
        var pendingStatements = Assert.IsType<BlockStatement>(readiness.ElseStatement)
            .Statements;
        Assert.Contains(pendingStatements, statement =>
            statement is LocalVariableDeclarationStatement declaration &&
            declaration.Declarators.Any(declarator => declarator.Name ==
                "__cx_async_callback_prefix_prefix"));
    }

    [Fact]
    public void AsyncTaskDirectLocalAfterFirstAwaitChecksEachTaskBeforeRelayState()
    {
        var project = CreateProject("""
            import System.Threading.Tasks;
            namespace App;
            async Task<int> Relay(Task<int> first, Task<int> second) {
                var left = await first;
                Task<int> selected = second;
                var right = await selected;
                return left + right;
            }
            """);

        new SemanticBinder().Bind(project);

        var relay = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Relay");
        var guarded = Assert.IsType<TryStatement>(Assert.Single(relay.Body!));
        var body = Assert.IsType<BlockStatement>(guarded.Body).Statements;
        var firstReadiness = Assert.IsType<IfStatement>(body[0]);
        Assert.Equal("IsCompleted", Assert.IsType<MemberAccessExpression>(
            firstReadiness.Condition).MemberName);

        var firstInline = Assert.IsType<BlockStatement>(firstReadiness.ThenStatement)
            .Statements;
        var selected = Assert.IsType<LocalVariableDeclarationStatement>(firstInline[1]);
        Assert.Equal("selected", selected.Declarators.Single().Name);
        Assert.Equal("second", Assert.IsType<IdentifierExpression>(
            selected.Declarators.Single().Initializer).Identifier.ToString());
        var secondReadiness = Assert.IsType<IfStatement>(firstInline[2]);
        Assert.Equal("IsCompleted", Assert.IsType<MemberAccessExpression>(
            secondReadiness.Condition).MemberName);
        Assert.DoesNotContain(body, statement =>
            statement is LocalVariableDeclarationStatement declaration &&
            declaration.Declarators.Any(declarator => declarator.Name ==
                "__cx_async_completion"));
        Assert.Contains(CoroutineSyntaxAnalysis.EnumerateStatements(body), statement =>
            statement is LocalVariableDeclarationStatement declaration &&
            declaration.Declarators.Any(declarator => declarator.Name ==
                "__cx_async_completion"));
    }

    [Fact]
    public void AsyncTaskCallFirstSourceAndLaterDirectLocalChecksReadinessInline()
    {
        var project = CreateProject("""
            import System.Threading.Tasks;
            namespace App;
            Task<int> First(Task<int> source) { return source; }
            async Task<int> Relay(Task<int> first, Task<int> second) {
                var left = await First(first);
                Task<int> selected = second;
                var right = await selected;
                return left + right;
            }
            """);

        new SemanticBinder().Bind(project);

        var relay = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Relay");
        var guarded = Assert.IsType<TryStatement>(Assert.Single(relay.Body!));
        var body = Assert.IsType<BlockStatement>(guarded.Body).Statements;
        var inlineBranch = Assert.IsType<BlockStatement>(body[0]).Statements;
        var firstSource = Assert.IsType<LocalVariableDeclarationStatement>(inlineBranch[0]);
        Assert.IsType<InvocationExpression>(firstSource.Declarators.Single().Initializer);
        var firstReadiness = Assert.IsType<IfStatement>(inlineBranch[1]);
        Assert.Equal("IsCompleted", Assert.IsType<MemberAccessExpression>(
            firstReadiness.Condition).MemberName);
        var firstInline = Assert.IsType<BlockStatement>(firstReadiness.ThenStatement)
            .Statements;
        var laterSource = Assert.IsType<LocalVariableDeclarationStatement>(firstInline[1]);
        Assert.Equal("selected", laterSource.Declarators.Single().Name);
        Assert.Equal("second", Assert.IsType<IdentifierExpression>(
            laterSource.Declarators.Single().Initializer).Identifier.ToString());
        var secondReadiness = Assert.IsType<IfStatement>(firstInline[2]);
        Assert.Equal("IsCompleted", Assert.IsType<MemberAccessExpression>(
            secondReadiness.Condition).MemberName);
        Assert.DoesNotContain(body, statement =>
            statement is LocalVariableDeclarationStatement declaration &&
            declaration.Declarators.Any(declarator => declarator.Name ==
                "__cx_async_completion"));
    }

    [Fact]
    public void AsyncTaskThreeInferredAndExplicitLaterLocalsCheckEachSourceInline()
    {
        var project = CreateProject("""
            import System.Threading.Tasks;
            namespace App;
            async Task<int> Relay(
                Task<int> first, Task<int> second, Task<int> third) {
                var left = await first;
                var selectedSecond = second;
                var middle = await selectedSecond;
                Task<int> selectedThird = third;
                var right = await selectedThird;
                return left + middle + right;
            }
            """);

        new SemanticBinder().Bind(project);

        var relay = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Relay");
        var guarded = Assert.IsType<TryStatement>(Assert.Single(relay.Body!));
        var body = Assert.IsType<BlockStatement>(guarded.Body).Statements;
        Assert.IsType<IfStatement>(body[0]);
        var firstReady = Assert.IsType<IfStatement>(body[0]);
        var firstInline = Assert.IsType<BlockStatement>(firstReady.ThenStatement).Statements;
        var selectedSecond = Assert.IsType<LocalVariableDeclarationStatement>(firstInline[1]);
        Assert.IsType<AutoType>(selectedSecond.DeclaredType);
        Assert.Equal("selectedSecond", selectedSecond.Declarators.Single().Name);
        var secondReady = Assert.IsType<IfStatement>(firstInline[2]);
        var secondInline = Assert.IsType<BlockStatement>(secondReady.ThenStatement).Statements;
        Assert.Equal("selectedThird", Assert.IsType<LocalVariableDeclarationStatement>(
            secondInline[1]).Declarators.Single().Name);
        var thirdReady = Assert.IsType<IfStatement>(secondInline[2]);
        Assert.DoesNotContain(body, statement =>
            statement is LocalVariableDeclarationStatement declaration &&
            declaration.Declarators.Any(declarator => declarator.Name ==
                "__cx_async_completion"));
        Assert.Contains(CoroutineSyntaxAnalysis.EnumerateStatements(body), statement =>
            statement is IfStatement conditional &&
            conditional.ElseStatement is BlockStatement pending &&
            pending.Statements.Any(nested =>
                nested is LocalVariableDeclarationStatement declaration &&
                declaration.Declarators.Any(declarator => declarator.Name ==
                    "__cx_async_completion")));
    }

    [Fact]
    public void AsyncTaskThreeAwaitMixedInferredCallSourceChecksEachSourceInline()
    {
        var project = CreateProject("""
            import System.Threading.Tasks;
            namespace App;
            Task<int> Later(int value, Task<int> source) { return source; }
            async Task<int> Relay(
                Task<int> first, Task<int> second, Task<int> third) {
                var left = await first;
                Task<int> selectedSecond = second;
                var middle = await selectedSecond;
                var selectedThird = Later(middle, third);
                var right = await selectedThird;
                return left + middle + right;
            }
            """);

        new SemanticBinder().Bind(project);

        var relay = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Relay");
        var guarded = Assert.IsType<TryStatement>(Assert.Single(relay.Body!));
        var body = Assert.IsType<BlockStatement>(guarded.Body).Statements;
        var firstReady = Assert.IsType<IfStatement>(body[0]);
        var firstInline = Assert.IsType<BlockStatement>(firstReady.ThenStatement).Statements;
        var secondReady = Assert.IsType<IfStatement>(firstInline[2]);
        var secondInline = Assert.IsType<BlockStatement>(secondReady.ThenStatement).Statements;
        var laterCall = Assert.IsType<LocalVariableDeclarationStatement>(secondInline[1]);
        Assert.IsType<AutoType>(laterCall.DeclaredType);
        Assert.IsType<InvocationExpression>(laterCall.Declarators.Single().Initializer);
        var thirdReady = Assert.IsType<IfStatement>(secondInline[2]);
        Assert.Equal("selectedThird", Assert.IsType<IdentifierExpression>(
            Assert.IsType<MemberAccessExpression>(thirdReady.Condition).Target)
            .Identifier.ToString());
    }

    [Fact]
    public void AsyncTaskThreeAwaitConditionalCallAfterDirectLocalChecksEachSourceInline()
    {
        var project = CreateProject("""
            import System.Threading.Tasks;
            namespace App;
            Task<int> Later(int value, Task<int> source) { return source; }
            async Task<int> Relay(
                Task<int> first, Task<int> second, Task<int> third, bool chooseCall) {
                var left = await first;
                var selectedSecond = second;
                var middle = await selectedSecond;
                var selectedThird = chooseCall ? Later(middle, third) : third;
                var right = await selectedThird;
                return left + middle + right;
            }
            """);

        new SemanticBinder().Bind(project);

        var relay = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Relay");
        var guarded = Assert.IsType<TryStatement>(Assert.Single(relay.Body!));
        var body = Assert.IsType<BlockStatement>(guarded.Body).Statements;
        var firstReady = Assert.IsType<IfStatement>(body[0]);
        var firstInline = Assert.IsType<BlockStatement>(firstReady.ThenStatement).Statements;
        Assert.IsType<AutoType>(Assert.IsType<LocalVariableDeclarationStatement>(
            firstInline[1]).DeclaredType);
        var secondReady = Assert.IsType<IfStatement>(firstInline[2]);
        var secondInline = Assert.IsType<BlockStatement>(secondReady.ThenStatement).Statements;
        var selectedThird = Assert.IsType<LocalVariableDeclarationStatement>(secondInline[1]);
        Assert.IsType<AutoType>(selectedThird.DeclaredType);
        var conditional = Assert.IsType<ConditionalExpression>(
            selectedThird.Declarators.Single().Initializer);
        Assert.IsType<InvocationExpression>(conditional.WhenTrue);
        Assert.IsType<IdentifierExpression>(conditional.WhenFalse);
        Assert.IsType<IfStatement>(secondInline[2]);
    }

    [Fact]
    public void AsyncTaskThreeAwaitNullCoalescingCallAfterDirectLocalChecksEachSourceInline()
    {
        var project = CreateProject("""
            import System.Threading.Tasks;
            namespace App;
            Task<int> Later(int value, Task<int> source) { return source; }
            async Task<int> Relay(Task<int> first, Task<int> second,
                Task<int> preferred, Task<int> fallback) {
                var left = await first;
                var selectedSecond = second;
                var middle = await selectedSecond;
                var selectedThird = preferred ?? Later(middle, fallback);
                var right = await selectedThird;
                return left + middle + right;
            }
            """);

        new SemanticBinder().Bind(project);

        var relay = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Relay");
        var guarded = Assert.IsType<TryStatement>(Assert.Single(relay.Body!));
        var body = Assert.IsType<BlockStatement>(guarded.Body).Statements;
        var firstReady = Assert.IsType<IfStatement>(body[0]);
        var firstInline = Assert.IsType<BlockStatement>(firstReady.ThenStatement).Statements;
        Assert.IsType<AutoType>(Assert.IsType<LocalVariableDeclarationStatement>(
            firstInline[1]).DeclaredType);
        var secondReady = Assert.IsType<IfStatement>(firstInline[2]);
        var secondInline = Assert.IsType<BlockStatement>(secondReady.ThenStatement).Statements;
        var selectedThird = Assert.IsType<LocalVariableDeclarationStatement>(secondInline[1]);
        Assert.IsType<AutoType>(selectedThird.DeclaredType);
        var coalescing = Assert.IsType<NullCoalescingExpression>(
            selectedThird.Declarators.Single().Initializer);
        Assert.IsType<IdentifierExpression>(coalescing.Left);
        Assert.IsType<InvocationExpression>(coalescing.Right);
        Assert.IsType<IfStatement>(secondInline[2]);
    }

    [Fact]
    public void AsyncTaskThreeAwaitCallFirstThenDirectLocalsChecksEachSourceInline()
    {
        var project = CreateProject("""
            import System.Threading.Tasks;
            namespace App;
            Task<int> First(Task<int> source) { return source; }
            async Task<int> Relay(Task<int> first, Task<int> second, Task<int> third) {
                var left = await First(first);
                var selectedSecond = second;
                var middle = await selectedSecond;
                var selectedThird = third;
                var right = await selectedThird;
                return left + middle + right;
            }
            """);

        new SemanticBinder().Bind(project);

        var relay = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Relay");
        var guarded = Assert.IsType<TryStatement>(Assert.Single(relay.Body!));
        var body = Assert.IsType<BlockStatement>(guarded.Body).Statements;
        var inlineBranch = Assert.IsType<BlockStatement>(body[0]).Statements;
        var firstSource = Assert.IsType<LocalVariableDeclarationStatement>(inlineBranch[0]);
        Assert.IsType<InvocationExpression>(firstSource.Declarators.Single().Initializer);
        var firstReady = Assert.IsType<IfStatement>(inlineBranch[1]);
        Assert.Equal("IsCompleted", Assert.IsType<MemberAccessExpression>(
            firstReady.Condition).MemberName);
        var firstInline = Assert.IsType<BlockStatement>(firstReady.ThenStatement).Statements;
        var secondReady = Assert.IsType<IfStatement>(firstInline[2]);
        var secondInline = Assert.IsType<BlockStatement>(secondReady.ThenStatement).Statements;
        var selectedSecond = Assert.IsType<LocalVariableDeclarationStatement>(firstInline[1]);
        Assert.IsType<AutoType>(selectedSecond.DeclaredType);
        Assert.Equal("second", Assert.IsType<IdentifierExpression>(
            selectedSecond.Declarators.Single().Initializer).Identifier.ToString());
        var thirdReady = Assert.IsType<IfStatement>(secondInline[2]);
        Assert.Equal("IsCompleted", Assert.IsType<MemberAccessExpression>(
            secondReady.Condition).MemberName);
        var selectedThird = Assert.IsType<LocalVariableDeclarationStatement>(secondInline[1]);
        Assert.Equal("selectedThird", selectedThird.Declarators.Single().Name);
        Assert.Equal("third", Assert.IsType<IdentifierExpression>(
            selectedThird.Declarators.Single().Initializer).Identifier.ToString());
        Assert.Equal("IsCompleted", Assert.IsType<MemberAccessExpression>(
            thirdReady.Condition).MemberName);
        Assert.Contains(CoroutineSyntaxAnalysis.EnumerateStatements(body), statement =>
            statement is IfStatement conditional &&
            conditional.ElseStatement is BlockStatement pending &&
            pending.Statements.Any(nested =>
                nested is LocalVariableDeclarationStatement declaration &&
                declaration.Declarators.Any(declarator => declarator.Name ==
                    "__cx_async_completion")));
    }

    [Fact]
    public void AsyncTaskThreeAwaitInferredFirstLocalThenDirectLocalsChecksEachSourceInline()
    {
        var project = CreateProject("""
            import System.Threading.Tasks;
            namespace App;
            async Task<int> Relay(Task<int> first, Task<int> second, Task<int> third) {
                var selectedFirst = first;
                var left = await selectedFirst;
                var selectedSecond = second;
                var middle = await selectedSecond;
                var selectedThird = third;
                var right = await selectedThird;
                return left + middle + right;
            }
            """);

        new SemanticBinder().Bind(project);

        var relay = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Relay");
        var guarded = Assert.IsType<TryStatement>(Assert.Single(relay.Body!));
        var body = Assert.IsType<BlockStatement>(guarded.Body).Statements;
        var firstSource = Assert.IsType<LocalVariableDeclarationStatement>(body[0]);
        Assert.IsType<AutoType>(firstSource.DeclaredType);
        Assert.Equal("selectedFirst", firstSource.Declarators.Single().Name);
        var firstReady = Assert.IsType<IfStatement>(body[1]);
        Assert.Equal("selectedFirst", Assert.IsType<IdentifierExpression>(
            Assert.IsType<MemberAccessExpression>(firstReady.Condition).Target)
            .Identifier.ToString());
        var firstInline = Assert.IsType<BlockStatement>(firstReady.ThenStatement).Statements;
        var selectedSecond = Assert.IsType<LocalVariableDeclarationStatement>(firstInline[1]);
        Assert.IsType<AutoType>(selectedSecond.DeclaredType);
        var secondReady = Assert.IsType<IfStatement>(firstInline[2]);
        var secondInline = Assert.IsType<BlockStatement>(secondReady.ThenStatement).Statements;
        var selectedThird = Assert.IsType<LocalVariableDeclarationStatement>(secondInline[1]);
        Assert.IsType<AutoType>(selectedThird.DeclaredType);
        var thirdReady = Assert.IsType<IfStatement>(secondInline[2]);
        Assert.Equal("IsCompleted", Assert.IsType<MemberAccessExpression>(
            thirdReady.Condition).MemberName);
    }

    [Fact]
    public void AsyncTaskThreeAwaitInferredFirstLocalThenConditionalSourceChecksInline()
    {
        var project = CreateProject("""
            import System.Threading.Tasks;
            namespace App;
            Task<int> Later(int value, Task<int> source) { return source; }
            async Task<int> Relay(Task<int> first, Task<int> second,
                Task<int> third, bool chooseCall) {
                var selectedFirst = first;
                var left = await selectedFirst;
                var selectedSecond = second;
                var middle = await selectedSecond;
                var selectedThird = chooseCall ? Later(middle, third) : third;
                var right = await selectedThird;
                return left + middle + right;
            }
            """);

        new SemanticBinder().Bind(project);

        var relay = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Relay");
        var guarded = Assert.IsType<TryStatement>(Assert.Single(relay.Body!));
        var body = Assert.IsType<BlockStatement>(guarded.Body).Statements;
        var firstLocal = Assert.IsType<LocalVariableDeclarationStatement>(body[0]);
        Assert.IsType<AutoType>(firstLocal.DeclaredType);
        Assert.Equal("first", Assert.IsType<IdentifierExpression>(
            firstLocal.Declarators.Single().Initializer).Identifier.ToString());
        var firstReady = Assert.IsType<IfStatement>(body[1]);
        var firstInline = Assert.IsType<BlockStatement>(firstReady.ThenStatement).Statements;
        var selectedSecond = Assert.IsType<LocalVariableDeclarationStatement>(firstInline[1]);
        Assert.IsType<AutoType>(selectedSecond.DeclaredType);
        var secondReady = Assert.IsType<IfStatement>(firstInline[2]);
        var secondInline = Assert.IsType<BlockStatement>(secondReady.ThenStatement).Statements;
        var selectedThird = Assert.IsType<LocalVariableDeclarationStatement>(secondInline[1]);
        Assert.IsType<AutoType>(selectedThird.DeclaredType);
        var conditional = Assert.IsType<ConditionalExpression>(
            selectedThird.Declarators.Single().Initializer);
        Assert.IsType<InvocationExpression>(conditional.WhenTrue);
        Assert.IsType<IdentifierExpression>(conditional.WhenFalse);
        var thirdReady = Assert.IsType<IfStatement>(secondInline[2]);
        Assert.Equal("selectedThird", Assert.IsType<IdentifierExpression>(
            Assert.IsType<MemberAccessExpression>(thirdReady.Condition).Target)
            .Identifier.ToString());
        foreach (var readiness in new[] { firstReady, secondReady, thirdReady })
        {
            Assert.Equal("IsCompleted", Assert.IsType<MemberAccessExpression>(
                readiness.Condition).MemberName);
        }
    }

    [Fact]
    public void AsyncTaskThreeAwaitInferredFirstLocalThenNullCoalescingSourceChecksInline()
    {
        var project = CreateProject("""
            import System.Threading.Tasks;
            namespace App;
            Task<int> Later(int value, Task<int> source) { return source; }
            async Task<int> Relay(Task<int> first, Task<int> second,
                Task<int> preferred, Task<int> fallback) {
                var selectedFirst = first;
                var left = await selectedFirst;
                var selectedSecond = second;
                var middle = await selectedSecond;
                var selectedThird = preferred ?? Later(middle, fallback);
                var right = await selectedThird;
                return left + middle + right;
            }
            """);

        new SemanticBinder().Bind(project);

        var relay = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Relay");
        var guarded = Assert.IsType<TryStatement>(Assert.Single(relay.Body!));
        var body = Assert.IsType<BlockStatement>(guarded.Body).Statements;
        var firstLocal = Assert.IsType<LocalVariableDeclarationStatement>(body[0]);
        Assert.IsType<AutoType>(firstLocal.DeclaredType);
        Assert.Equal("first", Assert.IsType<IdentifierExpression>(
            firstLocal.Declarators.Single().Initializer).Identifier.ToString());
        var firstReady = Assert.IsType<IfStatement>(body[1]);
        var firstInline = Assert.IsType<BlockStatement>(firstReady.ThenStatement).Statements;
        var selectedSecond = Assert.IsType<LocalVariableDeclarationStatement>(firstInline[1]);
        Assert.IsType<AutoType>(selectedSecond.DeclaredType);
        var secondReady = Assert.IsType<IfStatement>(firstInline[2]);
        var secondInline = Assert.IsType<BlockStatement>(secondReady.ThenStatement).Statements;
        var selectedThird = Assert.IsType<LocalVariableDeclarationStatement>(secondInline[1]);
        Assert.IsType<AutoType>(selectedThird.DeclaredType);
        var coalescing = Assert.IsType<NullCoalescingExpression>(
            selectedThird.Declarators.Single().Initializer);
        Assert.IsType<IdentifierExpression>(coalescing.Left);
        Assert.IsType<InvocationExpression>(coalescing.Right);
        var thirdReady = Assert.IsType<IfStatement>(secondInline[2]);
        Assert.Equal("selectedThird", Assert.IsType<IdentifierExpression>(
            Assert.IsType<MemberAccessExpression>(thirdReady.Condition).Target)
            .Identifier.ToString());
        foreach (var readiness in new[] { firstReady, secondReady, thirdReady })
        {
            Assert.Equal("IsCompleted", Assert.IsType<MemberAccessExpression>(
                readiness.Condition).MemberName);
        }
    }

    [Fact]
    public void AsyncTaskThreeAwaitInferredDirectFirstLocalThenSwitchSourceChecksInline()
    {
        var project = CreateProject("""
            import System.Threading.Tasks;
            namespace App;
            Task<int> Later(int value, Task<int> source) { return source; }
            async Task<int> Relay(Task<int> first, Task<int> second,
                Task<int> third, int mode) {
                var selectedFirst = first;
                var left = await selectedFirst;
                var selectedSecond = second;
                var middle = await selectedSecond;
                var selectedThird = mode switch {
                    0 => Later(middle, third),
                    _ => third,
                };
                var right = await selectedThird;
                return left + middle + right;
            }
            """);

        new SemanticBinder().Bind(project);

        var relay = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Relay");
        var guarded = Assert.IsType<TryStatement>(Assert.Single(relay.Body!));
        var body = Assert.IsType<BlockStatement>(guarded.Body).Statements;
        var firstLocal = Assert.IsType<LocalVariableDeclarationStatement>(body[0]);
        Assert.IsType<AutoType>(firstLocal.DeclaredType);
        Assert.Equal("first", Assert.IsType<IdentifierExpression>(
            firstLocal.Declarators.Single().Initializer).Identifier.ToString());
        var firstReady = Assert.IsType<IfStatement>(body[1]);
        var firstInline = Assert.IsType<BlockStatement>(firstReady.ThenStatement).Statements;
        var selectedSecond = Assert.IsType<LocalVariableDeclarationStatement>(firstInline[1]);
        Assert.IsType<AutoType>(selectedSecond.DeclaredType);
        var secondReady = Assert.IsType<IfStatement>(firstInline[2]);
        var secondInline = Assert.IsType<BlockStatement>(secondReady.ThenStatement).Statements;
        var selectedThird = Assert.IsType<LocalVariableDeclarationStatement>(secondInline[1]);
        Assert.IsType<AutoType>(selectedThird.DeclaredType);
        var selection = Assert.IsType<SwitchExpression>(
            selectedThird.Declarators.Single().Initializer);
        Assert.Equal("mode", Assert.IsType<IdentifierExpression>(selection.Selector)
            .Identifier.ToString());
        Assert.Equal(2, selection.Arms.Count);
        Assert.IsType<InvocationExpression>(selection.Arms[0].Value);
        Assert.Equal("third", Assert.IsType<IdentifierExpression>(selection.Arms[1].Value)
            .Identifier.ToString());
        var thirdReady = Assert.IsType<IfStatement>(secondInline[2]);
        Assert.Equal("selectedThird", Assert.IsType<IdentifierExpression>(
            Assert.IsType<MemberAccessExpression>(thirdReady.Condition).Target)
            .Identifier.ToString());
        foreach (var readiness in new[] { firstReady, secondReady, thirdReady })
        {
            Assert.Equal("IsCompleted", Assert.IsType<MemberAccessExpression>(
                readiness.Condition).MemberName);
        }
    }

    [Fact]
    public void AsyncTaskThreeAwaitInferredCallFirstLocalThenDirectLocalsChecksEachSourceInline()
    {
        var project = CreateProject("""
            import System.Threading.Tasks;
            namespace App;
            Task<int> First(Task<int> source) { return source; }
            async Task<int> Relay(Task<int> first, Task<int> second, Task<int> third) {
                var selectedFirst = First(first);
                var left = await selectedFirst;
                var selectedSecond = second;
                var middle = await selectedSecond;
                var selectedThird = third;
                var right = await selectedThird;
                return left + middle + right;
            }
            """);

        new SemanticBinder().Bind(project);

        var relay = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Relay");
        var guarded = Assert.IsType<TryStatement>(Assert.Single(relay.Body!));
        var body = Assert.IsType<BlockStatement>(guarded.Body).Statements;
        var firstSource = Assert.IsType<LocalVariableDeclarationStatement>(body[0]);
        Assert.IsType<AutoType>(firstSource.DeclaredType);
        Assert.Equal("selectedFirst", firstSource.Declarators.Single().Name);
        Assert.IsType<InvocationExpression>(firstSource.Declarators.Single().Initializer);
        var firstReady = Assert.IsType<IfStatement>(body[1]);
        Assert.Equal("selectedFirst", Assert.IsType<IdentifierExpression>(
            Assert.IsType<MemberAccessExpression>(firstReady.Condition).Target)
            .Identifier.ToString());
        var firstInline = Assert.IsType<BlockStatement>(firstReady.ThenStatement).Statements;
        var selectedSecond = Assert.IsType<LocalVariableDeclarationStatement>(firstInline[1]);
        Assert.IsType<AutoType>(selectedSecond.DeclaredType);
        var secondReady = Assert.IsType<IfStatement>(firstInline[2]);
        var secondInline = Assert.IsType<BlockStatement>(secondReady.ThenStatement).Statements;
        var selectedThird = Assert.IsType<LocalVariableDeclarationStatement>(secondInline[1]);
        Assert.IsType<AutoType>(selectedThird.DeclaredType);
        var thirdReady = Assert.IsType<IfStatement>(secondInline[2]);
        foreach (var readiness in new[] { firstReady, secondReady, thirdReady })
        {
            Assert.Equal("IsCompleted", Assert.IsType<MemberAccessExpression>(
                readiness.Condition).MemberName);
        }
    }

    [Fact]
    public void AsyncTaskThreeAwaitInferredCallFirstLocalThenConditionalSourceChecksInline()
    {
        var project = CreateProject("""
            import System.Threading.Tasks;
            namespace App;
            Task<int> First(Task<int> source) { return source; }
            Task<int> Later(int value, Task<int> source) { return source; }
            async Task<int> Relay(Task<int> first, Task<int> second,
                Task<int> third, bool chooseCall) {
                var selectedFirst = First(first);
                var left = await selectedFirst;
                var selectedSecond = second;
                var middle = await selectedSecond;
                var selectedThird = chooseCall ? Later(middle, third) : third;
                var right = await selectedThird;
                return left + middle + right;
            }
            """);

        new SemanticBinder().Bind(project);

        var relay = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Relay");
        var guarded = Assert.IsType<TryStatement>(Assert.Single(relay.Body!));
        var body = Assert.IsType<BlockStatement>(guarded.Body).Statements;
        var firstLocal = Assert.IsType<LocalVariableDeclarationStatement>(body[0]);
        Assert.IsType<AutoType>(firstLocal.DeclaredType);
        Assert.IsType<InvocationExpression>(firstLocal.Declarators.Single().Initializer);
        var firstReady = Assert.IsType<IfStatement>(body[1]);
        Assert.Equal("selectedFirst", Assert.IsType<IdentifierExpression>(
            Assert.IsType<MemberAccessExpression>(firstReady.Condition).Target)
            .Identifier.ToString());
        var firstInline = Assert.IsType<BlockStatement>(firstReady.ThenStatement).Statements;
        var selectedSecond = Assert.IsType<LocalVariableDeclarationStatement>(firstInline[1]);
        Assert.IsType<AutoType>(selectedSecond.DeclaredType);
        var secondReady = Assert.IsType<IfStatement>(firstInline[2]);
        var secondInline = Assert.IsType<BlockStatement>(secondReady.ThenStatement).Statements;
        var selectedThird = Assert.IsType<LocalVariableDeclarationStatement>(secondInline[1]);
        Assert.IsType<AutoType>(selectedThird.DeclaredType);
        var conditional = Assert.IsType<ConditionalExpression>(
            selectedThird.Declarators.Single().Initializer);
        Assert.IsType<InvocationExpression>(conditional.WhenTrue);
        Assert.IsType<IdentifierExpression>(conditional.WhenFalse);
        var thirdReady = Assert.IsType<IfStatement>(secondInline[2]);
        Assert.Equal("selectedThird", Assert.IsType<IdentifierExpression>(
            Assert.IsType<MemberAccessExpression>(thirdReady.Condition).Target)
            .Identifier.ToString());
        foreach (var readiness in new[] { firstReady, secondReady, thirdReady })
        {
            Assert.Equal("IsCompleted", Assert.IsType<MemberAccessExpression>(
                readiness.Condition).MemberName);
        }
    }

    [Fact]
    public void AsyncTaskThreeAwaitInferredCallFirstLocalThenNullCoalescingSourceChecksInline()
    {
        var project = CreateProject("""
            import System.Threading.Tasks;
            namespace App;
            Task<int> First(Task<int> source) { return source; }
            Task<int> Later(int value, Task<int> source) { return source; }
            async Task<int> Relay(Task<int> first, Task<int> second,
                Task<int> preferred, Task<int> fallback) {
                var selectedFirst = First(first);
                var left = await selectedFirst;
                var selectedSecond = second;
                var middle = await selectedSecond;
                var selectedThird = preferred ?? Later(middle, fallback);
                var right = await selectedThird;
                return left + middle + right;
            }
            """);

        new SemanticBinder().Bind(project);

        var relay = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Relay");
        var guarded = Assert.IsType<TryStatement>(Assert.Single(relay.Body!));
        var body = Assert.IsType<BlockStatement>(guarded.Body).Statements;
        var firstLocal = Assert.IsType<LocalVariableDeclarationStatement>(body[0]);
        Assert.IsType<AutoType>(firstLocal.DeclaredType);
        Assert.IsType<InvocationExpression>(firstLocal.Declarators.Single().Initializer);
        var firstReady = Assert.IsType<IfStatement>(body[1]);
        var firstInline = Assert.IsType<BlockStatement>(firstReady.ThenStatement).Statements;
        var selectedSecond = Assert.IsType<LocalVariableDeclarationStatement>(firstInline[1]);
        Assert.IsType<AutoType>(selectedSecond.DeclaredType);
        var secondReady = Assert.IsType<IfStatement>(firstInline[2]);
        var secondInline = Assert.IsType<BlockStatement>(secondReady.ThenStatement).Statements;
        var selectedThird = Assert.IsType<LocalVariableDeclarationStatement>(secondInline[1]);
        Assert.IsType<AutoType>(selectedThird.DeclaredType);
        var coalescing = Assert.IsType<NullCoalescingExpression>(
            selectedThird.Declarators.Single().Initializer);
        Assert.IsType<IdentifierExpression>(coalescing.Left);
        Assert.IsType<InvocationExpression>(coalescing.Right);
        var thirdReady = Assert.IsType<IfStatement>(secondInline[2]);
        Assert.Equal("selectedThird", Assert.IsType<IdentifierExpression>(
            Assert.IsType<MemberAccessExpression>(thirdReady.Condition).Target)
            .Identifier.ToString());
        foreach (var readiness in new[] { firstReady, secondReady, thirdReady })
        {
            Assert.Equal("IsCompleted", Assert.IsType<MemberAccessExpression>(
                readiness.Condition).MemberName);
        }
    }

    [Fact]
    public void AsyncTaskThreeAwaitInferredCallFirstLocalThenSwitchSourceChecksInline()
    {
        var project = CreateProject("""
            import System.Threading.Tasks;
            namespace App;
            Task<int> First(Task<int> source) { return source; }
            Task<int> Later(int value, Task<int> source) { return source; }
            async Task<int> Relay(Task<int> first, Task<int> second,
                Task<int> third, int mode) {
                var selectedFirst = First(first);
                var left = await selectedFirst;
                var selectedSecond = second;
                var middle = await selectedSecond;
                var selectedThird = mode switch {
                    0 => Later(middle, third),
                    _ => third,
                };
                var right = await selectedThird;
                return left + middle + right;
            }
            """);

        new SemanticBinder().Bind(project);

        var relay = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Relay");
        var guarded = Assert.IsType<TryStatement>(Assert.Single(relay.Body!));
        var body = Assert.IsType<BlockStatement>(guarded.Body).Statements;
        var firstLocal = Assert.IsType<LocalVariableDeclarationStatement>(body[0]);
        Assert.IsType<AutoType>(firstLocal.DeclaredType);
        Assert.IsType<InvocationExpression>(firstLocal.Declarators.Single().Initializer);
        var firstReady = Assert.IsType<IfStatement>(body[1]);
        var firstInline = Assert.IsType<BlockStatement>(firstReady.ThenStatement).Statements;
        var selectedSecond = Assert.IsType<LocalVariableDeclarationStatement>(firstInline[1]);
        Assert.IsType<AutoType>(selectedSecond.DeclaredType);
        var secondReady = Assert.IsType<IfStatement>(firstInline[2]);
        var secondInline = Assert.IsType<BlockStatement>(secondReady.ThenStatement).Statements;
        var selectedThird = Assert.IsType<LocalVariableDeclarationStatement>(secondInline[1]);
        Assert.IsType<AutoType>(selectedThird.DeclaredType);
        var selection = Assert.IsType<SwitchExpression>(
            selectedThird.Declarators.Single().Initializer);
        Assert.Equal("mode", Assert.IsType<IdentifierExpression>(selection.Selector)
            .Identifier.ToString());
        Assert.Equal(2, selection.Arms.Count);
        Assert.IsType<InvocationExpression>(selection.Arms[0].Value);
        Assert.Equal("third", Assert.IsType<IdentifierExpression>(selection.Arms[1].Value)
            .Identifier.ToString());
        var thirdReady = Assert.IsType<IfStatement>(secondInline[2]);
        Assert.Equal("selectedThird", Assert.IsType<IdentifierExpression>(
            Assert.IsType<MemberAccessExpression>(thirdReady.Condition).Target)
            .Identifier.ToString());
        foreach (var readiness in new[] { firstReady, secondReady, thirdReady })
        {
            Assert.Equal("IsCompleted", Assert.IsType<MemberAccessExpression>(
                readiness.Condition).MemberName);
        }
    }

    [Fact]
    public void AsyncTaskThreeAwaitInferredCallFirstLocalThenCastSourceChecksInline()
    {
        var project = CreateProject("""
            import System.Threading.Tasks;
            namespace App;
            Task<int> First(Task<int> source) { return source; }
            Task<int> Later(int value, Task<int> source) { return source; }
            async Task<int> Relay(Task<int> first, Task<int> second, Task<int> third) {
                var selectedFirst = First(first);
                var left = await selectedFirst;
                var selectedSecond = second;
                var middle = await selectedSecond;
                var selectedThird = (Task<int>)Later(middle, third);
                var right = await selectedThird;
                return left + middle + right;
            }
            """);

        new SemanticBinder().Bind(project);

        var relay = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Relay");
        var guarded = Assert.IsType<TryStatement>(Assert.Single(relay.Body!));
        var body = Assert.IsType<BlockStatement>(guarded.Body).Statements;
        var firstLocal = Assert.IsType<LocalVariableDeclarationStatement>(body[0]);
        Assert.IsType<AutoType>(firstLocal.DeclaredType);
        Assert.IsType<InvocationExpression>(firstLocal.Declarators.Single().Initializer);
        var firstReady = Assert.IsType<IfStatement>(body[1]);
        var firstInline = Assert.IsType<BlockStatement>(firstReady.ThenStatement).Statements;
        var selectedSecond = Assert.IsType<LocalVariableDeclarationStatement>(firstInline[1]);
        Assert.IsType<AutoType>(selectedSecond.DeclaredType);
        var secondReady = Assert.IsType<IfStatement>(firstInline[2]);
        var secondInline = Assert.IsType<BlockStatement>(secondReady.ThenStatement).Statements;
        var selectedThird = Assert.IsType<LocalVariableDeclarationStatement>(secondInline[1]);
        Assert.IsType<AutoType>(selectedThird.DeclaredType);
        var cast = Assert.IsType<CastExpression>(selectedThird.Declarators.Single().Initializer);
        Assert.IsType<InvocationExpression>(cast.Operand);
        var thirdReady = Assert.IsType<IfStatement>(secondInline[2]);
        Assert.Equal("selectedThird", Assert.IsType<IdentifierExpression>(
            Assert.IsType<MemberAccessExpression>(thirdReady.Condition).Target)
            .Identifier.ToString());
        foreach (var readiness in new[] { firstReady, secondReady, thirdReady })
        {
            Assert.Equal("IsCompleted", Assert.IsType<MemberAccessExpression>(
                readiness.Condition).MemberName);
        }
    }

    [Fact]
    public void AsyncTaskThreeAwaitInferredDirectFirstLocalThenCastSourceChecksInline()
    {
        var project = CreateProject("""
            import System.Threading.Tasks;
            namespace App;
            Task<int> Later(int value, Task<int> source) { return source; }
            async Task<int> Relay(Task<int> first, Task<int> second, Task<int> third) {
                var selectedFirst = first;
                var left = await selectedFirst;
                var selectedSecond = second;
                var middle = await selectedSecond;
                var selectedThird = (Task<int>)Later(middle, third);
                var right = await selectedThird;
                return left + middle + right;
            }
            """);

        new SemanticBinder().Bind(project);

        var relay = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Relay");
        var guarded = Assert.IsType<TryStatement>(Assert.Single(relay.Body!));
        var body = Assert.IsType<BlockStatement>(guarded.Body).Statements;
        var firstLocal = Assert.IsType<LocalVariableDeclarationStatement>(body[0]);
        Assert.IsType<AutoType>(firstLocal.DeclaredType);
        Assert.Equal("first", Assert.IsType<IdentifierExpression>(
            firstLocal.Declarators.Single().Initializer).Identifier.ToString());
        var firstReady = Assert.IsType<IfStatement>(body[1]);
        var firstInline = Assert.IsType<BlockStatement>(firstReady.ThenStatement).Statements;
        var selectedSecond = Assert.IsType<LocalVariableDeclarationStatement>(firstInline[1]);
        Assert.IsType<AutoType>(selectedSecond.DeclaredType);
        var secondReady = Assert.IsType<IfStatement>(firstInline[2]);
        var secondInline = Assert.IsType<BlockStatement>(secondReady.ThenStatement).Statements;
        var selectedThird = Assert.IsType<LocalVariableDeclarationStatement>(secondInline[1]);
        Assert.IsType<AutoType>(selectedThird.DeclaredType);
        var cast = Assert.IsType<CastExpression>(selectedThird.Declarators.Single().Initializer);
        Assert.IsType<InvocationExpression>(cast.Operand);
        var thirdReady = Assert.IsType<IfStatement>(secondInline[2]);
        Assert.Equal("selectedThird", Assert.IsType<IdentifierExpression>(
            Assert.IsType<MemberAccessExpression>(thirdReady.Condition).Target)
            .Identifier.ToString());
        foreach (var readiness in new[] { firstReady, secondReady, thirdReady })
        {
            Assert.Equal("IsCompleted", Assert.IsType<MemberAccessExpression>(
                readiness.Condition).MemberName);
        }
    }

    [Fact]
    public void AsyncTaskAwaitedAutoLocalReturnBuildsPendingCompletionRelay()
    {
        var project = CreateProject("""
            import System.Threading.Tasks;
            namespace App;
            async Task<int> Relay(Task<int> pending) {
                var result = await pending;
                return result;
            }
            """);

        new SemanticBinder().Bind(project);

        var relay = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Relay");
        var guarded = Assert.IsType<TryStatement>(Assert.Single(relay.Body!));
        var body = Assert.IsType<BlockStatement>(guarded.Body).Statements;
        Assert.IsType<LocalVariableDeclarationStatement>(body[0]);
        Assert.IsType<IfStatement>(body[1]);
        Assert.IsType<LocalVariableDeclarationStatement>(body[2]);
        Assert.IsType<LocalVariableDeclarationStatement>(body[3]);
        Assert.IsType<LocalVariableDeclarationStatement>(body[4]);
        var forward = Assert.IsType<InvocationExpression>(
            Assert.IsType<ExpressionStatement>(body[5]).Expression);
        Assert.Equal("OnCompleted", Assert.IsType<MemberAccessExpression>(forward.Target).MemberName);
    }

    [Fact]
    public void AsyncTaskDirectTaskAwaitWithTwoPrefixLocalsChecksInlineBeforeRelayState()
    {
        var project = CreateProject("""
            import System.Threading.Tasks;
            namespace App;
            async Task<int> Relay(Task<int> source) {
                int baseline = 2;
                string marker = "m18";
                var result = await source;
                return baseline + (int)marker.Length + result;
            }
            """);

        new SemanticBinder().Bind(project);

        var relay = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Relay");
        var guarded = Assert.IsType<TryStatement>(Assert.Single(relay.Body!));
        var lowered = Assert.IsType<BlockStatement>(guarded.Body).Statements;
        Assert.Equal(3, lowered.Count);
        Assert.Equal(2, lowered.Take(2)
            .OfType<LocalVariableDeclarationStatement>().Count());
        var readiness = Assert.IsType<IfStatement>(lowered[2]);
        Assert.Equal("IsCompleted",
            Assert.IsType<MemberAccessExpression>(readiness.Condition).MemberName);
        var inline = Assert.IsType<BlockStatement>(readiness.ThenStatement).Statements;
        Assert.IsType<ReturnStatement>(inline[^1]);
        var pending = Assert.IsType<BlockStatement>(readiness.ElseStatement).Statements;
        var aliases = CoroutineSyntaxAnalysis.EnumerateBodyStatements(pending)
            .OfType<LocalVariableDeclarationStatement>()
            .SelectMany(declaration => declaration.Declarators)
            .Select(declarator => declarator.Name)
            .ToArray();
        Assert.Contains(aliases, name => name.StartsWith(
            "__cx_async_callback_prefix_baseline", StringComparison.Ordinal));
        Assert.Contains(aliases, name => name.StartsWith(
            "__cx_async_callback_prefix_marker", StringComparison.Ordinal));
    }

    [Fact]
    public void AsyncTaskDirectTaskAwaitWithOnePrefixLocalChecksInlineBeforeRelayState()
    {
        var project = CreateProject("""
            import System.Threading.Tasks;
            namespace App;
            async Task<int> Relay(Task<int> source) {
                int baseline = 2;
                var result = await source;
                return baseline + result;
            }
            """);

        new SemanticBinder().Bind(project);

        var relay = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Relay");
        var guarded = Assert.IsType<TryStatement>(Assert.Single(relay.Body!));
        var lowered = Assert.IsType<BlockStatement>(guarded.Body).Statements;
        Assert.Equal(2, lowered.Count);
        Assert.IsType<LocalVariableDeclarationStatement>(lowered[0]);
        var readiness = Assert.IsType<IfStatement>(lowered[1]);
        Assert.Equal("IsCompleted",
            Assert.IsType<MemberAccessExpression>(readiness.Condition).MemberName);
        var pending = Assert.IsType<BlockStatement>(readiness.ElseStatement).Statements;
        Assert.Contains(CoroutineSyntaxAnalysis.EnumerateBodyStatements(pending)
            .OfType<LocalVariableDeclarationStatement>()
            .SelectMany(declaration => declaration.Declarators),
            declarator => declarator.Name.StartsWith(
                "__cx_async_callback_prefix_baseline", StringComparison.Ordinal));
    }

    [Fact]
    public void AsyncTaskDirectTaskAwaitWithThreePrefixLocalsChecksInlineBeforeRelayState()
    {
        var project = CreateProject("""
            import System.Threading.Tasks;
            namespace App;
            async Task<int> Relay(Task<int> source) {
                int baseline = 2;
                string marker = "m18";
                int adjustment = 4;
                var result = await source;
                return baseline + (int)marker.Length + adjustment + result;
            }
            """);

        new SemanticBinder().Bind(project);

        var relay = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Relay");
        var guarded = Assert.IsType<TryStatement>(Assert.Single(relay.Body!));
        var lowered = Assert.IsType<BlockStatement>(guarded.Body).Statements;
        Assert.Equal(4, lowered.Count);
        Assert.Equal(3, lowered.Take(3)
            .OfType<LocalVariableDeclarationStatement>().Count());
        var readiness = Assert.IsType<IfStatement>(lowered[3]);
        Assert.Equal("IsCompleted",
            Assert.IsType<MemberAccessExpression>(readiness.Condition).MemberName);
        var pending = Assert.IsType<BlockStatement>(readiness.ElseStatement).Statements;
        var aliases = CoroutineSyntaxAnalysis.EnumerateBodyStatements(pending)
            .OfType<LocalVariableDeclarationStatement>()
            .SelectMany(declaration => declaration.Declarators)
            .Select(declarator => declarator.Name)
            .ToArray();
        Assert.Contains(aliases, name => name.StartsWith(
            "__cx_async_callback_prefix_baseline", StringComparison.Ordinal));
        Assert.Contains(aliases, name => name.StartsWith(
            "__cx_async_callback_prefix_marker", StringComparison.Ordinal));
        Assert.Contains(aliases, name => name.StartsWith(
            "__cx_async_callback_prefix_adjustment", StringComparison.Ordinal));
    }

    [Fact]
    public void AsyncTaskDirectTaskAwaitRunsSynchronousExpressionPrefixBeforeReadiness()
    {
        var project = CreateProject("""
            import System.Threading.Tasks;
            namespace App;
            public static class PrefixCounter {
                public static int Calls;
                public static void Record() { Calls += 1; }
            }
            async Task<int> Relay(Task<int> source) {
                PrefixCounter.Record();
                var result = await source;
                return result;
            }
            """);

        new SemanticBinder().Bind(project);

        var relay = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Relay");
        var guarded = Assert.IsType<TryStatement>(Assert.Single(relay.Body!));
        var lowered = Assert.IsType<BlockStatement>(guarded.Body).Statements;
        Assert.Equal(2, lowered.Count);
        var prefix = Assert.IsType<ExpressionStatement>(lowered[0]);
        Assert.Equal("Record", Assert.IsType<MemberAccessExpression>(
            Assert.IsType<InvocationExpression>(prefix.Expression).Target).MemberName);
        var readiness = Assert.IsType<IfStatement>(lowered[1]);
        Assert.Equal("IsCompleted",
            Assert.IsType<MemberAccessExpression>(readiness.Condition).MemberName);
    }

    [Fact]
    public void AsyncTaskDirectTaskAwaitWithMultiDeclaratorPrefixChecksInlineBeforeRelayState()
    {
        var project = CreateProject("""
            import System.Threading.Tasks;
            namespace App;
            async Task<int> Relay(Task<int> source) {
                int baseline = 2, adjustment = 4;
                var result = await source;
                return baseline + adjustment + result;
            }
            """);

        new SemanticBinder().Bind(project);

        var relay = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Relay");
        var guarded = Assert.IsType<TryStatement>(Assert.Single(relay.Body!));
        var lowered = Assert.IsType<BlockStatement>(guarded.Body).Statements;
        Assert.Equal(2, lowered.Count);
        var prefix = Assert.IsType<LocalVariableDeclarationStatement>(lowered[0]);
        Assert.Equal(2, prefix.Declarators.Count);
        var readiness = Assert.IsType<IfStatement>(lowered[1]);
        Assert.Equal("IsCompleted",
            Assert.IsType<MemberAccessExpression>(readiness.Condition).MemberName);
        var aliases = CoroutineSyntaxAnalysis.EnumerateBodyStatements(
                Assert.IsType<BlockStatement>(readiness.ElseStatement).Statements)
            .OfType<LocalVariableDeclarationStatement>()
            .SelectMany(declaration => declaration.Declarators)
            .Select(declarator => declarator.Name)
            .ToArray();
        Assert.Contains(aliases, name => name.StartsWith(
            "__cx_async_callback_prefix_baseline", StringComparison.Ordinal));
        Assert.Contains(aliases, name => name.StartsWith(
            "__cx_async_callback_prefix_adjustment", StringComparison.Ordinal));
    }

    [Fact]
    public void AsyncTaskDirectTaskAwaitWithAssignedLocalPrefixChecksInlineBeforeRelayState()
    {
        var project = CreateProject("""
            import System.Threading.Tasks;
            namespace App;
            async Task<int> Relay(Task<int> source) {
                int baseline;
                baseline = 8;
                var result = await source;
                return baseline + result;
            }
            """);

        new SemanticBinder().Bind(project);

        var relay = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Relay");
        var guarded = Assert.IsType<TryStatement>(Assert.Single(relay.Body!));
        var lowered = Assert.IsType<BlockStatement>(guarded.Body).Statements;
        Assert.Equal(3, lowered.Count);
        Assert.IsType<LocalVariableDeclarationStatement>(lowered[0]);
        Assert.IsType<ExpressionStatement>(lowered[1]);
        var readiness = Assert.IsType<IfStatement>(lowered[2]);
        Assert.Equal("IsCompleted",
            Assert.IsType<MemberAccessExpression>(readiness.Condition).MemberName);
        var aliases = CoroutineSyntaxAnalysis.EnumerateBodyStatements(
                Assert.IsType<BlockStatement>(readiness.ElseStatement).Statements)
            .OfType<LocalVariableDeclarationStatement>()
            .SelectMany(declaration => declaration.Declarators)
            .Select(declarator => declarator.Name)
            .ToArray();
        Assert.Contains(aliases, name => name.StartsWith(
            "__cx_async_callback_prefix_baseline", StringComparison.Ordinal));
    }

    [Fact]
    public void AsyncTaskDirectTaskAwaitExecutesConditionalPrefixBeforeReadiness()
    {
        var project = CreateProject("""
            import System.Threading.Tasks;
            namespace App;
            public static class PrefixCounter {
                public static int Calls;
                public static void Record() { Calls += 1; }
            }
            async Task<int> Relay(bool record, Task<int> source) {
                if (record) {
                    PrefixCounter.Record();
                }
                var result = await source;
                return result;
            }
            """);

        new SemanticBinder().Bind(project);

        var relay = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Relay");
        var guarded = Assert.IsType<TryStatement>(Assert.Single(relay.Body!));
        var lowered = Assert.IsType<BlockStatement>(guarded.Body).Statements;
        Assert.Equal(2, lowered.Count);
        var prefix = Assert.IsType<IfStatement>(lowered[0]);
        Assert.IsType<BlockStatement>(prefix.ThenStatement);
        var readiness = Assert.IsType<IfStatement>(lowered[1]);
        Assert.Equal("IsCompleted",
            Assert.IsType<MemberAccessExpression>(readiness.Condition).MemberName);
    }

    [Fact]
    public void AsyncTaskAwaitLocalContinuesThroughSynchronousStatements()
    {
        var project = CreateProject("""
            import System.Threading.Tasks;
            namespace App;
            async Task<int> Continue(Task<int> pending) {
                var value = await pending;
                value += 2;
                return value * 3;
            }
            """);

        new SemanticBinder().Bind(project);

        var continuation = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Continue");
        var guarded = Assert.IsType<TryStatement>(Assert.Single(continuation.Body!));
        var body = Assert.IsType<BlockStatement>(guarded.Body).Statements;
        Assert.Equal(5, body.Count);
        Assert.IsType<LocalVariableDeclarationStatement>(body[0]);
        var continuationLocal = Assert.IsType<LocalVariableDeclarationStatement>(body[2]);
        var callback = Assert.IsType<LambdaExpression>(
            Assert.Single(continuationLocal.Declarators).Initializer);
        var callbackTry = Assert.IsType<TryStatement>(Assert.Single(callback.StatementBody!));
        var callbackStatements = Assert.IsType<BlockStatement>(callbackTry.Body).Statements;
        Assert.IsType<LocalVariableDeclarationStatement>(callbackStatements[0]);
        Assert.IsType<ExpressionStatement>(callbackStatements[1]);
        var completion = Assert.IsType<InvocationExpression>(
            Assert.IsType<ExpressionStatement>(callbackStatements[2]).Expression);
        Assert.Equal("SetResult", Assert.IsType<MemberAccessExpression>(completion.Target).MemberName);
        Assert.IsType<IfStatement>(body[3]);
        Assert.IsType<ReturnStatement>(body[4]);
    }

    [Fact]
    public void AsyncTaskSplitsAwaitedCallArgumentIntoPendingContinuation()
    {
        var project = CreateProject("""
            import System.Threading.Tasks;
            namespace App;
            async Task<int> Wrap(Task<int> pending) {
                return Consume(await pending);
            }
            int Consume(int value) { return value; }
            """);

        new SemanticBinder().Bind(project);

        var wrap = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Wrap");
        Assert.DoesNotContain(CoroutineSyntaxAnalysis.EnumerateBodyExpressions(wrap.Body!),
            expression => expression is AwaitExpression);
        Assert.Contains(CoroutineSyntaxAnalysis.EnumerateStatements(wrap.Body!),
            statement => statement is LocalVariableDeclarationStatement
            {
                Declarators: [{ Initializer: LambdaExpression }],
            });
        var inlineCheck = Assert.Single(CoroutineSyntaxAnalysis
            .EnumerateStatements(wrap.Body!).OfType<IfStatement>(), conditional =>
                conditional.Condition is MemberAccessExpression
                {
                    MemberName: "IsCompleted",
                } && conditional.ElseStatement is BlockStatement);
        var inlineStatements = Assert.IsType<BlockStatement>(
            inlineCheck.ThenStatement).Statements;
        Assert.Contains(CoroutineSyntaxAnalysis.EnumerateStatements(
                [inlineCheck.ThenStatement]),
            statement => statement is LocalVariableDeclarationStatement declaration &&
                declaration.Declarators.Any(declarator =>
                    declarator.Initializer is InvocationExpression invocation &&
                    invocation.Target is MemberAccessExpression
                    {
                        MemberName: "GetResult",
                    }));
        Assert.IsType<ReturnStatement>(inlineStatements[^1]);
    }

    [Fact]
    public void AsyncTaskSplitsMultipleCallArgumentsAroundOneAwait()
    {
        var project = CreateProject("""
            import System.Threading.Tasks;
            namespace App;
            async Task<int> Wrap(Task<int> pending) {
                return Combine(Before(), await pending, After());
            }
            int Before() { return 1; }
            int After() { return 2; }
            int Combine(int before, int value, int after) { return value; }
            """);

        new SemanticBinder().Bind(project);

        var wrap = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Wrap");
        Assert.DoesNotContain(CoroutineSyntaxAnalysis.EnumerateBodyExpressions(wrap.Body!),
            expression => expression is AwaitExpression);
        var continuation = Assert.Single(
            CoroutineSyntaxAnalysis.EnumerateStatements(wrap.Body!)
                .OfType<LocalVariableDeclarationStatement>()
                .SelectMany(declaration => declaration.Declarators)
                .Select(declarator => declarator.Initializer)
                .OfType<LambdaExpression>());
        var call = Assert.Single(CoroutineSyntaxAnalysis
            .EnumerateBodyExpressions(continuation.StatementBody!)
            .OfType<InvocationExpression>(), invocation =>
                invocation.Target is IdentifierExpression target &&
                target.Identifier.ToString() == "Combine");
        Assert.Equal(3, call.Arguments.Count);
        Assert.IsType<IdentifierExpression>(call.Arguments[0]);
        Assert.IsType<IdentifierExpression>(call.Arguments[1]);
        Assert.IsType<InvocationExpression>(call.Arguments[2]);
    }

    [Fact]
    public void AsyncTaskConditionalNestedAwaitKeepsUnselectedBranchLazy()
    {
        var project = CreateProject("""
            import System.Threading.Tasks;
            namespace App;
            async Task<int> Pick(bool chooseAwait, Task<int> pending) {
                return Choose(chooseAwait) ? await pending : Fallback();
            }
            bool Choose(bool value) { return value; }
            int Fallback() { return 7; }
            """);

        new SemanticBinder().Bind(project);

        var pick = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Pick");
        Assert.Contains(CoroutineSyntaxAnalysis.EnumerateBodyExpressions(pick.Body!),
            expression => expression is ConditionalExpression);
        Assert.Single(CoroutineSyntaxAnalysis.EnumerateBodyExpressions(pick.Body!)
            .OfType<InvocationExpression>(), invocation =>
                invocation.Target is IdentifierExpression target &&
                target.Identifier.ToString() == "Choose");
    }

    [Fact]
    public void AsyncTaskShortCircuitAwaitIsRewrittenAsLazyConditional()
    {
        var project = CreateProject("""
            import System.Threading.Tasks;
            namespace App;
            async Task<bool> And(bool left, Task<bool> pending) {
                return left && await pending;
            }
            async Task<bool> Or(bool left, Task<bool> pending) {
                return left || await pending;
            }
            """);

        new SemanticBinder().Bind(project);

        var functions = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Where(function => function.Name is "And" or "Or")
            .ToDictionary(function => function.Name);
        Assert.Equal(2, functions.Count);
        Assert.All(functions.Values, function =>
        {
            Assert.Contains(CoroutineSyntaxAnalysis.EnumerateBodyExpressions(function.Body!),
                expression => expression is ConditionalExpression);
            var guarded = Assert.IsType<TryStatement>(Assert.Single(function.Body!));
            var loweredBody = Assert.IsType<BlockStatement>(guarded.Body).Statements;
            var completionCheckIndex = loweredBody.ToList().FindIndex(statement =>
                statement is IfStatement
                {
                    Condition: MemberAccessExpression { MemberName: "IsCompleted" },
                    ThenStatement: BlockStatement,
                });
            var completionSourceIndex = loweredBody.ToList().FindIndex(statement =>
                statement is LocalVariableDeclarationStatement declaration &&
                declaration.Declarators.Any(declarator =>
                    declarator.Name.StartsWith(
                        "__cx_async_completion", StringComparison.Ordinal)));
            var registrationIndex = loweredBody.ToList().FindIndex(statement =>
                statement is ExpressionStatement
                {
                    Expression: InvocationExpression
                    {
                        Target: MemberAccessExpression { MemberName: "OnCompleted" },
                        Arguments: [LambdaExpression],
                    },
                });
            Assert.True(completionCheckIndex >= 0);
            Assert.True(completionSourceIndex > completionCheckIndex);
            Assert.True(registrationIndex > completionSourceIndex);
            Assert.DoesNotContain(loweredBody.Take(completionCheckIndex), statement =>
                statement is LocalVariableDeclarationStatement declaration &&
                declaration.Declarators.Any(declarator =>
                    declarator.Initializer is LambdaExpression));
        });
    }

    [Fact]
    public void AsyncTaskNullCoalescingAwaitEvaluatesLeftOnceAndAwaitsOnlyFallback()
    {
        var project = CreateProject("""
            import System.Threading.Tasks;
            namespace App;
            async Task<string> Pick(string value, Task<string> fallback) {
                return value ?? await SelectFallback(fallback);
            }
            Task<string> SelectFallback(Task<string> fallback) { return fallback; }
            """);

        new SemanticBinder().Bind(project);

        var pick = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Pick");
        Assert.DoesNotContain(CoroutineSyntaxAnalysis.EnumerateBodyExpressions(pick.Body!),
            expression => expression is BinaryExpression { Operator: "??" });
        Assert.Single(CoroutineSyntaxAnalysis.EnumerateBodyExpressions(pick.Body!)
            .OfType<AwaitExpression>());
        Assert.Single(CoroutineSyntaxAnalysis.EnumerateBodyExpressions(pick.Body!)
            .OfType<ConditionalExpression>());
        var guarded = Assert.IsType<TryStatement>(Assert.Single(pick.Body!));
        var loweredBody = Assert.IsType<BlockStatement>(guarded.Body).Statements;
        var completionCheckIndex = loweredBody.ToList().FindIndex(statement =>
            statement is IfStatement
            {
                Condition: MemberAccessExpression { MemberName: "IsCompleted" },
                ThenStatement: BlockStatement,
            });
        var completionSourceIndex = loweredBody.ToList().FindIndex(statement =>
            statement is LocalVariableDeclarationStatement declaration &&
            declaration.Declarators.Any(declarator =>
                declarator.Name.StartsWith("__cx_async_completion", StringComparison.Ordinal)));
        var registrationIndex = loweredBody.ToList().FindIndex(statement =>
            statement is ExpressionStatement
            {
                Expression: InvocationExpression
                {
                    Target: MemberAccessExpression { MemberName: "OnCompleted" },
                    Arguments: [LambdaExpression],
                },
            });
        Assert.True(completionCheckIndex >= 0);
        Assert.True(completionSourceIndex > completionCheckIndex);
        Assert.True(registrationIndex > completionSourceIndex);
        Assert.DoesNotContain(loweredBody.Take(completionCheckIndex), statement =>
            statement is LocalVariableDeclarationStatement declaration &&
            declaration.Declarators.Any(declarator =>
                declarator.Initializer is LambdaExpression));
    }

    [Fact]
    public void AsyncTaskRejectsMultipleNestedAwaitsWithSpecificDiagnostic()
    {
        var project = CreateProject("""
            import System.Threading.Tasks;
            namespace App;
            async Task<int> CombineAwaited(Task<int> first, Task<int> second) {
                return Combine(await first, await second);
            }
            int Combine(int first, int second) { return first + second; }
            """);

        var error = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(project));
        Assert.Contains("async Task nested-await shape is not supported yet", error.Message);
    }

    [Fact]
    public void AsyncTaskSplitsAwaitedBinaryOperandAfterEvaluatingLeftSide()
    {
        var project = CreateProject("""
            import System.Threading.Tasks;
            namespace App;
            async Task<int> Add(Task<int> pending) {
                return ReadLeft() + await pending;
            }
            int ReadLeft() { return 7; }
            """);

        new SemanticBinder().Bind(project);

        var add = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Add");
        Assert.DoesNotContain(CoroutineSyntaxAnalysis.EnumerateBodyExpressions(add.Body!),
            expression => expression is AwaitExpression);
        var leftTemporary = Assert.Single(
            CoroutineSyntaxAnalysis.EnumerateStatements(add.Body!)
                .OfType<LocalVariableDeclarationStatement>(),
            declaration => declaration.Declarators.Any(declarator =>
                declarator.Initializer is InvocationExpression invocation &&
                invocation.Target is IdentifierExpression target &&
                target.Identifier.ToString() == "ReadLeft"));
        Assert.IsType<InvocationExpression>(
            Assert.Single(leftTemporary.Declarators).Initializer);
        var inlineCheck = Assert.Single(CoroutineSyntaxAnalysis
            .EnumerateStatements(add.Body!).OfType<IfStatement>(), conditional =>
                conditional.Condition is MemberAccessExpression
                {
                    MemberName: "IsCompleted",
                } && conditional.ElseStatement is BlockStatement);
        var inlineStatements = Assert.IsType<BlockStatement>(
            inlineCheck.ThenStatement).Statements;
        Assert.Contains(inlineStatements.OfType<LocalVariableDeclarationStatement>(),
            declaration => declaration.Declarators.Any(declarator =>
                declarator.Initializer is InvocationExpression invocation &&
                invocation.Target is MemberAccessExpression
                {
                    MemberName: "GetResult",
                }));
        Assert.IsType<ReturnStatement>(inlineStatements[^1]);
        Assert.Contains(CoroutineSyntaxAnalysis.EnumerateStatements(
                [inlineCheck.ElseStatement!]),
            statement => statement is LocalVariableDeclarationStatement declaration &&
                declaration.Declarators.Any(declarator =>
                    declarator.Initializer is LambdaExpression));
    }

    [Fact]
    public void AsyncTaskContinuationCompletesFromConditionalReturnPaths()
    {
        var project = CreateProject("""
            import System.Threading.Tasks;
            namespace App;
            async Task<int> Continue(Task<int> pending, bool chooseValue) {
                var value = await pending;
                if (chooseValue) return value;
                return value + 1;
            }
            """);

        new SemanticBinder().Bind(project);

        var continuation = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Continue");
        var guarded = Assert.IsType<TryStatement>(Assert.Single(continuation.Body!));
        var body = Assert.IsType<BlockStatement>(guarded.Body).Statements;
        var callbackLocal = Assert.IsType<LocalVariableDeclarationStatement>(body[2]);
        var callback = Assert.IsType<LambdaExpression>(
            Assert.Single(callbackLocal.Declarators).Initializer);
        var callbackTry = Assert.IsType<TryStatement>(Assert.Single(callback.StatementBody!));
        var callbackStatements = Assert.IsType<BlockStatement>(callbackTry.Body).Statements;
        var conditional = Assert.IsType<IfStatement>(callbackStatements[1]);
        var conditionalBody = Assert.IsType<BlockStatement>(conditional.ThenStatement).Statements;
        Assert.Equal("SetResult", Assert.IsType<MemberAccessExpression>(
            Assert.IsType<InvocationExpression>(
                Assert.IsType<ExpressionStatement>(conditionalBody[0]).Expression).Target)
            .MemberName);
        Assert.Null(Assert.IsType<ReturnStatement>(conditionalBody[1]).Expression);
        Assert.Equal("SetResult", Assert.IsType<MemberAccessExpression>(
            Assert.IsType<InvocationExpression>(
                Assert.IsType<ExpressionStatement>(callbackStatements[^2]).Expression).Target)
            .MemberName);
        Assert.Null(Assert.IsType<ReturnStatement>(callbackStatements[^1]).Expression);
    }

    [Fact]
    public void AsyncVoidTaskContinuationCompletesFromConditionalReturnPaths()
    {
        var project = CreateProject("""
            import System.Threading.Tasks;
            namespace App;
            async Task Continue(Task pending, bool finishEarly) {
                await pending;
                if (finishEarly) return;
                return;
            }
            """);

        new SemanticBinder().Bind(project);

        var continuation = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Continue");
        var guarded = Assert.IsType<TryStatement>(Assert.Single(continuation.Body!));
        var body = Assert.IsType<BlockStatement>(guarded.Body).Statements;
        var callbackLocal = Assert.IsType<LocalVariableDeclarationStatement>(body[2]);
        var callback = Assert.IsType<LambdaExpression>(
            Assert.Single(callbackLocal.Declarators).Initializer);
        var callbackTry = Assert.IsType<TryStatement>(Assert.Single(callback.StatementBody!));
        var callbackStatements = Assert.IsType<BlockStatement>(callbackTry.Body).Statements;
        var conditional = Assert.IsType<IfStatement>(callbackStatements[1]);
        var conditionalBody = Assert.IsType<BlockStatement>(conditional.ThenStatement).Statements;
        Assert.Equal("SetResult", Assert.IsType<MemberAccessExpression>(
            Assert.IsType<InvocationExpression>(
                Assert.IsType<ExpressionStatement>(conditionalBody[0]).Expression).Target)
            .MemberName);
        Assert.Null(Assert.IsType<ReturnStatement>(conditionalBody[1]).Expression);
        Assert.Equal("SetResult", Assert.IsType<MemberAccessExpression>(
            Assert.IsType<InvocationExpression>(
                Assert.IsType<ExpressionStatement>(callbackStatements[^1]).Expression).Target)
            .MemberName);
    }

    [Fact]
    public void AsyncVoidTaskTwoAwaitsKeepReferencePrefixOutOfInlineCapture()
    {
        var project = CreateProject("""
            import System.Threading.Tasks;
            namespace App;
            async Task Run(Task first, Task second, string marker) {
                string prefix = marker;
                await first;
                await second;
                Record((int)prefix.Length);
            }
            void Record(int value) { }
            async Task RunInferred(Task first, Task second, string marker) {
                var prefix = marker;
                await first;
                await second;
                Record((int)prefix.Length);
            }
            """);

        new SemanticBinder().Bind(project);

        var outputDirectory = Path.Combine(
            Path.GetTempPath(), $"cxc-void-await-prefix-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(outputDirectory);
            CCodeOutputGenerator.GenerateOutput(
                project, Path.Combine(outputDirectory, "input.cx"));

            var generatedC = File.ReadAllText(Path.Combine(outputDirectory, "unnamed.c"));
            AssertPrefixCellIsPendingOnly(generatedC, "Run");
            AssertPrefixCellIsPendingOnly(generatedC, "RunInferred");
        }
        finally
        {
            if (Directory.Exists(outputDirectory))
            {
                Directory.Delete(outputDirectory, recursive: true);
            }
        }

        static void AssertPrefixCellIsPendingOnly(string generatedC, string functionName)
        {
            var methodStart = generatedC.IndexOf(
                $"{functionName}(", StringComparison.Ordinal);
            Assert.True(methodStart >= 0);
            var prefixDeclaration = generatedC.IndexOf(
                "= marker", methodStart, StringComparison.Ordinal);
            var firstReadinessCheck = generatedC.IndexOf(
                "IsCompleted", methodStart, StringComparison.Ordinal);
            var firstAllocation = generatedC.IndexOf(
                "Memory_Alloc", methodStart, StringComparison.Ordinal);
            Assert.True(prefixDeclaration >= 0 && prefixDeclaration < firstReadinessCheck,
                $"Expected {functionName}'s prefix declaration before its readiness check " +
                $"(prefix {prefixDeclaration}, readiness {firstReadinessCheck}).");
            Assert.True(firstReadinessCheck >= 0 && firstReadinessCheck < firstAllocation);
        }
    }

    [Fact]
    public void AsyncVoidTaskAwaitsFinallyBeforeCompletingAndPreservesTryFault()
    {
        var project = CreateProject("""
            import System.Threading.Tasks;
            namespace App;
            async Task Run(Task cleanup) {
                try {
                    int value = 1;
                    value = value + 1;
                } finally {
                    await cleanup;
                }
            }
            """);

        new SemanticBinder().Bind(project);

        var function = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(declaration => declaration.Name == "Run");
        var outerTry = Assert.IsType<TryStatement>(Assert.Single(function.Body!));
        var lowered = Assert.IsType<BlockStatement>(outerTry.Body).Statements;
        Assert.IsType<LocalVariableDeclarationStatement>(lowered[0]);
        var protectedTry = Assert.IsType<TryStatement>(lowered[1]);
        Assert.Single(protectedTry.CatchClauses);
        Assert.IsType<LocalVariableDeclarationStatement>(lowered[2]);
        var completionCheck = Assert.IsType<IfStatement>(lowered[3]);
        Assert.Equal("IsCompleted", Assert.IsType<MemberAccessExpression>(
            completionCheck.Condition).MemberName);
        var inlineBranch = Assert.IsType<BlockStatement>(completionCheck.ThenStatement);
        Assert.Contains(CoroutineSyntaxAnalysis.EnumerateStatements(inlineBranch.Statements),
            statement => statement is ReturnStatement
            {
                Expression: ObjectCreationExpression,
            });
        var pendingBranch = Assert.IsType<BlockStatement>(completionCheck.ElseStatement);
        var pendingDeclarations = CoroutineSyntaxAnalysis.EnumerateStatements(
                pendingBranch.Statements)
            .OfType<LocalVariableDeclarationStatement>()
            .SelectMany(declaration => declaration.Declarators)
            .ToArray();
        Assert.Contains(pendingDeclarations, declarator =>
            declarator.Name.StartsWith("__cx_async_cleanup_pending_exception", StringComparison.Ordinal));
        var continuationLocal = Assert.Single(pendingBranch.Statements
            .OfType<LocalVariableDeclarationStatement>(), declaration =>
            declaration.Declarators.Any(declarator =>
                declarator.Initializer is LambdaExpression));
        var continuation = Assert.IsType<LambdaExpression>(
            Assert.Single(continuationLocal.Declarators).Initializer);
        Assert.Contains(continuation.Captures,
            capture => capture.Local.Name.StartsWith(
                "__cx_async_cleanup_pending_exception", StringComparison.Ordinal));
        var callbackTry = Assert.IsType<TryStatement>(Assert.Single(continuation.StatementBody!));
        Assert.Equal(2, callbackTry.CatchClauses.Count);
        var callbackStatements = Assert.IsType<BlockStatement>(callbackTry.Body).Statements;
        var completionBranch = Assert.IsType<IfStatement>(callbackStatements[1]);
        Assert.Equal("SetException", Assert.IsType<MemberAccessExpression>(
            Assert.IsType<InvocationExpression>(
                Assert.IsType<ExpressionStatement>(completionBranch.ThenStatement).Expression).Target)
            .MemberName);
        Assert.Equal("SetResult", Assert.IsType<MemberAccessExpression>(
            Assert.IsType<InvocationExpression>(
                Assert.IsType<ExpressionStatement>(completionBranch.ElseStatement).Expression).Target)
            .MemberName);
    }

    [Fact]
    public void AsyncVoidTaskAwaitsMultipleCleanupTasksSequentially()
    {
        var project = CreateProject("""
            import System.Threading.Tasks;
            namespace App;
            async Task Run(Task first, Task second) {
                try { try { } finally { Cleanup(); } }
                finally {
                    await first;
                    await second;
                }
            }
            void Cleanup() { }
            """);

        new SemanticBinder().Bind(project);

        var function = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(declaration => declaration.Name == "Run");
        Assert.True(function.IsPendingAwaitRelay);
        Assert.DoesNotContain(CoroutineSyntaxAnalysis.EnumerateBodyExpressions(function.Body!),
            expression => expression is AwaitExpression);
        var firstContinuation = Assert.Single(CoroutineSyntaxAnalysis
            .EnumerateBodyExpressions(function.Body!).OfType<LambdaExpression>());
        Assert.Single(CoroutineSyntaxAnalysis
            .EnumerateBodyExpressions(firstContinuation.StatementBody!)
            .OfType<LambdaExpression>());
    }

    [Fact]
    public void AsyncVoidTaskUnwindsNestedAwaitedFinallyRegionsOutward()
    {
        var project = CreateProject("""
            import System.Threading.Tasks;
            namespace App;
            async Task Run(Task innerCleanup, Task outerCleanup) {
                try {
                    try { Cleanup(); }
                    finally { await innerCleanup; }
                }
                finally { await outerCleanup; }
            }
            void Cleanup() { }
            """);

        new SemanticBinder().Bind(project);

        var function = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(declaration => declaration.Name == "Run");
        Assert.True(function.IsPendingAwaitRelay);
        Assert.DoesNotContain(CoroutineSyntaxAnalysis.EnumerateBodyExpressions(function.Body!),
            expression => expression is AwaitExpression);
        var continuations = CoroutineSyntaxAnalysis
            .EnumerateBodyExpressions(function.Body!).OfType<LambdaExpression>().ToArray();
        Assert.Single(continuations);
        Assert.Contains(continuations, continuation =>
            CoroutineSyntaxAnalysis.EnumerateBodyExpressions(continuation.StatementBody!)
                .OfType<LambdaExpression>().Any());
    }

    [Fact]
    public void AsyncVoidTaskRunsAwaitedFinallyBeforeProtectedReturnCompletes()
    {
        var project = CreateProject("""
            import System.Threading.Tasks;
            namespace App;
            async Task Run(Task cleanup) {
                try { return; }
                finally { await cleanup; }
            }
            """);

        new SemanticBinder().Bind(project);

        var function = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(declaration => declaration.Name == "Run");
        Assert.True(function.IsPendingAwaitRelay);
        Assert.DoesNotContain(CoroutineSyntaxAnalysis.EnumerateBodyExpressions(function.Body!),
            expression => expression is AwaitExpression);
        Assert.Single(CoroutineSyntaxAnalysis.EnumerateBodyExpressions(function.Body!)
            .OfType<LambdaExpression>());
    }

    [Fact]
    public void AsyncTaskOfTTransfersReturnedValueThroughAwaitedFinally()
    {
        var project = CreateProject("""
            import System.Threading.Tasks;
            namespace App;
            async Task<int> Run(Task cleanup, int value) {
                try { return value + 1; }
                finally { await cleanup; }
            }
            """);

        new SemanticBinder().Bind(project);

        var function = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(declaration => declaration.Name == "Run");
        Assert.True(function.IsPendingAwaitRelay);
        Assert.DoesNotContain(CoroutineSyntaxAnalysis.EnumerateBodyExpressions(function.Body!),
            expression => expression is AwaitExpression);
        var exceptionWrapper = Assert.IsType<TryStatement>(Assert.Single(function.Body!));
        var loweredStatements = Assert.IsType<BlockStatement>(exceptionWrapper.Body).Statements;
        Assert.IsType<LocalVariableDeclarationStatement>(loweredStatements[0]);
        var protectedResult = Assert.IsType<TryStatement>(loweredStatements[2]);
        Assert.IsType<AssignmentExpression>(Assert.IsType<ExpressionStatement>(
            Assert.Single(Assert.IsType<BlockStatement>(protectedResult.Body).Statements)).Expression);
        var continuation = Assert.Single(CoroutineSyntaxAnalysis
            .EnumerateBodyExpressions(function.Body!).OfType<LambdaExpression>());
        Assert.Contains(CoroutineSyntaxAnalysis.EnumerateBodyExpressions(
                continuation.StatementBody!).OfType<InvocationExpression>(),
            invocation => invocation.Target is MemberAccessExpression { MemberName: "SetResult" });
    }

    [Fact]
    public void AsyncTaskOfTTransfersReturnedValueThroughNestedAwaitedFinally()
    {
        var project = CreateProject("""
            import System.Threading.Tasks;
            namespace App;
            async Task<int> Run(Task innerCleanup, Task outerCleanup, int value) {
                try {
                    try { return value; }
                    finally { await innerCleanup; }
                }
                finally { await outerCleanup; }
            }
            """);

        new SemanticBinder().Bind(project);

        var function = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(declaration => declaration.Name == "Run");
        Assert.True(function.IsPendingAwaitRelay);
        Assert.DoesNotContain(CoroutineSyntaxAnalysis.EnumerateBodyExpressions(function.Body!),
            expression => expression is AwaitExpression);
        var callback = Assert.Single(CoroutineSyntaxAnalysis
            .EnumerateBodyExpressions(function.Body!).OfType<LambdaExpression>());
        Assert.NotEmpty(CoroutineSyntaxAnalysis.EnumerateBodyExpressions(
            callback.StatementBody!).OfType<LambdaExpression>());
    }

    [Fact]
    public void AsyncVoidTaskCarriesLoopBreakAndContinueThroughAwaitedFinally()
    {
        var project = CreateProject("""
            import System.Threading.Tasks;
            namespace App;
            async Task BreakAfterCleanup(Task cleanup) {
                for (int index = 0; index < 2; index++) {
                    try { break; }
                    finally { await cleanup; }
                }
            }
            async Task ContinueAfterCleanup(Task cleanup) {
                for (int index = 0; index < 2; index++) {
                    try { continue; }
                    finally { await cleanup; }
                }
            }
            """);

        new SemanticBinder().Bind(project);

        var functions = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Where(declaration => declaration.Name is "BreakAfterCleanup" or "ContinueAfterCleanup")
            .ToArray();
        Assert.Equal(2, functions.Length);
        foreach (var function in functions)
        {
            Assert.True(function.IsPendingAwaitRelay);
            Assert.DoesNotContain(CoroutineSyntaxAnalysis.EnumerateBodyExpressions(function.Body!),
                expression => expression is AwaitExpression);
        }
    }

    [Fact]
    public void AsyncTaskOfTCarriesLoopBreakAndContinueThroughAwaitedFinally()
    {
        var project = CreateProject("""
            import System.Threading.Tasks;
            namespace App;
            async Task<int> BreakAfterCleanup(Task cleanup) {
                for (int index = 0; index < 2; index++) {
                    try { break; }
                    finally { await cleanup; }
                }
                return 42;
            }
            async Task<int> ContinueAfterCleanup(Task cleanup) {
                for (int index = 0; index < 1; index++) {
                    try { continue; }
                    finally { await cleanup; }
                }
                return 43;
            }
            """);

        new SemanticBinder().Bind(project);

        var functions = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Where(declaration => declaration.Name is "BreakAfterCleanup" or "ContinueAfterCleanup")
            .ToArray();
        Assert.Equal(2, functions.Length);
        foreach (var function in functions)
        {
            Assert.True(function.IsPendingAwaitRelay);
            Assert.DoesNotContain(CoroutineSyntaxAnalysis.EnumerateBodyExpressions(function.Body!),
                expression => expression is AwaitExpression);
        }
    }

    [Fact]
    public void AsyncTaskLoopInlineAwaitContinueUsesGeneratedStepLoop()
    {
        var project = CreateProject("""
            import System.Threading.Tasks;
            namespace App;
            async Task ContinueAfterCleanup(Task cleanup) {
                for (int index = 0; index < 2; index++) {
                    try { continue; }
                    finally { await cleanup; }
                }
            }
            """);

        new SemanticBinder().Bind(project);

        var function = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(declaration => declaration.Name == "ContinueAfterCleanup");
        var step = Assert.Single(CoroutineSyntaxAnalysis
            .EnumerateBodyExpressions(function.Body!).OfType<LambdaExpression>(),
            lambda => lambda.GeneratedCName?.EndsWith(
                "_step", StringComparison.Ordinal) == true);
        Assert.EndsWith("_step", step.GeneratedCName);
        var outputDirectory = Path.Combine(
            Path.GetTempPath(), $"cxc-await-loop-{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(outputDirectory);
            CCodeOutputGenerator.GenerateOutput(
                project, Path.Combine(outputDirectory, "input.cx"));
            var generatedC = File.ReadAllText(Path.Combine(outputDirectory, "unnamed.c"));
            Assert.Contains("_step", generatedC);
            Assert.Contains("continue;", generatedC);
        }
        finally
        {
            if (Directory.Exists(outputDirectory))
            {
                Directory.Delete(outputDirectory, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData("""
        async Task Run(Task pending) {
            try { await pending; }
            catch (Exception) {}
        }
        """)]
    [InlineData("""
        async Task Run(Task pending) {
            try {}
            catch (Exception) { await pending; }
        }
        """)]
    [InlineData("""
        async Task Run(Task<bool> filterTask) {
            try {}
            catch (Exception) when (await filterTask) {}
        }
        """)]
    public void RejectsAwaitAcrossTaskCatchRegions(string source)
    {
        var project = CreateProject($"import System;\nimport System.Threading.Tasks;\n{source}");

        var exception = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(project));

        Assert.Contains("Await is not supported in a try/catch body or exception filter",
            exception.Message);
    }

    [Fact]
    public void AsyncVoidTaskAwaitsInsideTryAndRunsSynchronousFinallyOnce()
    {
        var project = CreateProject("""
            import System.Threading.Tasks;
            namespace App;
            async Task Run(Task pending) {
                try { await pending; }
                finally { Cleanup(); }
            }
            void Cleanup() { }
            """);

        new SemanticBinder().Bind(project);

        var function = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(declaration => declaration.Name == "Run");
        Assert.True(function.IsPendingAwaitRelay);
        Assert.DoesNotContain(CoroutineSyntaxAnalysis.EnumerateBodyExpressions(function.Body!),
            expression => expression is AwaitExpression);
        var continuation = Assert.Single(CoroutineSyntaxAnalysis
            .EnumerateBodyExpressions(function.Body!).OfType<LambdaExpression>());
        var outerTry = Assert.IsType<TryStatement>(Assert.Single(continuation.StatementBody!));
        var callbackStatements = Assert.IsType<BlockStatement>(outerTry.Body).Statements;
        var protectedTry = Assert.IsType<TryStatement>(callbackStatements[0]);
        Assert.Single(Assert.IsType<BlockStatement>(protectedTry.FinallyBody).Statements);
    }

    [Fact]
    public void AsyncTaskContinuationCapturesLocalsDeclaredBeforeAwait()
    {
        var project = CreateProject("""
            import System.Threading.Tasks;
            namespace App;
            async Task<int> Continue(Task<int> pending) {
                int offset = 9;
                var value = await pending;
                return value + offset;
            }
            """);

        new SemanticBinder().Bind(project);

        var continuation = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Continue");
        var guarded = Assert.IsType<TryStatement>(Assert.Single(continuation.Body!));
        var body = Assert.IsType<BlockStatement>(guarded.Body).Statements;
        Assert.IsType<LocalVariableDeclarationStatement>(body[0]);
        var readiness = Assert.IsType<IfStatement>(body[1]);
        Assert.Equal("IsCompleted",
            Assert.IsType<MemberAccessExpression>(readiness.Condition).MemberName);
        var pending = Assert.IsType<BlockStatement>(readiness.ElseStatement).Statements;
        Assert.Contains(CoroutineSyntaxAnalysis.EnumerateBodyStatements(pending)
            .OfType<LocalVariableDeclarationStatement>()
            .SelectMany(declaration => declaration.Declarators),
            declarator => declarator.Name.StartsWith(
                "__cx_async_callback_prefix_offset", StringComparison.Ordinal));
    }

    [Fact]
    public void AsyncTaskChainsPendingAwaitsAndRetainsLocalsAcrossEachContinuation()
    {
        var project = CreateProject("""
            import System.Threading.Tasks;
            namespace App;
            async Task<int> Continue(Task<int> first, Task<int> second) {
                int baseline = 2;
                var left = await first;
                int adjusted = left + baseline;
                var right = await second;
                return adjusted + right;
            }
            """);

        new SemanticBinder().Bind(project);

        var continuation = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Continue");
        var guarded = Assert.IsType<TryStatement>(Assert.Single(continuation.Body!));
        var body = Assert.IsType<BlockStatement>(guarded.Body).Statements;
        Assert.IsType<LocalVariableDeclarationStatement>(body[0]);
        var firstCallbackLocal = Assert.IsType<LocalVariableDeclarationStatement>(body[3]);
        var firstCallback = Assert.IsType<LambdaExpression>(
            Assert.Single(firstCallbackLocal.Declarators).Initializer);
        Assert.Contains(firstCallback.Captures, capture => capture.Local.Name == "baseline");

        var firstCallbackTry = Assert.IsType<TryStatement>(
            Assert.Single(firstCallback.StatementBody!));
        var firstCallbackStatements = Assert.IsType<BlockStatement>(firstCallbackTry.Body).Statements;
        var secondCallbackLocal = Assert.IsType<LocalVariableDeclarationStatement>(
            firstCallbackStatements[3]);
        var secondCallback = Assert.IsType<LambdaExpression>(
            Assert.Single(secondCallbackLocal.Declarators).Initializer);
        Assert.Contains(secondCallback.Captures, capture => capture.Local.Name == "adjusted");
    }

    [Fact]
    public void GenericAsyncTaskAwaitedAutoLocalReturnBuildsPendingRelay()
    {
        var project = CreateProject("""
            import System.Threading.Tasks;
            namespace App;
            async Task<T> Continue<T>(Task<T> pending) {
                var result = await pending;
                return result;
            }
            """);

        new SemanticBinder().Bind(project);

        var continuation = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Continue");
        Assert.True(continuation.IsPendingAwaitRelay);
        var guarded = Assert.IsType<TryStatement>(Assert.Single(continuation.Body!));
        var body = Assert.IsType<BlockStatement>(guarded.Body).Statements;
        Assert.IsType<LocalVariableDeclarationStatement>(body[0]);
        Assert.IsType<IfStatement>(body[1]);
        Assert.IsType<LocalVariableDeclarationStatement>(body[2]);
    }

    [Fact]
    public void AsyncTaskTwoDirectTaskParametersCheckAllReadyBeforeRelayState()
    {
        var project = CreateProject("""
            import System.Threading.Tasks;
            namespace App;
            async Task<int> Continue(Task<int> first, Task<int> second) {
                var left = await first;
                var right = await second;
                return left;
            }
            """);

        new SemanticBinder().Bind(project);

        var continuation = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Continue");
        var guarded = Assert.IsType<TryStatement>(Assert.Single(continuation.Body!));
        var body = Assert.IsType<BlockStatement>(guarded.Body).Statements;
        var completionGuard = Assert.IsType<IfStatement>(Assert.Single(body));
        var condition = Assert.IsType<BinaryExpression>(completionGuard.Condition);
        Assert.Equal("&&", condition.Operator);
        var inlineStatements = Assert.IsType<BlockStatement>(
            completionGuard.ThenStatement).Statements;
        Assert.Equal(2, inlineStatements.OfType<LocalVariableDeclarationStatement>()
            .Count(declaration => declaration.Declarators.Single().Initializer is
                InvocationExpression
                {
                    Target: MemberAccessExpression { MemberName: "GetResult" },
                }));
        var pendingStatements = Assert.IsType<BlockStatement>(
            completionGuard.ElseStatement).Statements;
        Assert.Contains(pendingStatements.OfType<LocalVariableDeclarationStatement>(),
            declaration => declaration.Declarators.Any(declarator =>
                declarator.Initializer is LambdaExpression));
    }

    [Fact]
    public void GenericAsyncTaskMultipleTaskLocalsCreateCallbackAliases()
    {
        var project = CreateProject("""
            import System.Threading.Tasks;
            namespace App;
            async Task<T> Continue<T>(Task<T> first, Task<T> second) {
                var left = await first;
                var right = await second;
                return left;
            }
            """);

        new SemanticBinder().Bind(project);

        var continuation = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Continue");
        Assert.True(continuation.IsPendingAwaitRelay);
        var guarded = Assert.IsType<TryStatement>(Assert.Single(continuation.Body!));
        var relayBody = Assert.IsType<BlockStatement>(guarded.Body).Statements;
        var completionGuard = Assert.IsType<IfStatement>(relayBody[0]);
        var allCompleted = Assert.IsType<BinaryExpression>(completionGuard.Condition);
        Assert.Equal("&&", allCompleted.Operator);
        var inlineStatements = Assert.IsType<BlockStatement>(
            completionGuard.ThenStatement).Statements;
        Assert.Equal(2, inlineStatements.OfType<LocalVariableDeclarationStatement>()
            .Count(declaration => declaration.Declarators.Single().Initializer is
                InvocationExpression
                {
                    Target: MemberAccessExpression { MemberName: "GetResult" },
                }));
        var pendingStatements = Assert.IsType<BlockStatement>(
            completionGuard.ElseStatement).Statements;
        var callbackAlias = Assert.Single(pendingStatements
            .OfType<LocalVariableDeclarationStatement>()
            .SelectMany(declaration => declaration.Declarators),
            declarator => declarator.Name.StartsWith(
                "__cx_async_callback_prefix_second", StringComparison.Ordinal));
        Assert.Equal("second",
            Assert.IsType<IdentifierExpression>(callbackAlias.Initializer).Identifier.ToString());
        Assert.Contains(pendingStatements.OfType<LocalVariableDeclarationStatement>(),
            declaration => declaration.Declarators.Any(declarator =>
                declarator.Initializer is LambdaExpression));
    }

    [Fact]
    public void GenericAsyncTaskMultipleTaskLocalsDeferRelayStateAfterPrefix()
    {
        var project = CreateProject("""
            import System.Threading.Tasks;
            namespace App;
            public static class PrefixProbe {
                public static void Record(int value) { }
            }
            async Task<T> Continue<T>(Task<T> first, Task<T> second) {
                int baseline = 29;
                var left = await first;
                var right = await second;
                PrefixProbe.Record(baseline);
                return left;
            }
            """);

        new SemanticBinder().Bind(project);

        var continuation = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Continue");
        var guarded = Assert.IsType<TryStatement>(Assert.Single(continuation.Body!));
        var relayBody = Assert.IsType<BlockStatement>(guarded.Body).Statements;
        Assert.Equal(2, relayBody.Count);
        var baseline = Assert.IsType<LocalVariableDeclarationStatement>(relayBody[0]);
        Assert.Equal("baseline", baseline.Declarators.Single().Name);
        var readiness = Assert.IsType<IfStatement>(relayBody[1]);
        var readyCondition = Assert.IsType<BinaryExpression>(readiness.Condition);
        Assert.Equal("&&", readyCondition.Operator);
        var inline = Assert.IsType<BlockStatement>(readiness.ThenStatement).Statements;
        Assert.Equal(2, inline.OfType<LocalVariableDeclarationStatement>()
            .Count(declaration => declaration.Declarators.Single().Initializer is
                InvocationExpression
                {
                    Target: MemberAccessExpression { MemberName: "GetResult" },
                }));
        Assert.Contains(CoroutineSyntaxAnalysis.EnumerateBodyExpressions(inline)
            .OfType<InvocationExpression>(), invocation =>
                invocation.Target is MemberAccessExpression { MemberName: "Record" });
        var pending = Assert.IsType<BlockStatement>(readiness.ElseStatement).Statements;
        Assert.Contains(pending.OfType<LocalVariableDeclarationStatement>()
            .SelectMany(declaration => declaration.Declarators), declarator =>
                declarator.Name.StartsWith(
                    "__cx_async_callback_prefix_baseline", StringComparison.Ordinal));
        Assert.Contains(pending.OfType<LocalVariableDeclarationStatement>()
            .SelectMany(declaration => declaration.Declarators), declarator =>
                declarator.Name.StartsWith(
                    "__cx_async_callback_prefix_second", StringComparison.Ordinal));
    }

    [Fact]
    public void GenericOwnerAsyncTaskMultipleLocalsCreateCallbackAliases()
    {
        var project = CreateProject("""
            import System.Threading.Tasks;
            namespace App;
            public class Owner<T> {
                public async Task<T> Continue(Task<T> first, Task<T> second) {
                    var left = await first;
                    var right = await second;
                    return left;
                }
            }
            """);

        new SemanticBinder().Bind(project);

        var owner = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.ClassDeclaration>()
            .Single(declaration => declaration.Name == "Owner");
        var continuation = owner.MemberDeclarations.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Continue");
        Assert.True(continuation.IsPendingAwaitRelay);
        var guarded = Assert.IsType<TryStatement>(Assert.Single(continuation.Body!));
        var relayBody = Assert.IsType<BlockStatement>(guarded.Body).Statements;
        var completionGuard = Assert.IsType<IfStatement>(relayBody[0]);
        var allCompleted = Assert.IsType<BinaryExpression>(completionGuard.Condition);
        Assert.Equal("&&", allCompleted.Operator);
        var pendingStatements = Assert.IsType<BlockStatement>(
            completionGuard.ElseStatement).Statements;
        var callbackAlias = Assert.Single(pendingStatements
            .OfType<LocalVariableDeclarationStatement>()
            .SelectMany(declaration => declaration.Declarators),
            declarator => declarator.Name.StartsWith(
                "__cx_async_callback_prefix_second", StringComparison.Ordinal));
        Assert.Equal("second",
            Assert.IsType<IdentifierExpression>(callbackAlias.Initializer).Identifier.ToString());
        Assert.Contains(pendingStatements.OfType<LocalVariableDeclarationStatement>(),
            declaration => declaration.Declarators.Any(declarator =>
                declarator.Initializer is LambdaExpression));
    }

    [Fact]
    public void GenericOwnerAsyncTaskMultipleLocalsDeferRelayStateAfterPrefix()
    {
        var project = CreateProject("""
            import System.Threading.Tasks;
            namespace App;
            public static class PrefixProbe {
                public static void Record(int value) { }
            }
            public class Owner<T> {
                public async Task<T> Continue(Task<T> first, Task<T> second) {
                    int baseline = 29;
                    var left = await first;
                    var right = await second;
                    PrefixProbe.Record(baseline);
                    return left;
                }
            }
            """);

        new SemanticBinder().Bind(project);

        var owner = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.ClassDeclaration>()
            .Single(declaration => declaration.Name == "Owner");
        var continuation = owner.MemberDeclarations.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Continue");
        Assert.True(continuation.IsPendingAwaitRelay);
        var guarded = Assert.IsType<TryStatement>(Assert.Single(continuation.Body!));
        var relayBody = Assert.IsType<BlockStatement>(guarded.Body).Statements;
        Assert.Equal("baseline", Assert.IsType<LocalVariableDeclarationStatement>(relayBody[0])
            .Declarators.Single().Name);
        var readiness = Assert.IsType<IfStatement>(relayBody[1]);
        Assert.Equal("&&", Assert.IsType<BinaryExpression>(readiness.Condition).Operator);
        var pending = Assert.IsType<BlockStatement>(readiness.ElseStatement).Statements;
        var aliases = pending.OfType<LocalVariableDeclarationStatement>()
            .SelectMany(declaration => declaration.Declarators)
            .Select(declarator => declarator.Name)
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains(aliases, name => name.StartsWith(
            "__cx_async_callback_prefix_baseline", StringComparison.Ordinal));
        Assert.Contains(aliases, name => name.StartsWith(
            "__cx_async_callback_prefix_second", StringComparison.Ordinal));
    }

    [Fact]
    public void GenericAsyncTaskMultipleValueTaskLocalsCheckAllInlineBeforeRelayState()
    {
        var project = CreateProject("""
            import System.Threading.Tasks;
            namespace App;
            async Task<T> Relay<T>(ValueTask<T> first, ValueTask<T> second) {
                var firstResult = await first;
                var secondResult = await second;
                return firstResult;
            }
            """);

        new SemanticBinder().Bind(project);

        var relay = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Relay");
        Assert.True(relay.IsPendingAwaitRelay);
        var guarded = Assert.IsType<TryStatement>(Assert.Single(relay.Body!));
        var root = Assert.IsType<IfStatement>(
            Assert.Single(Assert.IsType<BlockStatement>(guarded.Body).Statements));
        var condition = Assert.IsType<BinaryExpression>(root.Condition);
        Assert.Equal("&&", condition.Operator);
        Assert.Equal("IsCompleted",
            Assert.IsType<MemberAccessExpression>(condition.Left).MemberName);
        Assert.Equal("IsCompleted",
            Assert.IsType<MemberAccessExpression>(condition.Right).MemberName);
        var inlineStatements = Assert.IsType<BlockStatement>(root.ThenStatement).Statements;
        Assert.Equal("firstResult",
            Assert.IsType<LocalVariableDeclarationStatement>(inlineStatements[0])
                .Declarators.Single().Name);
        Assert.Equal("secondResult",
            Assert.IsType<LocalVariableDeclarationStatement>(inlineStatements[1])
                .Declarators.Single().Name);
        Assert.IsType<ObjectCreationExpression>(
            Assert.IsType<ReturnStatement>(inlineStatements[^1]).Expression);
        var pendingStatements = Assert.IsType<BlockStatement>(root.ElseStatement).Statements;
        Assert.Contains(pendingStatements,
            statement => statement is LocalVariableDeclarationStatement
            {
                Declarators: [{ Initializer: ObjectCreationExpression }],
            });
    }

    [Fact]
    public void GenericAsyncTaskDirectReturnAwaitBuildsPendingCompletionRelay()
    {
        var project = CreateProject("""
            import System.Threading.Tasks;
            namespace App;
            async Task<T> Relay<T>(Task<T> pending) {
                return await pending;
            }
            """);

        new SemanticBinder().Bind(project);

        var relay = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Relay");
        var guarded = Assert.IsType<TryStatement>(Assert.Single(relay.Body!));
        var body = Assert.IsType<BlockStatement>(guarded.Body).Statements;
        Assert.IsType<LocalVariableDeclarationStatement>(body[0]);
        Assert.IsType<IfStatement>(body[1]);
        Assert.IsType<LocalVariableDeclarationStatement>(body[2]);
    }

    [Fact]
    public void GenericAsyncTaskDirectValueTaskAwaitChecksInlineBeforeRelayState()
    {
        var project = CreateProject("""
            import System.Threading.Tasks;
            namespace App;
            async Task<T> Relay<T>(ValueTask<T> pending) {
                return await pending;
            }
            Task<int> Invoke(ValueTask<int> pending) {
                return Relay(pending);
            }
            """);

        new SemanticBinder().Bind(project);

        var relay = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Relay");
        var guarded = Assert.IsType<TryStatement>(Assert.Single(relay.Body!));
        var body = Assert.IsType<BlockStatement>(guarded.Body).Statements;
        var inline = Assert.IsType<IfStatement>(body[0]);
        Assert.Equal("IsCompleted",
            Assert.IsType<MemberAccessExpression>(inline.Condition).MemberName);
        var completed = Assert.IsType<BlockStatement>(inline.ThenStatement).Statements;
        Assert.IsType<ObjectCreationExpression>(
            Assert.IsType<ReturnStatement>(completed[0]).Expression);
        var pendingRelay = body.Skip(1).ToArray();
        Assert.Contains(pendingRelay, statement => statement is LocalVariableDeclarationStatement
        {
            Declarators: [{ Initializer: ObjectCreationExpression }],
        });

        var invoke = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Invoke");
        var genericCall = Assert.Single(CoroutineSyntaxAnalysis
            .EnumerateBodyExpressions(invoke.Body!)
            .OfType<InvocationExpression>());
        var closedParameter = Assert.IsType<NamedType>(
            Assert.Single(genericCall.TargetSymbol!.ParameterTypes));
        Assert.Single(closedParameter.TypeArguments);
        Assert.Equal("Int", closedParameter.TypeArguments[0].Name);
        Assert.NotNull(closedParameter.ConstructedIdentity);
    }

    [Fact]
    public void AsyncTaskAwaitsValueTaskThroughItsTypedTaskBacking()
    {
        var project = CreateProject("""
            import System.Threading.Tasks;
            namespace App;
            async Task<int> Relay(ValueTask<int> pending) {
                return await pending;
            }
            """);

        new SemanticBinder().Bind(project);

        var relay = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Relay");
        var guarded = Assert.IsType<TryStatement>(Assert.Single(relay.Body!));
        var body = Assert.IsType<BlockStatement>(guarded.Body).Statements;
        var inline = Assert.IsType<IfStatement>(body[0]);
        Assert.Equal("IsCompleted",
            Assert.IsType<MemberAccessExpression>(inline.Condition).MemberName);
        var source = Assert.IsType<LocalVariableDeclarationStatement>(body[1]);
        var asTask = Assert.IsType<InvocationExpression>(
            source.Declarators.Single().Initializer);
        Assert.Equal("AsTask",
            Assert.IsType<MemberAccessExpression>(asTask.Target).MemberName);
    }

    [Fact]
    public void AsyncTaskLocalValueTaskAwaitsMaterializeContinuationOnlyInPendingBranch()
    {
        var project = CreateProject("""
            import System.Threading.Tasks;
            namespace App;
            async Task<int> Relay(ValueTask<int> pending) {
                var result = await pending;
                return result + 1;
            }
            """);

        new SemanticBinder().Bind(project);

        var relay = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Relay");
        var guarded = Assert.IsType<TryStatement>(Assert.Single(relay.Body!));
        var root = Assert.IsType<IfStatement>(
            Assert.Single(Assert.IsType<BlockStatement>(guarded.Body).Statements));
        Assert.Equal("IsCompleted",
            Assert.IsType<MemberAccessExpression>(root.Condition).MemberName);

        var inlineBlock = Assert.IsType<BlockStatement>(root.ThenStatement);
        var inlineResult = Assert.IsType<LocalVariableDeclarationStatement>(
            inlineBlock.Statements[0]);
        var inlineGetResult = Assert.IsType<InvocationExpression>(
            inlineResult.Declarators.Single().Initializer);
        Assert.Equal("GetResult",
            Assert.IsType<MemberAccessExpression>(inlineGetResult.Target).MemberName);
        Assert.IsType<ObjectCreationExpression>(
            Assert.IsType<ReturnStatement>(inlineBlock.Statements[1]).Expression);

        var pendingBlock = Assert.IsType<BlockStatement>(root.ElseStatement);
        var pendingStatements = pendingBlock.Statements;
        var asTask = Assert.IsType<InvocationExpression>(
            Assert.IsType<LocalVariableDeclarationStatement>(pendingStatements[0])
                .Declarators.Single().Initializer);
        Assert.Equal("AsTask",
            Assert.IsType<MemberAccessExpression>(asTask.Target).MemberName);
        Assert.IsType<ObjectCreationExpression>(
            Assert.IsType<LocalVariableDeclarationStatement>(pendingStatements[1])
                .Declarators.Single().Initializer);
        Assert.IsType<LambdaExpression>(
            Assert.IsType<LocalVariableDeclarationStatement>(pendingStatements[5])
                .Declarators.Single().Initializer);
    }

    [Fact]
    public void AsyncValueTaskDirectReturnAwaitUsesInlineValueAndPendingTaskBacking()
    {
        var project = CreateProject("""
            import System.Threading.Tasks;
            namespace App;
            async ValueTask<int> Relay(ValueTask<int> source) {
                return await source;
            }
            async ValueTask RelayVoid(ValueTask source) {
                await source;
            }
            async ValueTask<int> Inner(ValueTask<int> source) {
                return await source;
            }
            async ValueTask<int> Outer(ValueTask<int> source) {
                ValueTask<int> nested = Inner(source);
                return await nested;
            }
            async ValueTask<int> OuterTwice(ValueTask<int> source) {
                ValueTask<int> nested = Outer(source);
                return await nested;
            }
            async ValueTask InnerVoid(ValueTask source) {
                await source;
            }
            async ValueTask OuterVoid(ValueTask source) {
                ValueTask nested = InnerVoid(source);
                await nested;
            }
            async ValueTask OuterVoidTwice(ValueTask source) {
                ValueTask nested = OuterVoid(source);
                await nested;
            }
            """);

        new SemanticBinder().Bind(project);

        var relay = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Relay");
        var branch = Assert.IsType<IfStatement>(Assert.Single(relay.Body!));
        Assert.Equal("IsCompleted",
            Assert.IsType<MemberAccessExpression>(branch.Condition).MemberName);
        var completed = Assert.IsType<TryStatement>(branch.ThenStatement);
        Assert.Equal(2, completed.CatchClauses.Count);
        var completedReturn = Assert.IsType<ReturnStatement>(
            Assert.Single(Assert.IsType<BlockStatement>(completed.Body).Statements));
        var inlineValueTask = Assert.IsType<ObjectCreationExpression>(
            completedReturn.Expression);
        Assert.Contains("ValueTask", inlineValueTask.RequestedType.FullName.ToString());
        Assert.Equal("GetResult",
            Assert.IsType<MemberAccessExpression>(
                Assert.IsType<InvocationExpression>(inlineValueTask.Arguments[0]).Target)
                .MemberName);
        var pendingBlock = Assert.IsType<BlockStatement>(branch.ElseStatement);
        var pendingReturn = Assert.IsType<ReturnStatement>(pendingBlock.Statements[^1]);
        var pendingValueTask = Assert.IsType<IdentifierExpression>(
            pendingReturn.Expression);
        Assert.Contains(pendingBlock.Statements,
            statement => statement is ExpressionStatement expression &&
                expression.Expression is InvocationExpression invocation &&
                invocation.Target is MemberAccessExpression member &&
                member.MemberName == "SetTask");
        var setTask = Assert.IsType<InvocationExpression>(
            Assert.IsType<ExpressionStatement>(pendingBlock.Statements[1])
                .Expression);
        Assert.Equal("AsTask",
            Assert.IsType<MemberAccessExpression>(
                Assert.IsType<InvocationExpression>(setTask.Arguments[0]).Target)
                .MemberName);

        var relayVoid = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "RelayVoid");
        var voidBranch = Assert.IsType<IfStatement>(Assert.Single(relayVoid.Body!));
        var voidCompleted = Assert.IsType<TryStatement>(voidBranch.ThenStatement);
        var voidReturn = Assert.IsType<ReturnStatement>(Assert.Single(
            Assert.IsType<BlockStatement>(voidCompleted.Body).Statements));
        Assert.Empty(Assert.IsType<ObjectCreationExpression>(
            voidReturn.Expression).Arguments);
        var voidPendingReturn = Assert.IsType<ReturnStatement>(voidBranch.ElseStatement);
        Assert.Single(Assert.IsType<ObjectCreationExpression>(
            voidPendingReturn.Expression).Arguments);

        var outer = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Outer");
        var outerPrefix = Assert.IsType<LocalVariableDeclarationStatement>(
            outer.Body![0]);
        Assert.IsType<InvocationExpression>(
            outerPrefix.Declarators.Single().Initializer);
        var outerBranch = Assert.IsType<IfStatement>(outer.Body[1]);
        Assert.Equal("nested",
            Assert.IsType<IdentifierExpression>(
                Assert.IsType<MemberAccessExpression>(outerBranch.Condition).Target)
                .Identifier.ToString());
        var outerTwice = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "OuterTwice");
        Assert.IsType<InvocationExpression>(
            Assert.IsType<LocalVariableDeclarationStatement>(outerTwice.Body![0])
                .Declarators.Single().Initializer);
        Assert.IsType<IfStatement>(outerTwice.Body[1]);

        var outerVoid = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "OuterVoid");
        Assert.IsType<LocalVariableDeclarationStatement>(outerVoid.Body![0]);
        Assert.IsType<IfStatement>(outerVoid.Body[1]);
        var outerVoidTwice = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "OuterVoidTwice");
        Assert.IsType<LocalVariableDeclarationStatement>(outerVoidTwice.Body![0]);
        Assert.IsType<IfStatement>(outerVoidTwice.Body[1]);

    }

    [Fact]
    public void GenericAsyncValueTaskDirectReturnAwaitUsesInlineValueAndPendingTaskBacking()
    {
        var project = CreateProject("""
            import System.Threading.Tasks;
            namespace App;
            async ValueTask<T> Relay<T>(ValueTask<T> source) {
                return await source;
            }
            """);

        new SemanticBinder().Bind(project);

        var relay = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Relay");
        var branch = Assert.IsType<IfStatement>(Assert.Single(relay.Body!));
        Assert.Equal("IsCompleted",
            Assert.IsType<MemberAccessExpression>(branch.Condition).MemberName);
        var completed = Assert.IsType<TryStatement>(branch.ThenStatement);
        var inlineReturn = Assert.IsType<ReturnStatement>(
            Assert.Single(Assert.IsType<BlockStatement>(completed.Body).Statements));
        var inlineValueTask = Assert.IsType<ObjectCreationExpression>(inlineReturn.Expression);
        Assert.Single(inlineValueTask.Arguments);
        var pending = Assert.IsType<BlockStatement>(branch.ElseStatement);
        Assert.IsType<IdentifierExpression>(
            Assert.IsType<ReturnStatement>(pending.Statements[^1]).Expression);
        Assert.Contains(pending.Statements,
            statement => statement is ExpressionStatement expression &&
                expression.Expression is InvocationExpression invocation &&
                invocation.Target is MemberAccessExpression member &&
                member.MemberName == "SetTask");
    }

    [Fact]
    public void GenericAsyncValueTaskReturnAwaitPreservesExplicitPrefixLocal()
    {
        var project = CreateProject("""
            import System.Threading.Tasks;
            namespace App;
            async ValueTask<T> Relay<T>(ValueTask<T> input) {
                ValueTask<T> source = input;
                return await source;
            }
            """);

        new SemanticBinder().Bind(project);

        var relay = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Relay");
        Assert.IsType<LocalVariableDeclarationStatement>(relay.Body![0]);
        var branch = Assert.IsType<IfStatement>(relay.Body[1]);
        Assert.Equal("IsCompleted",
            Assert.IsType<MemberAccessExpression>(branch.Condition).MemberName);
        Assert.Contains(Assert.IsType<BlockStatement>(branch.ElseStatement).Statements,
            statement => statement is ExpressionStatement expression &&
                expression.Expression is InvocationExpression invocation &&
                invocation.Target is MemberAccessExpression member &&
                member.MemberName == "SetTask");
    }

    [Fact]
    public void GenericAsyncValueTaskReturnAwaitPreservesTwoExplicitPrefixLocals()
    {
        var project = CreateProject("""
            import System.Threading.Tasks;
            namespace App;
            async ValueTask<T> Relay<T>(ValueTask<T> input) {
                int prefix = 17;
                ValueTask<T> source = input;
                return await source;
            }
            """);

        new SemanticBinder().Bind(project);

        var relay = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Relay");
        var prefix = Assert.IsType<LocalVariableDeclarationStatement>(relay.Body![0]);
        Assert.Equal("prefix", prefix.Declarators.Single().Name);
        var source = Assert.IsType<LocalVariableDeclarationStatement>(relay.Body[1]);
        Assert.Equal("source", source.Declarators.Single().Name);
        Assert.Equal("input", Assert.IsType<IdentifierExpression>(
            source.Declarators.Single().Initializer).Identifier.ToString());
        var branch = Assert.IsType<IfStatement>(relay.Body[2]);
        Assert.Equal("IsCompleted",
            Assert.IsType<MemberAccessExpression>(branch.Condition).MemberName);
    }

    [Fact]
    public void GenericAsyncValueTaskReturnAwaitSupportsNestedGenericCallSource()
    {
        var project = CreateProject("""
            import System.Threading.Tasks;
            namespace App;
            async ValueTask<T> Inner<T>(ValueTask<T> source) {
                return await source;
            }
            async ValueTask<T> Outer<T>(ValueTask<T> input) {
                ValueTask<T> nested = Inner(input);
                return await nested;
            }
            """);

        new SemanticBinder().Bind(project);

        var outer = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Outer");
        var prefix = Assert.IsType<LocalVariableDeclarationStatement>(outer.Body![0]);
        Assert.IsType<InvocationExpression>(prefix.Declarators.Single().Initializer);
        var branch = Assert.IsType<IfStatement>(outer.Body[1]);
        Assert.Equal("IsCompleted",
            Assert.IsType<MemberAccessExpression>(branch.Condition).MemberName);
    }

    [Fact]
    public void AsyncTaskMultipleValueTaskLocalsCheckAllInlineBeforeRelayAllocation()
    {
        var project = CreateProject("""
            import System.Threading.Tasks;
            namespace App;
            async Task<int> Relay(ValueTask<int> first, ValueTask<int> second) {
                var firstResult = await first;
                firstResult++;
                ForceCollection();
                var secondResult = await second;
                return firstResult + secondResult;
            }
            public extern void ForceCollection();
            """);

        new SemanticBinder().Bind(project);

        var relay = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Relay");
        var guarded = Assert.IsType<TryStatement>(Assert.Single(relay.Body!));
        var body = Assert.IsType<BlockStatement>(guarded.Body).Statements;
        var root = Assert.IsType<IfStatement>(Assert.Single(body));
        var condition = Assert.IsType<BinaryExpression>(root.Condition);
        Assert.Equal("&&", condition.Operator);
        Assert.Equal("IsCompleted", Assert.IsType<MemberAccessExpression>(condition.Left).MemberName);
        Assert.Equal("IsCompleted", Assert.IsType<MemberAccessExpression>(condition.Right).MemberName);

        var inlineStatements = Assert.IsType<BlockStatement>(root.ThenStatement).Statements;
        Assert.Equal("firstResult",
            Assert.Single(Assert.IsType<LocalVariableDeclarationStatement>(inlineStatements[0])
                .Declarators).Name);
        Assert.IsType<UnaryExpression>(inlineStatements[1] is ExpressionStatement update
            ? update.Expression
            : null);
        Assert.IsType<InvocationExpression>(inlineStatements[2] is ExpressionStatement call
            ? call.Expression
            : null);
        Assert.Equal("secondResult",
            Assert.Single(Assert.IsType<LocalVariableDeclarationStatement>(inlineStatements[3])
                .Declarators).Name);
        Assert.IsType<ObjectCreationExpression>(
            Assert.IsType<ReturnStatement>(inlineStatements[^1]).Expression);

        var pendingStatements = Assert.IsType<BlockStatement>(root.ElseStatement).Statements;
        Assert.Contains(pendingStatements, statement =>
            statement is LocalVariableDeclarationStatement
            {
                Declarators: [{ Initializer: ObjectCreationExpression }],
            });
    }

    [Fact]
    public void AsyncTaskAllCompletedLaterConditionalCallDefersRelayAllocation()
    {
        var project = CreateProject("""
            import System.Threading.Tasks;
            namespace App;
            Task<int> FirstSource(Task<int> source) { return source; }
            Task<int> SecondSource(Task<int> source) { return source; }
            async Task<int> Combine(
                Task<int> first, Task<int> second, bool chooseFirst) {
                var left = await first;
                Task<int> selected = chooseFirst
                    ? FirstSource(second)
                    : SecondSource(second);
                var right = await selected;
                return left + right;
            }
            """);

        new SemanticBinder().Bind(project);

        var combine = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Combine");
        var guarded = Assert.IsType<TryStatement>(Assert.Single(combine.Body!));
        var body = Assert.IsType<BlockStatement>(guarded.Body).Statements;

        // The first ready check must precede every relay allocation. The
        // conditional source call must remain after the first result is read.
        var firstReady = Assert.IsType<IfStatement>(Assert.Single(body));
        Assert.Equal("IsCompleted", Assert.IsType<MemberAccessExpression>(
            firstReady.Condition).MemberName);
        var firstInline = Assert.IsType<BlockStatement>(firstReady.ThenStatement).Statements;
        Assert.Equal("left", Assert.IsType<LocalVariableDeclarationStatement>(
            firstInline[0]).Declarators.Single().Name);
        Assert.Equal("selected", Assert.IsType<LocalVariableDeclarationStatement>(
            firstInline[1]).Declarators.Single().Name);
        var secondReady = Assert.IsType<IfStatement>(firstInline[2]);
        Assert.Equal("IsCompleted", Assert.IsType<MemberAccessExpression>(
            secondReady.Condition).MemberName);
        var allInline = Assert.IsType<BlockStatement>(secondReady.ThenStatement).Statements;
        Assert.IsType<ObjectCreationExpression>(
            Assert.IsType<ReturnStatement>(allInline[^1]).Expression);

        var laterPending = Assert.IsType<BlockStatement>(secondReady.ElseStatement).Statements;
        Assert.Contains(laterPending, statement =>
            statement is LocalVariableDeclarationStatement
            {
                Declarators: [{ Initializer: ObjectCreationExpression }],
            });
        Assert.DoesNotContain(firstInline, statement =>
            statement is LocalVariableDeclarationStatement
            {
                Declarators: [{ Initializer: ObjectCreationExpression }],
            });

        var firstPending = Assert.IsType<BlockStatement>(firstReady.ElseStatement).Statements;
        Assert.Contains(firstPending, statement =>
            statement is LocalVariableDeclarationStatement
            {
                Declarators: [{ Initializer: ObjectCreationExpression }],
            });
    }

    [Fact]
    public void AsyncTaskAllCompletedLaterDirectCallDefersRelayAllocation()
    {
        var project = CreateProject("""
            import System.Threading.Tasks;
            namespace App;
            Task<int> LaterSource(int priorResult, Task<int> source) {
                return source;
            }
            async Task<int> Combine(Task<int> first, Task<int> second) {
                var left = await first;
                Task<int> selected = LaterSource(left, second);
                var right = await selected;
                return left + right;
            }
            """);

        new SemanticBinder().Bind(project);

        var combine = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Combine");
        var guarded = Assert.IsType<TryStatement>(Assert.Single(combine.Body!));
        var body = Assert.IsType<BlockStatement>(guarded.Body).Statements;
        var firstReady = Assert.IsType<IfStatement>(Assert.Single(body));
        Assert.Equal("IsCompleted", Assert.IsType<MemberAccessExpression>(
            firstReady.Condition).MemberName);

        var firstInline = Assert.IsType<BlockStatement>(firstReady.ThenStatement)
            .Statements;
        Assert.Equal("left", Assert.IsType<LocalVariableDeclarationStatement>(
            firstInline[0]).Declarators.Single().Name);
        var selected = Assert.IsType<LocalVariableDeclarationStatement>(firstInline[1]);
        Assert.IsType<InvocationExpression>(
            Assert.Single(selected.Declarators).Initializer);
        var secondReady = Assert.IsType<IfStatement>(firstInline[2]);
        Assert.Equal("IsCompleted", Assert.IsType<MemberAccessExpression>(
            secondReady.Condition).MemberName);
        var allInline = Assert.IsType<BlockStatement>(secondReady.ThenStatement)
            .Statements;
        Assert.IsType<ObjectCreationExpression>(
            Assert.IsType<ReturnStatement>(allInline[^1]).Expression);
        Assert.DoesNotContain(firstInline, statement =>
            statement is LocalVariableDeclarationStatement
            {
                Declarators: [{ Initializer: ObjectCreationExpression }],
            });

        var firstPending = Assert.IsType<BlockStatement>(firstReady.ElseStatement)
            .Statements;
        Assert.Contains(firstPending, statement =>
            statement is LocalVariableDeclarationStatement
            {
                Declarators: [{ Initializer: ObjectCreationExpression }],
            });
        var secondPending = Assert.IsType<BlockStatement>(secondReady.ElseStatement)
            .Statements;
        Assert.Contains(secondPending, statement =>
            statement is LocalVariableDeclarationStatement
            {
                Declarators: [{ Initializer: ObjectCreationExpression }],
            });
    }

    [Fact]
    public void AsyncTaskAllCompletedThreeAwaitCallSourcesDefersRelayAllocation()
    {
        var project = CreateProject("""
            import System.Threading.Tasks;
            namespace App;
            Task<int> LaterSource(int priorResult, Task<int> source) {
                return source;
            }
            async Task<int> Combine(
                Task<int> first, Task<int> second, Task<int> third) {
                var left = await first;
                Task<int> selected = LaterSource(left, second);
                var middle = await selected;
                Task<int> finalSelected = App.LaterSource(middle, third);
                var right = await finalSelected;
                return left + middle + right;
            }
            """);

        new SemanticBinder().Bind(project);

        var combine = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Combine");
        var guarded = Assert.IsType<TryStatement>(Assert.Single(combine.Body!));
        var firstReady = Assert.IsType<IfStatement>(
            Assert.Single(Assert.IsType<BlockStatement>(guarded.Body).Statements));
        Assert.Equal("IsCompleted",
            Assert.IsType<MemberAccessExpression>(firstReady.Condition).MemberName);

        var firstInline = Assert.IsType<BlockStatement>(firstReady.ThenStatement).Statements;
        Assert.Equal("left", Assert.IsType<LocalVariableDeclarationStatement>(
            firstInline[0]).Declarators.Single().Name);
        var selected = Assert.IsType<LocalVariableDeclarationStatement>(firstInline[1]);
        Assert.IsType<InvocationExpression>(selected.Declarators.Single().Initializer);
        var secondReady = Assert.IsType<IfStatement>(firstInline[2]);
        var secondInline = Assert.IsType<BlockStatement>(secondReady.ThenStatement).Statements;
        Assert.Equal("middle", Assert.IsType<LocalVariableDeclarationStatement>(
            secondInline[0]).Declarators.Single().Name);
        var finalSelected = Assert.IsType<LocalVariableDeclarationStatement>(secondInline[1]);
        Assert.IsType<InvocationExpression>(finalSelected.Declarators.Single().Initializer);
        var thirdReady = Assert.IsType<IfStatement>(secondInline[2]);
        var thirdInline = Assert.IsType<BlockStatement>(thirdReady.ThenStatement).Statements;
        Assert.Equal("right", Assert.IsType<LocalVariableDeclarationStatement>(
            thirdInline[0]).Declarators.Single().Name);
        Assert.IsType<ObjectCreationExpression>(
            Assert.IsType<ReturnStatement>(thirdInline[^1]).Expression);

        foreach (var pending in new[] { firstReady.ElseStatement!, secondReady.ElseStatement!,
                     thirdReady.ElseStatement! })
        {
            Assert.Contains(Assert.IsType<BlockStatement>(pending).Statements, statement =>
                statement is LocalVariableDeclarationStatement
                {
                    Declarators: [{ Initializer: ObjectCreationExpression }],
                });
        }
        Assert.DoesNotContain(firstInline, statement =>
            statement is LocalVariableDeclarationStatement
            {
                Declarators: [{ Initializer: ObjectCreationExpression }],
            });
        Assert.DoesNotContain(secondInline, statement =>
            statement is LocalVariableDeclarationStatement
            {
                Declarators: [{ Initializer: ObjectCreationExpression }],
            });
    }

    [Fact]
    public void AsyncTaskAllCompletedThreeAwaitMutatedCallSourcesDefersRelayAllocation()
    {
        var project = CreateProject("""
            import System.Threading.Tasks;
            namespace App;
            Task<int> LaterSource(int priorResult, Task<int> source) {
                return source;
            }
            async Task<int> Combine(
                Task<int> first, Task<int> second, Task<int> third) {
                var left = await first;
                left += 1;
                Task<int> selected = LaterSource(left, second);
                var middle = await selected;
                middle += 2;
                Task<int> finalSelected = LaterSource(middle, third);
                var right = await finalSelected;
                return left + middle + right;
            }
            """);

        new SemanticBinder().Bind(project);

        var combine = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Combine");
        var guarded = Assert.IsType<TryStatement>(Assert.Single(combine.Body!));
        var firstReady = Assert.IsType<IfStatement>(
            Assert.Single(Assert.IsType<BlockStatement>(guarded.Body).Statements));
        var firstInline = Assert.IsType<BlockStatement>(firstReady.ThenStatement).Statements;
        Assert.Equal("left", Assert.IsType<LocalVariableDeclarationStatement>(
            firstInline[0]).Declarators.Single().Name);
        var firstMutation = Assert.IsType<AssignmentExpression>(
            Assert.IsType<ExpressionStatement>(firstInline[1]).Expression);
        Assert.Equal("left",
            Assert.IsType<IdentifierExpression>(firstMutation.Target).Identifier.ToString());
        Assert.Equal("+=", firstMutation.Operator);
        Assert.IsType<InvocationExpression>(
            Assert.IsType<LocalVariableDeclarationStatement>(firstInline[2])
                .Declarators.Single().Initializer);

        var secondReady = Assert.IsType<IfStatement>(firstInline[3]);
        var secondInline = Assert.IsType<BlockStatement>(secondReady.ThenStatement).Statements;
        Assert.Equal("middle", Assert.IsType<LocalVariableDeclarationStatement>(
            secondInline[0]).Declarators.Single().Name);
        var secondMutation = Assert.IsType<AssignmentExpression>(
            Assert.IsType<ExpressionStatement>(secondInline[1]).Expression);
        Assert.Equal("middle",
            Assert.IsType<IdentifierExpression>(secondMutation.Target).Identifier.ToString());
        Assert.Equal("+=", secondMutation.Operator);
        Assert.IsType<InvocationExpression>(
            Assert.IsType<LocalVariableDeclarationStatement>(secondInline[2])
                .Declarators.Single().Initializer);

        var thirdReady = Assert.IsType<IfStatement>(secondInline[3]);
        var thirdInline = Assert.IsType<BlockStatement>(thirdReady.ThenStatement).Statements;
        Assert.Equal("right", Assert.IsType<LocalVariableDeclarationStatement>(
            thirdInline[0]).Declarators.Single().Name);
        Assert.IsType<ObjectCreationExpression>(
            Assert.IsType<ReturnStatement>(thirdInline[^1]).Expression);

        foreach (var pending in new[] { firstReady.ElseStatement!, secondReady.ElseStatement!,
                     thirdReady.ElseStatement! })
        {
            Assert.Contains(Assert.IsType<BlockStatement>(pending).Statements, statement =>
                statement is LocalVariableDeclarationStatement
                {
                    Declarators: [{ Initializer: ObjectCreationExpression }],
                });
        }
    }

    [Fact]
    public void AsyncTaskAllCompletedThreeAwaitLocalBeforeCallSourceDefersRelayAllocation()
    {
        var project = CreateProject("""
            import System.Threading.Tasks;
            namespace App;
            Task<int> LaterSource(int priorResult, Task<int> source) {
                return source;
            }
            async Task<int> Combine(
                Task<int> first, Task<int> second, Task<int> third) {
                var left = await first;
                int selectedPrior = left + 1;
                Task<int> selected = LaterSource(selectedPrior, second);
                var middle = await selected;
                int finalPrior = middle + 2;
                Task<int> finalSelected = LaterSource(finalPrior, third);
                var right = await finalSelected;
                return left + middle + right;
            }
            """);

        new SemanticBinder().Bind(project);

        var combine = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Combine");
        var guarded = Assert.IsType<TryStatement>(Assert.Single(combine.Body!));
        var firstReady = Assert.IsType<IfStatement>(
            Assert.Single(Assert.IsType<BlockStatement>(guarded.Body).Statements));
        var firstInline = Assert.IsType<BlockStatement>(firstReady.ThenStatement).Statements;
        Assert.Equal("left", Assert.IsType<LocalVariableDeclarationStatement>(
            firstInline[0]).Declarators.Single().Name);
        var selectedPrior = Assert.IsType<LocalVariableDeclarationStatement>(firstInline[1]);
        Assert.Equal("selectedPrior", selectedPrior.Declarators.Single().Name);
        Assert.IsType<CxCompiler.Model.Types.BuiltInTypes.IntType>(selectedPrior.DeclaredType);
        Assert.IsType<BinaryExpression>(selectedPrior.Declarators.Single().Initializer);
        Assert.Equal("selected", Assert.IsType<LocalVariableDeclarationStatement>(
            firstInline[2]).Declarators.Single().Name);

        var secondReady = Assert.IsType<IfStatement>(firstInline[3]);
        var secondInline = Assert.IsType<BlockStatement>(secondReady.ThenStatement).Statements;
        Assert.Equal("middle", Assert.IsType<LocalVariableDeclarationStatement>(
            secondInline[0]).Declarators.Single().Name);
        var finalPrior = Assert.IsType<LocalVariableDeclarationStatement>(secondInline[1]);
        Assert.Equal("finalPrior", finalPrior.Declarators.Single().Name);
        Assert.IsType<CxCompiler.Model.Types.BuiltInTypes.IntType>(finalPrior.DeclaredType);
        Assert.Equal("finalSelected", Assert.IsType<LocalVariableDeclarationStatement>(
            secondInline[2]).Declarators.Single().Name);

        var thirdReady = Assert.IsType<IfStatement>(secondInline[3]);
        var thirdInline = Assert.IsType<BlockStatement>(thirdReady.ThenStatement).Statements;
        Assert.Equal("right", Assert.IsType<LocalVariableDeclarationStatement>(
            thirdInline[0]).Declarators.Single().Name);
        Assert.IsType<ObjectCreationExpression>(
            Assert.IsType<ReturnStatement>(thirdInline[^1]).Expression);
    }

    [Fact]
    public void AsyncTaskAllCompletedThreeAwaitInferredLocalBeforeCallSourceDefersRelayAllocation()
    {
        var project = CreateProject("""
            import System.Threading.Tasks;
            namespace App;
            Task<int> LaterSource(int priorResult, Task<int> source) {
                return source;
            }
            async Task<int> Combine(
                Task<int> first, Task<int> second, Task<int> third) {
                var left = await first;
                var selectedPrior = left + 1;
                Task<int> selected = LaterSource(selectedPrior, second);
                var middle = await selected;
                var finalPrior = middle + 2;
                Task<int> finalSelected = LaterSource(finalPrior, third);
                var right = await finalSelected;
                return left + middle + right;
            }
            """);

        new SemanticBinder().Bind(project);

        var combine = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Combine");
        var guarded = Assert.IsType<TryStatement>(Assert.Single(combine.Body!));
        var firstReady = Assert.IsType<IfStatement>(
            Assert.Single(Assert.IsType<BlockStatement>(guarded.Body).Statements));
        var firstInline = Assert.IsType<BlockStatement>(firstReady.ThenStatement).Statements;
        Assert.Equal("left", Assert.IsType<LocalVariableDeclarationStatement>(
            firstInline[0]).Declarators.Single().Name);
        var selectedPrior = Assert.IsType<LocalVariableDeclarationStatement>(firstInline[1]);
        Assert.Equal("selectedPrior", selectedPrior.Declarators.Single().Name);
        Assert.IsType<CxCompiler.Model.Types.AutoType>(selectedPrior.DeclaredType);
        var selectedPriorDeclarator = selectedPrior.Declarators.Single();
        Assert.IsType<CxCompiler.Model.Types.BuiltInTypes.IntType>(selectedPriorDeclarator.Type);
        Assert.NotNull(selectedPriorDeclarator.Symbol);
        Assert.IsType<BinaryExpression>(selectedPriorDeclarator.Initializer);
        Assert.Equal("selected", Assert.IsType<LocalVariableDeclarationStatement>(
            firstInline[2]).Declarators.Single().Name);

        var secondReady = Assert.IsType<IfStatement>(firstInline[3]);
        var secondInline = Assert.IsType<BlockStatement>(secondReady.ThenStatement).Statements;
        Assert.Equal("middle", Assert.IsType<LocalVariableDeclarationStatement>(
            secondInline[0]).Declarators.Single().Name);
        var finalPrior = Assert.IsType<LocalVariableDeclarationStatement>(secondInline[1]);
        Assert.Equal("finalPrior", finalPrior.Declarators.Single().Name);
        Assert.IsType<CxCompiler.Model.Types.AutoType>(finalPrior.DeclaredType);
        var finalPriorDeclarator = finalPrior.Declarators.Single();
        Assert.IsType<CxCompiler.Model.Types.BuiltInTypes.IntType>(finalPriorDeclarator.Type);
        Assert.NotNull(finalPriorDeclarator.Symbol);
        Assert.IsType<BinaryExpression>(finalPriorDeclarator.Initializer);
        Assert.Equal("finalSelected", Assert.IsType<LocalVariableDeclarationStatement>(
            secondInline[2]).Declarators.Single().Name);

        var thirdReady = Assert.IsType<IfStatement>(secondInline[3]);
        var thirdInline = Assert.IsType<BlockStatement>(thirdReady.ThenStatement).Statements;
        Assert.Equal("right", Assert.IsType<LocalVariableDeclarationStatement>(
            thirdInline[0]).Declarators.Single().Name);
        Assert.IsType<ObjectCreationExpression>(
            Assert.IsType<ReturnStatement>(thirdInline[^1]).Expression);
    }

    [Fact]
    public void AsyncTaskAllCompletedThreeAwaitMultiDeclaratorBeforeCallSourceDefersRelayAllocation()
    {
        var project = CreateProject("""
            import System.Threading.Tasks;
            namespace App;
            Task<int> LaterSource(int priorResult, Task<int> source) {
                return source;
            }
            async Task<int> Combine(
                Task<int> first, Task<int> second, Task<int> third) {
                var left = await first;
                int selectedPrior = left + 1, selectedCallValue = selectedPrior + 1;
                Task<int> selected = LaterSource(selectedCallValue, second);
                var middle = await selected;
                int finalPrior = middle + 2, finalCallValue = finalPrior + 1;
                Task<int> finalSelected = LaterSource(finalCallValue, third);
                var right = await finalSelected;
                return left + middle + right;
            }
            """);

        new SemanticBinder().Bind(project);

        var combine = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Combine");
        var guarded = Assert.IsType<TryStatement>(Assert.Single(combine.Body!));
        var firstReady = Assert.IsType<IfStatement>(
            Assert.Single(Assert.IsType<BlockStatement>(guarded.Body).Statements));
        var firstInline = Assert.IsType<BlockStatement>(firstReady.ThenStatement).Statements;
        Assert.Equal("left", Assert.IsType<LocalVariableDeclarationStatement>(
            firstInline[0]).Declarators.Single().Name);
        var selectedDeclaration = Assert.IsType<LocalVariableDeclarationStatement>(firstInline[1]);
        Assert.IsType<CxCompiler.Model.Types.BuiltInTypes.IntType>(selectedDeclaration.DeclaredType);
        var selectedDeclarators = selectedDeclaration.Declarators;
        Assert.Equal(2, selectedDeclarators.Count);
        Assert.Equal("selectedPrior", selectedDeclarators[0].Name);
        Assert.Equal("selectedCallValue", selectedDeclarators[1].Name);
        Assert.All(selectedDeclarators, declarator =>
            Assert.IsType<CxCompiler.Model.Types.BuiltInTypes.IntType>(declarator.Type));
        Assert.All(selectedDeclarators, declarator => Assert.NotNull(declarator.Symbol));
        var selectedCallInitializer = Assert.IsType<BinaryExpression>(
            selectedDeclarators[1].Initializer);
        var selectedPriorReference = Assert.IsType<IdentifierExpression>(
            selectedCallInitializer.Left);
        Assert.Same(selectedDeclarators[0].Symbol, selectedPriorReference.TargetLocal);
        Assert.Equal("selected", Assert.IsType<LocalVariableDeclarationStatement>(
            firstInline[2]).Declarators.Single().Name);

        var secondReady = Assert.IsType<IfStatement>(firstInline[3]);
        var secondInline = Assert.IsType<BlockStatement>(secondReady.ThenStatement).Statements;
        Assert.Equal("middle", Assert.IsType<LocalVariableDeclarationStatement>(
            secondInline[0]).Declarators.Single().Name);
        var finalDeclaration = Assert.IsType<LocalVariableDeclarationStatement>(secondInline[1]);
        Assert.IsType<CxCompiler.Model.Types.BuiltInTypes.IntType>(finalDeclaration.DeclaredType);
        var finalDeclarators = finalDeclaration.Declarators;
        Assert.Equal(2, finalDeclarators.Count);
        Assert.Equal("finalPrior", finalDeclarators[0].Name);
        Assert.Equal("finalCallValue", finalDeclarators[1].Name);
        Assert.All(finalDeclarators, declarator =>
            Assert.IsType<CxCompiler.Model.Types.BuiltInTypes.IntType>(declarator.Type));
        Assert.All(finalDeclarators, declarator => Assert.NotNull(declarator.Symbol));
        var finalCallInitializer = Assert.IsType<BinaryExpression>(
            finalDeclarators[1].Initializer);
        var finalPriorReference = Assert.IsType<IdentifierExpression>(
            finalCallInitializer.Left);
        Assert.Same(finalDeclarators[0].Symbol, finalPriorReference.TargetLocal);
        Assert.Equal("finalSelected", Assert.IsType<LocalVariableDeclarationStatement>(
            secondInline[2]).Declarators.Single().Name);

        var thirdReady = Assert.IsType<IfStatement>(secondInline[3]);
        var thirdInline = Assert.IsType<BlockStatement>(thirdReady.ThenStatement).Statements;
        Assert.Equal("right", Assert.IsType<LocalVariableDeclarationStatement>(
            thirdInline[0]).Declarators.Single().Name);
        Assert.IsType<ObjectCreationExpression>(
            Assert.IsType<ReturnStatement>(thirdInline[^1]).Expression);
    }

    [Fact]
    public void AsyncTaskAllCompletedThreeAwaitAssignedLocalBeforeCallSourceDefersRelayAllocation()
    {
        var project = CreateProject("""
            import System.Threading.Tasks;
            namespace App;
            Task<int> LaterSource(int priorResult, Task<int> source) {
                return source;
            }
            async Task<int> Combine(
                Task<int> first, Task<int> second, Task<int> third) {
                var left = await first;
                int selectedPrior;
                selectedPrior = left + 1;
                Task<int> selected = LaterSource(selectedPrior, second);
                var middle = await selected;
                int finalPrior;
                finalPrior = middle + 2;
                Task<int> finalSelected = LaterSource(finalPrior, third);
                var right = await finalSelected;
                return left + middle + right;
            }
            """);

        new SemanticBinder().Bind(project);

        var combine = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Combine");
        var guarded = Assert.IsType<TryStatement>(Assert.Single(combine.Body!));
        var firstReady = Assert.IsType<IfStatement>(
            Assert.Single(Assert.IsType<BlockStatement>(guarded.Body).Statements));
        var firstInline = Assert.IsType<BlockStatement>(firstReady.ThenStatement).Statements;
        var leftDeclaration = Assert.IsType<LocalVariableDeclarationStatement>(firstInline[0]);
        Assert.Equal("left", leftDeclaration.Declarators.Single().Name);
        var selectedDeclaration = Assert.IsType<LocalVariableDeclarationStatement>(firstInline[1]);
        Assert.IsType<CxCompiler.Model.Types.BuiltInTypes.IntType>(selectedDeclaration.DeclaredType);
        var selectedPrior = selectedDeclaration.Declarators.Single();
        Assert.Equal("selectedPrior", selectedPrior.Name);
        Assert.Null(selectedPrior.Initializer);
        Assert.IsType<CxCompiler.Model.Types.BuiltInTypes.IntType>(selectedPrior.Type);
        Assert.NotNull(selectedPrior.Symbol);
        var selectedAssignment = Assert.IsType<AssignmentExpression>(
            Assert.IsType<ExpressionStatement>(firstInline[2]).Expression);
        Assert.Equal("=", selectedAssignment.Operator);
        var selectedAssignmentTarget = Assert.IsType<IdentifierExpression>(selectedAssignment.Target);
        Assert.Same(selectedPrior.Symbol, selectedAssignmentTarget.TargetLocal);
        Assert.IsType<BinaryExpression>(selectedAssignment.Value);
        Assert.Equal("selected", Assert.IsType<LocalVariableDeclarationStatement>(
            firstInline[3]).Declarators.Single().Name);

        var secondReady = Assert.IsType<IfStatement>(firstInline[4]);
        var secondInline = Assert.IsType<BlockStatement>(secondReady.ThenStatement).Statements;
        Assert.Equal("middle", Assert.IsType<LocalVariableDeclarationStatement>(
            secondInline[0]).Declarators.Single().Name);
        var finalDeclaration = Assert.IsType<LocalVariableDeclarationStatement>(secondInline[1]);
        Assert.IsType<CxCompiler.Model.Types.BuiltInTypes.IntType>(finalDeclaration.DeclaredType);
        var finalPrior = finalDeclaration.Declarators.Single();
        Assert.Equal("finalPrior", finalPrior.Name);
        Assert.Null(finalPrior.Initializer);
        Assert.IsType<CxCompiler.Model.Types.BuiltInTypes.IntType>(finalPrior.Type);
        Assert.NotNull(finalPrior.Symbol);
        var finalAssignment = Assert.IsType<AssignmentExpression>(
            Assert.IsType<ExpressionStatement>(secondInline[2]).Expression);
        Assert.Equal("=", finalAssignment.Operator);
        var finalAssignmentTarget = Assert.IsType<IdentifierExpression>(finalAssignment.Target);
        Assert.Same(finalPrior.Symbol, finalAssignmentTarget.TargetLocal);
        Assert.IsType<BinaryExpression>(finalAssignment.Value);
        Assert.Equal("finalSelected", Assert.IsType<LocalVariableDeclarationStatement>(
            secondInline[3]).Declarators.Single().Name);

        var thirdReady = Assert.IsType<IfStatement>(secondInline[4]);
        var thirdInline = Assert.IsType<BlockStatement>(thirdReady.ThenStatement).Statements;
        Assert.Equal("right", Assert.IsType<LocalVariableDeclarationStatement>(
            thirdInline[0]).Declarators.Single().Name);
        Assert.IsType<ObjectCreationExpression>(
            Assert.IsType<ReturnStatement>(thirdInline[^1]).Expression);
    }

    [Fact]
    public void AsyncTaskAllCompletedThreeAwaitConditionalBeforeCallSourceDefersRelayAllocation()
    {
        var project = CreateProject("""
            import System.Threading.Tasks;
            namespace App;
            Task<int> LaterSource(int priorResult, Task<int> source) {
                return source;
            }
            async Task<int> Combine(
                Task<int> first, Task<int> second, Task<int> third) {
                var left = await first;
                if (left > 0) left += 1;
                Task<int> selected = LaterSource(left, second);
                var middle = await selected;
                if (middle > 20) middle += 2;
                Task<int> finalSelected = LaterSource(middle, third);
                var right = await finalSelected;
                return left + middle + right;
            }
            """);

        new SemanticBinder().Bind(project);

        var combine = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Combine");
        var guarded = Assert.IsType<TryStatement>(Assert.Single(combine.Body!));
        var firstReady = Assert.IsType<IfStatement>(
            Assert.Single(Assert.IsType<BlockStatement>(guarded.Body).Statements));
        var firstInline = Assert.IsType<BlockStatement>(firstReady.ThenStatement).Statements;
        Assert.Equal("left", Assert.IsType<LocalVariableDeclarationStatement>(
            firstInline[0]).Declarators.Single().Name);
        var firstAdjustment = Assert.IsType<IfStatement>(firstInline[1]);
        var firstAdjustmentBody = Assert.IsType<ExpressionStatement>(
            firstAdjustment.ThenStatement);
        Assert.Equal("+=", Assert.IsType<AssignmentExpression>(
            firstAdjustmentBody.Expression).Operator);
        Assert.Equal("selected", Assert.IsType<LocalVariableDeclarationStatement>(
            firstInline[2]).Declarators.Single().Name);

        var secondReady = Assert.IsType<IfStatement>(firstInline[3]);
        var secondInline = Assert.IsType<BlockStatement>(secondReady.ThenStatement).Statements;
        Assert.Equal("middle", Assert.IsType<LocalVariableDeclarationStatement>(
            secondInline[0]).Declarators.Single().Name);
        var secondAdjustment = Assert.IsType<IfStatement>(secondInline[1]);
        Assert.IsType<ExpressionStatement>(secondAdjustment.ThenStatement);
        Assert.Equal("finalSelected", Assert.IsType<LocalVariableDeclarationStatement>(
            secondInline[2]).Declarators.Single().Name);

        var thirdReady = Assert.IsType<IfStatement>(secondInline[3]);
        var thirdInline = Assert.IsType<BlockStatement>(thirdReady.ThenStatement).Statements;
        Assert.Equal("right", Assert.IsType<LocalVariableDeclarationStatement>(
            thirdInline[0]).Declarators.Single().Name);
        Assert.IsType<ObjectCreationExpression>(
            Assert.IsType<ReturnStatement>(thirdInline[^1]).Expression);
    }

    [Fact]
    public void AsyncTaskAllCompletedThreeAwaitBlockBeforeCallSourceDefersRelayAllocation()
    {
        var project = CreateProject("""
            import System.Threading.Tasks;
            namespace App;
            Task<int> LaterSource(int priorResult, Task<int> source) {
                return source;
            }
            async Task<int> Combine(
                Task<int> first, Task<int> second, Task<int> third) {
                var left = await first;
                {
                    int selectedPrior = left + 1;
                    left = selectedPrior;
                }
                Task<int> selected = LaterSource(left, second);
                var middle = await selected;
                {
                    int finalPrior = middle + 2;
                    middle = finalPrior;
                }
                Task<int> finalSelected = LaterSource(middle, third);
                var right = await finalSelected;
                return left + middle + right;
            }
            """);

        new SemanticBinder().Bind(project);

        var combine = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Combine");
        var guarded = Assert.IsType<TryStatement>(Assert.Single(combine.Body!));
        var firstReady = Assert.IsType<IfStatement>(
            Assert.Single(Assert.IsType<BlockStatement>(guarded.Body).Statements));
        var firstInline = Assert.IsType<BlockStatement>(firstReady.ThenStatement).Statements;
        Assert.Equal("left", Assert.IsType<LocalVariableDeclarationStatement>(
            firstInline[0]).Declarators.Single().Name);
        var firstAdjustment = Assert.IsType<BlockStatement>(firstInline[1]).Statements;
        var selectedPrior = Assert.IsType<LocalVariableDeclarationStatement>(
            firstAdjustment[0]);
        Assert.Equal("selectedPrior", selectedPrior.Declarators.Single().Name);
        Assert.IsType<CxCompiler.Model.Types.BuiltInTypes.IntType>(
            selectedPrior.DeclaredType);
        Assert.NotNull(selectedPrior.Declarators.Single().Symbol);
        var firstAssignment = Assert.IsType<AssignmentExpression>(
            Assert.IsType<ExpressionStatement>(firstAdjustment[1]).Expression);
        Assert.Equal("left", Assert.IsType<IdentifierExpression>(
            firstAssignment.Target).Identifier.ToString());
        Assert.Equal("selectedPrior", Assert.IsType<IdentifierExpression>(
            firstAssignment.Value).Identifier.ToString());
        Assert.Equal("selected", Assert.IsType<LocalVariableDeclarationStatement>(
            firstInline[2]).Declarators.Single().Name);

        var secondReady = Assert.IsType<IfStatement>(firstInline[3]);
        var secondInline = Assert.IsType<BlockStatement>(secondReady.ThenStatement).Statements;
        Assert.Equal("middle", Assert.IsType<LocalVariableDeclarationStatement>(
            secondInline[0]).Declarators.Single().Name);
        var secondAdjustment = Assert.IsType<BlockStatement>(secondInline[1]).Statements;
        var finalPrior = Assert.IsType<LocalVariableDeclarationStatement>(
            secondAdjustment[0]);
        Assert.Equal("finalPrior", finalPrior.Declarators.Single().Name);
        Assert.IsType<CxCompiler.Model.Types.BuiltInTypes.IntType>(
            finalPrior.DeclaredType);
        Assert.NotNull(finalPrior.Declarators.Single().Symbol);
        Assert.Equal("finalSelected", Assert.IsType<LocalVariableDeclarationStatement>(
            secondInline[2]).Declarators.Single().Name);

        var thirdReady = Assert.IsType<IfStatement>(secondInline[3]);
        var thirdInline = Assert.IsType<BlockStatement>(thirdReady.ThenStatement).Statements;
        Assert.Equal("right", Assert.IsType<LocalVariableDeclarationStatement>(
            thirdInline[0]).Declarators.Single().Name);
        Assert.IsType<ObjectCreationExpression>(
            Assert.IsType<ReturnStatement>(thirdInline[^1]).Expression);
    }

    [Fact]
    public void AsyncTaskAllCompletedThreeAwaitLoopBeforeCallSourceDefersRelayAllocation()
    {
        var project = CreateProject("""
            import System.Threading.Tasks;
            namespace App;
            Task<int> LaterSource(int priorResult, Task<int> source) {
                return source;
            }
            async Task<int> Combine(
                Task<int> first, Task<int> second, Task<int> third) {
                var left = await first;
                while (left < 18) {
                    left += 1;
                }
                Task<int> selected = LaterSource(left, second);
                var middle = await selected;
                while (middle < 27) {
                    middle += 1;
                }
                Task<int> finalSelected = LaterSource(middle, third);
                var right = await finalSelected;
                return left + middle + right;
            }
            """);

        new SemanticBinder().Bind(project);

        var combine = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Combine");
        var guarded = Assert.IsType<TryStatement>(Assert.Single(combine.Body!));
        var firstReady = Assert.IsType<IfStatement>(
            Assert.Single(Assert.IsType<BlockStatement>(guarded.Body).Statements));
        var firstInline = Assert.IsType<BlockStatement>(firstReady.ThenStatement).Statements;
        Assert.Equal("left", Assert.IsType<LocalVariableDeclarationStatement>(
            firstInline[0]).Declarators.Single().Name);
        var firstLoop = Assert.IsType<WhileStatement>(firstInline[1]);
        Assert.IsType<BinaryExpression>(firstLoop.Condition);
        var firstLoopBody = Assert.IsType<BlockStatement>(firstLoop.Body).Statements;
        var firstMutation = Assert.IsType<AssignmentExpression>(
            Assert.IsType<ExpressionStatement>(firstLoopBody.Single()).Expression);
        Assert.Equal("left", Assert.IsType<IdentifierExpression>(
            firstMutation.Target).Identifier.ToString());
        Assert.Equal("+=", firstMutation.Operator);
        Assert.Equal("selected", Assert.IsType<LocalVariableDeclarationStatement>(
            firstInline[2]).Declarators.Single().Name);

        var secondReady = Assert.IsType<IfStatement>(firstInline[3]);
        var secondInline = Assert.IsType<BlockStatement>(secondReady.ThenStatement).Statements;
        Assert.Equal("middle", Assert.IsType<LocalVariableDeclarationStatement>(
            secondInline[0]).Declarators.Single().Name);
        var secondLoop = Assert.IsType<WhileStatement>(secondInline[1]);
        Assert.IsType<BinaryExpression>(secondLoop.Condition);
        var secondMutation = Assert.IsType<AssignmentExpression>(
            Assert.IsType<ExpressionStatement>(
                Assert.Single(Assert.IsType<BlockStatement>(secondLoop.Body).Statements))
                .Expression);
        Assert.Equal("middle", Assert.IsType<IdentifierExpression>(
            secondMutation.Target).Identifier.ToString());
        Assert.Equal("+=", secondMutation.Operator);
        Assert.Equal("finalSelected", Assert.IsType<LocalVariableDeclarationStatement>(
            secondInline[2]).Declarators.Single().Name);

        var thirdReady = Assert.IsType<IfStatement>(secondInline[3]);
        var thirdInline = Assert.IsType<BlockStatement>(thirdReady.ThenStatement).Statements;
        Assert.Equal("right", Assert.IsType<LocalVariableDeclarationStatement>(
            thirdInline[0]).Declarators.Single().Name);
        Assert.IsType<ObjectCreationExpression>(
            Assert.IsType<ReturnStatement>(thirdInline[^1]).Expression);
    }

    [Fact]
    public void AsyncTaskLoopAwaitsConditionalTaskSource()
    {
        var project = CreateProject("""
            import System.Threading.Tasks;
            namespace App;
            async Task<int> AwaitWhileCount(
                Task<int> first, Task<int> second, int limit) {
                int iterations = 0;
                int result = 0;
                while (iterations < limit) {
                    var value = await (iterations == 0 ? first : second);
                    result = value;
                    iterations++;
                }
                return result;
            }
            async Task<int> AwaitForCount(
                Task<int> first, Task<int> second, int limit) {
                int result = 0;
                for (int index = 0; index < limit; index++) {
                    var value = await (index == 0 ? first : second);
                    result = value;
                }
                return result;
            }
            Task<int> CompletedTask(int value) { return null; }
            async Task<int> AwaitWhileCompletedDepth(int limit) {
                int iterations = 0;
                int result = 0;
                while (iterations < limit) {
                    var value = await CompletedTask(1);
                    result += value;
                    iterations++;
                }
                return result;
            }
            """);

        new SemanticBinder().Bind(project);

        var function = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(candidate => candidate.Name == "AwaitWhileCount");
        var guarded = Assert.IsType<TryStatement>(Assert.Single(function.Body!));
        var loop = Assert.Single(Assert.IsType<BlockStatement>(guarded.Body).Statements,
            statement => statement is WhileStatement) as WhileStatement;
        Assert.NotNull(loop);
        Assert.Contains(Assert.IsType<BlockStatement>(loop!.Body).Statements,
            statement => statement is IfStatement);

        var forFunction = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(candidate => candidate.Name == "AwaitForCount");
        var forGuarded = Assert.IsType<TryStatement>(Assert.Single(forFunction.Body!));
        var forLoop = Assert.Single(Assert.IsType<BlockStatement>(forGuarded.Body).Statements,
            statement => statement is ForStatement) as ForStatement;
        Assert.NotNull(forLoop);
        Assert.Contains(Assert.IsType<BlockStatement>(forLoop!.Body).Statements,
            statement => statement is IfStatement);

        var callSource = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(candidate => candidate.Name == "AwaitWhileCompletedDepth");
        Assert.IsType<TryStatement>(Assert.Single(callSource.Body!));
    }

    [Fact]
    public void AsyncTaskAllCompletedFourAwaitCallSourcesDefersRelayAllocation()
    {
        var project = CreateProject("""
            import System.Threading.Tasks;
            namespace App;
            Task<int> LaterSource(int priorResult, Task<int> source) {
                return source;
            }
            async Task<int> Combine(
                Task<int> first, Task<int> second, Task<int> third, Task<int> fourth) {
                var firstResult = await first;
                Task<int> secondSelected = LaterSource(firstResult, second);
                var secondResult = await secondSelected;
                Task<int> thirdSelected = LaterSource(secondResult, third);
                var thirdResult = await thirdSelected;
                Task<int> fourthSelected = LaterSource(thirdResult, fourth);
                var fourthResult = await fourthSelected;
                return firstResult + secondResult + thirdResult + fourthResult;
            }
            """);

        new SemanticBinder().Bind(project);

        var combine = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Combine");
        var guarded = Assert.IsType<TryStatement>(Assert.Single(combine.Body!));
        var readiness = Assert.IsType<IfStatement>(
            Assert.Single(Assert.IsType<BlockStatement>(guarded.Body).Statements));
        var pendingBranches = new List<IfStatement>();
        string[] resultNames = ["firstResult", "secondResult", "thirdResult", "fourthResult"];
        for (var awaitNumber = 0; awaitNumber < resultNames.Length; awaitNumber++)
        {
            var readyStatements = Assert.IsType<BlockStatement>(readiness.ThenStatement)
                .Statements;
            Assert.Equal(resultNames[awaitNumber],
                Assert.IsType<LocalVariableDeclarationStatement>(readyStatements[0])
                    .Declarators.Single().Name);
            if (awaitNumber == resultNames.Length - 1)
            {
                Assert.IsType<ObjectCreationExpression>(
                    Assert.IsType<ReturnStatement>(readyStatements[^1]).Expression);
                break;
            }

            Assert.IsType<InvocationExpression>(
                Assert.IsType<LocalVariableDeclarationStatement>(readyStatements[1])
                    .Declarators.Single().Initializer);
            pendingBranches.Add(readiness);
            readiness = Assert.IsType<IfStatement>(readyStatements[2]);
        }

        Assert.Equal(3, pendingBranches.Count);
        foreach (var pendingBranch in pendingBranches)
        {
            Assert.Contains(Assert.IsType<BlockStatement>(pendingBranch.ElseStatement)
                .Statements, statement => statement is LocalVariableDeclarationStatement
                {
                    Declarators: [{ Initializer: ObjectCreationExpression }],
                });
        }
    }

    [Fact]
    public void AsyncTaskAllCompletedFiveAwaitCallSourcesDefersRelayAllocation()
    {
        var project = CreateProject("""
            import System.Threading.Tasks;
            namespace App;
            Task<int> LaterSource(int priorResult, Task<int> source) {
                return source;
            }
            async Task<int> Combine(
                Task<int> first, Task<int> second, Task<int> third,
                Task<int> fourth, Task<int> fifth) {
                var firstResult = await first;
                Task<int> secondSelected = LaterSource(firstResult, second);
                var secondResult = await secondSelected;
                Task<int> thirdSelected = LaterSource(secondResult, third);
                var thirdResult = await thirdSelected;
                Task<int> fourthSelected = LaterSource(thirdResult, fourth);
                var fourthResult = await fourthSelected;
                Task<int> fifthSelected = LaterSource(fourthResult, fifth);
                var fifthResult = await fifthSelected;
                return firstResult + secondResult + thirdResult + fourthResult + fifthResult;
            }
            """);

        new SemanticBinder().Bind(project);

        var combine = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Combine");
        var guarded = Assert.IsType<TryStatement>(Assert.Single(combine.Body!));
        var readiness = Assert.IsType<IfStatement>(
            Assert.Single(Assert.IsType<BlockStatement>(guarded.Body).Statements));
        var pendingBranches = new List<IfStatement>();
        string[] resultNames =
            ["firstResult", "secondResult", "thirdResult", "fourthResult", "fifthResult"];
        for (var awaitNumber = 0; awaitNumber < resultNames.Length; awaitNumber++)
        {
            var readyStatements = Assert.IsType<BlockStatement>(readiness.ThenStatement)
                .Statements;
            Assert.Equal(resultNames[awaitNumber],
                Assert.IsType<LocalVariableDeclarationStatement>(readyStatements[0])
                    .Declarators.Single().Name);
            if (awaitNumber == resultNames.Length - 1)
            {
                Assert.IsType<ObjectCreationExpression>(
                    Assert.IsType<ReturnStatement>(readyStatements[^1]).Expression);
                break;
            }

            Assert.IsType<InvocationExpression>(
                Assert.IsType<LocalVariableDeclarationStatement>(readyStatements[1])
                    .Declarators.Single().Initializer);
            pendingBranches.Add(readiness);
            readiness = Assert.IsType<IfStatement>(readyStatements[2]);
        }

        Assert.Equal(4, pendingBranches.Count);
        foreach (var pendingBranch in pendingBranches)
        {
            Assert.Contains(Assert.IsType<BlockStatement>(pendingBranch.ElseStatement)
                .Statements, statement => statement is LocalVariableDeclarationStatement
                {
                    Declarators: [{ Initializer: ObjectCreationExpression }],
                });
        }
    }

    [Fact]
    public void AsyncTaskAllCompletedThreeAwaitDoWhileBeforeCallSourceDefersRelayAllocation()
    {
        var project = CreateProject("""
            import System.Threading.Tasks;
            namespace App;
            Task<int> LaterSource(int priorResult, Task<int> source) {
                return source;
            }
            async Task<int> Combine(
                Task<int> first, Task<int> second, Task<int> third) {
                var left = await first;
                do {
                    left += 1;
                } while (left < 18);
                Task<int> selected = LaterSource(left, second);
                var middle = await selected;
                do {
                    middle += 1;
                } while (middle < 27);
                Task<int> finalSelected = LaterSource(middle, third);
                var right = await finalSelected;
                return left + middle + right;
            }
            """);

        new SemanticBinder().Bind(project);

        var combine = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Combine");
        var guarded = Assert.IsType<TryStatement>(Assert.Single(combine.Body!));
        var firstReady = Assert.IsType<IfStatement>(
            Assert.Single(Assert.IsType<BlockStatement>(guarded.Body).Statements));
        var firstInline = Assert.IsType<BlockStatement>(firstReady.ThenStatement).Statements;
        Assert.Equal("left", Assert.IsType<LocalVariableDeclarationStatement>(
            firstInline[0]).Declarators.Single().Name);
        var firstLoop = Assert.IsType<DoWhileStatement>(firstInline[1]);
        var firstMutation = Assert.IsType<AssignmentExpression>(
            Assert.IsType<ExpressionStatement>(
                Assert.Single(Assert.IsType<BlockStatement>(firstLoop.Body).Statements))
                .Expression);
        Assert.Equal("left", Assert.IsType<IdentifierExpression>(
            firstMutation.Target).Identifier.ToString());
        Assert.Equal("+=", firstMutation.Operator);
        Assert.IsType<BinaryExpression>(firstLoop.Condition);
        Assert.Equal("selected", Assert.IsType<LocalVariableDeclarationStatement>(
            firstInline[2]).Declarators.Single().Name);

        var secondReady = Assert.IsType<IfStatement>(firstInline[3]);
        var secondInline = Assert.IsType<BlockStatement>(secondReady.ThenStatement).Statements;
        Assert.Equal("middle", Assert.IsType<LocalVariableDeclarationStatement>(
            secondInline[0]).Declarators.Single().Name);
        var secondLoop = Assert.IsType<DoWhileStatement>(secondInline[1]);
        var secondMutation = Assert.IsType<AssignmentExpression>(
            Assert.IsType<ExpressionStatement>(
                Assert.Single(Assert.IsType<BlockStatement>(secondLoop.Body).Statements))
                .Expression);
        Assert.Equal("middle", Assert.IsType<IdentifierExpression>(
            secondMutation.Target).Identifier.ToString());
        Assert.Equal("+=", secondMutation.Operator);
        Assert.IsType<BinaryExpression>(secondLoop.Condition);
        Assert.Equal("finalSelected", Assert.IsType<LocalVariableDeclarationStatement>(
            secondInline[2]).Declarators.Single().Name);

        var thirdReady = Assert.IsType<IfStatement>(secondInline[3]);
        var thirdInline = Assert.IsType<BlockStatement>(thirdReady.ThenStatement).Statements;
        Assert.Equal("right", Assert.IsType<LocalVariableDeclarationStatement>(
            thirdInline[0]).Declarators.Single().Name);
        Assert.IsType<ObjectCreationExpression>(
            Assert.IsType<ReturnStatement>(thirdInline[^1]).Expression);
    }

    [Fact]
    public void AsyncTaskAllCompletedThreeAwaitForBeforeCallSourceDefersRelayAllocation()
    {
        var project = CreateProject("""
            import System.Threading.Tasks;
            namespace App;
            Task<int> LaterSource(int priorResult, Task<int> source) {
                return source;
            }
            async Task<int> Combine(
                Task<int> first, Task<int> second, Task<int> third) {
                var left = await first;
                for (int index = left; index < 18; index += 1) {
                    left += 1;
                }
                Task<int> selected = LaterSource(left, second);
                var middle = await selected;
                for (int index = middle; index < 27; index += 1) {
                    middle += 1;
                }
                Task<int> finalSelected = LaterSource(middle, third);
                var right = await finalSelected;
                return left + middle + right;
            }
            """);

        new SemanticBinder().Bind(project);

        var combine = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Combine");
        var guarded = Assert.IsType<TryStatement>(Assert.Single(combine.Body!));
        var firstReady = Assert.IsType<IfStatement>(
            Assert.Single(Assert.IsType<BlockStatement>(guarded.Body).Statements));
        var firstInline = Assert.IsType<BlockStatement>(firstReady.ThenStatement).Statements;
        Assert.Equal("left", Assert.IsType<LocalVariableDeclarationStatement>(
            firstInline[0]).Declarators.Single().Name);
        var firstLoop = Assert.IsType<ForStatement>(firstInline[1]);
        var firstInitializer = Assert.IsType<LocalVariableDeclarationStatement>(
            firstLoop.DeclarationInitializer);
        Assert.Equal("index", firstInitializer.Declarators.Single().Name);
        Assert.IsType<CxCompiler.Model.Types.BuiltInTypes.IntType>(
            firstInitializer.DeclaredType);
        Assert.NotNull(firstInitializer.Declarators.Single().Symbol);
        Assert.IsType<BinaryExpression>(firstLoop.Condition);
        var firstIterator = Assert.IsType<AssignmentExpression>(
            Assert.Single(firstLoop.Iterators));
        Assert.Equal("+=", firstIterator.Operator);
        var firstMutation = Assert.IsType<AssignmentExpression>(
            Assert.IsType<ExpressionStatement>(
                Assert.Single(Assert.IsType<BlockStatement>(firstLoop.Body).Statements))
                .Expression);
        Assert.Equal("left", Assert.IsType<IdentifierExpression>(
            firstMutation.Target).Identifier.ToString());
        Assert.Equal("selected", Assert.IsType<LocalVariableDeclarationStatement>(
            firstInline[2]).Declarators.Single().Name);

        var secondReady = Assert.IsType<IfStatement>(firstInline[3]);
        var secondInline = Assert.IsType<BlockStatement>(secondReady.ThenStatement).Statements;
        Assert.Equal("middle", Assert.IsType<LocalVariableDeclarationStatement>(
            secondInline[0]).Declarators.Single().Name);
        var secondLoop = Assert.IsType<ForStatement>(secondInline[1]);
        Assert.Equal("index", Assert.IsType<LocalVariableDeclarationStatement>(
            secondLoop.DeclarationInitializer).Declarators.Single().Name);
        Assert.IsType<BinaryExpression>(secondLoop.Condition);
        Assert.IsType<AssignmentExpression>(Assert.Single(secondLoop.Iterators));
        var secondMutation = Assert.IsType<AssignmentExpression>(
            Assert.IsType<ExpressionStatement>(
                Assert.Single(Assert.IsType<BlockStatement>(secondLoop.Body).Statements))
                .Expression);
        Assert.Equal("middle", Assert.IsType<IdentifierExpression>(
            secondMutation.Target).Identifier.ToString());
        Assert.Equal("finalSelected", Assert.IsType<LocalVariableDeclarationStatement>(
            secondInline[2]).Declarators.Single().Name);

        var thirdReady = Assert.IsType<IfStatement>(secondInline[3]);
        var thirdInline = Assert.IsType<BlockStatement>(thirdReady.ThenStatement).Statements;
        Assert.Equal("right", Assert.IsType<LocalVariableDeclarationStatement>(
            thirdInline[0]).Declarators.Single().Name);
        Assert.IsType<ObjectCreationExpression>(
            Assert.IsType<ReturnStatement>(thirdInline[^1]).Expression);
    }

    [Fact]
    public void AsyncTaskAllCompletedThreeAwaitNestedForBeforeCallSourceDefersRelayAllocation()
    {
        var project = CreateProject("""
            import System.Threading.Tasks;
            namespace App;
            Task<int> LaterSource(int priorResult, Task<int> source) {
                return source;
            }
            async Task<int> Combine(
                Task<int> first, Task<int> second, Task<int> third) {
                var left = await first;
                for (int outer = 0; outer < 2; outer += 1) {
                    for (int inner = 0; inner < 3; inner += 1) {
                        left += 1;
                    }
                }
                Task<int> selected = LaterSource(left, second);
                var middle = await selected;
                for (int outer = 0; outer < 2; outer += 1) {
                    for (int inner = 0; inner < 3; inner += 1) {
                        middle += 1;
                    }
                }
                Task<int> finalSelected = LaterSource(middle, third);
                var right = await finalSelected;
                return left + middle + right;
            }
            """);

        new SemanticBinder().Bind(project);

        var combine = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Combine");
        var guarded = Assert.IsType<TryStatement>(Assert.Single(combine.Body!));
        var firstReady = Assert.IsType<IfStatement>(
            Assert.Single(Assert.IsType<BlockStatement>(guarded.Body).Statements));
        var firstInline = Assert.IsType<BlockStatement>(firstReady.ThenStatement).Statements;
        Assert.Equal("left", Assert.IsType<LocalVariableDeclarationStatement>(
            firstInline[0]).Declarators.Single().Name);
        var firstOuter = Assert.IsType<ForStatement>(firstInline[1]);
        var firstOuterDeclaration = Assert.IsType<LocalVariableDeclarationStatement>(
            firstOuter.DeclarationInitializer);
        Assert.Equal("outer", firstOuterDeclaration.Declarators.Single().Name);
        Assert.NotNull(firstOuterDeclaration.Declarators.Single().Symbol);
        var firstOuterBody = Assert.IsType<BlockStatement>(firstOuter.Body);
        var firstInner = Assert.IsType<ForStatement>(
            Assert.Single(firstOuterBody.Statements));
        var firstInnerDeclaration = Assert.IsType<LocalVariableDeclarationStatement>(
            firstInner.DeclarationInitializer);
        Assert.Equal("inner", firstInnerDeclaration.Declarators.Single().Name);
        Assert.NotNull(firstInnerDeclaration.Declarators.Single().Symbol);
        Assert.NotSame(firstOuterDeclaration.Declarators.Single().Symbol,
            firstInnerDeclaration.Declarators.Single().Symbol);
        var firstMutation = Assert.IsType<AssignmentExpression>(
            Assert.IsType<ExpressionStatement>(
                Assert.Single(Assert.IsType<BlockStatement>(firstInner.Body).Statements))
                .Expression);
        Assert.Equal("left", Assert.IsType<IdentifierExpression>(
            firstMutation.Target).Identifier.ToString());
        Assert.Equal("selected", Assert.IsType<LocalVariableDeclarationStatement>(
            firstInline[2]).Declarators.Single().Name);

        var secondReady = Assert.IsType<IfStatement>(firstInline[3]);
        var secondInline = Assert.IsType<BlockStatement>(secondReady.ThenStatement).Statements;
        Assert.Equal("middle", Assert.IsType<LocalVariableDeclarationStatement>(
            secondInline[0]).Declarators.Single().Name);
        var secondOuter = Assert.IsType<ForStatement>(secondInline[1]);
        Assert.Equal("outer", Assert.IsType<LocalVariableDeclarationStatement>(
            secondOuter.DeclarationInitializer).Declarators.Single().Name);
        var secondOuterBody = Assert.IsType<BlockStatement>(secondOuter.Body);
        var secondInner = Assert.IsType<ForStatement>(
            Assert.Single(secondOuterBody.Statements));
        Assert.Equal("inner", Assert.IsType<LocalVariableDeclarationStatement>(
            secondInner.DeclarationInitializer).Declarators.Single().Name);
        var secondMutation = Assert.IsType<AssignmentExpression>(
            Assert.IsType<ExpressionStatement>(
                Assert.Single(Assert.IsType<BlockStatement>(secondInner.Body).Statements))
                .Expression);
        Assert.Equal("middle", Assert.IsType<IdentifierExpression>(
            secondMutation.Target).Identifier.ToString());
        Assert.Equal("finalSelected", Assert.IsType<LocalVariableDeclarationStatement>(
            secondInline[2]).Declarators.Single().Name);

        var thirdReady = Assert.IsType<IfStatement>(secondInline[3]);
        var thirdInline = Assert.IsType<BlockStatement>(thirdReady.ThenStatement).Statements;
        Assert.Equal("right", Assert.IsType<LocalVariableDeclarationStatement>(
            thirdInline[0]).Declarators.Single().Name);
        Assert.IsType<ObjectCreationExpression>(
            Assert.IsType<ReturnStatement>(thirdInline[^1]).Expression);
    }

    [Fact]
    public void AsyncTaskAllCompletedThreeAwaitSwitchBeforeCallSourceDefersRelayAllocation()
    {
        var project = CreateProject("""
            import System.Threading.Tasks;
            namespace App;
            Task<int> LaterSource(int priorResult, Task<int> source) {
                return source;
            }
            async Task<int> Combine(
                Task<int> first, Task<int> second, Task<int> third) {
                var left = await first;
                switch (left) {
                    case 17: left += 1; break;
                    default: left += 2; break;
                }
                Task<int> selected = LaterSource(left, second);
                var middle = await selected;
                switch (middle) {
                    case 25: middle += 2; break;
                    default: middle += 1; break;
                }
                Task<int> finalSelected = LaterSource(middle, third);
                var right = await finalSelected;
                return left + middle + right;
            }
            """);

        new SemanticBinder().Bind(project);

        var combine = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Combine");
        var guarded = Assert.IsType<TryStatement>(Assert.Single(combine.Body!));
        var firstReady = Assert.IsType<IfStatement>(
            Assert.Single(Assert.IsType<BlockStatement>(guarded.Body).Statements));
        var firstInline = Assert.IsType<BlockStatement>(firstReady.ThenStatement).Statements;
        Assert.Equal("left", Assert.IsType<LocalVariableDeclarationStatement>(
            firstInline[0]).Declarators.Single().Name);
        var firstSwitch = Assert.IsType<SwitchStatement>(firstInline[1]);
        Assert.Equal("left", Assert.IsType<IdentifierExpression>(
            firstSwitch.Expression).Identifier.ToString());
        Assert.Equal(2, firstSwitch.Sections.Count);
        Assert.Contains(firstSwitch.Sections.SelectMany(section => section.Labels),
            label => label.Value is LiteralExpression literal && literal.SourceText == "17");
        Assert.Contains(firstSwitch.Sections.SelectMany(section => section.Statements),
            statement => statement is BreakStatement);
        Assert.Contains(firstSwitch.Sections.SelectMany(section => section.Statements)
                .OfType<ExpressionStatement>().Select(statement => statement.Expression)
                .OfType<AssignmentExpression>(),
            assignment => Assert.IsType<IdentifierExpression>(assignment.Target)
                .Identifier.ToString() == "left");
        Assert.Equal("selected", Assert.IsType<LocalVariableDeclarationStatement>(
            firstInline[2]).Declarators.Single().Name);

        var secondReady = Assert.IsType<IfStatement>(firstInline[3]);
        var secondInline = Assert.IsType<BlockStatement>(secondReady.ThenStatement).Statements;
        Assert.Equal("middle", Assert.IsType<LocalVariableDeclarationStatement>(
            secondInline[0]).Declarators.Single().Name);
        var secondSwitch = Assert.IsType<SwitchStatement>(secondInline[1]);
        Assert.Equal("middle", Assert.IsType<IdentifierExpression>(
            secondSwitch.Expression).Identifier.ToString());
        Assert.Equal(2, secondSwitch.Sections.Count);
        Assert.Contains(secondSwitch.Sections.SelectMany(section => section.Statements)
                .OfType<ExpressionStatement>().Select(statement => statement.Expression)
                .OfType<AssignmentExpression>(),
            assignment => Assert.IsType<IdentifierExpression>(assignment.Target)
                .Identifier.ToString() == "middle");
        Assert.Equal("finalSelected", Assert.IsType<LocalVariableDeclarationStatement>(
            secondInline[2]).Declarators.Single().Name);

        var thirdReady = Assert.IsType<IfStatement>(secondInline[3]);
        var thirdInline = Assert.IsType<BlockStatement>(thirdReady.ThenStatement).Statements;
        Assert.Equal("right", Assert.IsType<LocalVariableDeclarationStatement>(
            thirdInline[0]).Declarators.Single().Name);
        Assert.IsType<ObjectCreationExpression>(
            Assert.IsType<ReturnStatement>(thirdInline[^1]).Expression);
    }

    [Fact]
    public void AsyncTaskAllCompletedThreeAwaitTryFinallyBeforeCallSourceDefersRelayAllocation()
    {
        var project = CreateProject("""
            import System.Threading.Tasks;
            namespace App;
            Task<int> LaterSource(int priorResult, Task<int> source) {
                return source;
            }
            async Task<int> Combine(
                Task<int> first, Task<int> second, Task<int> third) {
                var left = await first;
                try { left += 1; } finally { left += 1; }
                Task<int> selected = LaterSource(left, second);
                var middle = await selected;
                try { middle += 1; } finally { middle += 1; }
                Task<int> finalSelected = LaterSource(middle, third);
                var right = await finalSelected;
                return left + middle + right;
            }
            """);

        new SemanticBinder().Bind(project);

        var combine = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Combine");
        var guarded = Assert.IsType<TryStatement>(Assert.Single(combine.Body!));
        var firstReady = Assert.IsType<IfStatement>(
            Assert.Single(Assert.IsType<BlockStatement>(guarded.Body).Statements));
        var firstInline = Assert.IsType<BlockStatement>(firstReady.ThenStatement).Statements;
        Assert.Equal("left", Assert.IsType<LocalVariableDeclarationStatement>(
            firstInline[0]).Declarators.Single().Name);
        var firstTryFinally = Assert.IsType<TryStatement>(firstInline[1]);
        Assert.Empty(firstTryFinally.CatchClauses);
        Assert.Equal("left", Assert.IsType<IdentifierExpression>(
            Assert.IsType<AssignmentExpression>(
                Assert.IsType<ExpressionStatement>(Assert.Single(
                    Assert.IsType<BlockStatement>(firstTryFinally.Body).Statements))
                    .Expression).Target).Identifier.ToString());
        Assert.Equal("left", Assert.IsType<IdentifierExpression>(
            Assert.IsType<AssignmentExpression>(
                Assert.IsType<ExpressionStatement>(Assert.Single(
                    Assert.IsType<BlockStatement>(firstTryFinally.FinallyBody).Statements))
                    .Expression).Target).Identifier.ToString());
        Assert.Equal("selected", Assert.IsType<LocalVariableDeclarationStatement>(
            firstInline[2]).Declarators.Single().Name);

        var secondReady = Assert.IsType<IfStatement>(firstInline[3]);
        var secondInline = Assert.IsType<BlockStatement>(secondReady.ThenStatement).Statements;
        Assert.Equal("middle", Assert.IsType<LocalVariableDeclarationStatement>(
            secondInline[0]).Declarators.Single().Name);
        var secondTryFinally = Assert.IsType<TryStatement>(secondInline[1]);
        Assert.Empty(secondTryFinally.CatchClauses);
        Assert.Equal("middle", Assert.IsType<IdentifierExpression>(
            Assert.IsType<AssignmentExpression>(
                Assert.IsType<ExpressionStatement>(Assert.Single(
                    Assert.IsType<BlockStatement>(secondTryFinally.Body).Statements))
                    .Expression).Target).Identifier.ToString());
        Assert.Equal("middle", Assert.IsType<IdentifierExpression>(
            Assert.IsType<AssignmentExpression>(
                Assert.IsType<ExpressionStatement>(Assert.Single(
                    Assert.IsType<BlockStatement>(secondTryFinally.FinallyBody).Statements))
                    .Expression).Target).Identifier.ToString());
        Assert.Equal("finalSelected", Assert.IsType<LocalVariableDeclarationStatement>(
            secondInline[2]).Declarators.Single().Name);

        var thirdReady = Assert.IsType<IfStatement>(secondInline[3]);
        var thirdInline = Assert.IsType<BlockStatement>(thirdReady.ThenStatement).Statements;
        Assert.Equal("right", Assert.IsType<LocalVariableDeclarationStatement>(
            thirdInline[0]).Declarators.Single().Name);
        Assert.IsType<ObjectCreationExpression>(
            Assert.IsType<ReturnStatement>(thirdInline[^1]).Expression);
    }

    [Fact]
    public void AsyncTaskAllCompletedThreeAwaitTryCatchFilterBeforeCallSourceClonesAliases()
    {
        var project = CreateProject("""
            import System.Threading.Tasks;
            namespace App;
            Task<int> LaterSource(int priorResult, Task<int> source) {
                return source;
            }
            async Task<int> Combine(Task<int> first, Task<int> second,
                Task<int> third, Exception failure) {
                var left = await first;
                try { throw failure; }
                catch (Exception error) when (left >= 17) {
                    if (error != null) left += 1;
                } finally { left += 1; }
                Task<int> selected = LaterSource(left, second);
                var middle = await selected;
                try { throw failure; }
                catch (Exception error) when (middle >= 25) {
                    if (error != null) middle += 1;
                } finally { middle += 1; }
                Task<int> finalSelected = LaterSource(middle, third);
                var right = await finalSelected;
                return left + middle + right;
            }
            """);

        new SemanticBinder().Bind(project);

        var combine = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Combine");
        var callbacks = CoroutineSyntaxAnalysis
            .EnumerateBodyExpressions(combine.Body!).OfType<LambdaExpression>()
            .Where(lambda => lambda.StatementBody is not null &&
                CoroutineSyntaxAnalysis.EnumerateStatements(lambda.StatementBody)
                    .OfType<TryStatement>().Any(statement =>
                        statement.CatchClauses.Count != 0))
            .ToArray();
        Assert.Equal(3, callbacks.Length);
        var callbackTries = callbacks.SelectMany(callback =>
                CoroutineSyntaxAnalysis.EnumerateStatements(callback.StatementBody!))
            .OfType<TryStatement>()
            .ToArray();
        var filteredCatches = callbackTries
            .SelectMany(statement => statement.CatchClauses)
            .Where(clause => clause.Filter is not null)
            .ToArray();
        Assert.NotEmpty(filteredCatches);
        foreach (var catchClause in filteredCatches)
        {
            Assert.NotNull(catchClause.VariableSymbol);
            Assert.Contains(CoroutineSyntaxAnalysis.EnumerateAllExpressions(
                    catchClause.Filter!).OfType<IdentifierExpression>(), identifier =>
                identifier.Identifier.ToString().StartsWith(
                    "__cx_async_callback_result_", StringComparison.Ordinal));
            Assert.Contains(CoroutineSyntaxAnalysis.EnumerateBodyExpressions(
                    [catchClause.Body]).OfType<IdentifierExpression>(), identifier =>
                identifier.Identifier.ToString() == catchClause.VariableName);
        }
        var gapTries = callbackTries.Where(statement =>
            statement.FinallyBody is not null &&
            statement.CatchClauses.Any(clause => clause.Filter is not null)).ToArray();
        Assert.NotEmpty(gapTries);
        foreach (var gapTry in gapTries)
        {
            Assert.Contains(CoroutineSyntaxAnalysis.EnumerateBodyExpressions(
                    [gapTry.FinallyBody!]).OfType<IdentifierExpression>(), identifier =>
                identifier.Identifier.ToString().StartsWith(
                    "__cx_async_callback_result_", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void AsyncTaskAllCompletedThreeAwaitArrayForeachBeforeCallSourceClonesAliases()
    {
        var project = CreateProject("""
            import System.Threading.Tasks;
            namespace App;
            Task<int> LaterSource(int priorResult, Task<int> source) {
                return source;
            }
            async Task<int> Combine(Task<int> first, Task<int> second,
                Task<int> third, int[] adjustments) {
                var left = await first;
                foreach (int adjustment in adjustments) {
                    left += adjustment;
                }
                Task<int> selected = LaterSource(left, second);
                var middle = await selected;
                foreach (int adjustment in adjustments) {
                    middle += adjustment;
                }
                Task<int> finalSelected = LaterSource(middle, third);
                var right = await finalSelected;
                return left + middle + right;
            }
            """);

        new SemanticBinder().Bind(project);

        var combine = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Combine");
        var callbackLoops = CoroutineSyntaxAnalysis
            .EnumerateBodyExpressions(combine.Body!).OfType<LambdaExpression>()
            .Where(lambda => lambda.StatementBody is not null)
            .SelectMany(lambda => CoroutineSyntaxAnalysis.EnumerateStatements(
                lambda.StatementBody!))
            .OfType<ForeachStatement>().ToArray();
        Assert.Equal(2, callbackLoops.Length);
        Assert.All(callbackLoops, loop =>
        {
            Assert.False(loop.IsAwait);
            Assert.Null(loop.EnumeratorSymbol);
            Assert.NotNull(loop.VariableSymbol);
            Assert.Contains(CoroutineSyntaxAnalysis.EnumerateBodyExpressions(
                    [loop.Body]).OfType<IdentifierExpression>(), identifier =>
                identifier.Identifier.ToString().StartsWith(
                    "__cx_async_callback_result_", StringComparison.Ordinal));
        });
        Assert.Contains(CoroutineSyntaxAnalysis.EnumerateStatements(combine.Body!),
            statement => statement is IfStatement);
    }

    [Fact]
    public void AsyncTaskAllCompletedThreeAwaitEnumeratorForeachRebindsCallbacksAndDispose()
    {
        var project = CreateProject("""
            import System.Threading.Tasks;
            namespace App;
            class AdjustmentSequence {
                public int First;
                public int Second;
                public AdjustmentEnumerator GetEnumerator() {
                    return new AdjustmentEnumerator(this);
                }
            }
            struct AdjustmentEnumerator {
                private AdjustmentSequence _owner;
                private int _index;
                public int Current {
                    const get => _index == 1 ? _owner.First : _owner.Second;
                }
                public bool MoveNext() {
                    if (_index >= 2) return false;
                    _index += 1;
                    return true;
                }
                public void Dispose() { _owner.Second += 0; }
                public constructor(AdjustmentSequence owner) {
                    _owner = owner;
                    _index = 0;
                }
            }
            Task<int> LaterSource(int priorResult, Task<int> source) {
                return source;
            }
            async Task<int> Combine(Task<int> first, Task<int> second,
                Task<int> third, AdjustmentSequence adjustments) {
                var left = await first;
                foreach (int adjustment in adjustments) {
                    left += adjustment;
                }
                Task<int> selected = LaterSource(left, second);
                var middle = await selected;
                foreach (int adjustment in adjustments) {
                    middle += adjustment;
                }
                Task<int> finalSelected = LaterSource(middle, third);
                var right = await finalSelected;
                return left + middle + right;
            }
            """);

        new SemanticBinder().Bind(project);

        var combine = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Combine");
        var callbackLoops = CoroutineSyntaxAnalysis
            .EnumerateBodyExpressions(combine.Body!).OfType<LambdaExpression>()
            .Where(lambda => lambda.StatementBody is not null)
            .SelectMany(lambda => CoroutineSyntaxAnalysis.EnumerateStatements(
                lambda.StatementBody!))
            .OfType<ForeachStatement>().ToArray();
        Assert.Equal(2, callbackLoops.Length);
        Assert.All(callbackLoops, loop =>
        {
            Assert.False(loop.IsAwait);
            Assert.NotNull(loop.VariableSymbol);
            Assert.NotNull(loop.EnumeratorSymbol);
            Assert.NotNull(loop.GetEnumeratorInvocation);
            Assert.NotNull(loop.MoveNextInvocation);
            Assert.NotNull(loop.CurrentExpression);
            Assert.NotNull(loop.DisposeInvocation);
            Assert.Contains(CoroutineSyntaxAnalysis.EnumerateBodyExpressions(
                    [loop.Body]).OfType<IdentifierExpression>(), identifier =>
                identifier.Identifier.ToString().StartsWith(
                    "__cx_async_callback_result_", StringComparison.Ordinal));
        });
        Assert.NotSame(callbackLoops[0].VariableSymbol,
            callbackLoops[1].VariableSymbol);
        Assert.NotSame(callbackLoops[0].EnumeratorSymbol,
            callbackLoops[1].EnumeratorSymbol);
    }

    [Fact]
    public void AsyncTaskAllCompletedThreeAwaitConditionalCallSourceDefersRelayAllocation()
    {
        var project = CreateProject("""
            import System.Threading.Tasks;
            namespace App;
            Task<int> FirstSource(Task<int> source) { return source; }
            Task<int> SecondSource(Task<int> source) { return source; }
            Task<int> LaterSource(int priorResult, Task<int> source) { return source; }
            async Task<int> Combine(Task<int> first, Task<int> second,
                Task<int> third, bool chooseFirst) {
                var left = await first;
                Task<int> selected = chooseFirst
                    ? FirstSource(second) : SecondSource(second);
                var middle = await selected;
                Task<int> finalSelected = LaterSource(middle, third);
                var right = await finalSelected;
                return left + middle + right;
            }
            """);

        new SemanticBinder().Bind(project);

        var combine = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Combine");
        var guarded = Assert.IsType<TryStatement>(Assert.Single(combine.Body!));
        var firstReady = Assert.IsType<IfStatement>(
            Assert.Single(Assert.IsType<BlockStatement>(guarded.Body).Statements));
        var firstInline = Assert.IsType<BlockStatement>(firstReady.ThenStatement).Statements;
        var selected = Assert.IsType<LocalVariableDeclarationStatement>(firstInline[1]);
        Assert.IsType<ConditionalExpression>(selected.Declarators.Single().Initializer);
        var secondReady = Assert.IsType<IfStatement>(firstInline[2]);
        var secondInline = Assert.IsType<BlockStatement>(secondReady.ThenStatement).Statements;
        Assert.IsType<InvocationExpression>(
            Assert.IsType<LocalVariableDeclarationStatement>(secondInline[1])
                .Declarators.Single().Initializer);
        var thirdReady = Assert.IsType<IfStatement>(secondInline[2]);
        var thirdInline = Assert.IsType<BlockStatement>(thirdReady.ThenStatement).Statements;
        Assert.IsType<ObjectCreationExpression>(
            Assert.IsType<ReturnStatement>(thirdInline[^1]).Expression);

        foreach (var pending in new[]
                     { firstReady.ElseStatement!, secondReady.ElseStatement!,
                       thirdReady.ElseStatement! })
        {
            Assert.Contains(Assert.IsType<BlockStatement>(pending).Statements, statement =>
                statement is LocalVariableDeclarationStatement
                {
                    Declarators: [{ Initializer: ObjectCreationExpression }],
                });
        }
    }

    [Fact]
    public void AsyncTaskAllCompletedThreeAwaitNestedConditionalCallSourceDefersRelayAllocation()
    {
        var project = CreateProject("""
            import System.Threading.Tasks;
            namespace App;
            public static class TaskSources {
                public static Task<int> FirstSource(Task<int> source) { return source; }
                public static Task<int> SecondSource(Task<int> source) { return source; }
            }
            Task<int> LaterSource(int priorResult, Task<int> source) { return source; }
            async Task<int> Combine(Task<int> first, Task<int> second,
                Task<int> third, bool chooseFirst, bool chooseSecond) {
                var left = await first;
                Task<int> selected = chooseFirst
                    ? App.TaskSources.FirstSource(second)
                    : chooseSecond ? App.TaskSources.SecondSource(second) : second;
                var middle = await selected;
                Task<int> finalSelected = LaterSource(middle, third);
                var right = await finalSelected;
                return left + middle + right;
            }
            """);

        new SemanticBinder().Bind(project);

        var combine = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Combine");
        var guarded = Assert.IsType<TryStatement>(Assert.Single(combine.Body!));
        var firstReady = Assert.IsType<IfStatement>(
            Assert.Single(Assert.IsType<BlockStatement>(guarded.Body).Statements));
        var firstInline = Assert.IsType<BlockStatement>(firstReady.ThenStatement).Statements;
        var selected = Assert.IsType<LocalVariableDeclarationStatement>(firstInline[1]);
        var outer = Assert.IsType<ConditionalExpression>(
            selected.Declarators.Single().Initializer);
        Assert.IsType<InvocationExpression>(outer.WhenTrue);
        var inner = Assert.IsType<ConditionalExpression>(outer.WhenFalse);
        Assert.IsType<InvocationExpression>(inner.WhenTrue);
        Assert.IsType<IdentifierExpression>(inner.WhenFalse);
        var sources = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.ClassDeclaration>()
            .Single(type => type.Name == "TaskSources");
        foreach (var invocation in new[]
                 {
                     (InvocationExpression)outer.WhenTrue,
                     (InvocationExpression)inner.WhenTrue,
                 })
        {
            Assert.Same(sources, invocation.TargetSymbol?.Declaration?.ParentClassDeclaration);
            Assert.True(invocation.TargetSymbol?.Declaration?.IsStatic);
            Assert.Null(invocation.Receiver);
        }
        var secondReady = Assert.IsType<IfStatement>(firstInline[2]);
        var secondInline = Assert.IsType<BlockStatement>(secondReady.ThenStatement).Statements;
        var thirdReady = Assert.IsType<IfStatement>(secondInline[2]);
        var thirdInline = Assert.IsType<BlockStatement>(thirdReady.ThenStatement).Statements;
        var qualifiedFunctionInvocation = Assert.Single(secondInline
            .OfType<LocalVariableDeclarationStatement>()
            .SelectMany(statement => statement.Declarators)
            .Select(declarator => declarator.Initializer)
            .OfType<InvocationExpression>(),
            invocation => invocation.TargetSymbol?.Declaration?.Name == "LaterSource");
        Assert.Equal("App",
            qualifiedFunctionInvocation.TargetSymbol?.Declaration?.Namespace.ToString());
        Assert.Null(qualifiedFunctionInvocation.TargetSymbol?.Declaration?.ParentClassDeclaration);
        Assert.IsType<ObjectCreationExpression>(
            Assert.IsType<ReturnStatement>(thirdInline[^1]).Expression);
        Assert.NotNull(firstReady.ElseStatement);
        Assert.NotNull(secondReady.ElseStatement);
        Assert.NotNull(thirdReady.ElseStatement);
    }

    [Fact]
    public void AsyncTaskAllCompletedThreeAwaitImportedCallSourceDefersRelayAllocation()
    {
        var project = CxProject.CreateDefaultApplicationProject();
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            import System.Threading.Tasks;
            namespace Math;
            public static class TaskSources {
                public static Task<int> Forward(Task<int> source) { return source; }
            }
            """));
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            import Math;
            import System.Threading.Tasks;
            namespace App;
            async Task<int> Combine(Task<int> first, Task<int> second,
                Task<int> third) {
                var left = await first;
                Task<int> selected = Math.TaskSources.Forward(second);
                var middle = await selected;
                Task<int> finalSelected = Math.TaskSources.Forward(third);
                var right = await finalSelected;
                return left + middle + right;
            }
            """));

        new SemanticBinder().Bind(project);

        var combine = project.CompilationContexts
            .SelectMany(context => context.DeclarationScope.Declarations)
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Combine");
        Assert.True(combine.IsPendingAwaitRelay);
        var guarded = Assert.IsType<TryStatement>(Assert.Single(combine.Body!));
        var firstReady = Assert.IsType<IfStatement>(
            Assert.Single(Assert.IsType<BlockStatement>(guarded.Body).Statements));
        var firstInline = Assert.IsType<BlockStatement>(firstReady.ThenStatement).Statements;
        var selected = Assert.Single(firstInline
            .OfType<LocalVariableDeclarationStatement>(),
            declaration => declaration.Declarators.Single().Name == "selected");
        var importedCall = Assert.IsType<InvocationExpression>(
            selected.Declarators.Single().Initializer);
        Assert.Equal("TaskSources",
            importedCall.TargetSymbol?.Declaration?.ParentClassDeclaration?.Name);
        Assert.Equal("Math.TaskSources.Forward",
            importedCall.TargetSymbol?.Declaration?.FullName.ToString());
        Assert.True(importedCall.TargetSymbol?.Declaration?.IsStatic);
        Assert.Null(importedCall.Receiver);
    }

    [Fact]
    public void AsyncTaskAllCompletedImportedTopLevelCallSourceDefersRelayAllocation()
    {
        var project = CxProject.CreateDefaultApplicationProject();
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            import System.Threading.Tasks;
            namespace Math;
            public Task<int> Forward(int priorResult, Task<int> source) {
                return source;
            }
            """));
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            import Math;
            import System.Threading.Tasks;
            namespace App;
            async Task<int> Combine(Task<int> first, Task<int> second) {
                var priorResult = await first;
                Task<int> selected = Forward(priorResult, second);
                var result = await selected;
                return priorResult + result;
            }
            """));

        new SemanticBinder().Bind(project);

        var combine = project.CompilationContexts
            .SelectMany(context => context.DeclarationScope.Declarations)
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Combine");
        Assert.True(combine.IsPendingAwaitRelay);
        var guarded = Assert.IsType<TryStatement>(Assert.Single(combine.Body!));
        var firstReady = Assert.IsType<IfStatement>(
            Assert.Single(Assert.IsType<BlockStatement>(guarded.Body).Statements));
        var firstInline = Assert.IsType<BlockStatement>(firstReady.ThenStatement).Statements;
        var selected = Assert.Single(firstInline
            .OfType<LocalVariableDeclarationStatement>(),
            declaration => declaration.Declarators.Single().Name == "selected");
        var importedCall = Assert.IsType<InvocationExpression>(
            selected.Declarators.Single().Initializer);
        Assert.Equal("Math.Forward",
            importedCall.TargetSymbol?.Declaration?.FullName.ToString());
        Assert.Null(importedCall.TargetSymbol?.Declaration?.ParentClassDeclaration);
        Assert.Null(importedCall.Receiver);
        Assert.Equal("System.Int",
            Assert.IsType<NamedType>(importedCall.TargetSymbol?.Declaration?.ReturnType)
                .TypeArguments.Single().FullName.ToString());
    }

    [Fact]
    public void AsyncTaskAllCompletedImportedConditionalCallSourceDefersRelayAllocation()
    {
        var project = CxProject.CreateDefaultApplicationProject();
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            import System.Threading.Tasks;
            namespace Math;
            public static class TaskSources {
                public static Task<int> Forward(Task<int> source) { return source; }
            }
            """));
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            import Math;
            import System.Threading.Tasks;
            namespace App;
            async Task<int> Combine(Task<int> first, Task<int> second,
                Task<int> third, bool chooseImported) {
                var left = await first;
                Task<int> selected = chooseImported
                    ? Math.TaskSources.Forward(second)
                    : second;
                var middle = await selected;
                Task<int> finalSelected = Math.TaskSources.Forward(third);
                var right = await finalSelected;
                return left + middle + right;
            }
            """));

        new SemanticBinder().Bind(project);

        var combine = project.CompilationContexts
            .SelectMany(context => context.DeclarationScope.Declarations)
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Combine");
        Assert.True(combine.IsPendingAwaitRelay);
        var guarded = Assert.IsType<TryStatement>(Assert.Single(combine.Body!));
        var firstReady = Assert.IsType<IfStatement>(
            Assert.Single(Assert.IsType<BlockStatement>(guarded.Body).Statements));
        var firstInline = Assert.IsType<BlockStatement>(firstReady.ThenStatement).Statements;
        var selected = Assert.Single(firstInline
            .OfType<LocalVariableDeclarationStatement>(),
            declaration => declaration.Declarators.Single().Name == "selected");
        var condition = Assert.IsType<ConditionalExpression>(
            selected.Declarators.Single().Initializer);
        var importedCall = Assert.IsType<InvocationExpression>(condition.WhenTrue);
        Assert.Equal("Math.TaskSources.Forward",
            importedCall.TargetSymbol?.Declaration?.FullName.ToString());
        Assert.True(importedCall.TargetSymbol?.Declaration?.IsStatic);
        Assert.Null(importedCall.Receiver);
        Assert.IsType<IdentifierExpression>(condition.WhenFalse);
        var secondReady = Assert.IsType<IfStatement>(firstInline[^1]);
        var secondInline = Assert.IsType<BlockStatement>(secondReady.ThenStatement).Statements;
        var thirdReady = Assert.IsType<IfStatement>(secondInline[^1]);
        Assert.IsType<ObjectCreationExpression>(
            Assert.IsType<ReturnStatement>(
                Assert.IsType<BlockStatement>(thirdReady.ThenStatement).Statements[^1])
                .Expression);
    }

    [Fact]
    public void AsyncTaskImportedOverloadCallSourceKeepsFallbackAndBindsMatchingOverload()
    {
        var project = CxProject.CreateDefaultApplicationProject();
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            import System.Threading.Tasks;
            namespace Math;
            public static class TaskSources {
                public static Task<int> Forward(Task<int> source) { return source; }
                public static Task<int> Forward(Task<int> source, int offset) {
                    return source;
                }
            }
            """));
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            import Math;
            import System.Threading.Tasks;
            namespace App;
            async Task<int> Combine(Task<int> first, Task<int> second) {
                var left = await first;
                Task<int> selected = Math.TaskSources.Forward(second);
                var right = await selected;
                return left + right;
            }
            """));

        new SemanticBinder().Bind(project);

        var combine = project.CompilationContexts
            .SelectMany(context => context.DeclarationScope.Declarations)
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Combine");
        Assert.True(combine.IsPendingAwaitRelay);
        var fallbackCallbacks = CoroutineSyntaxAnalysis
            .EnumerateBodyExpressions(combine.Body!)
            .OfType<LambdaExpression>();
        var call = Assert.Single(fallbackCallbacks.SelectMany(callback =>
                CoroutineSyntaxAnalysis.EnumerateBodyExpressions(
                    callback.StatementBody ?? []))
            .OfType<InvocationExpression>(), invocation =>
                invocation.Target is MemberAccessExpression
                {
                    MemberName: "Forward",
                });
        var selectedOverload = Assert.IsType<CxCompiler.Model.Types.FunctionDeclaration>(
            call.TargetSymbol?.Declaration);
        Assert.Equal("Math.TaskSources.Forward",
            selectedOverload.FullName.ToString());
        Assert.Single(selectedOverload.Parameters);
        Assert.True(selectedOverload.IsStatic);
        Assert.Null(call.Receiver);

        var outerTry = Assert.IsType<TryStatement>(Assert.Single(combine.Body!));
        var loweredBody = Assert.IsType<BlockStatement>(outerTry.Body).Statements;
        var completionSourceIndex = loweredBody.ToList().FindIndex(statement =>
            CoroutineSyntaxAnalysis.EnumerateBodyExpressions([statement])
                .OfType<ObjectCreationExpression>()
                .Any(creation => creation.RequestedType.FullName.ToString().Contains(
                    "TaskCompletionSource", StringComparison.Ordinal)));
        var completionCheckIndex = loweredBody.ToList().FindIndex(statement =>
            statement is IfStatement
            {
                Condition: MemberAccessExpression { MemberName: "IsCompleted" },
            });
        Assert.True(completionSourceIndex >= 0,
            "The ambiguous producer set should use the existing relay fallback.");
        Assert.True(completionCheckIndex > completionSourceIndex,
            "Fallback relay state should be initialized before the readiness check.");
    }

    [Fact]
    public void AsyncTaskImportedOverloadConditionalCallSourceKeepsFallbackAndBindsBranches()
    {
        var project = CxProject.CreateDefaultApplicationProject();
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            import System.Threading.Tasks;
            namespace Math;
            public static class TaskSources {
                public static Task<int> Forward(Task<int> source) { return source; }
                public static Task<int> Forward(Task<int> source, int offset) {
                    return source;
                }
            }
            """));
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            import Math;
            import System.Threading.Tasks;
            namespace App;
            async Task<int> Combine(Task<int> first, Task<int> second,
                    bool chooseOneArgument) {
                var left = await first;
                Task<int> selected = chooseOneArgument
                    ? Math.TaskSources.Forward(second)
                    : Math.TaskSources.Forward(second, 3);
                var right = await selected;
                return left + right;
            }
            """));

        new SemanticBinder().Bind(project);

        var combine = project.CompilationContexts
            .SelectMany(context => context.DeclarationScope.Declarations)
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Combine");
        Assert.True(combine.IsPendingAwaitRelay);
        var callbacks = CoroutineSyntaxAnalysis
            .EnumerateBodyExpressions(combine.Body!)
            .OfType<LambdaExpression>();
        var calls = callbacks.SelectMany(callback =>
                CoroutineSyntaxAnalysis.EnumerateBodyExpressions(
                    callback.StatementBody ?? []))
            .OfType<InvocationExpression>()
            .Where(invocation => invocation.Target is MemberAccessExpression
            {
                MemberName: "Forward",
            })
            .ToArray();
        Assert.Equal(2, calls.Length);
        Assert.Equal([1, 2], calls.Select(call =>
            Assert.IsType<CxCompiler.Model.Types.FunctionDeclaration>(
                call.TargetSymbol?.Declaration).Parameters.Count).Order().ToArray());
        Assert.All(calls, call => Assert.Null(call.Receiver));

        var outerTry = Assert.IsType<TryStatement>(Assert.Single(combine.Body!));
        var loweredBody = Assert.IsType<BlockStatement>(outerTry.Body).Statements;
        var completionSourceIndex = loweredBody.ToList().FindIndex(statement =>
            CoroutineSyntaxAnalysis.EnumerateBodyExpressions([statement])
                .OfType<ObjectCreationExpression>()
                .Any(creation => creation.RequestedType.FullName.ToString().Contains(
                    "TaskCompletionSource", StringComparison.Ordinal)));
        var completionCheckIndex = loweredBody.ToList().FindIndex(statement =>
            statement is IfStatement
            {
                Condition: MemberAccessExpression { MemberName: "IsCompleted" },
            });
        Assert.True(completionSourceIndex >= 0);
        Assert.True(completionCheckIndex > completionSourceIndex);
    }

    [Fact]
    public void AsyncTaskImportedOverloadConditionalParameterSourceKeepsFallback()
    {
        var project = CxProject.CreateDefaultApplicationProject();
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            import System.Threading.Tasks;
            namespace Math;
            public static class TaskSources {
                public static Task<int> Forward(Task<int> source) { return source; }
                public static Task<int> Forward(Task<int> source, int offset) {
                    return source;
                }
            }
            """));
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            import Math;
            import System.Threading.Tasks;
            namespace App;
            async Task<int> Combine(Task<int> first, Task<int> second,
                    Task<int> alternate, bool chooseImported) {
                var left = await first;
                Task<int> selected = chooseImported
                    ? Math.TaskSources.Forward(second)
                    : alternate;
                var right = await selected;
                return left + right;
            }
            """));

        new SemanticBinder().Bind(project);

        var combine = project.CompilationContexts
            .SelectMany(context => context.DeclarationScope.Declarations)
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Combine");
        Assert.True(combine.IsPendingAwaitRelay);
        var callbacks = CoroutineSyntaxAnalysis
            .EnumerateBodyExpressions(combine.Body!)
            .OfType<LambdaExpression>()
            .ToArray();
        var call = Assert.Single(callbacks.SelectMany(callback =>
                CoroutineSyntaxAnalysis.EnumerateBodyExpressions(
                    callback.StatementBody ?? []))
            .OfType<InvocationExpression>(), invocation =>
                invocation.Target is MemberAccessExpression
                {
                    MemberName: "Forward",
                });
        var selectedOverload = Assert.IsType<CxCompiler.Model.Types.FunctionDeclaration>(
            call.TargetSymbol?.Declaration);
        Assert.Single(selectedOverload.Parameters);
        var sourceTree = Assert.Single(callbacks.SelectMany(callback =>
                CoroutineSyntaxAnalysis.EnumerateBodyExpressions(
                    callback.StatementBody ?? []))
            .OfType<ConditionalExpression>(), conditional =>
                conditional.WhenTrue is InvocationExpression invocation &&
                invocation.Target is MemberAccessExpression
                {
                    MemberName: "Forward",
                });
        Assert.IsType<IdentifierExpression>(sourceTree.WhenFalse);

        var outerTry = Assert.IsType<TryStatement>(Assert.Single(combine.Body!));
        var loweredBody = Assert.IsType<BlockStatement>(outerTry.Body).Statements;
        var completionSourceIndex = loweredBody.ToList().FindIndex(statement =>
            CoroutineSyntaxAnalysis.EnumerateBodyExpressions([statement])
                .OfType<ObjectCreationExpression>()
                .Any(creation => creation.RequestedType.FullName.ToString().Contains(
                    "TaskCompletionSource", StringComparison.Ordinal)));
        var completionCheckIndex = loweredBody.ToList().FindIndex(statement =>
            statement is IfStatement
            {
                Condition: MemberAccessExpression { MemberName: "IsCompleted" },
            });
        Assert.True(completionSourceIndex >= 0);
        Assert.True(completionCheckIndex > completionSourceIndex);
    }

    [Fact]
    public void AsyncTaskAmbiguousImportedTopLevelCallSourceKeepsBinderDiagnostic()
    {
        var project = CxProject.CreateDefaultApplicationProject();
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            import System.Threading.Tasks;
            namespace MathA;
            public Task<int> Forward(Task<int> source) { return source; }
            """));
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            import System.Threading.Tasks;
            namespace MathB;
            public Task<int> Forward(Task<int> source) { return source; }
            """));
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            import MathA;
            import MathB;
            import System.Threading.Tasks;
            namespace App;
            async Task<int> Combine(Task<int> first, Task<int> second) {
                var left = await first;
                Task<int> selected = Forward(second);
                var right = await selected;
                return left + right;
            }
            """));

        var error = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(project));
        Assert.Contains("Function call 'Forward' with 1 argument(s) is ambiguous",
            error.Message);
    }

    [Fact]
    public void AsyncTaskImportedCallSourceOnlyMatchesPublicApiCandidates()
    {
        var project = CxProject.CreateDefaultApplicationProject();
        project.AddReferencedCompilationContext(CompilerTestHelper.Parse("""
            import System.Threading.Tasks;
            namespace Math;
            public static class TaskSources {
                public static Task<int> Forward(Task<int> source) { return source; }
                private static Task<int> Forward(Task<string> source) { return null; }
            }
            """), "math");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            import Math;
            import System.Threading.Tasks;
            namespace App;
            async Task<int> Combine(Task<int> first, Task<string> second) {
                var left = await first;
                Task<int> selected = Math.TaskSources.Forward(second);
                var right = await selected;
                return left + right;
            }
            """));

        var combine = project.CompilationContexts
            .SelectMany(context => context.DeclarationScope.Declarations)
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Combine");
        var error = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(project));

        Assert.Contains("No overload of 'Forward'", error.Message);
        var guarded = Assert.IsType<TryStatement>(Assert.Single(combine.Body!));
        var loweredBody = Assert.IsType<BlockStatement>(guarded.Body).Statements;
        var firstReady = Assert.IsType<IfStatement>(Assert.Single(loweredBody));
        Assert.IsType<MemberAccessExpression>(firstReady.Condition);
        Assert.Equal("IsCompleted",
            Assert.IsType<MemberAccessExpression>(firstReady.Condition).MemberName);
        Assert.Contains(CoroutineSyntaxAnalysis.EnumerateBodyExpressions(
                firstReady.ElseStatement is BlockStatement pending
                    ? pending.Statements
                    : [firstReady.ElseStatement!])
            .OfType<ObjectCreationExpression>(), creation =>
                creation.RequestedType.FullName.ToString().Contains(
                    "TaskCompletionSource", StringComparison.Ordinal));
        var firstInline = Assert.IsType<BlockStatement>(firstReady.ThenStatement).Statements;
        var selected = Assert.Single(firstInline
            .OfType<LocalVariableDeclarationStatement>(),
            declaration => declaration.Declarators.Single().Name == "selected");
        var call = Assert.IsType<InvocationExpression>(selected.Declarators.Single().Initializer);
        Assert.Null(call.TargetSymbol);
        Assert.IsType<ObjectCreationExpression>(
            Assert.IsType<ReturnStatement>(
                Assert.IsType<BlockStatement>(
                    Assert.IsType<IfStatement>(firstInline[^1]).ThenStatement).Statements[^1])
                .Expression);
    }

    [Fact]
    public void AsyncTaskImportedPropertyTaskSourceKeepsFallbackAndBindsGetter()
    {
        var project = CxProject.CreateDefaultApplicationProject();
        project.AddReferencedCompilationContext(CompilerTestHelper.Parse("""
            import System.Threading.Tasks;
            namespace Math;
            public static class TaskSources {
                public static Task<int> Current { extern get; }
            }
            """), "math");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            import Math;
            import System.Threading.Tasks;
            namespace App;
            async Task<int> Combine(Task<int> first) {
                var left = await first;
                Task<int> selected = Math.TaskSources.Current;
                var right = await selected;
                return left + right;
            }
            """));

        new SemanticBinder().Bind(project);

        var combine = project.CompilationContexts
            .Where(context => !context.IsProjectReference)
            .SelectMany(context => context.DeclarationScope.Declarations)
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Combine");
        Assert.True(combine.IsPendingAwaitRelay);
        var guarded = Assert.IsType<TryStatement>(Assert.Single(combine.Body!));
        var loweredBody = Assert.IsType<BlockStatement>(guarded.Body).Statements;
        var completionSourceIndex = loweredBody.ToList().FindIndex(statement =>
            CoroutineSyntaxAnalysis.EnumerateBodyExpressions([statement])
                .OfType<ObjectCreationExpression>()
                .Any(creation => creation.RequestedType.FullName.ToString().Contains(
                    "TaskCompletionSource", StringComparison.Ordinal)));
        var completionCheckIndex = loweredBody.ToList().FindIndex(statement =>
            statement is IfStatement
            {
                Condition: MemberAccessExpression { MemberName: "IsCompleted" },
            });
        Assert.True(completionSourceIndex >= 0);
        Assert.True(completionCheckIndex > completionSourceIndex,
            "Property-backed sources should retain the relay fallback until property-source classification is supported.");
        var callbacks = CoroutineSyntaxAnalysis
            .EnumerateBodyExpressions(combine.Body!)
            .OfType<LambdaExpression>().ToArray();
        var memberAccesses = CoroutineSyntaxAnalysis
            .EnumerateBodyExpressions(combine.Body!)
            .Concat(callbacks.SelectMany(callback =>
                CoroutineSyntaxAnalysis.EnumerateBodyExpressions(
                    callback.StatementBody ?? [])))
            .OfType<MemberAccessExpression>().ToArray();
        var propertyAccess = Assert.Single(memberAccesses, access =>
            access.TargetProperty?.FullName.ToString() == "Math.TaskSources.Current");
        Assert.NotNull(propertyAccess.PropertyGetter);
        Assert.Equal("Math.TaskSources.Current",
            propertyAccess.TargetProperty!.FullName.ToString());
        Assert.True(propertyAccess.TargetProperty.IsStatic);
    }

    [Fact]
    public void AsyncTaskImportedFieldTaskSourceKeepsFallbackAndBindsField()
    {
        var project = CxProject.CreateDefaultApplicationProject();
        project.AddReferencedCompilationContext(CompilerTestHelper.Parse("""
            import System.Threading.Tasks;
            namespace Math;
            public static class TaskSources {
                public static Task<int> Current;
            }
            """), "math");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            import Math;
            import System.Threading.Tasks;
            namespace App;
            async Task<int> Combine(Task<int> first) {
                var left = await first;
                Task<int> selected = Math.TaskSources.Current;
                var right = await selected;
                return left + right;
            }
            """));

        new SemanticBinder().Bind(project);

        var combine = project.CompilationContexts
            .Where(context => !context.IsProjectReference)
            .SelectMany(context => context.DeclarationScope.Declarations)
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Combine");
        Assert.True(combine.IsPendingAwaitRelay);
        var guarded = Assert.IsType<TryStatement>(Assert.Single(combine.Body!));
        var loweredBody = Assert.IsType<BlockStatement>(guarded.Body).Statements;
        var completionSourceIndex = loweredBody.ToList().FindIndex(statement =>
            CoroutineSyntaxAnalysis.EnumerateBodyExpressions([statement])
                .OfType<ObjectCreationExpression>()
                .Any(creation => creation.RequestedType.FullName.ToString().Contains(
                    "TaskCompletionSource", StringComparison.Ordinal)));
        var completionCheckIndex = loweredBody.ToList().FindIndex(statement =>
            statement is IfStatement
            {
                Condition: MemberAccessExpression { MemberName: "IsCompleted" },
            });
        Assert.True(completionSourceIndex >= 0);
        Assert.True(completionCheckIndex > completionSourceIndex,
            "Field-backed sources should retain the relay fallback until field-source classification is supported.");
        var callbacks = CoroutineSyntaxAnalysis
            .EnumerateBodyExpressions(combine.Body!)
            .OfType<LambdaExpression>().ToArray();
        var fieldAccesses = CoroutineSyntaxAnalysis
            .EnumerateBodyExpressions(combine.Body!)
            .Concat(callbacks.SelectMany(callback =>
                CoroutineSyntaxAnalysis.EnumerateBodyExpressions(
                    callback.StatementBody ?? [])))
            .OfType<MemberAccessExpression>().ToArray();
        var fieldAccess = Assert.Single(fieldAccesses, access =>
            access.TargetField?.Declaration.FullName.ToString() ==
                "Math.TaskSources.Current");
        Assert.True(fieldAccess.TargetField!.Declaration.MemberModifiers
            .Contains(CxCompiler.Model.Types.MemberModifier.Public));
    }

    [Fact]
    public void AsyncTaskImportedExtensionTaskSourceKeepsFallbackAndBindsExtension()
    {
        var project = CxProject.CreateDefaultApplicationProject();
        project.AddReferencedCompilationContext(CompilerTestHelper.Parse("""
            import System.Threading.Tasks;
            namespace Math;
            public class Source {
                public Task<int> Current { extern get; }
            }
            public extension SourceExtensions : Source {
                public Task<int> Forward() const { return this.Current; }
            }
            """), "math");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            import Math;
            import System.Threading.Tasks;
            namespace App;
            async Task<int> Combine(Task<int> first, Source second) {
                var left = await first;
                Task<int> selected = second.Forward();
                var right = await selected;
                return left + right;
            }
            """));

        new SemanticBinder().Bind(project);

        var combine = project.CompilationContexts
            .Where(context => !context.IsProjectReference)
            .SelectMany(context => context.DeclarationScope.Declarations)
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Combine");
        Assert.True(combine.IsPendingAwaitRelay);
        var guarded = Assert.IsType<TryStatement>(Assert.Single(combine.Body!));
        var loweredBody = Assert.IsType<BlockStatement>(guarded.Body).Statements;
        var completionSourceIndex = loweredBody.ToList().FindIndex(statement =>
            CoroutineSyntaxAnalysis.EnumerateBodyExpressions([statement])
                .OfType<ObjectCreationExpression>()
                .Any(creation => creation.RequestedType.FullName.ToString().Contains(
                    "TaskCompletionSource", StringComparison.Ordinal)));
        var completionCheckIndex = loweredBody.ToList().FindIndex(statement =>
            statement is IfStatement
            {
                Condition: MemberAccessExpression { MemberName: "IsCompleted" },
            });
        Assert.True(completionSourceIndex >= 0);
        Assert.True(completionCheckIndex > completionSourceIndex,
            "Imported extension producers should retain relay fallback until extension identity is supported.");
        var callbackInvocations = CoroutineSyntaxAnalysis
            .EnumerateBodyExpressions(combine.Body!)
            .OfType<LambdaExpression>()
            .SelectMany(callback => CoroutineSyntaxAnalysis
                .EnumerateBodyExpressions(callback.StatementBody ?? []))
            .OfType<InvocationExpression>();
        var call = Assert.Single(callbackInvocations, invocation =>
            invocation.Target is MemberAccessExpression { MemberName: "Forward" });
        Assert.True(call.TargetSymbol?.Declaration?.IsExtension);
        Assert.Equal("Math.SourceExtensions.Forward",
            call.TargetSymbol?.Declaration?.FullName.ToString());
        Assert.NotNull(call.Receiver);
    }

    [Fact]
    public void AsyncTaskImportedNullCoalescingCallSourceKeepsBranchAndUsesInlinePath()
    {
        var project = CxProject.CreateDefaultApplicationProject();
        project.AddReferencedCompilationContext(CompilerTestHelper.Parse("""
            import System.Threading.Tasks;
            namespace Math;
            public static class TaskSources {
                public static Task<int> Forward(Task<int> source) { return source; }
            }
            """), "math");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            import Math;
            import System.Threading.Tasks;
            namespace App;
            async Task<int> Combine(Task<int> first, Task<int> second) {
                var left = await first;
                Task<int> selected = second ?? Math.TaskSources.Forward(second);
                var right = await selected;
                return left + right;
            }
            """));

        new SemanticBinder().Bind(project);

        var combine = project.CompilationContexts
            .Where(context => !context.IsProjectReference)
            .SelectMany(context => context.DeclarationScope.Declarations)
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Combine");
        Assert.True(combine.IsPendingAwaitRelay);
        var guarded = Assert.IsType<TryStatement>(Assert.Single(combine.Body!));
        var loweredBody = Assert.IsType<BlockStatement>(guarded.Body).Statements;
        var firstReady = Assert.IsType<IfStatement>(Assert.Single(loweredBody));
        Assert.Equal("IsCompleted",
            Assert.IsType<MemberAccessExpression>(firstReady.Condition).MemberName);
        var firstInline = Assert.IsType<BlockStatement>(firstReady.ThenStatement).Statements;
        var selected = Assert.Single(firstInline
            .OfType<LocalVariableDeclarationStatement>(),
            declaration => declaration.Declarators.Single().Name == "selected");
        var coalescing = Assert.IsType<NullCoalescingExpression>(
            selected.Declarators.Single().Initializer);
        Assert.Equal("second", Assert.IsType<IdentifierExpression>(coalescing.Left)
            .Identifier.ToString());
        var call = Assert.IsType<InvocationExpression>(coalescing.Right);
        Assert.Equal("Math.TaskSources.Forward",
            call.TargetSymbol?.Declaration?.FullName.ToString());
        var pending = Assert.IsType<BlockStatement>(firstReady.ElseStatement).Statements;
        Assert.Contains(CoroutineSyntaxAnalysis.EnumerateBodyExpressions(pending)
            .OfType<ObjectCreationExpression>(), creation =>
                creation.RequestedType.FullName.ToString().Contains(
                    "TaskCompletionSource", StringComparison.Ordinal));
    }

    [Fact]
    public void AsyncTaskImportedSwitchCallSourceKeepsLazyArmAndUsesInlinePath()
    {
        var project = CxProject.CreateDefaultApplicationProject();
        project.AddReferencedCompilationContext(CompilerTestHelper.Parse("""
            import System.Threading.Tasks;
            namespace Math;
            public static class TaskSources {
                public static Task<int> Forward(Task<int> source) { return source; }
            }
            """), "math");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            import Math;
            import System.Threading.Tasks;
            namespace App;
            async Task<int> Combine(Task<int> first, Task<int> second, int mode) {
                var left = await first;
                Task<int> selected = mode switch {
                    0 => second,
                    _ => Math.TaskSources.Forward(second),
                };
                var right = await selected;
                return left + right;
            }
            """));

        new SemanticBinder().Bind(project);

        var combine = project.CompilationContexts
            .Where(context => !context.IsProjectReference)
            .SelectMany(context => context.DeclarationScope.Declarations)
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Combine");
        Assert.True(combine.IsPendingAwaitRelay);
        var guarded = Assert.IsType<TryStatement>(Assert.Single(combine.Body!));
        var loweredBody = Assert.IsType<BlockStatement>(guarded.Body).Statements;
        var firstReady = Assert.IsType<IfStatement>(Assert.Single(loweredBody));
        var firstInline = Assert.IsType<BlockStatement>(firstReady.ThenStatement).Statements;
        var selected = Assert.Single(firstInline
            .OfType<LocalVariableDeclarationStatement>(),
            declaration => declaration.Declarators.Single().Name == "selected");
        var selection = Assert.IsType<SwitchExpression>(
            selected.Declarators.Single().Initializer);
        Assert.Equal("mode", Assert.IsType<IdentifierExpression>(selection.Selector)
            .Identifier.ToString());
        Assert.Equal(2, selection.Arms.Count);
        Assert.Equal("second", Assert.IsType<IdentifierExpression>(selection.Arms[0].Value)
            .Identifier.ToString());
        var call = Assert.IsType<InvocationExpression>(selection.Arms[1].Value);
        Assert.Equal("Math.TaskSources.Forward",
            call.TargetSymbol?.Declaration?.FullName.ToString());
        var pending = Assert.IsType<BlockStatement>(firstReady.ElseStatement).Statements;
        Assert.Contains(CoroutineSyntaxAnalysis.EnumerateBodyExpressions(pending)
            .OfType<ObjectCreationExpression>(), creation =>
                creation.RequestedType.FullName.ToString().Contains(
                    "TaskCompletionSource", StringComparison.Ordinal));
    }

    [Fact]
    public void AsyncTaskImportedCastCallSourceKeepsInlinePath()
    {
        var project = CxProject.CreateDefaultApplicationProject();
        project.AddReferencedCompilationContext(CompilerTestHelper.Parse("""
            import System.Threading.Tasks;
            namespace Math;
            public static class TaskSources {
                public static Task<int> Forward(Task<int> source) { return source; }
            }
            """), "math");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            import Math;
            import System.Threading.Tasks;
            namespace App;
            async Task<int> Combine(Task<int> first, Task<int> second) {
                var left = await first;
                Task<int> selected = (Task<int>)Math.TaskSources.Forward(second);
                var right = await selected;
                return left + right;
            }
            """));

        new SemanticBinder().Bind(project);

        var combine = project.CompilationContexts
            .Where(context => !context.IsProjectReference)
            .SelectMany(context => context.DeclarationScope.Declarations)
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Combine");
        Assert.True(combine.IsPendingAwaitRelay);
        var guarded = Assert.IsType<TryStatement>(Assert.Single(combine.Body!));
        var loweredBody = Assert.IsType<BlockStatement>(guarded.Body).Statements;
        var firstReady = Assert.IsType<IfStatement>(Assert.Single(loweredBody));
        var firstInline = Assert.IsType<BlockStatement>(firstReady.ThenStatement).Statements;
        var selected = Assert.Single(firstInline
            .OfType<LocalVariableDeclarationStatement>(),
            declaration => declaration.Declarators.Single().Name == "selected");
        var cast = Assert.IsType<CastExpression>(
            selected.Declarators.Single().Initializer);
        var call = Assert.IsType<InvocationExpression>(cast.Operand);
        Assert.Equal("Math.TaskSources.Forward",
            call.TargetSymbol?.Declaration?.FullName.ToString());
        var pending = Assert.IsType<BlockStatement>(firstReady.ElseStatement).Statements;
        Assert.Contains(CoroutineSyntaxAnalysis.EnumerateBodyExpressions(pending)
            .OfType<ObjectCreationExpression>(), creation =>
                creation.RequestedType.FullName.ToString().Contains(
                    "TaskCompletionSource", StringComparison.Ordinal));
    }

    [Fact]
    public void AsyncTaskImportedFirstCallSourceChecksReadinessAfterSingleEvaluation()
    {
        var project = CxProject.CreateDefaultApplicationProject();
        project.AddReferencedCompilationContext(CompilerTestHelper.Parse("""
            import System.Threading.Tasks;
            namespace Math;
            public static class TaskSources {
                public static Task<int> Forward(Task<int> source) { return source; }
            }
            """), "math");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            import Math;
            import System.Threading.Tasks;
            namespace App;
            async Task<int> Combine(Task<int> first, Task<int> second) {
                var left = await Math.TaskSources.Forward(first);
                var right = await second;
                return left + right;
            }
            """));

        new SemanticBinder().Bind(project);

        var combine = project.CompilationContexts
            .Where(context => !context.IsProjectReference)
            .SelectMany(context => context.DeclarationScope.Declarations)
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Combine");
        Assert.True(combine.IsPendingAwaitRelay);
        var guarded = Assert.IsType<TryStatement>(Assert.Single(combine.Body!));
        var callbackBody = Assert.IsType<BlockStatement>(guarded.Body).Statements;
        var optimizedScope = Assert.IsType<BlockStatement>(Assert.Single(callbackBody));
        var optimizedStatements = optimizedScope.Statements;
        var source = Assert.IsType<LocalVariableDeclarationStatement>(
            optimizedStatements[0]);
        Assert.StartsWith("__cx_async_first_task_source",
            source.Declarators.Single().Name);
        var sourceCall = Assert.IsType<InvocationExpression>(
            source.Declarators.Single().Initializer);
        Assert.Equal("Math.TaskSources.Forward",
            sourceCall.TargetSymbol?.Declaration?.FullName.ToString());
        var firstReady = Assert.IsType<IfStatement>(optimizedStatements[1]);
        var readySource = Assert.IsType<MemberAccessExpression>(firstReady.Condition);
        Assert.Equal("IsCompleted", readySource.MemberName);
        Assert.Equal(source.Declarators.Single().Name,
            Assert.IsType<IdentifierExpression>(readySource.Target)
                .Identifier.ToString());

        var pending = Assert.IsType<BlockStatement>(firstReady.ElseStatement).Statements;
        Assert.DoesNotContain(CoroutineSyntaxAnalysis.EnumerateBodyExpressions(pending)
            .OfType<InvocationExpression>(), invocation =>
                invocation.TargetSymbol?.Declaration?.FullName.ToString() ==
                    "Math.TaskSources.Forward");
        Assert.Contains(CoroutineSyntaxAnalysis.EnumerateBodyStatements(pending),
            statement => statement is LocalVariableDeclarationStatement
            {
                Declarators: [{ Initializer: IdentifierExpression pendingSource }],
            } && pendingSource.Identifier.ToString() == source.Declarators.Single().Name);
    }

    [Fact]
    public void AsyncTaskSingleImportedCallSourceChecksReadinessBeforeRelayState()
    {
        var project = CxProject.CreateDefaultApplicationProject();
        project.AddReferencedCompilationContext(CompilerTestHelper.Parse("""
            import System.Threading.Tasks;
            namespace Math;
            public static class TaskSources {
                public static Task<int> Forward(Task<int> source) { return source; }
            }
            """), "math");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            import Math;
            import System.Threading.Tasks;
            namespace App;
            async Task<int> Combine(Task<int> source) {
                var result = await Math.TaskSources.Forward(source);
                return result;
            }
            """));

        new SemanticBinder().Bind(project);

        var combine = project.CompilationContexts
            .Where(context => !context.IsProjectReference)
            .SelectMany(context => context.DeclarationScope.Declarations)
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Combine");
        Assert.True(combine.IsPendingAwaitRelay);
        var guarded = Assert.IsType<TryStatement>(Assert.Single(combine.Body!));
        var callbackBody = Assert.IsType<BlockStatement>(guarded.Body).Statements;
        var optimizedScope = Assert.IsType<BlockStatement>(Assert.Single(callbackBody));
        var statements = optimizedScope.Statements;
        var source = Assert.IsType<LocalVariableDeclarationStatement>(statements[0]);
        var sourceName = source.Declarators.Single().Name;
        Assert.StartsWith("__cx_async_first_task_source", sourceName);
        var sourceCall = Assert.IsType<InvocationExpression>(
            source.Declarators.Single().Initializer);
        Assert.Equal("Math.TaskSources.Forward",
            sourceCall.TargetSymbol?.Declaration?.FullName.ToString());

        var ready = Assert.IsType<IfStatement>(statements[1]);
        Assert.Equal("IsCompleted",
            Assert.IsType<MemberAccessExpression>(ready.Condition).MemberName);
        Assert.Equal(sourceName,
            Assert.IsType<IdentifierExpression>(
                Assert.IsType<MemberAccessExpression>(ready.Condition).Target)
                .Identifier.ToString());
        var pending = Assert.IsType<BlockStatement>(ready.ElseStatement).Statements;
        Assert.DoesNotContain(CoroutineSyntaxAnalysis.EnumerateBodyExpressions(pending)
            .OfType<InvocationExpression>(), invocation =>
                invocation.TargetSymbol?.Declaration?.FullName.ToString() ==
                    "Math.TaskSources.Forward");
        Assert.Contains(CoroutineSyntaxAnalysis.EnumerateBodyStatements(pending),
            statement => statement is LocalVariableDeclarationStatement
            {
                Declarators: [{ Initializer: IdentifierExpression pendingSource }],
            } && pendingSource.Identifier.ToString() == sourceName);
    }

    [Fact]
    public void AsyncTaskTwoAwaitImportedValueTaskCallSourceDefersRelayState()
    {
        var project = CxProject.CreateDefaultApplicationProject();
        project.AddReferencedCompilationContext(CompilerTestHelper.Parse("""
            import System.Threading.Tasks;
            namespace Math;
            public static class TaskSources {
                public static ValueTask<int> ForwardValueTask(ValueTask<int> source) {
                    return source;
                }
            }
            """), "math");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            import Math;
            import System.Threading.Tasks;
            namespace App;
            async Task<int> Combine(ValueTask<int> first, Task<int> second) {
                var left = await Math.TaskSources.ForwardValueTask(first);
                var right = await second;
                return left + right;
            }
            """));

        new SemanticBinder().Bind(project);

        var combine = project.CompilationContexts
            .Where(context => !context.IsProjectReference)
            .SelectMany(context => context.DeclarationScope.Declarations)
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Combine");
        var guarded = Assert.IsType<TryStatement>(Assert.Single(combine.Body!));
        var loweredBody = Assert.IsType<BlockStatement>(guarded.Body).Statements;
        var scope = Assert.IsType<BlockStatement>(Assert.Single(loweredBody));
        var source = Assert.IsType<LocalVariableDeclarationStatement>(scope.Statements[0]);
        var sourceName = source.Declarators.Single().Name;
        Assert.Equal("Math.TaskSources.ForwardValueTask",
            Assert.IsType<InvocationExpression>(source.Declarators.Single().Initializer)
                .TargetSymbol?.Declaration?.FullName.ToString());
        var firstReady = Assert.IsType<IfStatement>(scope.Statements[1]);
        Assert.Equal(sourceName, Assert.IsType<IdentifierExpression>(
            Assert.IsType<MemberAccessExpression>(firstReady.Condition).Target)
                .Identifier.ToString());
        var firstInline = Assert.IsType<BlockStatement>(firstReady.ThenStatement).Statements;
        var secondReady = Assert.IsType<IfStatement>(firstInline[^1]);
        Assert.Equal("IsCompleted",
            Assert.IsType<MemberAccessExpression>(secondReady.Condition).MemberName);
        var allInline = Assert.IsType<BlockStatement>(secondReady.ThenStatement).Statements;
        Assert.IsType<ObjectCreationExpression>(
            Assert.IsType<ReturnStatement>(allInline[^1]).Expression);

        var pendingFirst = Assert.IsType<BlockStatement>(firstReady.ElseStatement).Statements;
        Assert.Contains(CoroutineSyntaxAnalysis.EnumerateBodyExpressions(pendingFirst)
            .OfType<InvocationExpression>(), invocation =>
                invocation.Target is MemberAccessExpression
                {
                    MemberName: "AsTask", Target: IdentifierExpression target,
                } && target.Identifier.ToString() == sourceName);
        Assert.Equal(1, CoroutineSyntaxAnalysis.EnumerateBodyExpressions(combine.Body!)
            .OfType<InvocationExpression>().Count(invocation =>
                invocation.TargetSymbol?.Declaration?.FullName.ToString() ==
                    "Math.TaskSources.ForwardValueTask"));
    }

    [Fact]
    public void AsyncTaskTwoAwaitImportedValueTaskCallSourcePreservesTypedPrefix()
    {
        var project = CxProject.CreateDefaultApplicationProject();
        project.AddReferencedCompilationContext(CompilerTestHelper.Parse("""
            import System.Threading.Tasks;
            namespace Math;
            public static class TaskSources {
                public static ValueTask<int> ForwardValueTask(ValueTask<int> source) {
                    return source;
                }
            }
            """), "math");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            import Math;
            import System.Threading.Tasks;
            namespace App;
            async Task<int> Combine(ValueTask<int> first, Task<int> second) {
                int baseline = 1;
                var left = await Math.TaskSources.ForwardValueTask(first);
                var right = await second;
                return baseline + left + right;
            }
            """));

        new SemanticBinder().Bind(project);

        var combine = project.CompilationContexts
            .Where(context => !context.IsProjectReference)
            .SelectMany(context => context.DeclarationScope.Declarations)
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Combine");
        var guarded = Assert.IsType<TryStatement>(Assert.Single(combine.Body!));
        var outer = Assert.IsType<BlockStatement>(guarded.Body).Statements;
        var baseline = Assert.IsType<LocalVariableDeclarationStatement>(outer[0]);
        Assert.Equal("baseline", baseline.Declarators.Single().Name);
        Assert.IsType<CxCompiler.Model.Types.BuiltInTypes.IntType>(baseline.DeclaredType);
        var optimized = Assert.IsType<BlockStatement>(outer[1]);
        var source = Assert.IsType<LocalVariableDeclarationStatement>(optimized.Statements[0]);
        var sourceName = source.Declarators.Single().Name;
        Assert.Equal("Math.TaskSources.ForwardValueTask",
            Assert.IsType<InvocationExpression>(source.Declarators.Single().Initializer)
                .TargetSymbol?.Declaration?.FullName.ToString());
        var ready = Assert.IsType<IfStatement>(optimized.Statements[1]);
        Assert.Equal(sourceName,
            Assert.IsType<IdentifierExpression>(
                Assert.IsType<MemberAccessExpression>(ready.Condition).Target)
                    .Identifier.ToString());
        var inlineStatements = Assert.IsType<BlockStatement>(ready.ThenStatement).Statements;
        var secondReady = Assert.IsType<IfStatement>(inlineStatements[^1]);
        var terminal = Assert.IsType<ReturnStatement>(
            Assert.IsType<BlockStatement>(secondReady.ThenStatement).Statements[^1]);
        Assert.Contains("baseline", CoroutineSyntaxAnalysis
            .EnumerateAllExpressions(terminal.Expression!)
            .OfType<IdentifierExpression>()
            .Select(identifier => identifier.Identifier.ToString()));

        var pendingFirst = Assert.IsType<BlockStatement>(ready.ElseStatement).Statements;
        Assert.Contains(CoroutineSyntaxAnalysis.EnumerateBodyExpressions(pendingFirst)
            .OfType<InvocationExpression>(), invocation =>
                invocation.Target is MemberAccessExpression
                {
                    MemberName: "AsTask", Target: IdentifierExpression target,
                } && target.Identifier.ToString() == sourceName);
        var baselineAlias = Assert.Single(CoroutineSyntaxAnalysis
            .EnumerateBodyStatements(pendingFirst)
            .OfType<LocalVariableDeclarationStatement>()
            .SelectMany(declaration => declaration.Declarators)
            .ToArray(), declarator => declarator.Name.StartsWith(
                "__cx_async_callback_prefix_baseline", StringComparison.Ordinal));
        IEnumerable<LambdaExpression> CallbackLambdas(
            IEnumerable<StatementBase> statements)
        {
            foreach (var lambda in CoroutineSyntaxAnalysis
                .EnumerateBodyExpressions(statements).OfType<LambdaExpression>())
            {
                yield return lambda;
                if (lambda.StatementBody is { } nestedStatements)
                {
                    foreach (var nested in CallbackLambdas(nestedStatements))
                    {
                        yield return nested;
                    }
                }
            }
        }
        var callbackExpressions = CallbackLambdas(pendingFirst)
            .Where(lambda => lambda.StatementBody is not null)
            .SelectMany(lambda => CoroutineSyntaxAnalysis
                .EnumerateBodyExpressions(lambda.StatementBody!));
        Assert.Contains(callbackExpressions.OfType<IdentifierExpression>(), identifier =>
            identifier.Identifier.ToString() == baselineAlias.Name);
        Assert.Equal(1, CoroutineSyntaxAnalysis.EnumerateBodyExpressions(combine.Body!)
            .OfType<InvocationExpression>().Count(invocation =>
                invocation.TargetSymbol?.Declaration?.FullName.ToString() ==
                    "Math.TaskSources.ForwardValueTask"));
    }

    [Fact]
    public void AsyncTaskTwoAwaitImportedValueTaskCallSourcePreservesTwoTypedPrefixes()
    {
        var project = CxProject.CreateDefaultApplicationProject();
        project.AddReferencedCompilationContext(CompilerTestHelper.Parse("""
            import System.Threading.Tasks;
            namespace Math;
            public static class TaskSources {
                public static ValueTask<int> ForwardValueTask(ValueTask<int> source) {
                    return source;
                }
            }
            """), "math");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            import Math;
            import System.Threading.Tasks;
            namespace App;
            async Task<int> Combine(ValueTask<int> first, Task<int> second) {
                int baseline = 1;
                string marker = "prefix";
                var left = await Math.TaskSources.ForwardValueTask(first);
                var right = await second;
                return baseline + (int)marker.Length + left + right;
            }
            """));

        new SemanticBinder().Bind(project);

        var combine = project.CompilationContexts
            .Where(context => !context.IsProjectReference)
            .SelectMany(context => context.DeclarationScope.Declarations)
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Combine");
        var guarded = Assert.IsType<TryStatement>(Assert.Single(combine.Body!));
        var outer = Assert.IsType<BlockStatement>(guarded.Body).Statements;
        Assert.Equal(2, outer.Take(2)
            .OfType<LocalVariableDeclarationStatement>().Count());
        var optimized = Assert.IsType<BlockStatement>(outer[2]);
        var readiness = Assert.IsType<IfStatement>(optimized.Statements[1]);
        var pending = Assert.IsType<BlockStatement>(readiness.ElseStatement).Statements;
        var aliases = CoroutineSyntaxAnalysis.EnumerateBodyStatements(pending)
            .OfType<LocalVariableDeclarationStatement>()
            .SelectMany(declaration => declaration.Declarators)
            .Where(declarator => declarator.Name.StartsWith(
                "__cx_async_callback_prefix_", StringComparison.Ordinal))
            .Select(declarator => declarator.Name)
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains(aliases, name => name.Contains("baseline", StringComparison.Ordinal));
        Assert.Contains(aliases, name => name.Contains("marker", StringComparison.Ordinal));
    }

    [Fact]
    public void AsyncTaskTwoAwaitImportedValueTaskCallSourcePreservesThreeTypedPrefixes()
    {
        var project = CxProject.CreateDefaultApplicationProject();
        project.AddReferencedCompilationContext(CompilerTestHelper.Parse("""
            import System.Threading.Tasks;
            namespace Math;
            public static class TaskSources {
                public static ValueTask<int> ForwardValueTask(ValueTask<int> source) {
                    return source;
                }
            }
            """), "math");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            import Math;
            import System.Threading.Tasks;
            namespace App;
            async Task<int> Combine(ValueTask<int> first, Task<int> second) {
                int baseline = 1;
                string marker = "prefix";
                int adjustment = 3;
                var left = await Math.TaskSources.ForwardValueTask(first);
                var right = await second;
                return baseline + (int)marker.Length + adjustment + left + right;
            }
            """));

        new SemanticBinder().Bind(project);

        var combine = project.CompilationContexts
            .Where(context => !context.IsProjectReference)
            .SelectMany(context => context.DeclarationScope.Declarations)
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Combine");
        var guarded = Assert.IsType<TryStatement>(Assert.Single(combine.Body!));
        var outer = Assert.IsType<BlockStatement>(guarded.Body).Statements;
        Assert.Equal(3, outer.Take(3)
            .OfType<LocalVariableDeclarationStatement>().Count());
        var optimized = Assert.IsType<BlockStatement>(outer[3]);
        var ready = Assert.IsType<IfStatement>(optimized.Statements[1]);
        var pending = Assert.IsType<BlockStatement>(ready.ElseStatement).Statements;
        var aliases = CoroutineSyntaxAnalysis.EnumerateBodyStatements(pending)
            .OfType<LocalVariableDeclarationStatement>()
            .SelectMany(declaration => declaration.Declarators)
            .Where(declarator => declarator.Name.StartsWith(
                "__cx_async_callback_prefix_", StringComparison.Ordinal))
            .Select(declarator => declarator.Name)
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains(aliases, name => name.Contains("baseline", StringComparison.Ordinal));
        Assert.Contains(aliases, name => name.Contains("marker", StringComparison.Ordinal));
        Assert.Contains(aliases, name => name.Contains("adjustment", StringComparison.Ordinal));
    }

    [Fact]
    public void AsyncTaskTwoAwaitImportedValueTaskCallSourceExecutesConditionalPrefix()
    {
        var project = CxProject.CreateDefaultApplicationProject();
        project.AddReferencedCompilationContext(CompilerTestHelper.Parse("""
            import System.Threading.Tasks;
            namespace Math;
            public static class TaskSources {
                public static ValueTask<int> ForwardValueTask(ValueTask<int> source) {
                    return source;
                }
            }
            """), "math");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            import Math;
            import System.Threading.Tasks;
            namespace App;
            public static class PrefixProbe {
                public static void Record() { }
            }
            async Task<int> Combine(bool record, ValueTask<int> first, Task<int> second) {
                if (record) {
                    PrefixProbe.Record();
                }
                var left = await Math.TaskSources.ForwardValueTask(first);
                var right = await second;
                return left + right;
            }
            """));

        new SemanticBinder().Bind(project);

        var combine = project.CompilationContexts
            .Where(context => !context.IsProjectReference)
            .SelectMany(context => context.DeclarationScope.Declarations)
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Combine");
        var guarded = Assert.IsType<TryStatement>(Assert.Single(combine.Body!));
        var outer = Assert.IsType<BlockStatement>(guarded.Body).Statements;
        var prefix = Assert.IsType<IfStatement>(outer[0]);
        Assert.IsType<BlockStatement>(prefix.ThenStatement);
        var optimized = Assert.IsType<BlockStatement>(outer[1]);
        var readiness = Assert.IsType<IfStatement>(optimized.Statements[1]);
        Assert.Equal("IsCompleted",
            Assert.IsType<MemberAccessExpression>(readiness.Condition).MemberName);
    }

    [Fact]
    public void AsyncTaskTwoAwaitImportedValueTaskCallSourceUsesBoundInferredPrefixType()
    {
        var project = CxProject.CreateDefaultApplicationProject();
        project.AddReferencedCompilationContext(CompilerTestHelper.Parse("""
            import System.Threading.Tasks;
            namespace Math;
            public static class TaskSources {
                public static ValueTask<int> ForwardValueTask(ValueTask<int> source) {
                    return source;
                }
            }
            """), "math");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            import Math;
            import System.Threading.Tasks;
            namespace App;
            async Task<int> Combine(ValueTask<int> first, Task<int> second) {
                var baseline = 1;
                var left = await Math.TaskSources.ForwardValueTask(first);
                var right = await second;
                return baseline + left + right;
            }
            """));

        new SemanticBinder().Bind(project);

        var combine = project.CompilationContexts
            .Where(context => !context.IsProjectReference)
            .SelectMany(context => context.DeclarationScope.Declarations)
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Combine");
        var guarded = Assert.IsType<TryStatement>(Assert.Single(combine.Body!));
        var outer = Assert.IsType<BlockStatement>(guarded.Body).Statements;
        var prefix = Assert.IsType<LocalVariableDeclarationStatement>(outer[0]);
        Assert.IsType<AutoType>(prefix.DeclaredType);
        Assert.IsType<CxCompiler.Model.Types.BuiltInTypes.IntType>(
            prefix.Declarators.Single().Type);
        var optimized = Assert.IsType<BlockStatement>(outer[1]);
        Assert.IsType<LocalVariableDeclarationStatement>(optimized.Statements[0]);
        var ready = Assert.IsType<IfStatement>(optimized.Statements[1]);
        var pendingFirst = Assert.IsType<BlockStatement>(ready.ElseStatement).Statements;
        Assert.Contains(CoroutineSyntaxAnalysis.EnumerateBodyStatements(pendingFirst)
            .OfType<LocalVariableDeclarationStatement>(), declaration =>
                declaration.Declarators.Any(declarator => declarator.Name.StartsWith(
                    "__cx_async_callback_prefix_baseline", StringComparison.Ordinal)));
        var baselineAliasDeclaration = Assert.Single(CoroutineSyntaxAnalysis
            .EnumerateBodyStatements(pendingFirst)
            .OfType<LocalVariableDeclarationStatement>()
            .ToArray(), declaration => declaration.Declarators.Any(declarator =>
                declarator.Name.StartsWith(
                    "__cx_async_callback_prefix_baseline", StringComparison.Ordinal)));
        Assert.IsType<CxCompiler.Model.Types.BuiltInTypes.IntType>(
            Assert.Single(baselineAliasDeclaration.Declarators).Type);
        Assert.Equal(1, CoroutineSyntaxAnalysis.EnumerateBodyExpressions(combine.Body!)
            .OfType<InvocationExpression>().Count(invocation =>
                invocation.TargetSymbol?.Declaration?.FullName.ToString() ==
                    "Math.TaskSources.ForwardValueTask"));
    }

    [Fact]
    public void AsyncTaskTwoAwaitImportedValueTaskCallSourceCapturesReferencePrefix()
    {
        var project = CxProject.CreateDefaultApplicationProject();
        project.AddReferencedCompilationContext(CompilerTestHelper.Parse("""
            import System.Threading.Tasks;
            namespace Math;
            public struct Payload {
                public int Number;
                public string Label;
                public constructor(int number, string label) {
                    Number = number;
                    Label = label;
                }
            }
            public static class TaskSources {
                public static ValueTask<int> ForwardValueTask(ValueTask<int> source) {
                    return source;
                }
            }
            """), "math");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            import Math;
            import System.Threading.Tasks;
            namespace App;
            async Task<int> Combine(ValueTask<int> first, Task<int> second) {
                Math.Payload baseline = new Math.Payload(1, "prefix");
                var left = await Math.TaskSources.ForwardValueTask(first);
                var right = await second;
                return baseline.Number + (int)baseline.Label.Length + left + right;
            }
            """));

        new SemanticBinder().Bind(project);

        var combine = project.CompilationContexts
            .Where(context => !context.IsProjectReference)
            .SelectMany(context => context.DeclarationScope.Declarations)
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Combine");
        var guarded = Assert.IsType<TryStatement>(Assert.Single(combine.Body!));
        var outer = Assert.IsType<BlockStatement>(guarded.Body).Statements;
        var baseline = Assert.IsType<LocalVariableDeclarationStatement>(outer[0]);
        Assert.IsType<NamedType>(baseline.DeclaredType);
        var optimized = Assert.IsType<BlockStatement>(outer[1]);
        var ready = Assert.IsType<IfStatement>(optimized.Statements[1]);
        var pendingFirst = Assert.IsType<BlockStatement>(ready.ElseStatement).Statements;
        var baselineAliasDeclaration = Assert.Single(CoroutineSyntaxAnalysis
            .EnumerateBodyStatements(pendingFirst)
            .OfType<LocalVariableDeclarationStatement>()
            .ToArray(), declaration => declaration.Declarators.Any(declarator =>
                declarator.Name.StartsWith(
                    "__cx_async_callback_prefix_baseline", StringComparison.Ordinal)));
        Assert.Equal(baseline.DeclaredType.ToString(),
            Assert.Single(baselineAliasDeclaration.Declarators).Type?.ToString());
        Assert.Equal("baseline", Assert.IsType<IdentifierExpression>(
            Assert.Single(baselineAliasDeclaration.Declarators).Initializer)
                .Identifier.ToString());
        Assert.Equal(1, CoroutineSyntaxAnalysis.EnumerateBodyExpressions(combine.Body!)
            .OfType<InvocationExpression>().Count(invocation =>
                invocation.TargetSymbol?.Declaration?.FullName.ToString() ==
                    "Math.TaskSources.ForwardValueTask"));
    }

    [Fact]
    public void AsyncTaskSingleImportedValueTaskCallSourceChecksReadinessBeforeRelayState()
    {
        var project = CxProject.CreateDefaultApplicationProject();
        project.AddReferencedCompilationContext(CompilerTestHelper.Parse("""
            import System.Threading.Tasks;
            namespace Math;
            public static class TaskSources {
                public static ValueTask<int> ForwardValueTask(ValueTask<int> source) {
                    return source;
                }
            }
            """), "math");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            import Math;
            import System.Threading.Tasks;
            namespace App;
            async Task<int> Combine(ValueTask<int> source) {
                var result = await Math.TaskSources.ForwardValueTask(source);
                return result;
            }
            """));

        new SemanticBinder().Bind(project);

        var combine = project.CompilationContexts
            .Where(context => !context.IsProjectReference)
            .SelectMany(context => context.DeclarationScope.Declarations)
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Combine");
        var guarded = Assert.IsType<TryStatement>(Assert.Single(combine.Body!));
        var statements = Assert.IsType<BlockStatement>(guarded.Body).Statements;
        Assert.Equal(2, statements.Count);
        var source = Assert.IsType<LocalVariableDeclarationStatement>(statements[0]);
        var sourceName = source.Declarators.Single().Name;
        Assert.StartsWith("__cx_async_value_task_call", sourceName);
        var sourceCall = Assert.IsType<InvocationExpression>(
            source.Declarators.Single().Initializer);
        Assert.Equal("Math.TaskSources.ForwardValueTask",
            sourceCall.TargetSymbol?.Declaration?.FullName.ToString());

        var ready = Assert.IsType<IfStatement>(statements[1]);
        var readyTarget = Assert.IsType<IdentifierExpression>(
            Assert.IsType<MemberAccessExpression>(ready.Condition).Target);
        Assert.Equal(sourceName, readyTarget.Identifier.ToString());
        var inlineStatements = Assert.IsType<BlockStatement>(ready.ThenStatement).Statements;
        Assert.Equal("GetResult", Assert.IsType<MemberAccessExpression>(
            Assert.IsType<InvocationExpression>(
                Assert.IsType<LocalVariableDeclarationStatement>(inlineStatements[0])
                    .Declarators.Single().Initializer).Target).MemberName);
        Assert.IsType<ObjectCreationExpression>(
            Assert.IsType<ReturnStatement>(inlineStatements[^1]).Expression);

        var pending = Assert.IsType<BlockStatement>(ready.ElseStatement).Statements;
        Assert.Contains(pending, statement => statement is LocalVariableDeclarationStatement
        {
            Declarators: [{ Initializer: InvocationExpression
            {
                Target: MemberAccessExpression { MemberName: "AsTask" },
            } }],
        });
        Assert.Contains(pending, statement => statement is LocalVariableDeclarationStatement
        {
            Declarators: [{ Initializer: ObjectCreationExpression }],
        });
        Assert.Equal(1, CoroutineSyntaxAnalysis.EnumerateBodyExpressions(combine.Body!)
            .OfType<InvocationExpression>()
            .Count(invocation => invocation.TargetSymbol?.Declaration?.FullName.ToString() ==
                "Math.TaskSources.ForwardValueTask"));
    }

    [Fact]
    public void AsyncTaskSingleImportedReferencePayloadCallSourceChecksReadiness()
    {
        var project = CxProject.CreateDefaultApplicationProject();
        project.AddReferencedCompilationContext(CompilerTestHelper.Parse("""
            import System.Threading.Tasks;
            namespace Math;
            public struct TaskPayload { public string Label; }
            public static class PayloadSources {
                public static Task<TaskPayload> Forward(Task<TaskPayload> source) {
                    return source;
                }
            }
            """), "math");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            import Math;
            import System.Threading.Tasks;
            namespace App;
            async Task<Math.TaskPayload> Combine(Task<Math.TaskPayload> source) {
                var result = await Math.PayloadSources.Forward(source);
                return result;
            }
            """));

        new SemanticBinder().Bind(project);

        var combine = project.CompilationContexts
            .Where(context => !context.IsProjectReference)
            .SelectMany(context => context.DeclarationScope.Declarations)
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Combine");
        Assert.True(combine.IsPendingAwaitRelay);
        var guarded = Assert.IsType<TryStatement>(Assert.Single(combine.Body!));
        var callbackBody = Assert.IsType<BlockStatement>(guarded.Body).Statements;
        var optimizedScope = Assert.IsType<BlockStatement>(Assert.Single(callbackBody));
        var source = Assert.IsType<LocalVariableDeclarationStatement>(
            optimizedScope.Statements[0]);
        var sourceName = source.Declarators.Single().Name;
        var sourceCall = Assert.IsType<InvocationExpression>(
            source.Declarators.Single().Initializer);
        Assert.Equal("Math.PayloadSources.Forward",
            sourceCall.TargetSymbol?.Declaration?.FullName.ToString());
        var ready = Assert.IsType<IfStatement>(optimizedScope.Statements[1]);
        Assert.Equal("IsCompleted",
            Assert.IsType<MemberAccessExpression>(ready.Condition).MemberName);
        var pending = Assert.IsType<BlockStatement>(ready.ElseStatement).Statements;
        Assert.DoesNotContain(CoroutineSyntaxAnalysis.EnumerateBodyExpressions(pending)
            .OfType<InvocationExpression>(), invocation =>
                invocation.TargetSymbol?.Declaration?.FullName.ToString() ==
                    "Math.PayloadSources.Forward");
        Assert.Contains(CoroutineSyntaxAnalysis.EnumerateBodyStatements(pending),
            statement => statement is LocalVariableDeclarationStatement
            {
                Declarators: [{ Initializer: IdentifierExpression pendingSource }],
            } && pendingSource.Identifier.ToString() == sourceName);
    }

    [Fact]
    public void AsyncTaskSingleImportedLargePayloadCallSourceChecksReadiness()
    {
        var project = CxProject.CreateDefaultApplicationProject();
        project.AddReferencedCompilationContext(CompilerTestHelper.Parse("""
            import System.Threading.Tasks;
            namespace Math;
            public struct LargeTaskPayload {
                public int Number;
                public string Label0;
                public string Label1;
                public string Label2;
                public string Label3;
                public string Label4;
                public string Label5;
                public string Label6;
                public string Label7;
                public string Label8;
                public string Label9;
                public string Label10;
                public string Label11;
            }
            public static class PayloadSources {
                public static Task<LargeTaskPayload> Forward(
                    Task<LargeTaskPayload> source) {
                    return source;
                }
            }
            """), "math");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            import Math;
            import System.Threading.Tasks;
            namespace App;
            async Task<Math.LargeTaskPayload> Combine(
                Task<Math.LargeTaskPayload> source) {
                var result = await Math.PayloadSources.Forward(source);
                return result;
            }
            """));

        new SemanticBinder().Bind(project);

        var combine = project.CompilationContexts
            .Where(context => !context.IsProjectReference)
            .SelectMany(context => context.DeclarationScope.Declarations)
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Combine");
        Assert.True(combine.IsPendingAwaitRelay);
        var guarded = Assert.IsType<TryStatement>(Assert.Single(combine.Body!));
        var callbackBody = Assert.IsType<BlockStatement>(guarded.Body).Statements;
        var optimizedScope = Assert.IsType<BlockStatement>(Assert.Single(callbackBody));
        var source = Assert.IsType<LocalVariableDeclarationStatement>(
            optimizedScope.Statements[0]);
        var sourceName = source.Declarators.Single().Name;
        var sourceCall = Assert.IsType<InvocationExpression>(
            source.Declarators.Single().Initializer);
        Assert.Equal("Math.PayloadSources.Forward",
            sourceCall.TargetSymbol?.Declaration?.FullName.ToString());
        var ready = Assert.IsType<IfStatement>(optimizedScope.Statements[1]);
        Assert.Equal("IsCompleted",
            Assert.IsType<MemberAccessExpression>(ready.Condition).MemberName);
        var pending = Assert.IsType<BlockStatement>(ready.ElseStatement).Statements;
        Assert.DoesNotContain(CoroutineSyntaxAnalysis.EnumerateBodyExpressions(pending)
            .OfType<InvocationExpression>(), invocation =>
                invocation.TargetSymbol?.Declaration?.FullName.ToString() ==
                    "Math.PayloadSources.Forward");
        Assert.Contains(CoroutineSyntaxAnalysis.EnumerateBodyStatements(pending),
            statement => statement is LocalVariableDeclarationStatement
            {
                Declarators: [{ Initializer: IdentifierExpression pendingSource }],
            } && pendingSource.Identifier.ToString() == sourceName);
    }

    [Fact]
    public void AsyncTaskImportedFirstCallSourceDefersThreeAwaitStateUntilPending()
    {
        var project = CxProject.CreateDefaultApplicationProject();
        project.AddReferencedCompilationContext(CompilerTestHelper.Parse("""
            import System.Threading.Tasks;
            namespace Math;
            public static class TaskSources {
                public static Task<int> Forward(Task<int> source) { return source; }
            }
            """), "math");
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            import Math;
            import System.Threading.Tasks;
            namespace App;
            async Task<int> Combine(
                Task<int> first, Task<int> second, Task<int> third) {
                var left = await Math.TaskSources.Forward(first);
                Task<int> selected = Math.TaskSources.Forward(second);
                var middle = await selected;
                Task<int> finalSelected = Math.TaskSources.Forward(third);
                var right = await finalSelected;
                return left + middle + right;
            }
            """));

        new SemanticBinder().Bind(project);

        var combine = project.CompilationContexts
            .Where(context => !context.IsProjectReference)
            .SelectMany(context => context.DeclarationScope.Declarations)
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Combine");
        Assert.True(combine.IsPendingAwaitRelay);
        var guarded = Assert.IsType<TryStatement>(Assert.Single(combine.Body!));
        var callbackBody = Assert.IsType<BlockStatement>(guarded.Body).Statements;
        var optimizedScope = Assert.IsType<BlockStatement>(Assert.Single(callbackBody));
        var optimizedStatements = optimizedScope.Statements;
        var source = Assert.IsType<LocalVariableDeclarationStatement>(
            optimizedStatements[0]);
        var firstCall = Assert.IsType<InvocationExpression>(
            source.Declarators.Single().Initializer);
        Assert.Equal("Math.TaskSources.Forward",
            firstCall.TargetSymbol?.Declaration?.FullName.ToString());
        Assert.Single(CoroutineSyntaxAnalysis.EnumerateBodyExpressions(combine.Body!)
            .OfType<InvocationExpression>(), invocation =>
                invocation.TargetSymbol?.Declaration?.FullName.ToString() ==
                    "Math.TaskSources.Forward" &&
                invocation.Arguments.OfType<IdentifierExpression>()
                    .Any(identifier => identifier.Identifier.ToString() == "first"));
        var firstReady = Assert.IsType<IfStatement>(optimizedStatements[1]);
        var readySource = Assert.IsType<MemberAccessExpression>(firstReady.Condition);
        Assert.Equal(source.Declarators.Single().Name,
            Assert.IsType<IdentifierExpression>(readySource.Target)
                .Identifier.ToString());
        var pending = Assert.IsType<BlockStatement>(firstReady.ElseStatement).Statements;
        Assert.DoesNotContain(CoroutineSyntaxAnalysis.EnumerateBodyExpressions(pending)
            .OfType<InvocationExpression>(), invocation =>
                invocation.TargetSymbol?.Declaration?.FullName.ToString() ==
                    "Math.TaskSources.Forward" &&
                invocation.Arguments.OfType<IdentifierExpression>()
                    .Any(identifier => identifier.Identifier.ToString() == "first"));
    }

    [Fact]
    public void AsyncTaskImportedGenericCallSourceKeepsFallbackAndBindsSpecialization()
    {
        var project = CxProject.CreateDefaultApplicationProject();
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            import System.Threading.Tasks;
            namespace Math;
            public static class TaskSources {
                public static Task<T> Forward<T>(Task<T> source) { return source; }
            }
            """));
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            import Math;
            import System.Threading.Tasks;
            namespace App;
            async Task<int> Combine(Task<int> first, Task<int> second) {
                var left = await first;
                Task<int> selected = Math.TaskSources.Forward(second);
                var right = await selected;
                return left + right;
            }
            """));

        new SemanticBinder().Bind(project);

        var combine = project.CompilationContexts
            .SelectMany(context => context.DeclarationScope.Declarations)
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Combine");
        Assert.True(combine.IsPendingAwaitRelay);
        var callbacks = CoroutineSyntaxAnalysis
            .EnumerateBodyExpressions(combine.Body!)
            .OfType<LambdaExpression>();
        var call = Assert.Single(callbacks.SelectMany(callback =>
                CoroutineSyntaxAnalysis.EnumerateBodyExpressions(
                    callback.StatementBody ?? []))
            .OfType<InvocationExpression>(), invocation =>
                invocation.Target is MemberAccessExpression
                {
                    MemberName: "Forward",
                });
        var genericTarget = Assert.IsType<CxCompiler.Model.Types.FunctionDeclaration>(
            call.TargetSymbol?.Declaration);
        Assert.Equal("Math.TaskSources.Forward", genericTarget.FullName.ToString());
        Assert.Equal(["T"], genericTarget.GenericTypeNames);
        Assert.Equal("System.Int", call.TargetSymbol?.GenericTypeArguments["T"]
            .FullName.ToString());
        Assert.Equal("System.Int",
            Assert.IsType<NamedType>(call.TargetSymbol?.ReturnType)
                .TypeArguments.Single().FullName.ToString());

        var outerTry = Assert.IsType<TryStatement>(Assert.Single(combine.Body!));
        var loweredBody = Assert.IsType<BlockStatement>(outerTry.Body).Statements;
        var completionSourceIndex = loweredBody.ToList().FindIndex(statement =>
            CoroutineSyntaxAnalysis.EnumerateBodyExpressions([statement])
                .OfType<ObjectCreationExpression>()
                .Any(creation => creation.RequestedType.FullName.ToString().Contains(
                    "TaskCompletionSource", StringComparison.Ordinal)));
        var completionCheckIndex = loweredBody.ToList().FindIndex(statement =>
            statement is IfStatement
            {
                Condition: MemberAccessExpression { MemberName: "IsCompleted" },
            });
        Assert.True(completionSourceIndex >= 0);
        Assert.True(completionCheckIndex > completionSourceIndex);
    }

    [Fact]
    public void AsyncTaskImportedExplicitGenericCallSourceKeepsFallbackAndBindsTypeArgument()
    {
        var project = CxProject.CreateDefaultApplicationProject();
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            import System.Threading.Tasks;
            namespace Math;
            public static class TaskSources {
                public static Task<T> Forward<T>(Task<T> source) { return source; }
            }
            """));
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            import Math;
            import System.Threading.Tasks;
            namespace App;
            async Task<int> Combine(Task<int> first, Task<int> second) {
                var left = await first;
                Task<int> selected = Math.TaskSources.Forward<int>(second);
                var right = await selected;
                return left + right;
            }
            """));

        new SemanticBinder().Bind(project);

        var combine = project.CompilationContexts
            .SelectMany(context => context.DeclarationScope.Declarations)
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Combine");
        Assert.True(combine.IsPendingAwaitRelay);
        var callbacks = CoroutineSyntaxAnalysis
            .EnumerateBodyExpressions(combine.Body!)
            .OfType<LambdaExpression>();
        var call = Assert.Single(callbacks.SelectMany(callback =>
                CoroutineSyntaxAnalysis.EnumerateBodyExpressions(
                    callback.StatementBody ?? []))
            .OfType<InvocationExpression>(), invocation =>
                invocation.Target is MemberAccessExpression
                {
                    MemberName: "Forward",
                });
        Assert.Equal("System.Int", Assert.Single(call.ExplicitTypeArguments)
            .FullName.ToString());
        Assert.Equal("System.Int", call.TargetSymbol?.GenericTypeArguments["T"]
            .FullName.ToString());
        Assert.Equal("System.Int",
            Assert.IsType<NamedType>(call.TargetSymbol?.ReturnType)
                .TypeArguments.Single().FullName.ToString());

        var outerTry = Assert.IsType<TryStatement>(Assert.Single(combine.Body!));
        var loweredBody = Assert.IsType<BlockStatement>(outerTry.Body).Statements;
        var completionSourceIndex = loweredBody.ToList().FindIndex(statement =>
            CoroutineSyntaxAnalysis.EnumerateBodyExpressions([statement])
                .OfType<ObjectCreationExpression>()
                .Any(creation => creation.RequestedType.FullName.ToString().Contains(
                    "TaskCompletionSource", StringComparison.Ordinal)));
        var completionCheckIndex = loweredBody.ToList().FindIndex(statement =>
            statement is IfStatement
            {
                Condition: MemberAccessExpression { MemberName: "IsCompleted" },
            });
        Assert.True(completionSourceIndex >= 0);
        Assert.True(completionCheckIndex > completionSourceIndex);
    }

    [Fact]
    public void AsyncTaskImportedTopLevelCallSourceKeepsFallbackWhenShadowedByLocalFunction()
    {
        var project = CxProject.CreateDefaultApplicationProject();
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            import System.Threading.Tasks;
            namespace Math;
            public Task<int> Forward(Task<int> source) { return source; }
            """));
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            import Math;
            import System.Threading.Tasks;
            namespace App;
            async Task<int> Combine(Task<int> first, Task<int> second) {
                Task<int> Forward(Task<int> source) { return source; }
                var left = await first;
                Task<int> selected = Forward(second);
                var right = await selected;
                return left + right;
            }
            """));

        new SemanticBinder().Bind(project);

        var combine = project.CompilationContexts
            .SelectMany(context => context.DeclarationScope.Declarations)
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Combine");
        Assert.True(combine.IsPendingAwaitRelay);
        var localForward = Assert.Single(CoroutineSyntaxAnalysis
            .EnumerateStatements(combine.Body!)
            .OfType<LocalFunctionDeclarationStatement>());
        var callbacks = CoroutineSyntaxAnalysis
            .EnumerateBodyExpressions(combine.Body!)
            .OfType<LambdaExpression>();
        var call = Assert.Single(callbacks.SelectMany(callback =>
                CoroutineSyntaxAnalysis.EnumerateBodyExpressions(
                    callback.StatementBody ?? []))
            .OfType<InvocationExpression>(), invocation =>
                invocation.Target is IdentifierExpression target &&
                target.Identifier.ToString() == "Forward");
        Assert.Same(localForward.Function, call.TargetSymbol?.Declaration);
        Assert.NotSame(
            project.CompilationContexts[0].DeclarationScope.Declarations
                .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
                .Single(function => function.Name == "Forward"),
            call.TargetSymbol?.Declaration);

        var outerTry = Assert.IsType<TryStatement>(Assert.Single(combine.Body!));
        var loweredBody = Assert.IsType<BlockStatement>(outerTry.Body).Statements;
        var completionSourceIndex = loweredBody.ToList().FindIndex(statement =>
            CoroutineSyntaxAnalysis.EnumerateBodyExpressions([statement])
                .OfType<ObjectCreationExpression>()
                .Any(creation => creation.RequestedType.FullName.ToString().Contains(
                    "TaskCompletionSource", StringComparison.Ordinal)));
        var completionCheckIndex = loweredBody.ToList().FindIndex(statement =>
            statement is IfStatement
            {
                Condition: MemberAccessExpression { MemberName: "IsCompleted" },
            });
        Assert.True(completionSourceIndex >= 0);
        Assert.True(completionCheckIndex > completionSourceIndex);
    }

    [Fact]
    public void AsyncTaskImportedGenericClassCallSourceKeepsFallbackAndBindsClosedOwner()
    {
        var project = CxProject.CreateDefaultApplicationProject();
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            import System.Threading.Tasks;
            namespace Math;
            public class TaskSources<T> {
                public static Task<T> Forward(Task<T> source) { return source; }
            }
            """));
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            import Math;
            import System.Threading.Tasks;
            namespace App;
            async Task<int> Combine(Task<int> first, Task<int> second) {
                var left = await first;
                Task<int> selected = Math.TaskSources<int>.Forward(second);
                var right = await selected;
                return left + right;
            }
            """));

        new SemanticBinder().Bind(project);

        var combine = project.CompilationContexts
            .SelectMany(context => context.DeclarationScope.Declarations)
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Combine");
        Assert.True(combine.IsPendingAwaitRelay);
        var callbacks = CoroutineSyntaxAnalysis
            .EnumerateBodyExpressions(combine.Body!)
            .OfType<LambdaExpression>();
        var call = Assert.Single(callbacks.SelectMany(callback =>
                CoroutineSyntaxAnalysis.EnumerateBodyExpressions(
                    callback.StatementBody ?? []))
            .OfType<InvocationExpression>(), invocation =>
                invocation.Target is MemberAccessExpression
                {
                    MemberName: "Forward",
                });
        var declaration = Assert.IsType<CxCompiler.Model.Types.FunctionDeclaration>(
            call.TargetSymbol?.Declaration);
        Assert.Equal("Math.TaskSources.Forward", declaration.FullName.ToString());
        Assert.Equal(1, declaration.ParentClassDeclaration?.GenericTypeNames.Length);
        Assert.Equal("unnamed.Math.TaskSources", call.TargetSymbol?.ClosedContainingType?
            .ResolvedTypeFullName.ToString());
        Assert.Equal("System.Int",
            Assert.IsType<NamedType>(call.TargetSymbol?.ReturnType)
                .TypeArguments.Single().FullName.ToString());

        var outerTry = Assert.IsType<TryStatement>(Assert.Single(combine.Body!));
        var loweredBody = Assert.IsType<BlockStatement>(outerTry.Body).Statements;
        var completionSourceIndex = loweredBody.ToList().FindIndex(statement =>
            CoroutineSyntaxAnalysis.EnumerateBodyExpressions([statement])
                .OfType<ObjectCreationExpression>()
                .Any(creation => creation.RequestedType.FullName.ToString().Contains(
                    "TaskCompletionSource", StringComparison.Ordinal)));
        var completionCheckIndex = loweredBody.ToList().FindIndex(statement =>
            statement is IfStatement
            {
                Condition: MemberAccessExpression { MemberName: "IsCompleted" },
            });
        Assert.True(completionSourceIndex >= 0);
        Assert.True(completionCheckIndex > completionSourceIndex);
    }

    [Fact]
    public void AsyncTaskImportedInstanceCallSourceKeepsFallbackAndBindsReceiver()
    {
        var project = CxProject.CreateDefaultApplicationProject();
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            import System.Threading.Tasks;
            namespace Math;
            public class TaskSources {
                public Task<int> Forward(Task<int> source) { return source; }
            }
            """));
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            import Math;
            import System.Threading.Tasks;
            namespace App;
            async Task<int> Combine(Math.TaskSources sources,
                    Task<int> first, Task<int> second) {
                var left = await first;
                Task<int> selected = sources.Forward(second);
                var right = await selected;
                return left + right;
            }
            """));

        new SemanticBinder().Bind(project);

        var combine = project.CompilationContexts
            .SelectMany(context => context.DeclarationScope.Declarations)
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Combine");
        Assert.True(combine.IsPendingAwaitRelay);
        var callbacks = CoroutineSyntaxAnalysis
            .EnumerateBodyExpressions(combine.Body!)
            .OfType<LambdaExpression>();
        var call = Assert.Single(callbacks.SelectMany(callback =>
                CoroutineSyntaxAnalysis.EnumerateBodyExpressions(
                    callback.StatementBody ?? []))
            .OfType<InvocationExpression>(), invocation =>
                invocation.Target is MemberAccessExpression
                {
                    MemberName: "Forward",
                });
        var declaration = Assert.IsType<CxCompiler.Model.Types.FunctionDeclaration>(
            call.TargetSymbol?.Declaration);
        Assert.Equal("Math.TaskSources.Forward", declaration.FullName.ToString());
        Assert.False(declaration.IsStatic);
        Assert.NotNull(call.Receiver);
        Assert.Equal("System.Int",
            Assert.IsType<NamedType>(call.TargetSymbol?.ReturnType)
                .TypeArguments.Single().FullName.ToString());

        var outerTry = Assert.IsType<TryStatement>(Assert.Single(combine.Body!));
        var loweredBody = Assert.IsType<BlockStatement>(outerTry.Body).Statements;
        var completionSourceIndex = loweredBody.ToList().FindIndex(statement =>
            CoroutineSyntaxAnalysis.EnumerateBodyExpressions([statement])
                .OfType<ObjectCreationExpression>()
                .Any(creation => creation.RequestedType.FullName.ToString().Contains(
                    "TaskCompletionSource", StringComparison.Ordinal)));
        var completionCheckIndex = loweredBody.ToList().FindIndex(statement =>
            statement is IfStatement
            {
                Condition: MemberAccessExpression { MemberName: "IsCompleted" },
            });
        Assert.True(completionSourceIndex >= 0);
        Assert.True(completionCheckIndex > completionSourceIndex);
    }

    [Fact]
    public void AsyncTaskImportedFunctionValueCallSourceKeepsFallbackAndBindsFunctionType()
    {
        var project = CxProject.CreateDefaultApplicationProject();
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            import System.Threading.Tasks;
            namespace Math;
            public Task<int> Forward(Task<int> source) { return source; }
            """));
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            import Math;
            import System.Threading.Tasks;
            namespace App;
            async Task<int> Combine(Task<int> first, Task<int> second) {
                delegate(Task<int>) => Task<int> producer =
                    task => Math.Forward(task);
                var left = await first;
                Task<int> selected = producer(second);
                var right = await selected;
                return left + right;
            }
            """));

        new SemanticBinder().Bind(project);

        var combine = project.CompilationContexts
            .SelectMany(context => context.DeclarationScope.Declarations)
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Combine");
        Assert.True(combine.IsPendingAwaitRelay);
        var callbacks = CoroutineSyntaxAnalysis
            .EnumerateBodyExpressions(combine.Body!)
            .OfType<LambdaExpression>();
        var call = Assert.Single(callbacks.SelectMany(callback =>
                CoroutineSyntaxAnalysis.EnumerateBodyExpressions(
                    callback.StatementBody ?? []))
            .OfType<InvocationExpression>(), invocation =>
                invocation.Target is IdentifierExpression target &&
                target.Identifier.ToString() == "producer");
        Assert.NotNull(call.FunctionType);
        Assert.Null(call.TargetSymbol);
        Assert.Equal("System.Int",
            Assert.IsType<NamedType>(call.FunctionType?.ReturnType)
                .TypeArguments.Single().FullName.ToString());

        var outerTry = Assert.IsType<TryStatement>(Assert.Single(combine.Body!));
        var loweredBody = Assert.IsType<BlockStatement>(outerTry.Body).Statements;
        var completionSourceIndex = loweredBody.ToList().FindIndex(statement =>
            CoroutineSyntaxAnalysis.EnumerateBodyExpressions([statement])
                .OfType<ObjectCreationExpression>()
                .Any(creation => creation.RequestedType.FullName.ToString().Contains(
                    "TaskCompletionSource", StringComparison.Ordinal)));
        var completionCheckIndex = loweredBody.ToList().FindIndex(statement =>
            statement is IfStatement
            {
                Condition: MemberAccessExpression { MemberName: "IsCompleted" },
            });
        Assert.True(completionSourceIndex >= 0);
        Assert.True(completionCheckIndex > completionSourceIndex);
    }

    [Fact]
    public void AsyncTaskImportedDelegateArrayCallSourceKeepsFallbackAndBindsFunctionType()
    {
        var project = CxProject.CreateDefaultApplicationProject();
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            import System.Threading.Tasks;
            namespace Math;
            public delegate Task<int> TaskSource(Task<int> source);
            public Task<int> Forward(Task<int> source) { return source; }
            """));
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            import Math;
            import System.Threading.Tasks;
            namespace App;
            async Task<int> Combine(Task<int> first, Task<int> second) {
                TaskSource[] producers = new TaskSource[1];
                producers[0] = task => Forward(task);
                var left = await first;
                Task<int> selected = producers[0](second);
                var right = await selected;
                return left + right;
            }
            """));

        new SemanticBinder().Bind(project);

        var combine = project.CompilationContexts
            .SelectMany(context => context.DeclarationScope.Declarations)
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Combine");
        Assert.True(combine.IsPendingAwaitRelay);
        var callbacks = CoroutineSyntaxAnalysis
            .EnumerateBodyExpressions(combine.Body!)
            .OfType<LambdaExpression>();
        var call = Assert.Single(callbacks.SelectMany(callback =>
                CoroutineSyntaxAnalysis.EnumerateBodyExpressions(
                    callback.StatementBody ?? []))
            .OfType<InvocationExpression>(), invocation =>
                invocation.Target is ArrayAccessExpression);
        Assert.NotNull(call.FunctionType);
        Assert.Null(call.TargetSymbol);
        Assert.Equal("System.Int",
            Assert.IsType<NamedType>(call.FunctionType?.ReturnType)
                .TypeArguments.Single().FullName.ToString());

        var outerTry = Assert.IsType<TryStatement>(Assert.Single(combine.Body!));
        var loweredBody = Assert.IsType<BlockStatement>(outerTry.Body).Statements;
        var completionSourceIndex = loweredBody.ToList().FindIndex(statement =>
            CoroutineSyntaxAnalysis.EnumerateBodyExpressions([statement])
                .OfType<ObjectCreationExpression>()
                .Any(creation => creation.RequestedType.FullName.ToString().Contains(
                    "TaskCompletionSource", StringComparison.Ordinal)));
        var completionCheckIndex = loweredBody.ToList().FindIndex(statement =>
            statement is IfStatement
            {
                Condition: MemberAccessExpression { MemberName: "IsCompleted" },
            });
        Assert.True(completionSourceIndex >= 0);
        Assert.True(completionCheckIndex > completionSourceIndex);
    }

    [Fact]
    public void AsyncTaskCannotInvokeImportedFunctionValuedMemberAsCallSource()
    {
        var project = CxProject.CreateDefaultApplicationProject();
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            import System.Threading.Tasks;
            namespace Math;
            public delegate Task<int> TaskSource(Task<int> source);
            public class TaskSourceHolder { public TaskSource Producer; }
            """));
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            import Math;
            import System.Threading.Tasks;
            namespace App;
            async Task<int> Combine(Math.TaskSourceHolder holder,
                    Task<int> first, Task<int> second) {
                var left = await first;
                Task<int> selected = holder.Producer(second);
                var right = await selected;
                return left + right;
            }
            """));

        var error = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(project));
        Assert.Contains("Cannot resolve function 'Producer'", error.Message);
    }

    [Fact]
    public void AsyncTaskCannotInvokeImportedFunctionValuedPropertyAsCallSource()
    {
        var project = CxProject.CreateDefaultApplicationProject();
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            import System.Threading.Tasks;
            namespace Math;
            public delegate Task<int> TaskSource(Task<int> source);
            public class TaskSourceHolder {
                public TaskSource Producer { extern get; }
            }
            """));
        project.AddCompilationContext(CompilerTestHelper.Parse("""
            import Math;
            import System.Threading.Tasks;
            namespace App;
            async Task<int> Combine(Math.TaskSourceHolder holder,
                    Task<int> first, Task<int> second) {
                var left = await first;
                Task<int> selected = holder.Producer(second);
                var right = await selected;
                return left + right;
            }
            """));

        var error = Assert.Throws<CompilationErrorException>(
            () => new SemanticBinder().Bind(project));
        Assert.Contains("Cannot resolve function 'Producer'", error.Message);
    }

    [Fact]
    public void AsyncTaskMultipleExplicitValueTaskLocalsPreserveDeclaredTypes()
    {
        var project = CreateProject("""
            import System.Threading.Tasks;
            namespace App;
            public extern void ForceCollection();
            async Task<int> Relay(ValueTask<int> first, ValueTask<int> second) {
                int baseline = 2;
                int firstResult = await first;
                firstResult++;
                ForceCollection();
                int secondResult = await second;
                return baseline + firstResult + secondResult;
            }
            """);

        new SemanticBinder().Bind(project);

        var relay = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Relay");
        var guarded = Assert.IsType<TryStatement>(Assert.Single(relay.Body!));
        var body = Assert.IsType<BlockStatement>(guarded.Body).Statements;
        var allInline = Assert.IsType<IfStatement>(body[1]);
        var completionCheck = Assert.IsType<BinaryExpression>(allInline.Condition);
        Assert.Equal("&&", completionCheck.Operator);
        var inlineStatements = Assert.IsType<BlockStatement>(allInline.ThenStatement)
            .Statements;
        Assert.Equal("System.Int",
            Assert.IsType<LocalVariableDeclarationStatement>(inlineStatements[0])
                .DeclaredType.FullName.ToString());
        Assert.Equal("System.Int",
            Assert.IsType<LocalVariableDeclarationStatement>(inlineStatements[3])
                .DeclaredType.FullName.ToString());
        Assert.IsType<ObjectCreationExpression>(
            Assert.IsType<ReturnStatement>(inlineStatements[^1]).Expression);

        var pendingStatements = Assert.IsType<BlockStatement>(allInline.ElseStatement)
            .Statements;
        Assert.Contains(pendingStatements, statement =>
            statement is LocalVariableDeclarationStatement
            {
                Declarators: [{ Initializer: ObjectCreationExpression }],
            });
    }

    [Fact]
    public void AsyncTaskSingleValueTaskLocalChecksInlineBeforeRelayAllocation()
    {
        var project = CreateProject("""
            import System.Threading.Tasks;
            namespace App;
            public extern void ForceCollection();
            async Task<int> Relay(ValueTask<int> source) {
                int prefix = 7;
                var result = await source;
                return prefix + result;
            }
            async Task<int> RelayExplicit(ValueTask<int> source) {
                int prefix = 7;
                int result = await source;
                return prefix + result;
            }
            async Task<int> RelayContinuation(ValueTask<int> source) {
                int prefix = 7;
                var result = await source;
                result++;
                ForceCollection();
                return prefix + result;
            }
            public struct Payload {
                public int Number;
                public string Label;
            }
            async Task<Payload> RelayExplicitPayload(ValueTask<Payload> source) {
                Payload result = await source;
                return result;
            }
            """);

        new SemanticBinder().Bind(project);

        var relay = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Relay");
        var guarded = Assert.IsType<TryStatement>(Assert.Single(relay.Body!));
        var loweredStatements = Assert.IsType<BlockStatement>(guarded.Body).Statements;
        Assert.Equal(2, loweredStatements.Count);
        Assert.IsType<LocalVariableDeclarationStatement>(loweredStatements[0]);
        var branch = Assert.IsType<IfStatement>(loweredStatements[1]);
        Assert.Equal("IsCompleted",
            Assert.IsType<MemberAccessExpression>(branch.Condition).MemberName);
        var inlineStatements = Assert.IsType<BlockStatement>(branch.ThenStatement).Statements;
        Assert.Equal("result",
            Assert.Single(Assert.IsType<LocalVariableDeclarationStatement>(
                inlineStatements[0]).Declarators).Name);
        Assert.Equal("GetResult",
            Assert.IsType<MemberAccessExpression>(
                Assert.IsType<InvocationExpression>(
                    Assert.IsType<LocalVariableDeclarationStatement>(
                        inlineStatements[0]).Declarators.Single().Initializer)
                    .Target).MemberName);
        Assert.IsType<ObjectCreationExpression>(
            Assert.IsType<ReturnStatement>(inlineStatements[^1]).Expression);

        var pendingStatements = Assert.IsType<BlockStatement>(branch.ElseStatement)
            .Statements;
        Assert.Contains(pendingStatements, statement =>
            statement is LocalVariableDeclarationStatement
            {
                Declarators: [{ Initializer: ObjectCreationExpression }],
            });
        Assert.Contains(pendingStatements, statement =>
            statement is LocalVariableDeclarationStatement
            {
                Declarators: [{ Initializer: LambdaExpression }],
            });

        var explicitRelay = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "RelayExplicit");
        var explicitGuard = Assert.IsType<TryStatement>(
            Assert.Single(explicitRelay.Body!));
        var explicitBody = Assert.IsType<BlockStatement>(explicitGuard.Body).Statements;
        var explicitBranch = Assert.IsType<IfStatement>(explicitBody[1]);
        var explicitInlineStatements = Assert.IsType<BlockStatement>(
            explicitBranch.ThenStatement).Statements;
        var explicitAwaitedLocal = Assert.IsType<LocalVariableDeclarationStatement>(
            explicitInlineStatements[0]);
        Assert.Equal("System.Int", explicitAwaitedLocal.DeclaredType.FullName.ToString());

        var continuationRelay = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "RelayContinuation");
        var continuationGuard = Assert.IsType<TryStatement>(
            Assert.Single(continuationRelay.Body!));
        var continuationStatements = Assert.IsType<BlockStatement>(
            continuationGuard.Body).Statements;
        var continuationBranch = Assert.IsType<IfStatement>(continuationStatements[1]);
        var continuationInline = Assert.IsType<BlockStatement>(
            continuationBranch.ThenStatement).Statements;
        Assert.IsType<LocalVariableDeclarationStatement>(continuationInline[0]);
        Assert.IsType<UnaryExpression>(Assert.IsType<ExpressionStatement>(
            continuationInline[1]).Expression);
        Assert.Equal("ForceCollection",
            Assert.IsType<IdentifierExpression>(
                Assert.IsType<InvocationExpression>(
                    Assert.IsType<ExpressionStatement>(continuationInline[2])
                        .Expression).Target).Identifier.ToString());
        Assert.IsType<ObjectCreationExpression>(
            Assert.IsType<ReturnStatement>(continuationInline[^1]).Expression);

        var payloadRelay = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "RelayExplicitPayload");
        var payloadGuard = Assert.IsType<TryStatement>(Assert.Single(payloadRelay.Body!));
        var payloadStatements = Assert.IsType<BlockStatement>(payloadGuard.Body).Statements;
        var payloadBranch = Assert.IsType<IfStatement>(payloadStatements[0]);
        Assert.Equal("IsCompleted",
            Assert.IsType<MemberAccessExpression>(payloadBranch.Condition).MemberName);
        var payloadInlineStatements = Assert.IsType<BlockStatement>(
            payloadBranch.ThenStatement).Statements;
        var payloadInlineLocal = Assert.IsType<LocalVariableDeclarationStatement>(
            payloadInlineStatements[0]);
        Assert.Equal("Payload", payloadInlineLocal.DeclaredType.FullName.ToString());
    }

    [Fact]
    public void AsyncTaskSingleValueTaskLocalConditionalReturnsCheckInlineFirst()
    {
        var project = CreateProject("""
            import System.Threading.Tasks;
            namespace App;
            async Task<int> Relay(ValueTask<int> source) {
                int prefix = 5;
                var result = await source;
                if (result > 0) {
                    return prefix + result;
                } else {
                    return prefix - result;
                }
            }
            """);

        new SemanticBinder().Bind(project);

        var relay = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Relay");
        var guarded = Assert.IsType<TryStatement>(Assert.Single(relay.Body!));
        var loweredStatements = Assert.IsType<BlockStatement>(guarded.Body).Statements;
        var outerBranch = Assert.IsType<IfStatement>(loweredStatements[1]);
        Assert.Equal("IsCompleted",
            Assert.IsType<MemberAccessExpression>(outerBranch.Condition).MemberName);

        var inlineStatements = Assert.IsType<BlockStatement>(
            outerBranch.ThenStatement).Statements;
        var conditional = Assert.IsType<IfStatement>(inlineStatements[^1]);
        Assert.Equal(">",
            Assert.IsType<BinaryExpression>(conditional.Condition).Operator);
        foreach (var branch in new[]
                 { conditional.ThenStatement, conditional.ElseStatement! })
        {
            var body = branch is BlockStatement block
                ? block.Statements
                : [branch];
            Assert.IsType<ObjectCreationExpression>(
                Assert.IsType<ReturnStatement>(body[^1]).Expression);
        }

        var pendingStatements = Assert.IsType<BlockStatement>(
            outerBranch.ElseStatement).Statements;
        Assert.Contains(pendingStatements, statement =>
            statement is LocalVariableDeclarationStatement
            {
                Declarators: [{ Initializer: ObjectCreationExpression }],
            });
        Assert.Contains(pendingStatements, statement =>
            statement is LocalVariableDeclarationStatement
            {
                Declarators: [{ Initializer: LambdaExpression }],
            });
    }

    [Fact]
    public void AsyncTaskMultipleValueTaskLocalsCapturePrefixOnlyInPendingBranch()
    {
        var project = CreateProject("""
            import System.Threading.Tasks;
            namespace App;
            async Task<int> Relay(ValueTask<int> first, ValueTask<int> second) {
                int baseline = 2;
                var firstResult = await first;
                var secondResult = await second;
                return baseline + firstResult + secondResult;
            }
            """);

        new SemanticBinder().Bind(project);

        var relay = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Relay");
        var guarded = Assert.IsType<TryStatement>(Assert.Single(relay.Body!));
        var body = Assert.IsType<BlockStatement>(guarded.Body).Statements;
        Assert.IsType<LocalVariableDeclarationStatement>(body[0]);
        var allInline = Assert.IsType<IfStatement>(body[1]);
        Assert.IsType<BinaryExpression>(allInline.Condition);
        var pendingStatements = Assert.IsType<BlockStatement>(
            allInline.ElseStatement).Statements;
        Assert.Contains(pendingStatements.OfType<LocalVariableDeclarationStatement>(),
            declaration => declaration.Declarators.Any(declarator =>
                declarator.Name.StartsWith(
                    "__cx_async_callback_prefix_baseline", StringComparison.Ordinal)));
    }

    [Fact]
    public void AsyncTaskMultipleValueTaskLocalsClonesConditionalMutationAlias()
    {
        var project = CreateProject("""
            import System.Threading.Tasks;
            namespace App;
            async Task<int> Relay(
                ValueTask<int> first, ValueTask<int> second, bool adjust) {
                int baseline = 2;
                var firstResult = await first;
                if (adjust && baseline > 0) {
                    int conditionalValue = firstResult + baseline;
                    firstResult = conditionalValue;
                }
                var secondResult = await second;
                return baseline + firstResult + secondResult;
            }
            """);

        new SemanticBinder().Bind(project);

        var relay = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Relay");
        Assert.True(relay.IsPendingAwaitRelay);
        Assert.DoesNotContain(CoroutineSyntaxAnalysis.EnumerateBodyExpressions(relay.Body!),
            expression => expression is AwaitExpression);
        var callback = Assert.Single(CoroutineSyntaxAnalysis
            .EnumerateBodyExpressions(relay.Body!).OfType<LambdaExpression>());
        var callbackTry = Assert.IsType<TryStatement>(
            Assert.Single(callback.StatementBody!));
        var callbackBody = Assert.IsType<BlockStatement>(callbackTry.Body);
        var conditional = Assert.Single(callbackBody.Statements.OfType<IfStatement>(),
            statement => CoroutineSyntaxAnalysis.EnumerateBodyExpressions([statement])
                .OfType<IdentifierExpression>().Any(identifier =>
                    identifier.Identifier.ToString() ==
                    "__cx_async_callback_result_firstResult"));
        Assert.Contains(CoroutineSyntaxAnalysis.EnumerateBodyExpressions([conditional])
            .OfType<IdentifierExpression>(), identifier =>
            identifier.Identifier.ToString() == "__cx_async_callback_result_firstResult");
        Assert.Contains(CoroutineSyntaxAnalysis.EnumerateBodyExpressions([conditional])
            .OfType<IdentifierExpression>(), identifier =>
            identifier.Identifier.ToString() == "__cx_async_callback_prefix_baseline");
        var clonedConditionalBlock = Assert.IsType<BlockStatement>(
            conditional.ThenStatement);
        var clonedConditionalLocal = Assert.IsType<LocalVariableDeclarationStatement>(
            clonedConditionalBlock.Statements[0]);
        Assert.Equal("conditionalValue", Assert.Single(
            clonedConditionalLocal.Declarators).Name);
    }

    [Fact]
    public void AsyncTaskExplicitLargeValueTaskLocalClonesConditionalMutationAlias()
    {
        var project = CreateProject("""
            import System.Threading.Tasks;
            namespace App;
            public extern void ForceCollection();
            public struct Payload {
                public int Number;
                public string Label;
                public constructor(int number, string label) {
                    Number = number;
                    Label = label;
                }
            }
            async Task<int> Relay(ValueTask<Payload> first, ValueTask<int> second) {
                Payload payload = await first;
                if (payload.Number > 0) {
                    payload.Number += 1;
                    ForceCollection();
                }
                int suffix = await second;
                return payload.Number + suffix;
            }
            """);

        new SemanticBinder().Bind(project);

        var relay = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Relay");
        Assert.True(relay.IsPendingAwaitRelay);
        var callback = Assert.Single(CoroutineSyntaxAnalysis
            .EnumerateBodyExpressions(relay.Body!).OfType<LambdaExpression>());
        var callbackTry = Assert.IsType<TryStatement>(
            Assert.Single(callback.StatementBody!));
        var callbackBody = Assert.IsType<BlockStatement>(callbackTry.Body);
        var alias = "__cx_async_callback_result_payload";
        var conditional = Assert.Single(callbackBody.Statements.OfType<IfStatement>(),
            statement => CoroutineSyntaxAnalysis
                .EnumerateBodyExpressions([statement])
                .OfType<IdentifierExpression>()
                .Any(identifier => identifier.Identifier.ToString() == alias));
        Assert.Contains(CoroutineSyntaxAnalysis.EnumerateBodyExpressions([conditional])
            .OfType<IdentifierExpression>(), identifier =>
            identifier.Identifier.ToString() == alias);
        Assert.Contains(CoroutineSyntaxAnalysis.EnumerateStatements(
                [conditional.ThenStatement]),
            statement => statement is ExpressionStatement expression &&
                expression.Expression is AssignmentExpression assignment &&
                assignment.Target is MemberAccessExpression
                {
                    Target: IdentifierExpression target,
                } &&
                target.Identifier.ToString() == alias);
    }

    [Fact]
    public void AsyncTaskExplicitLargeValueTaskLocalCarriesAliasAcrossThreeAwaits()
    {
        var project = CreateProject("""
            import System.Threading.Tasks;
            namespace App;
            public extern void ForceCollection();
            public struct Payload {
                public int Number;
                public string Label;
            }
            async Task<int> Relay(
                ValueTask<Payload> first,
                ValueTask<int> second,
                ValueTask<int> third) {
                Payload payload = await first;
                if (payload.Number > 0) {
                    payload.Number += 1;
                    ForceCollection();
                }
                int middle = await second;
                ForceCollection();
                int suffix = await third;
                return payload.Number + middle + suffix;
            }
            """);

        new SemanticBinder().Bind(project);

        var relay = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Relay");
        Assert.True(relay.IsPendingAwaitRelay);
        Assert.DoesNotContain(CoroutineSyntaxAnalysis.EnumerateBodyExpressions(relay.Body!),
            expression => expression is AwaitExpression);

        var callbacks = new List<LambdaExpression>();
        void CollectCallbacks(IEnumerable<StatementBase> statements)
        {
            foreach (var callback in CoroutineSyntaxAnalysis
                .EnumerateBodyExpressions(statements).OfType<LambdaExpression>())
            {
                callbacks.Add(callback);
                CollectCallbacks(callback.StatementBody ?? []);
            }
        }
        CollectCallbacks(relay.Body!);
        Assert.Equal(3, callbacks.Count);

        const string payloadAlias = "__cx_async_callback_result_payload";
        var firstCallbackTry = Assert.IsType<TryStatement>(
            Assert.Single(callbacks[0].StatementBody!));
        var firstCallbackBody = Assert.IsType<BlockStatement>(
            firstCallbackTry.Body);
        var conditional = Assert.Single(firstCallbackBody.Statements
            .OfType<IfStatement>(), statement =>
                CoroutineSyntaxAnalysis.EnumerateBodyExpressions([statement])
                    .OfType<IdentifierExpression>()
                    .Any(identifier =>
                        identifier.Identifier.ToString() == payloadAlias));
        Assert.Contains(CoroutineSyntaxAnalysis.EnumerateBodyExpressions([conditional])
            .OfType<IdentifierExpression>(), identifier =>
            identifier.Identifier.ToString() == payloadAlias);

        Assert.Contains(CoroutineSyntaxAnalysis.EnumerateBodyExpressions(
                callbacks[^1].StatementBody!)
            .OfType<IdentifierExpression>(), identifier =>
            identifier.Identifier.ToString() == payloadAlias);
    }

    [Fact]
    public void AsyncTaskMultipleValueTaskLocalsClonesLoopMutationAlias()
    {
        var project = CreateProject("""
            import System.Threading.Tasks;
            namespace App;
            async Task<int> Relay(
                ValueTask<int> first, ValueTask<int> second, bool adjust) {
                var firstResult = await first;
                while (adjust && firstResult < 5) {
                    if (firstResult == 3) {
                        firstResult++;
                        continue;
                    }
                    firstResult++;
                }
                var secondResult = await second;
                return firstResult + secondResult;
            }
            """);

        new SemanticBinder().Bind(project);

        var relay = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Relay");
        Assert.True(relay.IsPendingAwaitRelay);
        var callback = Assert.Single(CoroutineSyntaxAnalysis
            .EnumerateBodyExpressions(relay.Body!).OfType<LambdaExpression>());
        var callbackTry = Assert.IsType<TryStatement>(
            Assert.Single(callback.StatementBody!));
        var callbackBody = Assert.IsType<BlockStatement>(callbackTry.Body);
        var loop = Assert.Single(callbackBody.Statements.OfType<WhileStatement>());
        Assert.Contains(CoroutineSyntaxAnalysis.EnumerateAllExpressions(loop.Condition)
            .OfType<IdentifierExpression>(), identifier =>
            identifier.Identifier.ToString() ==
            "__cx_async_callback_result_firstResult");
        Assert.Contains(CoroutineSyntaxAnalysis.EnumerateBodyExpressions([loop.Body])
            .OfType<IdentifierExpression>(), identifier =>
            identifier.Identifier.ToString() ==
            "__cx_async_callback_result_firstResult");
        Assert.Contains(CoroutineSyntaxAnalysis.EnumerateStatements([loop]),
            statement => statement is ContinueStatement);
    }

    [Fact]
    public void AsyncTaskMultipleValueTaskLocalsClonesDoWhileMutationAlias()
    {
        var project = CreateProject("""
            import System.Threading.Tasks;
            namespace App;
            async Task<int> Relay(
                ValueTask<int> first, ValueTask<int> second, bool adjust) {
                var firstResult = await first;
                do { firstResult++; } while (adjust && firstResult < 5);
                var secondResult = await second;
                return firstResult + secondResult;
            }
            """);

        new SemanticBinder().Bind(project);

        var relay = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Relay");
        Assert.True(relay.IsPendingAwaitRelay);
        var callback = Assert.Single(CoroutineSyntaxAnalysis
            .EnumerateBodyExpressions(relay.Body!).OfType<LambdaExpression>());
        var callbackTry = Assert.IsType<TryStatement>(
            Assert.Single(callback.StatementBody!));
        var callbackBody = Assert.IsType<BlockStatement>(callbackTry.Body);
        var loop = Assert.Single(callbackBody.Statements.OfType<DoWhileStatement>());
        Assert.Contains(CoroutineSyntaxAnalysis.EnumerateAllExpressions(loop.Condition)
            .OfType<IdentifierExpression>(), identifier =>
            identifier.Identifier.ToString() ==
            "__cx_async_callback_result_firstResult");
        Assert.Contains(CoroutineSyntaxAnalysis.EnumerateBodyExpressions([loop.Body])
            .OfType<IdentifierExpression>(), identifier =>
            identifier.Identifier.ToString() ==
            "__cx_async_callback_result_firstResult");
    }

    [Fact]
    public void AsyncTaskMultipleValueTaskLocalsClonesForMutationAlias()
    {
        var project = CreateProject("""
            import System.Threading.Tasks;
            namespace App;
            async Task<int> Relay(ValueTask<int> first, ValueTask<int> second) {
                var firstResult = await first;
                for (firstResult = firstResult; firstResult < 6; firstResult++) {
                    if (firstResult == 4) break;
                }
                var secondResult = await second;
                return firstResult + secondResult;
            }
            """);

        new SemanticBinder().Bind(project);

        var relay = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Relay");
        Assert.True(relay.IsPendingAwaitRelay);
        var callback = Assert.Single(CoroutineSyntaxAnalysis
            .EnumerateBodyExpressions(relay.Body!).OfType<LambdaExpression>());
        var callbackTry = Assert.IsType<TryStatement>(
            Assert.Single(callback.StatementBody!));
        var callbackBody = Assert.IsType<BlockStatement>(callbackTry.Body);
        var loop = Assert.Single(callbackBody.Statements.OfType<ForStatement>());
        Assert.Contains(CoroutineSyntaxAnalysis.EnumerateBodyExpressions([loop])
            .OfType<IdentifierExpression>(), identifier =>
            identifier.Identifier.ToString() ==
            "__cx_async_callback_result_firstResult");
        Assert.Contains(CoroutineSyntaxAnalysis.EnumerateStatements([loop]),
            statement => statement is BreakStatement);
    }

    [Fact]
    public void AsyncTaskMultipleValueTaskLocalsClonesSwitchMutationAlias()
    {
        var project = CreateProject("""
            import System.Threading.Tasks;
            namespace App;
            public extern void ForceCollection();
            async Task<int> Relay(ValueTask<int> first, ValueTask<int> second) {
                var firstResult = await first;
                switch (1) {
                    case 1 when firstResult > 0: ForceCollection(); break;
                    default: break;
                }
                firstResult++;
                var secondResult = await second;
                return firstResult + secondResult;
            }
            """);

        new SemanticBinder().Bind(project);

        var relay = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Relay");
        Assert.True(relay.IsPendingAwaitRelay);
        var callback = Assert.Single(CoroutineSyntaxAnalysis
            .EnumerateBodyExpressions(relay.Body!).OfType<LambdaExpression>());
        var callbackTry = Assert.IsType<TryStatement>(
            Assert.Single(callback.StatementBody!));
        var callbackBody = Assert.IsType<BlockStatement>(callbackTry.Body);
        var clonedSwitch = Assert.IsType<SwitchStatement>(
            Assert.Single(callbackBody.Statements,
                item => item is SwitchStatement));
        Assert.IsType<LiteralExpression>(clonedSwitch.Expression);
        Assert.Contains(clonedSwitch.Sections.SelectMany(section => section.Labels)
            .Where(label => label.Filter is not null)
            .SelectMany(label => CoroutineSyntaxAnalysis
                .EnumerateAllExpressions(label.Filter!))
            .OfType<IdentifierExpression>(), identifier =>
            identifier.Identifier.ToString() ==
            "__cx_async_callback_result_firstResult");
        Assert.Contains(CoroutineSyntaxAnalysis.EnumerateStatements(
                clonedSwitch.Sections.SelectMany(section => section.Statements)),
            statement => statement is BreakStatement);
    }

    [Fact]
    public void AsyncTaskMultipleValueTaskLocalsClonesThrowExceptionAlias()
    {
        var project = CreateProject("""
            import System.Threading.Tasks;
            namespace App;
            async Task<int> Relay(
                ValueTask<int> first, ValueTask<int> second, Exception failure) {
                Exception capturedFailure = failure;
                var firstResult = await first;
                if (firstResult < 0) throw capturedFailure;
                firstResult++;
                var secondResult = await second;
                if (capturedFailure != null) secondResult++;
                return firstResult + secondResult;
            }
            """);

        new SemanticBinder().Bind(project);

        var relay = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Relay");
        Assert.True(relay.IsPendingAwaitRelay);
        var callback = Assert.Single(CoroutineSyntaxAnalysis
            .EnumerateBodyExpressions(relay.Body!).OfType<LambdaExpression>());
        var callbackTry = Assert.IsType<TryStatement>(
            Assert.Single(callback.StatementBody!));
        var callbackBody = Assert.IsType<BlockStatement>(callbackTry.Body);
        var conditional = Assert.Single(callbackBody.Statements
            .OfType<IfStatement>(), statement =>
                statement.ThenStatement is ThrowStatement);
        var thrown = Assert.IsType<ThrowStatement>(conditional.ThenStatement);
        Assert.Equal("__cx_async_callback_prefix_capturedFailure",
            Assert.IsType<IdentifierExpression>(thrown.Expression)
                .Identifier.ToString());
    }

    [Fact]
    public void AsyncTaskMultipleValueTaskLocalsClonesTryCatchAndFinallyAliases()
    {
        var project = CreateProject("""
            import System.Threading.Tasks;
            namespace App;
            public extern void ForceCollection();
            async Task<int> Relay(ValueTask<int> first, ValueTask<int> second) {
                var firstResult = await first;
                try {
                    if (firstResult < 0) throw new Exception();
                } catch (Exception error) when (firstResult < 0) {
                    firstResult++;
                    ForceCollection();
                } finally {
                    if (firstResult == 0) ForceCollection();
                }
                var secondResult = await second;
                return firstResult + secondResult;
            }
            """);

        new SemanticBinder().Bind(project);

        var relay = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Relay");
        Assert.True(relay.IsPendingAwaitRelay);
        var callback = Assert.Single(CoroutineSyntaxAnalysis
            .EnumerateBodyExpressions(relay.Body!).OfType<LambdaExpression>());
        var callbackTry = Assert.IsType<TryStatement>(
            Assert.Single(callback.StatementBody!));
        var callbackBody = Assert.IsType<BlockStatement>(callbackTry.Body);
        var clonedTry = Assert.Single(callbackBody.Statements
            .OfType<TryStatement>());
        var clonedCatch = Assert.Single(clonedTry.CatchClauses);
        Assert.Contains(CoroutineSyntaxAnalysis.EnumerateAllExpressions(
                clonedCatch.Filter!).OfType<IdentifierExpression>(), identifier =>
            identifier.Identifier.ToString() ==
            "__cx_async_callback_result_firstResult");
        Assert.NotNull(clonedCatch.VariableSymbol);
        Assert.Contains(CoroutineSyntaxAnalysis.EnumerateStatements(
                [clonedCatch.Body, clonedTry.FinallyBody!]),
            statement => statement is ExpressionStatement expression &&
                CoroutineSyntaxAnalysis.EnumerateAllExpressions(expression.Expression)
                    .OfType<IdentifierExpression>()
                    .Any(identifier => identifier.Identifier.ToString() ==
                        "__cx_async_callback_result_firstResult"));
    }

    [Fact]
    public void AsyncTaskThreeValueTaskLocalsCarryAliasesAcrossCallbackBoundaries()
    {
        var project = CreateProject("""
            import System.Threading.Tasks;
            namespace App;
            async Task<int> Relay(
                ValueTask<int> first, ValueTask<int> second,
                ValueTask<int> third) {
                int baseline = 1;
                var firstResult = await first;
                firstResult++;
                var secondResult = await second;
                var thirdResult = await third;
                return baseline + firstResult + secondResult + thirdResult;
            }
            """);

        new SemanticBinder().Bind(project);

        var relay = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Relay");
        Assert.True(relay.IsPendingAwaitRelay);
        Assert.DoesNotContain(CoroutineSyntaxAnalysis.EnumerateBodyExpressions(relay.Body!),
            expression => expression is AwaitExpression);
        var callbacks = new List<LambdaExpression>();
        void CollectCallbacks(IEnumerable<StatementBase> statements)
        {
            foreach (var callback in CoroutineSyntaxAnalysis
                .EnumerateBodyExpressions(statements).OfType<LambdaExpression>())
            {
                callbacks.Add(callback);
                CollectCallbacks(callback.StatementBody ?? []);
            }
        }
        CollectCallbacks(relay.Body!);
        Assert.Equal(3, callbacks.Count);
        Assert.Contains(callbacks.SelectMany(callback =>
                CoroutineSyntaxAnalysis.EnumerateBodyExpressions(callback.StatementBody!))
            .OfType<IdentifierExpression>(), identifier =>
            identifier.Identifier.ToString() ==
                "__cx_async_callback_result_firstResult");
    }

    [Fact]
    public void AsyncTaskAwaitsNonGenericValueTaskThroughItsTaskBacking()
    {
        var project = CreateProject("""
            import System.Threading.Tasks;
            namespace App;
            async Task Relay(ValueTask pending) {
                await pending;
            }
            """);

        new SemanticBinder().Bind(project);

        var relay = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Relay");
        var guarded = Assert.IsType<TryStatement>(Assert.Single(relay.Body!));
        var body = Assert.IsType<BlockStatement>(guarded.Body).Statements;
        var inline = Assert.IsType<IfStatement>(body[0]);
        Assert.Equal("IsCompleted",
            Assert.IsType<MemberAccessExpression>(inline.Condition).MemberName);
        var source = Assert.IsType<LocalVariableDeclarationStatement>(body[1]);
        var asTask = Assert.IsType<InvocationExpression>(
            source.Declarators.Single().Initializer);
        Assert.Equal("AsTask",
            Assert.IsType<MemberAccessExpression>(asTask.Target).MemberName);
    }

    [Fact]
    public void AsyncVoidValueTaskLocalAwaitDefersCompletionStateUntilPendingBranch()
    {
        var project = CreateProject("""
            import System.Threading.Tasks;
            namespace App;
            async Task Relay(ValueTask pending) {
                await pending;
                return;
            }
            """);

        new SemanticBinder().Bind(project);

        var relay = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Relay");
        var guarded = Assert.IsType<TryStatement>(Assert.Single(relay.Body!));
        var root = Assert.IsType<IfStatement>(
            Assert.Single(Assert.IsType<BlockStatement>(guarded.Body).Statements));
        Assert.Equal("IsCompleted",
            Assert.IsType<MemberAccessExpression>(root.Condition).MemberName);
        var inlineStatements = Assert.IsType<BlockStatement>(
            root.ThenStatement).Statements;
        Assert.Equal("GetResult", Assert.IsType<MemberAccessExpression>(
            Assert.IsType<InvocationExpression>(
                Assert.IsType<ExpressionStatement>(inlineStatements[0]).Expression)
                .Target).MemberName);
        Assert.IsType<ObjectCreationExpression>(
            Assert.IsType<ReturnStatement>(inlineStatements[1]).Expression);

        var pendingStatements = Assert.IsType<BlockStatement>(
            root.ElseStatement).Statements;
        var asTask = Assert.IsType<InvocationExpression>(
            Assert.IsType<LocalVariableDeclarationStatement>(pendingStatements[0])
                .Declarators.Single().Initializer);
        Assert.Equal("AsTask",
            Assert.IsType<MemberAccessExpression>(asTask.Target).MemberName);
        Assert.IsType<ObjectCreationExpression>(
            Assert.IsType<LocalVariableDeclarationStatement>(pendingStatements[1])
                .Declarators.Single().Initializer);
        Assert.IsType<LambdaExpression>(
            Assert.IsType<LocalVariableDeclarationStatement>(pendingStatements[5])
                .Declarators.Single().Initializer);
    }

    [Fact]
    public void AsyncTaskWithoutResultCompletesAndAwaits()
    {
        var project = CreateProject("""
            import System.Threading.Tasks;
            Task Ready() { return new Task(); }
            async Task Run() {
                await Ready();
            }
            """);

        new SemanticBinder().Bind(project);

        var run = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Run");
        var guarded = Assert.IsType<TryStatement>(Assert.Single(run.Body!));
        var body = ((BlockStatement)guarded.Body).Statements;
        Assert.IsType<LocalVariableDeclarationStatement>(body[0]);
        var completedBranch = Assert.IsType<IfStatement>(body[1]);
        var completedStatements = Assert.IsType<BlockStatement>(
            completedBranch.ThenStatement).Statements;
        var getResult = Assert.IsType<InvocationExpression>(
            Assert.IsType<ExpressionStatement>(completedStatements[0]).Expression);
        Assert.Equal("GetResult",
            Assert.IsType<MemberAccessExpression>(getResult.Target).MemberName);
        var task = Assert.IsType<ObjectCreationExpression>(
            Assert.IsType<ReturnStatement>(completedStatements[1]).Expression);
        Assert.Equal("Task",
            ((CxCompiler.Model.Types.NamedType)task.RequestedType).Name);
        Assert.IsType<LocalVariableDeclarationStatement>(body[2]);
        var forward = Assert.IsType<InvocationExpression>(
            Assert.IsType<ExpressionStatement>(body[3]).Expression);
        Assert.Equal("ForwardVoid",
            Assert.IsType<MemberAccessExpression>(forward.Target).MemberName);
        var pendingReturn = Assert.IsType<CastExpression>(
            Assert.IsType<ReturnStatement>(body[4]).Expression);
        Assert.Equal("GetVoidTask",
            Assert.IsType<MemberAccessExpression>(
                Assert.IsType<InvocationExpression>(pendingReturn.Operand).Target).MemberName);
    }

    [Fact]
    public void AsyncTaskWithoutResultChainsAwaitStatements()
    {
        var project = CreateProject("""
            import System.Threading.Tasks;
            namespace App;
            async Task Run(Task first, Task second) {
                await first;
                await second;
            }
            """);

        new SemanticBinder().Bind(project);

        var run = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Run");
        var guarded = Assert.IsType<TryStatement>(Assert.Single(run.Body!));
        var body = Assert.IsType<BlockStatement>(guarded.Body).Statements;
        var allInline = Assert.IsType<IfStatement>(body[0]);
        Assert.Equal("&&", Assert.IsType<BinaryExpression>(allInline.Condition).Operator);
        var inlineStatements = Assert.IsType<BlockStatement>(allInline.ThenStatement).Statements;
        Assert.Equal(3, inlineStatements.Count);
        Assert.All(inlineStatements.Take(2), statement =>
        {
            var result = Assert.IsType<InvocationExpression>(
                Assert.IsType<ExpressionStatement>(statement).Expression);
            Assert.Equal("GetResult",
                Assert.IsType<MemberAccessExpression>(result.Target).MemberName);
        });
        Assert.IsType<ReturnStatement>(inlineStatements[^1]);
        var pendingStatements = Assert.IsType<BlockStatement>(
            allInline.ElseStatement).Statements;
        var firstCallbackLocal = Assert.Single(pendingStatements
            .OfType<LocalVariableDeclarationStatement>(), declaration =>
            declaration.Declarators.Any(declarator =>
                declarator.Initializer is LambdaExpression));
        var firstCallback = Assert.IsType<LambdaExpression>(
            Assert.Single(firstCallbackLocal.Declarators).Initializer);
        var firstCallbackTry = Assert.IsType<TryStatement>(
            Assert.Single(firstCallback.StatementBody!));
        var callbackStatements = Assert.IsType<BlockStatement>(firstCallbackTry.Body).Statements;
        Assert.IsType<ExpressionStatement>(callbackStatements[0]);
        var secondCallbackLocal = Assert.IsType<LocalVariableDeclarationStatement>(
            callbackStatements[2]);
        var secondCallback = Assert.IsType<LambdaExpression>(
            Assert.Single(secondCallbackLocal.Declarators).Initializer);
        Assert.IsType<IfStatement>(callbackStatements[^1]);
        var secondCallbackTry = Assert.IsType<TryStatement>(
            Assert.Single(secondCallback.StatementBody!));
        var secondCallbackStatements = Assert.IsType<BlockStatement>(secondCallbackTry.Body).Statements;
        var completed = Assert.IsType<InvocationExpression>(
            Assert.IsType<ExpressionStatement>(secondCallbackStatements[^1]).Expression);
        Assert.Equal("SetResult",
            Assert.IsType<MemberAccessExpression>(completed.Target).MemberName);
    }

    [Fact]
    public void AsyncTaskReturnIsWrappedByFaultHandler()
    {
        var project = CreateProject("""
            import System;
            import System.Threading.Tasks;
            async Task Fail() {
                throw new Exception("failure");
            }
            """);

        new SemanticBinder().Bind(project);

        var fail = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Fail");
        var guarded = Assert.IsType<TryStatement>(Assert.Single(fail.Body!));
        var catchClause = Assert.Single(guarded.CatchClauses,
            clause => clause.VariableName == "__cx_async_exception");
        Assert.Equal("__cx_async_exception", catchClause.VariableName);
        var returnedFactory = Assert.IsType<InvocationExpression>(
            Assert.IsType<ReturnStatement>(
                Assert.Single(((BlockStatement)catchClause.Body).Statements)).Expression);
        Assert.Equal("Faulted", ((MemberAccessExpression)returnedFactory.Target).MemberName);
        Assert.Single(returnedFactory.Arguments);
    }

    [Theory]
    [InlineData("Task", "await source;")]
    [InlineData("Task<int>", "var value = await source; return value + 1;")]
    public void AsyncTaskInlineCancellationIsHandledBeforeOrdinaryFaults(
        string taskType, string body)
    {
        var project = CreateProject($$"""
            import System.Threading.Tasks;
            async {{taskType}} Relay({{taskType}} source) { {{body}} }
            """);

        new SemanticBinder().Bind(project);

        var relay = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Relay");
        var guarded = Assert.IsType<TryStatement>(Assert.Single(relay.Body!));
        Assert.Equal(2, guarded.CatchClauses.Count);
        var cancellation = guarded.CatchClauses[0];
        Assert.Equal("__cx_async_canceled_exception", cancellation.VariableName);
        Assert.Contains(CoroutineSyntaxAnalysis.EnumerateBodyExpressions(
            Assert.IsType<BlockStatement>(cancellation.Body).Statements)
            .OfType<InvocationExpression>(), invocation => invocation.Target is
                MemberAccessExpression { MemberName: "SetCanceled" });
        Assert.Equal("__cx_async_exception", guarded.CatchClauses[1].VariableName);
    }

    [Fact]
    public void AsyncGenericTaskReturnPreservesFaultedException()
    {
        var project = CreateProject("""
            import System;
            import System.Threading.Tasks;
            async Task<int> Fail() {
                throw new Exception("failure");
            }
            """);

        new SemanticBinder().Bind(project);

        var fail = project.CompilationContexts.Single().DeclarationScope.Declarations
            .OfType<CxCompiler.Model.Types.FunctionDeclaration>()
            .Single(function => function.Name == "Fail");
        var guarded = Assert.IsType<TryStatement>(Assert.Single(fail.Body!));
        var catchClause = Assert.Single(guarded.CatchClauses,
            clause => clause.VariableName == "__cx_async_exception");
        Assert.Equal("__cx_async_exception", catchClause.VariableName);
        var faultHandler = ((BlockStatement)catchClause.Body).Statements;
        Assert.Equal(3, faultHandler.Count);
        var returnedFault = Assert.IsType<ObjectCreationExpression>(
            Assert.IsType<LocalVariableDeclarationStatement>(faultHandler[0])
                .Declarators[0].Initializer);
        Assert.Equal("Task",
            ((CxCompiler.Model.Types.NamedType)returnedFault.RequestedType).Name);
        Assert.Empty(returnedFault.Arguments);
        var setFault = Assert.IsType<InvocationExpression>(
            Assert.IsType<ExpressionStatement>(faultHandler[1]).Expression);
        Assert.Equal("SetFault", ((MemberAccessExpression)setFault.Target).MemberName);
        Assert.Single(setFault.Arguments);
        var returnedTask = Assert.IsType<IdentifierExpression>(
            Assert.IsType<ReturnStatement>(faultHandler[2]).Expression);
        Assert.Equal("__cx_async_faulted_task",
            returnedTask.Identifier.ToString());
    }

    private static CxProject CreateProject(string source)
    {
        var project = CxProject.CreateDefaultApplicationProject();
        project.AddCompilationContext(CompilerTestHelper.Parse(source));
        return project;
    }
}

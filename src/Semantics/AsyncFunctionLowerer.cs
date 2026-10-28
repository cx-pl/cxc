using CxCompiler.Model.Errors;
using CxCompiler.Model.Expressions;
using CxCompiler.Model.Common;
using CxCompiler.Model.Statements;
using CxCompiler.Model.Types;
using CxCompiler.Model.Types.BuiltInTypes;

namespace CxCompiler.Semantics;

/// <summary>
/// Lowers async Task returns to completed task values or continuation-backed
/// pending tasks for supported await forms, and preserves producer exceptions.
/// </summary>
internal static class AsyncFunctionLowerer
{
    public static void Lower(
        FunctionDeclaration function,
        IReadOnlyList<FunctionDeclaration> availableFunctions,
        IReadOnlyList<QualifiedIdentifier> imports)
    {
        if (!function.IsAsync || function.Body is null)
        {
            return;
        }

        var returnType = function.ReturnType is ConstType constant
            ? constant.UnderlyingType
            : function.ReturnType;
        if (IsAsyncEnumerableType(returnType))
        {
            return;
        }
        if (returnType is NamedType valueTaskReturnType &&
            IsValueTaskType(valueTaskReturnType))
        {
            if (!TryLowerDirectValueTaskReturnAwait(function,
                    valueTaskReturnType))
            {
                throw new CompilationErrorException(
                    "Async ValueTask functions currently support one direct await of a same-shaped ValueTask parameter or initialized local, with an initialized-local-only synchronous prefix.",
                    function.SourceSpan);
            }
            if (function.GenericTypeNames.Length != 0)
            {
                function.MarkPendingAwaitRelay();
            }
            return;
        }
        if (returnType is not NamedType taskType ||
            (!IsTaskType(taskType) && !IsNonGenericTaskType(taskType)))
        {
            var foundType = function.ReturnType is NamedType named
                ? $"{named.ResolvedTypeFullName} with {named.TypeArguments.Count} type argument(s)"
                : function.ReturnType.FullName;
            throw new CompilationErrorException(
                $"Async functions must return System.Threading.Tasks.Task<T> in the initial async task implementation; found '{foundType}'.",
                function.SourceSpan);
        }

        var hasResult = taskType.TypeArguments.Count == 1;
        if (!hasResult && taskType.TypeArguments.Count != 0)
        {
            throw new CompilationErrorException(
                "Async functions must return Task or Task<T>.",
                function.SourceSpan);
        }

        if (TryLowerAwaitForeachTaskConsumer(
                function, taskType, availableFunctions))
        {
            function.MarkPendingAwaitRelay();
            WrapExceptions(function, taskType, hasResult);
            return;
        }

        if (hasResult && HasGenericTypeContext(function) &&
            !IsGenericAwaitLocalChain(function, taskType) &&
            function.Body.Count(statement => statement is
                LocalVariableDeclarationStatement
                {
                    Declarators: [{ Initializer: AwaitExpression }],
                }) > 1)
        {
            throw new CompilationErrorException(
                "Chained pending awaits in generic async functions are not supported yet.",
                function.SourceSpan);
        }

        if (hasResult)
        {
            TryNormalizeNestedAwaitCallArgument(function);
            TryNormalizeNestedShortCircuitAwait(function, taskType);
            TryNormalizeNestedNullCoalescingAwait(function, taskType);
            TryNormalizeNestedAwaitBinaryOperand(function);
            TryNormalizeAwaitSwitch(function);
            TryNormalizeConditionalExpressionAwait(function, taskType);
            if (!TryNormalizeConditionalReturnAwait(function))
            {
                TryNormalizeConditionalAwaitBranchContinuation(function);
            }
        }
        else if (TryNormalizeVoidAwaitSwitch(function))
        {
            // The selected void task is awaited once, then synchronous case
            // continuations resume under the cached selector.
        }
        else if (TryNormalizeConditionalVoidAwait(function))
        {
            // The direct void-await relay preserves lazy branch selection.
        }

        TryNormalizeLoopTransferAwaitedFinally(function, hasResult);

        if (TryLowerAwaitLoopContinuation(
                function, taskType, hasResult, availableFunctions, imports))
        {
            function.MarkPendingAwaitRelay();
            WrapExceptions(function, taskType, hasResult: true);
            return;
        }

        if (hasResult && TryLowerTaskResultReturnAwaitedFinally(function, taskType))
        {
            function.MarkPendingAwaitRelay();
            WrapExceptions(function, taskType, hasResult: true);
            return;
        }

        if (hasResult && TryLowerReturnAwait(
                function, taskType, availableFunctions, imports))
        {
            function.MarkPendingAwaitRelay();
            WrapExceptions(function, taskType, hasResult: true);
            return;
        }
        if (!hasResult && TryLowerVoidAwait(function, taskType))
        {
            function.MarkPendingAwaitRelay();
            WrapExceptions(function, taskType, hasResult: false);
            return;
        }

        RejectAwaitAcrossCatchRegions(function);

        var unsupportedAwaitForeach = EnumerateStatements(function.Body)
            .OfType<ForeachStatement>()
            .FirstOrDefault(statement => statement.IsAwait);
        if (unsupportedAwaitForeach is not null)
        {
            throw new CompilationErrorException(
                "This async Task await foreach shape is not supported yet.",
                unsupportedAwaitForeach.SourceSpan ?? function.SourceSpan);
        }

        if (EnumerateStatements(function.Body)
            .OfType<TryStatement>()
            .Where(statement => statement.FinallyBody is not null)
            .SelectMany(statement => EnumerateStatements([statement.FinallyBody!]))
            .Any(statement => statement switch
            {
                ExpressionStatement { Expression: AwaitExpression } => true,
                LocalVariableDeclarationStatement declaration => declaration.Declarators
                    .Any(declarator => declarator.Initializer is AwaitExpression),
                _ => false,
            }))
        {
            throw new CompilationErrorException(
                "This async Task awaited-finally form is not supported yet.",
                function.SourceSpan);
        }

        var unsupportedLoop = EnumerateStatements(function.Body)
            .FirstOrDefault(statement => statement switch
            {
                WhileStatement loop => CoroutineSyntaxAnalysis
                    .EnumerateBodyExpressions([loop]).Any(expression => expression is AwaitExpression),
                DoWhileStatement loop => CoroutineSyntaxAnalysis
                    .EnumerateBodyExpressions([loop]).Any(expression => expression is AwaitExpression),
                ForStatement loop => CoroutineSyntaxAnalysis
                    .EnumerateBodyExpressions([loop]).Any(expression => expression is AwaitExpression),
                _ => false,
            });
        if (unsupportedLoop is not null)
        {
            throw new CompilationErrorException(
                "This async Task loop-await shape is not supported yet.",
                unsupportedLoop.SourceSpan ?? function.SourceSpan);
        }

        var unsupportedSwitch = EnumerateStatements(function.Body)
            .OfType<SwitchStatement>()
            .FirstOrDefault(statement => statement.Sections
                .Any(section => EnumerateStatements(section.Statements)
                    .Any(CoroutineSyntaxAnalysis.StatementContainsAwait)));
        if (unsupportedSwitch is not null)
        {
            throw new CompilationErrorException(
                "This async Task switch-await shape is not supported yet.",
                unsupportedSwitch.SourceSpan ?? function.SourceSpan);
        }

        var unsupportedNestedAwait = EnumerateStatements(function.Body)
            .SelectMany(CoroutineSyntaxAnalysis.DirectExpressions)
            .Where(expression => expression is not AwaitExpression)
            .SelectMany(expression => CoroutineSyntaxAnalysis.EnumerateAwaits(expression))
            .FirstOrDefault();
        if (unsupportedNestedAwait is not null)
        {
            throw new CompilationErrorException(
                "This async Task nested-await shape is not supported yet.",
                unsupportedNestedAwait.SourceSpan ?? function.SourceSpan);
        }

        foreach (var statement in EnumerateStatements(function.Body))
        {
            if (statement is not ReturnStatement { Expression: { } expression } returnStatement)
            {
                if (statement is ReturnStatement bareReturn)
                {
                    if (hasResult)
                    {
                        throw new CompilationErrorException(
                            "An async Task<T> function must return a value.",
                            statement.SourceSpan ?? function.SourceSpan);
                    }
                    bareReturn.SetExpression(CreateCompletedTask(taskType, null, bareReturn.SourceSpan));
                }
                continue;
            }

            if (!hasResult)
            {
                throw new CompilationErrorException(
                    "An async Task function cannot return a value.",
                    expression.SourceSpan ?? returnStatement.SourceSpan);
            }
            returnStatement.SetExpression(CreateCompletedTask(
                taskType, expression, expression.SourceSpan ?? returnStatement.SourceSpan));
        }

        if (!hasResult)
        {
            function.SetBody(function.Body.Append(
                new ReturnStatement(CreateCompletedTask(taskType, null, function.SourceSpan)))
                .ToArray());
        }

        WrapExceptions(function, taskType, hasResult);
    }

    private static bool TryLowerDirectValueTaskReturnAwait(
        FunctionDeclaration function,
        NamedType valueTaskType)
    {
        if (function.Body is null)
        {
            return false;
        }

        IReadOnlyList<StatementBase> prefixStatements = [];
        StatementBase awaitStatement;
        if (function.Body.Count == 1)
        {
            awaitStatement = function.Body[0];
        }
        else if (function.Body.Count >= 2 &&
            function.Body.Take(function.Body.Count - 1).All(statement =>
                statement is LocalVariableDeclarationStatement
                {
                    Declarators: [{ Initializer: not null }],
                } && !CoroutineSyntaxAnalysis.StatementContainsAwait(statement)))
        {
            var awaitedIdentifier = function.Body[^1] switch
            {
                ReturnStatement
                {
                    Expression: AwaitExpression
                    {
                        Operand: IdentifierExpression identifier,
                    },
                } => identifier,
                ExpressionStatement
                {
                    Expression: AwaitExpression
                    {
                        Operand: IdentifierExpression identifier,
                    },
                } when valueTaskType.TypeArguments.Count == 0 => identifier,
                _ => null,
            };
            var awaitedSourceDeclaration = (LocalVariableDeclarationStatement)
                function.Body[^2];
            if (awaitedIdentifier is null ||
                awaitedSourceDeclaration.Declarators[0].Name !=
                    awaitedIdentifier.Identifier.ToString())
            {
                return false;
            }
            prefixStatements = function.Body.Take(function.Body.Count - 1).ToArray();
            awaitStatement = function.Body[^1];
        }
        else
        {
            return false;
        }

        var awaitExpression = awaitStatement switch
        {
            ReturnStatement { Expression: AwaitExpression awaited } => awaited,
            ExpressionStatement { Expression: AwaitExpression awaited }
                when valueTaskType.TypeArguments.Count == 0 => awaited,
            _ => null,
        };
        if (awaitExpression is null)
        {
            return false;
        }
        var awaitSpan = awaitStatement.SourceSpan;

        TypeBase? awaitedType = null;
        string? sourceLocalName = null;
        if (awaitExpression.Operand is IdentifierExpression operand)
        {
            var parameter = function.Parameters.FirstOrDefault(candidate =>
                candidate.Name == operand.Identifier.ToString());
            awaitedType = parameter?.ParameterType is ConstType constant
                ? constant.UnderlyingType
                : parameter?.ParameterType;
            if (awaitedType is null && prefixStatements.Count > 0 &&
                prefixStatements[^1] is LocalVariableDeclarationStatement localDeclaration)
            {
                var local = localDeclaration.Declarators.Single();
                if (local.Name != operand.Identifier.ToString())
                {
                    return false;
                }
                awaitedType = localDeclaration.DeclaredType;
                sourceLocalName = local.Name;
            }
        }
        awaitedType = awaitedType is ConstType awaitedConstant
            ? awaitedConstant.UnderlyingType
            : awaitedType;
        if (awaitedType is not NamedType awaitedValueTaskType ||
            !(IsValueTaskType(awaitedValueTaskType) ||
              (prefixStatements.Count != 0 &&
               awaitedValueTaskType.FullName.Parts.LastOrDefault() is
                   "ValueTask" or "ValueTaskHandle")) ||
            awaitedValueTaskType.TypeArguments.Count != valueTaskType.TypeArguments.Count)
        {
            return false;
        }

        var hasResult = valueTaskType.TypeArguments.Count == 1;
        if (hasResult && awaitedValueTaskType.TypeArguments[0].FullName !=
            valueTaskType.TypeArguments[0].FullName)
        {
            return false;
        }

        var names = function.Parameters.Select(candidate => candidate.Name)
            .Concat(prefixStatements
                .OfType<LocalVariableDeclarationStatement>()
                .SelectMany(declaration => declaration.Declarators)
                .Select(declarator => declarator.Name))
            .ToHashSet(StringComparer.Ordinal);
        var completionName = CreateUniqueName(names,
            "__cx_async_value_task_completion");
        var cancellationName = CreateUniqueName(names,
            "__cx_async_value_task_cancellation");
        var exceptionName = CreateUniqueName(names,
            "__cx_async_value_task_exception");
        var backingName = CreateUniqueName(names,
            "__cx_async_value_task_backing");
        var completionType = NamedType.CreateWithTypeArguments(
            "System.Threading.Tasks.TaskCompletionSource",
            hasResult ? [valueTaskType.TypeArguments[0]] : []);
        IdentifierExpression Id(string name) =>
            new(new QualifiedIdentifier(name));
        ExpressionBase Source() => sourceLocalName is null
            ? CloneAwaitOperand(awaitExpression.Operand)
            : Id(sourceLocalName);
        InvocationExpression Call(ExpressionBase target, string name,
            params ExpressionBase[] arguments) =>
            new(new MemberAccessExpression(target, name), arguments);
        ObjectCreationExpression ReturnValueTask(params ExpressionBase[] values) =>
            new(valueTaskType, values)
            {
                SourceSpan = awaitSpan,
            };
        StatementBase CreateCaughtReturn(string methodName,
            IReadOnlyList<ExpressionBase> arguments)
        {
            var completion = Id(completionName);
            var statements = new List<StatementBase>
            {
                new LocalVariableDeclarationStatement(completionType,
                    [new LocalVariableDeclarator(completionName,
                        new ObjectCreationExpression(completionType, []))]),
                new ExpressionStatement(Call(completion, methodName,
                    arguments.ToArray())),
            };
            ExpressionBase backingTask = hasResult
                ? Call(Id(completionName), "GetTask")
                : new MemberAccessExpression(Id(completionName), "Task");
            if (hasResult)
            {
                statements.Add(new LocalVariableDeclarationStatement(valueTaskType,
                    [new LocalVariableDeclarator(backingName,
                        new ObjectCreationExpression(valueTaskType, []))]));
                statements.Add(new ExpressionStatement(Call(Id(backingName),
                    "SetTask", backingTask)));
                statements.Add(new ReturnStatement(Id(backingName)));
            }
            else
            {
                statements.Add(new ReturnStatement(ReturnValueTask(backingTask)));
            }
            return new BlockStatement(statements);
        }

        var exceptionType = new NamedType("System.Exception", []);
        var cancellationType = new NamedType(
            "System.Threading.Tasks.OperationCanceledException", []);
        var completedBody = new BlockStatement([
            new ReturnStatement(hasResult
                ? ReturnValueTask(Call(
                    Source(), "GetResult"))
                : ReturnValueTask()),
        ]);
        var completedTry = new TryStatement(completedBody,
        [
            new CatchClause(cancellationType, cancellationName, null,
                CreateCaughtReturn("SetCanceled", [])),
            new CatchClause(exceptionType, exceptionName, null,
                CreateCaughtReturn("SetException", [Id(exceptionName)])),
        ], null)
        {
            SourceSpan = awaitSpan,
        };
        StatementBase pendingReturn;
        var backingTaskExpression = Call(
            Source(), "AsTask");
        if (hasResult)
        {
            pendingReturn = new BlockStatement([
                new LocalVariableDeclarationStatement(valueTaskType,
                    [new LocalVariableDeclarator(backingName,
                        new ObjectCreationExpression(valueTaskType, []))]),
                new ExpressionStatement(Call(Id(backingName), "SetTask",
                    backingTaskExpression)),
                new ReturnStatement(Id(backingName)),
            ]);
        }
        else
        {
            pendingReturn = new ReturnStatement(ReturnValueTask(
                backingTaskExpression))
            {
                SourceSpan = awaitSpan,
            };
        }
        var loweredBody = new List<StatementBase>(prefixStatements);
        loweredBody.Add(
            new IfStatement(
                new MemberAccessExpression(
                    Source(), "IsCompleted"),
                completedTry,
                pendingReturn)
            {
                SourceSpan = awaitSpan,
            });
        function.SetBody(loweredBody);
        return true;
    }

    private static bool TryLowerAwaitForeachTaskConsumer(
        FunctionDeclaration function,
        NamedType taskType,
        IReadOnlyList<FunctionDeclaration> availableFunctions)
    {
        if (function.Body is null || function.GenericTypeNames.Length != 0 ||
            taskType.TypeArguments.Count > 1)
        {
            return false;
        }

        var statements = function.Body.ToArray();
        var loopIndices = statements.Select((statement, index) =>
                (statement, index))
            .Where(item => item.statement is ForeachStatement { IsAwait: true })
            .Select(item => item.index)
            .ToArray();
        if (loopIndices.Length != 1 ||
            statements[loopIndices[0]] is not ForeachStatement { IsAwait: true } loop)
        {
            return false;
        }

        var hasResult = taskType.TypeArguments.Count == 1;
        ExpressionBase? resultExpression = null;
        if (hasResult)
        {
            if (loopIndices[0] != statements.Length - 2 ||
                statements[^1] is not ReturnStatement { Expression: { } expression } ||
                CoroutineSyntaxAnalysis.ContainsAwait(expression) ||
                ContainsReturn(loop.Body))
            {
                return false;
            }
            resultExpression = expression;
        }
        else
        {
            if (loopIndices[0] != statements.Length - 1)
            {
                return false;
            }
        }

        var prefix = statements.Take(loopIndices[0]).ToArray();
        if (prefix.Any(statement => !CanLowerAwaitForeachConsumerBody(statement)) ||
            !CanLowerAwaitForeachConsumerBody(loop.Body))
        {
            return false;
        }

        TypeBase elementType = loop.DeclaredType;
        if (loop.DeclaredType is AutoType)
        {
            if (loop.Collection is IdentifierExpression identifier &&
                function.Parameters.FirstOrDefault(parameter =>
                    parameter.Name == identifier.Identifier.ToString()) is
                    { ParameterType: NamedType { TypeArguments.Count: 1 } collectionType } &&
                IsAsyncEnumerableType(collectionType))
            {
                elementType = collectionType.TypeArguments[0];
            }
            else
            {
                return false;
            }
        }

        var names = function.Parameters.Select(parameter => parameter.Name)
            .Concat(EnumerateStatements(function.Body)
                .OfType<LocalVariableDeclarationStatement>()
                .SelectMany(declaration => declaration.Declarators)
                .Select(declarator => declarator.Name))
            .ToHashSet(StringComparer.Ordinal);
        var enumeratorName = CreateUniqueName(names, "__cx_async_foreach_enumerator");
        var completionName = CreateUniqueName(names, "__cx_async_foreach_completion");
        var stepName = CreateUniqueName(names, "__cx_async_foreach_step");
        var finishName = CreateUniqueName(names, "__cx_async_foreach_finish");
        var runningName = CreateUniqueName(names, "__cx_async_foreach_running");
        var requestedName = CreateUniqueName(names, "__cx_async_foreach_requested");
        var disposingName = CreateUniqueName(names, "__cx_async_foreach_disposing");
        var moveTaskName = CreateUniqueName(names, "__cx_async_foreach_move_task");
        var initialMoveTaskName = CreateUniqueName(names, "__cx_async_foreach_initial_move_task");
        var continuationEnumeratorName = CreateUniqueName(names, "__cx_async_foreach_continuation_enumerator");
        var continuationInitialMoveTaskName = CreateUniqueName(names, "__cx_async_foreach_continuation_initial_move_task");
        var continuationHasInitialMoveName = CreateUniqueName(names, "__cx_async_foreach_continuation_has_initial_move");
        var moveCallbackName = CreateUniqueName(names, "__cx_async_foreach_move_callback");
        var disposeTaskName = CreateUniqueName(names, "__cx_async_foreach_dispose_task");
        var disposeCallbackName = CreateUniqueName(names, "__cx_async_foreach_dispose_callback");
        var exceptionName = CreateUniqueName(names, "__cx_async_foreach_exception");
        var cleanupExceptionName = CreateUniqueName(names, "__cx_async_foreach_cleanup_exception");
        var cancellationName = CreateUniqueName(names, "__cx_async_foreach_cancellation");
        var moveCancellationName = CreateUniqueName(names, "__cx_async_foreach_move_cancellation");
        var moveExceptionName = CreateUniqueName(names, "__cx_async_foreach_move_exception");
        SourceSpan? LambdaSpan(string role) => function.SourceSpan is { } span
            ? span with
            {
                FilePath = $"{span.FilePath}#awaitforeach:{function.FullName}:{role}",
                StartLine = 1,
                StartColumn = 1,
            }
            : null;

        IdentifierExpression Id(string name) =>
            new(new QualifiedIdentifier(name));
        MemberAccessExpression Member(ExpressionBase target, string name) =>
            new(target, name);
        InvocationExpression Call(ExpressionBase target, string name,
            params ExpressionBase[] arguments) =>
            new(Member(target, name), arguments);
        InvocationExpression InvokeLocal(ExpressionBase target,
            params ExpressionBase[] arguments) =>
            new(target, arguments) { SourceSpan = function.SourceSpan };
        ExpressionStatement Invoke(ExpressionBase target, string name,
            params ExpressionBase[] arguments) =>
            new(Call(target, name, arguments));
        IdentifierExpression Enumerator() => Id(enumeratorName);
        IdentifierExpression Completion() => Id(completionName);
        IdentifierExpression Step() => Id(stepName);
        IdentifierExpression Finish() => Id(finishName);
        IdentifierExpression Running() => Id(runningName);
        IdentifierExpression Requested() => Id(requestedName);
        IdentifierExpression Disposing() => Id(disposingName);
        var callbackType = new FunctionType(QualifiedIdentifier.Empty,
            BuiltInSystemTypes.Void, []);
        var exceptionType = new NamedType("System.Exception", []);
        var canceledType = new NamedType(
            "System.Threading.Tasks.OperationCanceledException", []);
        var trueLiteral = new LiteralExpression("true");
        var falseLiteral = new LiteralExpression("false");

        var completionSourceType = NamedType.CreateWithTypeArguments(
            "System.Threading.Tasks.TaskCompletionSource",
            hasResult ? [taskType.TypeArguments[0]] : []);
        var asyncEnumeratorType = NamedType.CreateWithTypeArguments(
            "System.Collections.IAsyncEnumerator", [elementType]);
        var moveTaskReturnType = loop.MoveNextInvocation?.TargetSymbol?.ReturnType ??
            loop.MoveNextInvocation?.InferredType ??
            availableFunctions.Where(candidate =>
                candidate.Name == "MoveNextAsync" &&
                candidate.ParentClassDeclaration is { } owner &&
                owner.Name == "IAsyncEnumerator" &&
                owner.Namespace.ToString() == "System.Collections")
                .Select(candidate => candidate.ReturnType)
                .DistinctBy(type => type.ToString())
                .SingleOrDefault();
        var moveTaskType = moveTaskReturnType switch
        {
            ConstType constant => constant.UnderlyingType as NamedType,
            NamedType named => named,
            _ => null,
        };
        if (moveTaskType is null)
        {
            return false;
        }
        ExpressionBase emptyMoveTask = IsValueTaskType(moveTaskType)
            ? new ObjectCreationExpression(moveTaskType, [])
            : new LiteralExpression("null");
        var callbackLiveNames = CoroutineSyntaxAnalysis
            .EnumerateBodyExpressions([loop.Body])
            .Concat(resultExpression is null ? [] : CoroutineSyntaxAnalysis.EnumerateAllExpressions(resultExpression))
            .OfType<IdentifierExpression>()
            .Select(identifier => identifier.Identifier.ToString())
            .ToHashSet(StringComparer.Ordinal);
        var callbackAliases = new Dictionary<string, (string Alias, TypeBase Type)>(
            StringComparer.Ordinal);
        foreach (var parameter in function.Parameters)
        {
            if (callbackLiveNames.Contains(parameter.Name))
            {
                callbackAliases.TryAdd(parameter.Name,
                    (CreateUniqueName(names, $"__cx_async_saved_{parameter.Name}"), parameter.ParameterType));
            }
        }
        foreach (var declaration in prefix.OfType<LocalVariableDeclarationStatement>())
        {
            foreach (var declarator in declaration.Declarators)
            {
                var type = declarator.Symbol?.Type ?? declarator.Type ?? declaration.DeclaredType;
                if (callbackLiveNames.Contains(declarator.Name) && type is not AutoType)
                {
                    callbackAliases.TryAdd(declarator.Name,
                        (CreateUniqueName(names, $"__cx_async_saved_{declarator.Name}"), type));
                }
            }
        }
        var callbackAliasDeclarations = callbackAliases.Select(pair =>
            (StatementBase)new LocalVariableDeclarationStatement(
                pair.Value.Type,
                [new LocalVariableDeclarator(pair.Value.Alias, Id(pair.Key))])).ToArray();
        if (callbackLiveNames.Any(name =>
                !function.Parameters.Any(parameter => parameter.Name == name) &&
                !callbackAliases.ContainsKey(name) &&
                prefix.OfType<LocalVariableDeclarationStatement>()
                    .SelectMany(declaration => declaration.Declarators)
                    .Any(declarator => declarator.Name == name)))
        {
            return false;
        }
        var callbackSubstitutions = callbackLiveNames.ToDictionary(
            name => name,
            name => callbackAliases.TryGetValue(name, out var alias) ? alias.Alias : name,
            StringComparer.Ordinal);
        bool TryCloneCallbackBody(StatementBase statement, out StatementBase clone) =>
            TryCloneAwaitCallbackStatement(statement, callbackSubstitutions, out clone);
        ExpressionBase? callbackResultExpression = null;
        if (hasResult && !TryCloneValueTaskResultExpression(
                resultExpression!, callbackSubstitutions, out callbackResultExpression))
        {
            return false;
        }
        var getEnumerator = new InvocationExpression(
            Member(loop.Collection, "GetAsyncEnumerator"),
            [new MemberAccessExpression(new IdentifierExpression(new QualifiedIdentifier(
                "System", "Threading", "Tasks", "CancellationToken")), "None")]);

        var disposeSucceeded = new List<StatementBase>
        {
            new IfStatement(
                Id(cancellationName),
                Invoke(Completion(), "SetCanceled"),
                new IfStatement(
                    new BinaryExpression(Id(exceptionName), "!=", new LiteralExpression("null")),
                    Invoke(Completion(), "SetException", Id(exceptionName)),
                    hasResult
                        ? Invoke(Completion(), "SetResult", callbackResultExpression!)
                        : Invoke(Completion(), "SetResult"))),
        };
        var disposeCallback = new LambdaExpression([], null,
        [
            new TryStatement(
                new BlockStatement([
                    Invoke(Id(disposeTaskName), "GetResult"),
                    .. disposeSucceeded,
                ]),
                [
                    new CatchClause(canceledType, cleanupExceptionName, null,
                        new BlockStatement([Invoke(Completion(), "SetCanceled")])),
                    new CatchClause(exceptionType, cleanupExceptionName, null,
                        new BlockStatement([Invoke(Completion(), "SetException", Id(cleanupExceptionName))])),
                ],
                null),
        ])
        {
            SourceSpan = LambdaSpan("dispose-callback"),
        };
        var startDispose = new List<StatementBase>
        {
            new LocalVariableDeclarationStatement(new AutoType(false),
                [new LocalVariableDeclarator(disposeTaskName,
                    Call(Id(continuationEnumeratorName), "DisposeAsync"))]),
            new LocalVariableDeclarationStatement(callbackType,
                [new LocalVariableDeclarator(disposeCallbackName, disposeCallback)]),
            new IfStatement(
                Member(Id(disposeTaskName), "IsCompleted"),
                new ExpressionStatement(InvokeLocal(Id(disposeCallbackName))),
                new ExpressionStatement(new InvocationExpression(
                    Member(Id(disposeTaskName), "OnCompleted"),
                    [Id(disposeCallbackName)]))),
        };
        var finishLambda = new LambdaExpression(
            [new LambdaParameter(exceptionName, exceptionType),
             new LambdaParameter(cancellationName, BuiltInSystemTypes.Bool)],
            null,
            [
                new IfStatement(Disposing(), new ReturnStatement(null), null),
                new ExpressionStatement(new AssignmentExpression(
                    Disposing(), "=", trueLiteral)),
                new TryStatement(
                    new BlockStatement(startDispose),
                    [
                        new CatchClause(canceledType, moveCancellationName, null,
                            new BlockStatement([Invoke(Completion(), "SetCanceled")])),
                        new CatchClause(exceptionType, moveExceptionName, null,
                            new BlockStatement([Invoke(Completion(), "SetException", Id(moveExceptionName))])),
                    ],
                    null),
            ])
        {
            SourceSpan = LambdaSpan("finish"),
        };

        var rewrittenCallbackBody = RewriteAwaitForeachConsumerBody(
            loop.Body, Finish, Step);
        if (!TryCloneCallbackBody(rewrittenCallbackBody, out var callbackBody))
        {
            return false;
        }
        var moveCallback = new LambdaExpression([], null,
        [
            new TryStatement(
                new BlockStatement([
                    new IfStatement(
                        Call(Id(moveTaskName), "GetResult"),
                        new BlockStatement([
                            new LocalVariableDeclarationStatement(
                                loop.DeclaredType is AutoType ? new AutoType(false) : loop.DeclaredType,
                                [new LocalVariableDeclarator(
                                    loop.VariableName,
                                    Member(Id(continuationEnumeratorName), "Current"))]),
                            callbackBody,
                            new ExpressionStatement(InvokeLocal(Step())),
                        ]),
                        new BlockStatement([new ExpressionStatement(InvokeLocal(
                            Finish(), new LiteralExpression("null"), falseLiteral))])),
                ]),
                [
                    new CatchClause(canceledType, moveCancellationName, null,
                        new BlockStatement([new ExpressionStatement(InvokeLocal(
                            Finish(), new LiteralExpression("null"), trueLiteral))])),
                    new CatchClause(exceptionType, moveExceptionName, null,
                        new BlockStatement([new ExpressionStatement(InvokeLocal(
                            Finish(), Id(moveExceptionName), falseLiteral))])),
                ],
                null),
        ])
        {
            SourceSpan = LambdaSpan("move-callback"),
        };

        var stepStatements = new List<StatementBase>
        {
            new IfStatement(
                Running(),
                new BlockStatement([
                    new ExpressionStatement(new AssignmentExpression(
                        Requested(), "=", trueLiteral)),
                    new ReturnStatement(null),
                ]),
                null),
            new ExpressionStatement(new AssignmentExpression(Running(), "=", trueLiteral)),
            new WhileStatement(trueLiteral, new BlockStatement([
                new ExpressionStatement(new AssignmentExpression(Requested(), "=", falseLiteral)),
                new IfStatement(Disposing(), new BlockStatement([
                    new ExpressionStatement(new AssignmentExpression(Running(), "=", falseLiteral)),
                    new ReturnStatement(null),
                ]), null),
                new TryStatement(
                    new BlockStatement([
                        new LocalVariableDeclarationStatement(new AutoType(false),
                            [new LocalVariableDeclarator(moveTaskName,
                                new ConditionalExpression(
                                    Id(continuationHasInitialMoveName),
                                    Id(continuationInitialMoveTaskName),
                                    Call(Id(continuationEnumeratorName), "MoveNextAsync")))]),
                        new IfStatement(Id(continuationHasInitialMoveName),
                            new ExpressionStatement(new AssignmentExpression(
                                Id(continuationHasInitialMoveName), "=", falseLiteral)), null),
                        new LocalVariableDeclarationStatement(callbackType,
                            [new LocalVariableDeclarator(moveCallbackName, moveCallback)]),
                        new IfStatement(
                            Member(Id(moveTaskName), "IsCompleted"),
                            new ExpressionStatement(InvokeLocal(Id(moveCallbackName))),
                            new ExpressionStatement(new InvocationExpression(
                                Member(Id(moveTaskName), "OnCompleted"),
                                [Id(moveCallbackName)]))),
                    ]),
                    [
                        new CatchClause(canceledType, cancellationName, null,
                            new BlockStatement([new ExpressionStatement(InvokeLocal(
                                Finish(), new LiteralExpression("null"), trueLiteral))])),
                        new CatchClause(exceptionType, exceptionName, null,
                            new BlockStatement([new ExpressionStatement(InvokeLocal(
                                Finish(), Id(exceptionName), falseLiteral))])),
                    ],
                    null),
                new IfStatement(
                    new UnaryExpression("!", Requested()),
                    new BlockStatement([
                        new ExpressionStatement(new AssignmentExpression(Running(), "=", falseLiteral)),
                        new ReturnStatement(null),
                    ]),
                    null),
            ])),
        };
        var stepLambda = new LambdaExpression([], null, stepStatements)
        {
            SourceSpan = LambdaSpan("step"),
        };

        var loweredBody = new List<StatementBase>(prefix)
        {
            new LocalVariableDeclarationStatement(asyncEnumeratorType,
                [new LocalVariableDeclarator(enumeratorName, getEnumerator)]),
            new LocalVariableDeclarationStatement(asyncEnumeratorType,
                [new LocalVariableDeclarator(continuationEnumeratorName, Enumerator())]),
            new LocalVariableDeclarationStatement(moveTaskType,
                [new LocalVariableDeclarator(continuationInitialMoveTaskName,
                    emptyMoveTask)]),
            new LocalVariableDeclarationStatement(BuiltInSystemTypes.Bool,
                [new LocalVariableDeclarator(continuationHasInitialMoveName, falseLiteral)]),
            new LocalVariableDeclarationStatement(completionSourceType,
                [new LocalVariableDeclarator(completionName,
                    new ObjectCreationExpression(completionSourceType, []))]),
            new LocalVariableDeclarationStatement(callbackType,
                [new LocalVariableDeclarator(stepName, null)]),
            new LocalVariableDeclarationStatement(BuiltInSystemTypes.Bool,
                [new LocalVariableDeclarator(runningName, falseLiteral)]),
            new LocalVariableDeclarationStatement(BuiltInSystemTypes.Bool,
                [new LocalVariableDeclarator(requestedName, falseLiteral)]),
            new LocalVariableDeclarationStatement(BuiltInSystemTypes.Bool,
                [new LocalVariableDeclarator(disposingName, falseLiteral)]),
            new LocalVariableDeclarationStatement(new FunctionType(
                QualifiedIdentifier.Empty, BuiltInSystemTypes.Void,
                [exceptionType, BuiltInSystemTypes.Bool]),
                [new LocalVariableDeclarator(finishName, finishLambda)]),
            new ExpressionStatement(new AssignmentExpression(Step(), "=", stepLambda)),
            new ExpressionStatement(InvokeLocal(Step())),
            new ReturnStatement(Member(Completion(), "Task")),
        };
        loweredBody.InsertRange(prefix.Length + 4, callbackAliasDeclarations);

        if (CanLowerAwaitForeachInlineBody(loop.Body))
        {
            var inlineBodySubstitutions = CoroutineSyntaxAnalysis
                .EnumerateBodyExpressions([loop.Body])
                .OfType<IdentifierExpression>()
                .Select(identifier => identifier.Identifier.ToString())
                .Distinct(StringComparer.Ordinal)
                .ToDictionary(name => name, name => name, StringComparer.Ordinal);
            if (!TryCloneAwaitCallbackStatement(
                    loop.Body, inlineBodySubstitutions, out var inlineLoopBody))
            {
                function.SetBody(loweredBody);
                return true;
            }
            ExpressionBase? inlineResultExpression = null;
            if (hasResult && !TryCloneValueTaskResultExpression(
                    resultExpression!, inlineBodySubstitutions,
                    out inlineResultExpression))
            {
                function.SetBody(loweredBody);
                return true;
            }
            var inlineFinishedName = CreateUniqueName(names, "__cx_async_foreach_inline_finished");
            var inlineExceptionName = CreateUniqueName(names, "__cx_async_foreach_inline_exception");
            var inlineCancellationName = CreateUniqueName(names, "__cx_async_foreach_inline_cancellation");
            var inlineMoveTaskName = CreateUniqueName(names, "__cx_async_foreach_inline_move_task");
            var inlineDisposeTaskName = CreateUniqueName(names, "__cx_async_foreach_inline_dispose_task");
            var inlineCleanupExceptionName = CreateUniqueName(names, "__cx_async_foreach_inline_cleanup_exception");
            var inlineCleanupCancellationName = CreateUniqueName(names, "__cx_async_foreach_inline_cleanup_cancellation");
            var inlineCaughtExceptionName = CreateUniqueName(names, "__cx_async_foreach_inline_caught_exception");
            var inlineCaughtCancellationName = CreateUniqueName(names, "__cx_async_foreach_inline_caught_cancellation");
            var inlineCompletionName = CreateUniqueName(names, "__cx_async_foreach_inline_completion");
            var inlineDisposeCallbackName = CreateUniqueName(names, "__cx_async_foreach_inline_dispose_callback");
            var inlineCallbackExceptionName = CreateUniqueName(names, "__cx_async_foreach_inline_callback_exception");
            var inlineCallbackCancellationName = CreateUniqueName(names, "__cx_async_foreach_inline_callback_cancellation");
            var inlineCallbackResultName = CreateUniqueName(names, "__cx_async_foreach_inline_callback_result");
            var inlineCallbackDisposeTaskName = CreateUniqueName(names, "__cx_async_foreach_inline_callback_dispose_task");
            IdentifierExpression InlineFinished() => Id(inlineFinishedName);
            IdentifierExpression InlineException() => Id(inlineExceptionName);
            IdentifierExpression InlineCancellation() => Id(inlineCancellationName);
            IdentifierExpression InlineMoveTask() => Id(inlineMoveTaskName);
            IdentifierExpression InlineDisposeTask() => Id(inlineDisposeTaskName);
            IdentifierExpression InlineCleanupException() => Id(inlineCleanupExceptionName);
            IdentifierExpression InlineCleanupCancellation() => Id(inlineCleanupCancellationName);
            IdentifierExpression InlineCompletion() => Id(inlineCompletionName);
            IdentifierExpression InlineDisposeCallback() => Id(inlineDisposeCallbackName);
            IdentifierExpression InlineCallbackException() => Id(inlineCallbackExceptionName);
            IdentifierExpression InlineCallbackCancellation() => Id(inlineCallbackCancellationName);
            IdentifierExpression InlineCallbackResult() => Id(inlineCallbackResultName);
            IdentifierExpression InlineCallbackDisposeTask() => Id(inlineCallbackDisposeTaskName);
            inlineLoopBody = RewriteAwaitForeachInlineBody(
                inlineLoopBody,
                InlineFinished,
                InlineMoveTask,
                Enumerator);

            var inlineExceptionType = new NamedType("System.Exception", []);
            var inlineCanceledType = new NamedType(
                "System.Threading.Tasks.OperationCanceledException", []);
            var inlineIteration = new TryStatement(
                new BlockStatement([
                    new IfStatement(
                        Call(InlineMoveTask(), "GetResult"),
                        new BlockStatement([
                            new LocalVariableDeclarationStatement(
                                loop.DeclaredType is AutoType ? new AutoType(false) : loop.DeclaredType,
                                [new LocalVariableDeclarator(
                                    loop.VariableName,
                                    Member(Enumerator(), "Current"))]),
                            inlineLoopBody,
                            new ExpressionStatement(new AssignmentExpression(
                                InlineMoveTask(), "=", Call(Enumerator(), "MoveNextAsync"))),
                        ]),
                        new BlockStatement([
                            new ExpressionStatement(new AssignmentExpression(
                                InlineFinished(), "=", trueLiteral)),
                        ])),
                ]),
                [
                    new CatchClause(inlineCanceledType, inlineCaughtCancellationName, null,
                        new BlockStatement([
                            new ExpressionStatement(new AssignmentExpression(
                                InlineCancellation(), "=", trueLiteral)),
                            new ExpressionStatement(new AssignmentExpression(
                                InlineFinished(), "=", trueLiteral)),
                        ])),
                    new CatchClause(inlineExceptionType, inlineCaughtExceptionName, null,
                        new BlockStatement([
                            new ExpressionStatement(new AssignmentExpression(
                                InlineException(), "=", Id(inlineCaughtExceptionName))),
                            new ExpressionStatement(new AssignmentExpression(
                                InlineFinished(), "=", trueLiteral)),
                        ])),
                ], null);
            var inlineLoop = new WhileStatement(
                new BinaryExpression(
                    new UnaryExpression("!", InlineFinished()),
                    "&&",
                    Member(InlineMoveTask(), "IsCompleted")),
                new BlockStatement([inlineIteration]));

            var inlineFailureCompletion = (ExpressionBase exception, ExpressionBase canceled) =>
                new BlockStatement([
                    new LocalVariableDeclarationStatement(completionSourceType,
                        [new LocalVariableDeclarator(inlineCompletionName,
                            new ObjectCreationExpression(completionSourceType, []))]),
                    new IfStatement(canceled,
                        Invoke(InlineCompletion(), "SetCanceled"),
                        Invoke(InlineCompletion(), "SetException", exception)),
                    new ReturnStatement(Member(InlineCompletion(), "Task")),
                ]);

            var inlineDisposeCallback = new LambdaExpression([], null,
            [
                new TryStatement(
                    new BlockStatement([
                        Invoke(InlineCallbackDisposeTask(), "GetResult"),
                        new IfStatement(
                            InlineCallbackCancellation(),
                            Invoke(InlineCompletion(), "SetCanceled"),
                            new IfStatement(
                                new BinaryExpression(InlineCallbackException(), "!=", new LiteralExpression("null")),
                                Invoke(InlineCompletion(), "SetException", InlineCallbackException()),
                                hasResult
                                    ? Invoke(InlineCompletion(), "SetResult", InlineCallbackResult())
                                    : Invoke(InlineCompletion(), "SetResult"))),
                    ]),
                    [
                        new CatchClause(inlineCanceledType, inlineCaughtCancellationName, null,
                            new BlockStatement([Invoke(InlineCompletion(), "SetCanceled")])),
                        new CatchClause(inlineExceptionType, inlineCaughtExceptionName, null,
                            new BlockStatement([Invoke(InlineCompletion(), "SetException", Id(inlineCaughtExceptionName))])),
                    ], null),
            ])
            {
                SourceSpan = LambdaSpan("inline-dispose-callback"),
            };

            var inlineCompletedDispose = new BlockStatement([
                new TryStatement(
                    new BlockStatement([Invoke(InlineDisposeTask(), "GetResult")]),
                    [
                        new CatchClause(inlineCanceledType, inlineCaughtCancellationName, null,
                            new BlockStatement([new ExpressionStatement(new AssignmentExpression(
                                InlineCleanupCancellation(), "=", trueLiteral))])),
                        new CatchClause(inlineExceptionType, inlineCaughtExceptionName, null,
                            new BlockStatement([new ExpressionStatement(new AssignmentExpression(
                                InlineCleanupException(), "=", Id(inlineCaughtExceptionName)))])),
                    ], null),
                new IfStatement(
                    new BinaryExpression(InlineCleanupCancellation(), "||", InlineCancellation()),
                    inlineFailureCompletion(new LiteralExpression("null"), trueLiteral),
                    new IfStatement(
                        new BinaryExpression(InlineCleanupException(), "!=", new LiteralExpression("null")),
                        inlineFailureCompletion(InlineCleanupException(), falseLiteral),
                        new IfStatement(
                            new BinaryExpression(InlineException(), "!=", new LiteralExpression("null")),
                            inlineFailureCompletion(InlineException(), falseLiteral),
                            new ReturnStatement(new ObjectCreationExpression(taskType,
                                hasResult ? [inlineResultExpression!] : []))))),
            ]);
            var inlinePendingDisposeStatements = new List<StatementBase>
            {
                new LocalVariableDeclarationStatement(completionSourceType,
                    [new LocalVariableDeclarator(inlineCompletionName,
                        new ObjectCreationExpression(completionSourceType, []))]),
                new LocalVariableDeclarationStatement(inlineExceptionType,
                    [new LocalVariableDeclarator(inlineCallbackExceptionName, InlineException())]),
                new LocalVariableDeclarationStatement(BuiltInSystemTypes.Bool,
                    [new LocalVariableDeclarator(inlineCallbackCancellationName, InlineCancellation())]),
                new LocalVariableDeclarationStatement(new AutoType(false),
                    [new LocalVariableDeclarator(inlineCallbackDisposeTaskName, InlineDisposeTask())]),
            };
            if (hasResult)
            {
                inlinePendingDisposeStatements.Add(new LocalVariableDeclarationStatement(
                    taskType.TypeArguments[0],
                    [new LocalVariableDeclarator(inlineCallbackResultName, inlineResultExpression!)]));
            }
            inlinePendingDisposeStatements.AddRange([
                new LocalVariableDeclarationStatement(callbackType,
                    [new LocalVariableDeclarator(inlineDisposeCallbackName, inlineDisposeCallback)]),
                new ExpressionStatement(new InvocationExpression(
                    Member(InlineCallbackDisposeTask(), "OnCompleted"), [InlineDisposeCallback()])),
                new ReturnStatement(Member(InlineCompletion(), "Task")),
            ]);
            var inlinePendingDispose = new BlockStatement(inlinePendingDisposeStatements);
            var inlinePendingMove = new BlockStatement([
                new LocalVariableDeclarationStatement(asyncEnumeratorType,
                    [new LocalVariableDeclarator(continuationEnumeratorName, Enumerator())]),
                new LocalVariableDeclarationStatement(new AutoType(false),
                    [new LocalVariableDeclarator(continuationInitialMoveTaskName, InlineMoveTask())]),
                new LocalVariableDeclarationStatement(BuiltInSystemTypes.Bool,
                    [new LocalVariableDeclarator(continuationHasInitialMoveName, trueLiteral)]),
                .. callbackAliasDeclarations,
                new LocalVariableDeclarationStatement(completionSourceType,
                    [new LocalVariableDeclarator(completionName,
                        new ObjectCreationExpression(completionSourceType, []))]),
                new LocalVariableDeclarationStatement(callbackType,
                    [new LocalVariableDeclarator(stepName, null)]),
                new LocalVariableDeclarationStatement(BuiltInSystemTypes.Bool,
                    [new LocalVariableDeclarator(runningName, falseLiteral)]),
                new LocalVariableDeclarationStatement(BuiltInSystemTypes.Bool,
                    [new LocalVariableDeclarator(requestedName, falseLiteral)]),
                new LocalVariableDeclarationStatement(BuiltInSystemTypes.Bool,
                    [new LocalVariableDeclarator(disposingName, falseLiteral)]),
                new LocalVariableDeclarationStatement(new FunctionType(
                    QualifiedIdentifier.Empty, BuiltInSystemTypes.Void,
                    [exceptionType, BuiltInSystemTypes.Bool]),
                    [new LocalVariableDeclarator(finishName, finishLambda)]),
                new ExpressionStatement(new AssignmentExpression(Step(), "=", stepLambda)),
                new ExpressionStatement(InvokeLocal(Step())),
                new ReturnStatement(Member(Completion(), "Task")),
            ]);

            var fastPath = new List<StatementBase>(prefix)
            {
                new LocalVariableDeclarationStatement(asyncEnumeratorType,
                    [new LocalVariableDeclarator(enumeratorName, getEnumerator)]),
                new LocalVariableDeclarationStatement(new AutoType(false),
                    [new LocalVariableDeclarator(initialMoveTaskName,
                        Call(Enumerator(), "MoveNextAsync"))]),
                new LocalVariableDeclarationStatement(BuiltInSystemTypes.Bool,
                    [new LocalVariableDeclarator(inlineFinishedName, falseLiteral)]),
                new LocalVariableDeclarationStatement(inlineExceptionType,
                    [new LocalVariableDeclarator(inlineExceptionName, new LiteralExpression("null"))]),
                new LocalVariableDeclarationStatement(inlineExceptionType,
                    [new LocalVariableDeclarator(inlineCleanupExceptionName, new LiteralExpression("null"))]),
                new LocalVariableDeclarationStatement(BuiltInSystemTypes.Bool,
                    [new LocalVariableDeclarator(inlineCleanupCancellationName, falseLiteral)]),
                new LocalVariableDeclarationStatement(BuiltInSystemTypes.Bool,
                    [new LocalVariableDeclarator(inlineCancellationName, falseLiteral)]),
                new LocalVariableDeclarationStatement(new AutoType(false),
                    [new LocalVariableDeclarator(inlineMoveTaskName, Id(initialMoveTaskName))]),
                inlineLoop,
                new IfStatement(
                    new UnaryExpression("!", InlineFinished()),
                    inlinePendingMove,
                    new BlockStatement([
                        new LocalVariableDeclarationStatement(new AutoType(false),
                            [new LocalVariableDeclarator(inlineDisposeTaskName,
                                Call(Enumerator(), "DisposeAsync"))]),
                        new IfStatement(Member(InlineDisposeTask(), "IsCompleted"),
                            inlineCompletedDispose, inlinePendingDispose),
                    ])),
            };
            function.SetBody(fastPath);
            return true;
        }
        function.SetBody(loweredBody);
        return true;
    }

    private static bool ContainsReturn(StatementBase statement) => statement switch
    {
        ReturnStatement => true,
        BlockStatement block => block.Statements.Any(ContainsReturn),
        IfStatement conditional => ContainsReturn(conditional.ThenStatement) ||
            (conditional.ElseStatement is not null &&
             ContainsReturn(conditional.ElseStatement)),
        _ => false,
    };

    private static bool CanLowerAwaitForeachConsumerBody(StatementBase statement) => statement switch
    {
        BlockStatement block => block.Statements.All(CanLowerAwaitForeachConsumerBody),
        IfStatement conditional =>
            !CoroutineSyntaxAnalysis.ContainsAwait(conditional.Condition) &&
            CanLowerAwaitForeachConsumerBody(conditional.ThenStatement) &&
            (conditional.ElseStatement is null ||
             CanLowerAwaitForeachConsumerBody(conditional.ElseStatement)),
        BreakStatement or ContinueStatement => true,
        ReturnStatement { Expression: null } => true,
        ExpressionStatement expression => !CoroutineSyntaxAnalysis.ContainsAwait(expression.Expression),
        LocalVariableDeclarationStatement declaration => declaration.Declarators
            .All(declarator => declarator.Initializer is null ||
                !CoroutineSyntaxAnalysis.ContainsAwait(declarator.Initializer)),
        ThrowStatement { Expression: { } exception } => !CoroutineSyntaxAnalysis.ContainsAwait(exception),
        _ => false,
    };

    private static bool CanLowerAwaitForeachInlineBody(StatementBase statement) => statement switch
    {
        BlockStatement block => block.Statements.All(CanLowerAwaitForeachInlineBody),
        IfStatement conditional =>
            !CoroutineSyntaxAnalysis.ContainsAwait(conditional.Condition) &&
            CanLowerAwaitForeachInlineBody(conditional.ThenStatement) &&
            (conditional.ElseStatement is null ||
             CanLowerAwaitForeachInlineBody(conditional.ElseStatement)),
        ExpressionStatement expression =>
            !CoroutineSyntaxAnalysis.ContainsAwait(expression.Expression),
        LocalVariableDeclarationStatement declaration => declaration.Declarators
            .All(declarator => declarator.Initializer is null ||
                !CoroutineSyntaxAnalysis.ContainsAwait(declarator.Initializer)),
        BreakStatement or ContinueStatement or ReturnStatement { Expression: null } => true,
        ThrowStatement { Expression: { } exception } =>
            !CoroutineSyntaxAnalysis.ContainsAwait(exception),
        _ => false,
    };

    private static StatementBase RewriteAwaitForeachInlineBody(
        StatementBase statement,
        Func<IdentifierExpression> finished,
        Func<IdentifierExpression> moveTask,
        Func<IdentifierExpression> enumerator)
    {
        switch (statement)
        {
            case BlockStatement block:
                return new BlockStatement(block.Statements.Select(item =>
                    RewriteAwaitForeachInlineBody(item, finished, moveTask, enumerator))
                    .ToArray(), block.IsUnsafe)
                {
                    SourceSpan = block.SourceSpan,
                };
            case IfStatement conditional:
                return new IfStatement(
                    conditional.Condition,
                    RewriteAwaitForeachInlineBody(
                        conditional.ThenStatement, finished, moveTask, enumerator),
                    conditional.ElseStatement is null ? null :
                        RewriteAwaitForeachInlineBody(
                            conditional.ElseStatement, finished, moveTask, enumerator))
                {
                    SourceSpan = conditional.SourceSpan,
                };
            case BreakStatement:
                return new BlockStatement([
                    new ExpressionStatement(new AssignmentExpression(
                        finished(), "=", new LiteralExpression("true")))
                    {
                        SourceSpan = statement.SourceSpan,
                    },
                    new BreakStatement { SourceSpan = statement.SourceSpan },
                ])
                {
                    SourceSpan = statement.SourceSpan,
                };
            case ContinueStatement:
                return new BlockStatement([
                    new ExpressionStatement(new AssignmentExpression(
                        moveTask(), "=", new InvocationExpression(
                            new MemberAccessExpression(enumerator(), "MoveNextAsync"), [])))
                    {
                        SourceSpan = statement.SourceSpan,
                    },
                    new ContinueStatement { SourceSpan = statement.SourceSpan },
                ])
                {
                    SourceSpan = statement.SourceSpan,
                };
            case ReturnStatement { Expression: null }:
                return new BlockStatement([
                    new ExpressionStatement(new AssignmentExpression(
                        finished(), "=", new LiteralExpression("true")))
                    {
                        SourceSpan = statement.SourceSpan,
                    },
                    new BreakStatement { SourceSpan = statement.SourceSpan },
                ])
                {
                    SourceSpan = statement.SourceSpan,
                };
            default:
                return statement;
        }
    }

    private static StatementBase RewriteAwaitForeachConsumerBody(
        StatementBase statement,
        Func<IdentifierExpression> finish,
        Func<IdentifierExpression> step)
    {
        switch (statement)
        {
            case BlockStatement block:
                return new BlockStatement(block.Statements.Select(item =>
                    RewriteAwaitForeachConsumerBody(item, finish, step)).ToArray(), block.IsUnsafe)
                {
                    SourceSpan = block.SourceSpan,
                };
            case IfStatement conditional:
                return new IfStatement(
                    conditional.Condition,
                    RewriteAwaitForeachConsumerBody(conditional.ThenStatement, finish, step),
                    conditional.ElseStatement is null ? null :
                        RewriteAwaitForeachConsumerBody(conditional.ElseStatement, finish, step))
                {
                    SourceSpan = conditional.SourceSpan,
                };
            case BreakStatement or ReturnStatement { Expression: null }:
                return new BlockStatement([
                    new ExpressionStatement(new InvocationExpression(finish(),
                        [new LiteralExpression("null"), new LiteralExpression("false")])
                    {
                        SourceSpan = statement.SourceSpan,
                    })
                    {
                        SourceSpan = statement.SourceSpan,
                    },
                    new ReturnStatement(null) { SourceSpan = statement.SourceSpan },
                ])
                {
                    SourceSpan = statement.SourceSpan,
                };
            case ContinueStatement:
                return new BlockStatement([
                    new ExpressionStatement(new InvocationExpression(step(), [])
                    {
                        SourceSpan = statement.SourceSpan,
                    })
                    {
                        SourceSpan = statement.SourceSpan,
                    },
                    new ReturnStatement(null) { SourceSpan = statement.SourceSpan },
                ])
                {
                    SourceSpan = statement.SourceSpan,
                };
            default:
                return statement;
        }
    }

    private static void RejectAwaitAcrossCatchRegions(FunctionDeclaration function)
    {
        if (function.Body is not { } body)
        {
            return;
        }

        var unsupportedTry = EnumerateStatements(body)
            .OfType<TryStatement>()
            .FirstOrDefault(statement => statement.CatchClauses.Count != 0 &&
                (CoroutineSyntaxAnalysis.EnumerateBodyStatements([statement.Body])
                     .Any(CoroutineSyntaxAnalysis.StatementContainsAwait) ||
                 statement.CatchClauses.Any(clause =>
                     (clause.Filter is not null &&
                      CoroutineSyntaxAnalysis.ContainsAwait(clause.Filter)) ||
                     CoroutineSyntaxAnalysis.EnumerateBodyStatements([clause.Body])
                         .Any(CoroutineSyntaxAnalysis.StatementContainsAwait))));
        if (unsupportedTry is not null)
        {
            throw new CompilationErrorException(
                "Await is not supported in a try/catch body or exception filter in async Task functions.",
                unsupportedTry.SourceSpan ?? function.SourceSpan);
        }
    }

    private static bool TryLowerReturnAwait(
        FunctionDeclaration function,
        NamedType taskType,
        IReadOnlyList<FunctionDeclaration> availableFunctions,
        IReadOnlyList<QualifiedIdentifier> imports)
    {
        if (TryLowerAwaitLocalContinuation(
                function, taskType, availableFunctions, imports))
        {
            return true;
        }
        if (!TryGetReturnAwait(function, out var awaitExpression))
        {
            return false;
        }

        var resultType = taskType.TypeArguments[0];
        var awaitedTaskType = NamedType.CreateWithTypeArguments(
            "System.Threading.Tasks.Task", [resultType]);
        var names = function.Parameters.Select(parameter => parameter.Name)
            .ToHashSet(StringComparer.Ordinal);
        var sourceName = CreateUniqueName(names, "__cx_async_source");
        var completionName = CreateUniqueName(names, "__cx_async_completion");
        var callbackSourceName = CreateUniqueName(names, "__cx_async_callback_source");
        var callbackCompletionName = CreateUniqueName(
            names, "__cx_async_callback_completion");
        IdentifierExpression Source() =>
            new(new QualifiedIdentifier(sourceName));
        IdentifierExpression Completion() =>
            new(new QualifiedIdentifier(completionName));
        IdentifierExpression CallbackSource() =>
            new(new QualifiedIdentifier(callbackSourceName));
        IdentifierExpression CallbackCompletion() =>
            new(new QualifiedIdentifier(callbackCompletionName));
        var completedAwait = new AwaitExpression(
            Source())
        {
            SourceSpan = awaitExpression.SourceSpan,
        };
        var cancellationExceptionName = CreateUniqueName(
            names, "__cx_async_canceled");
        var exceptionName = CreateUniqueName(names, "__cx_async_exception");
        var callback = new LambdaExpression(
            [],
            null,
            [new TryStatement(
                new BlockStatement([new ExpressionStatement(
                    new InvocationExpression(
                        new MemberAccessExpression(CallbackCompletion(), "SetResult"),
                        [new InvocationExpression(
                            new MemberAccessExpression(CallbackSource(), "GetResult"),
                            [])]))]),
                [
                    new CatchClause(
                        new NamedType(
                            "System.Threading.Tasks.OperationCanceledException", []),
                        cancellationExceptionName,
                        null,
                        new BlockStatement([new ExpressionStatement(
                            new InvocationExpression(
                                new MemberAccessExpression(
                                    CallbackCompletion(), "SetCanceled"),
                                []))])),
                    new CatchClause(
                        new NamedType("System.Exception", []),
                        exceptionName,
                        null,
                        new BlockStatement([new ExpressionStatement(
                            new InvocationExpression(
                                new MemberAccessExpression(
                                    CallbackCompletion(), "SetFault"),
                                [new IdentifierExpression(
                                    new QualifiedIdentifier(exceptionName))]))])),
                ],
                null)])
        {
            SourceSpan = awaitExpression.SourceSpan ?? function.SourceSpan,
        };
        var body = new StatementBase[]
        {
            CreateInlineValueTaskCompletionBranch(
                function, taskType, awaitExpression)!,
            new LocalVariableDeclarationStatement(awaitedTaskType,
            [new LocalVariableDeclarator(
                sourceName,
                NormalizeParameterValueTaskAwaitOperand(function, awaitExpression.Operand))]),
            new IfStatement(
                new MemberAccessExpression(Source(), "IsCompleted"),
                new BlockStatement([
                    new ReturnStatement(new ObjectCreationExpression(
                        awaitedTaskType, [completedAwait])),
                ]),
                null),
            new LocalVariableDeclarationStatement(awaitedTaskType,
            [new LocalVariableDeclarator(
                completionName,
                new ObjectCreationExpression(awaitedTaskType, []))]),
            new LocalVariableDeclarationStatement(awaitedTaskType,
            [new LocalVariableDeclarator(callbackSourceName, Source())]),
            new LocalVariableDeclarationStatement(awaitedTaskType,
            [new LocalVariableDeclarator(callbackCompletionName, Completion())]),
            new ExpressionStatement(new InvocationExpression(
                new MemberAccessExpression(Source(), "OnCompleted"),
                [callback])),
            new ReturnStatement(Completion()),
        };
        if (body[0] is not IfStatement { ElseStatement: null })
        {
            body = body.Skip(1).ToArray();
        }
        function.SetBody(body);
        return true;
    }

    private static IfStatement? CreateInlineValueTaskCompletionBranch(
        FunctionDeclaration function,
        NamedType taskType,
        AwaitExpression awaitExpression)
    {
        if (awaitExpression.Operand is not IdentifierExpression identifier)
        {
            return null;
        }

        var parameter = function.Parameters.FirstOrDefault(candidate =>
            candidate.Name == identifier.Identifier.ToString());
        var parameterType = parameter?.ParameterType is ConstType constant
            ? constant.UnderlyingType
            : parameter?.ParameterType;
        if (parameterType is not NamedType valueTaskType ||
            !IsValueTaskType(valueTaskType))
        {
            return null;
        }

        var inlineGetResult = new InvocationExpression(
            new MemberAccessExpression(awaitExpression.Operand, "GetResult"), []);
        var inlineStatements = taskType.TypeArguments.Count == 1
            ? new StatementBase[]
            {
                new ReturnStatement(new ObjectCreationExpression(
                    taskType, [inlineGetResult])),
            }
            : new StatementBase[]
            {
                new ExpressionStatement(inlineGetResult),
                new ReturnStatement(new ObjectCreationExpression(taskType, [])),
            };
        return new IfStatement(
            new MemberAccessExpression(awaitExpression.Operand, "IsCompleted"),
            new BlockStatement(inlineStatements),
            null)
        {
            SourceSpan = awaitExpression.SourceSpan ?? function.SourceSpan,
        };
    }

    private static ExpressionBase NormalizeParameterValueTaskAwaitOperand(
        FunctionDeclaration function,
        ExpressionBase operand)
    {
        if (operand is not IdentifierExpression identifier)
        {
            return operand;
        }

        var parameter = function.Parameters.FirstOrDefault(candidate =>
            candidate.Name == identifier.Identifier.ToString());
        var parameterType = parameter?.ParameterType is ConstType constant
            ? constant.UnderlyingType
            : parameter?.ParameterType;
        if (parameterType is not NamedType named || !IsValueTaskType(named))
        {
            return operand;
        }

        return new InvocationExpression(
            new MemberAccessExpression(operand, "AsTask"), [])
        {
            SourceSpan = operand.SourceSpan,
        };
    }

    private static bool TryNormalizeConditionalReturnAwait(FunctionDeclaration function)
    {
        if (function.Body is not [IfStatement conditional] ||
            conditional.ElseStatement is null ||
            !TryGetBranchAwait(conditional.ThenStatement, out var whenTrue) ||
            !TryGetBranchAwait(conditional.ElseStatement, out var whenFalse))
        {
            return false;
        }

        var selectedTask = new ConditionalExpression(
            conditional.Condition,
            whenTrue.Operand,
            whenFalse.Operand)
        {
            SourceSpan = conditional.SourceSpan,
        };
        function.SetBody([
            new ReturnStatement(new AwaitExpression(selectedTask)
            {
                SourceSpan = conditional.SourceSpan,
            }),
        ]);
        return true;
    }

    private static bool TryNormalizeConditionalExpressionAwait(
        FunctionDeclaration function,
        NamedType taskType)
    {
        if (function.GenericTypeNames.Length != 0 ||
            taskType.TypeArguments.Count != 1 ||
            function.Body is not [ReturnStatement
            {
                Expression: ConditionalExpression conditional,
            }])
        {
            return false;
        }

        var trueAwait = conditional.WhenTrue as AwaitExpression;
        var falseAwait = conditional.WhenFalse as AwaitExpression;
        if ((trueAwait is null) == (falseAwait is null))
        {
            return false;
        }

        var awaited = trueAwait ?? falseAwait!;
        var synchronous = trueAwait is null
            ? conditional.WhenTrue
            : conditional.WhenFalse;
        if (CoroutineSyntaxAnalysis.ContainsAwait(awaited.Operand) ||
            CoroutineSyntaxAnalysis.ContainsAwait(synchronous))
        {
            return false;
        }

        var completedTask = CreateCompletedTask(
            taskType, synchronous, conditional.SourceSpan ?? function.SourceSpan);
        var selectedTask = new ConditionalExpression(
            conditional.Condition,
            trueAwait is null ? completedTask : awaited.Operand,
            falseAwait is null ? completedTask : awaited.Operand)
        {
            SourceSpan = conditional.SourceSpan,
        };
        function.SetBody([
            new ReturnStatement(new AwaitExpression(selectedTask)
            {
                SourceSpan = conditional.SourceSpan ?? function.SourceSpan,
            })
            {
                SourceSpan = function.Body[0].SourceSpan,
            },
        ]);
        return true;
    }

    private static bool TryNormalizeNestedAwaitCallArgument(FunctionDeclaration function)
    {
        if (function.GenericTypeNames.Length != 0 ||
            function.ReturnType is not NamedType { TypeArguments.Count: 1 } ||
            function.Body is not [ReturnStatement
                {
                    Expression: InvocationExpression
                    {
                        Target: IdentifierExpression,
                        Arguments: { Count: >= 1 } arguments,
                    } invocation,
                }])
        {
            return false;
        }

        var awaitIndexes = arguments.Select((argument, index) =>
                (argument, index))
            .Where(pair => pair.argument is AwaitExpression)
            .Select(pair => pair.index)
            .ToArray();
        if (awaitIndexes.Length != 1 || arguments.Select((argument, index) =>
                (argument, index)).Any(pair => pair.index != awaitIndexes[0] &&
                    CoroutineSyntaxAnalysis.ContainsAwait(pair.argument)) ||
            arguments[awaitIndexes[0]] is not AwaitExpression awaited ||
            CoroutineSyntaxAnalysis.ContainsAwait(awaited.Operand))
        {
            return false;
        }

        var names = function.Parameters.Select(parameter => parameter.Name)
            .ToHashSet(StringComparer.Ordinal);
        var awaitedName = CreateUniqueName(names, "__cx_async_nested_await_value");
        var resumedArguments = arguments.ToArray();
        resumedArguments[awaitIndexes[0]] =
            new IdentifierExpression(new QualifiedIdentifier(awaitedName));
        var prefix = new List<StatementBase>();
        for (var index = 0; index < awaitIndexes[0]; index++)
        {
            var argumentName = CreateUniqueName(names, $"__cx_async_nested_argument_{index}");
            prefix.Add(new LocalVariableDeclarationStatement(
                new AutoType(false),
                [new LocalVariableDeclarator(argumentName, arguments[index])])
            {
                SourceSpan = arguments[index].SourceSpan,
            });
            resumedArguments[index] =
                new IdentifierExpression(new QualifiedIdentifier(argumentName));
        }

        var invocationAfterAwait = new InvocationExpression(
            invocation.Target,
            resumedArguments,
            invocation.ExplicitTypeArguments)
        {
            SourceSpan = invocation.SourceSpan,
        };
        prefix.Add(new LocalVariableDeclarationStatement(
            new AutoType(false),
            [new LocalVariableDeclarator(awaitedName, awaited)])
        {
            SourceSpan = awaited.SourceSpan,
        });
        prefix.Add(new ReturnStatement(invocationAfterAwait)
        {
            SourceSpan = function.Body[0].SourceSpan,
        });
        function.SetBody(prefix);
        return true;
    }

    private static bool TryNormalizeNestedAwaitBinaryOperand(FunctionDeclaration function)
    {
        if (function.GenericTypeNames.Length != 0 ||
            function.ReturnType is not NamedType { TypeArguments.Count: 1 } ||
            function.Body is not [ReturnStatement
                { Expression: BinaryExpression binary } returnStatement])
        {
            return false;
        }

        var awaitInLeft = binary.Left as AwaitExpression;
        var awaitInRight = binary.Right as AwaitExpression;
        if ((awaitInLeft is null) == (awaitInRight is null) ||
            CoroutineSyntaxAnalysis.ContainsAwait(awaitInLeft?.Operand ?? binary.Left) ||
            CoroutineSyntaxAnalysis.ContainsAwait(awaitInRight?.Operand ?? binary.Right))
        {
            return false;
        }

        var names = function.Parameters.Select(parameter => parameter.Name)
            .ToHashSet(StringComparer.Ordinal);
        var awaitedName = CreateUniqueName(names, "__cx_async_nested_await_value");
        var awaitedLocal = new LocalVariableDeclarationStatement(
            new AutoType(false),
            [new LocalVariableDeclarator(awaitedName, awaitInLeft ?? awaitInRight!)])
        {
            SourceSpan = (awaitInLeft ?? awaitInRight)!.SourceSpan,
        };

        var statements = new List<StatementBase>();
        ExpressionBase left;
        ExpressionBase right;
        if (awaitInRight is not null)
        {
            var leftName = CreateUniqueName(names, "__cx_async_nested_left");
            statements.Add(new LocalVariableDeclarationStatement(
                new AutoType(false),
                [new LocalVariableDeclarator(leftName, binary.Left)])
            {
                SourceSpan = binary.Left.SourceSpan,
            });
            left = new IdentifierExpression(new QualifiedIdentifier(leftName));
            right = new IdentifierExpression(new QualifiedIdentifier(awaitedName));
        }
        else
        {
            left = new IdentifierExpression(new QualifiedIdentifier(awaitedName));
            right = binary.Right;
        }

        statements.Add(awaitedLocal);
        statements.Add(new ReturnStatement(new BinaryExpression(left, binary.Operator, right)
        {
            SourceSpan = binary.SourceSpan,
        })
        {
            SourceSpan = returnStatement.SourceSpan,
        });
        function.SetBody(statements);
        return true;
    }

    private static bool TryNormalizeNestedShortCircuitAwait(
        FunctionDeclaration function,
        NamedType taskType)
    {
        if (function.GenericTypeNames.Length != 0 ||
            taskType.TypeArguments.Count != 1 ||
            function.Body is not [ReturnStatement
                { Expression: BinaryExpression binary } returnStatement] ||
            binary.Operator is not ("&&" or "||") ||
            binary.Right is not AwaitExpression awaited ||
            CoroutineSyntaxAnalysis.ContainsAwait(binary.Left) ||
            CoroutineSyntaxAnalysis.ContainsAwait(awaited.Operand))
        {
            return false;
        }

        var completedValue = new LiteralExpression(
            binary.Operator == "&&" ? "false" : "true");
        var completedTask = CreateCompletedTask(
            taskType, completedValue, binary.SourceSpan ?? function.SourceSpan);
        var selectedTask = new ConditionalExpression(
            binary.Left,
            binary.Operator == "&&" ? awaited.Operand : completedTask,
            binary.Operator == "&&" ? completedTask : awaited.Operand)
        {
            SourceSpan = binary.SourceSpan,
        };
        function.SetBody([
            new ReturnStatement(new AwaitExpression(selectedTask)
            {
                SourceSpan = binary.SourceSpan ?? function.SourceSpan,
            })
            {
                SourceSpan = returnStatement.SourceSpan,
            },
        ]);
        return true;
    }

    private static bool TryNormalizeNestedNullCoalescingAwait(
        FunctionDeclaration function,
        NamedType taskType)
    {
        if (function.GenericTypeNames.Length != 0 ||
            taskType.TypeArguments.Count != 1 ||
            function.Body is not { Count: >= 1 } body ||
            body[^1] is not ReturnStatement
                { Expression: NullCoalescingExpression { Right: AwaitExpression awaited } coalescing } returnStatement ||
            body.Take(body.Count - 1).Any(CoroutineSyntaxAnalysis.StatementContainsAwait) ||
            coalescing.Left is not IdentifierExpression ||
            CoroutineSyntaxAnalysis.ContainsAwait(coalescing.Left) ||
            CoroutineSyntaxAnalysis.ContainsAwait(awaited.Operand))
        {
            return false;
        }

        var names = function.Parameters.Select(parameter => parameter.Name)
            .ToHashSet(StringComparer.Ordinal);
        var awaitedName = CreateUniqueName(names, "__cx_async_coalesce_value");
        var leftName = ((IdentifierExpression)coalescing.Left).Identifier.ToString();
        var nonNullTask = CreateCompletedTask(
            taskType,
            new IdentifierExpression(new QualifiedIdentifier(leftName)),
            coalescing.SourceSpan ?? function.SourceSpan);
        var selectedTask = new ConditionalExpression(
            new BinaryExpression(
                new IdentifierExpression(new QualifiedIdentifier(leftName)),
                "!=",
                new LiteralExpression("null")),
            nonNullTask,
            awaited.Operand)
        {
            SourceSpan = coalescing.SourceSpan,
        };
        var awaitedDeclaration = new LocalVariableDeclarationStatement(
            new AutoType(false),
            [new LocalVariableDeclarator(awaitedName, new AwaitExpression(selectedTask)
            {
                SourceSpan = awaited.SourceSpan,
            })])
        {
            SourceSpan = awaited.SourceSpan,
        };
        function.SetBody(body.Take(body.Count - 1).Append(awaitedDeclaration).Append(
            new ReturnStatement(new IdentifierExpression(new QualifiedIdentifier(awaitedName)))
            {
                SourceSpan = returnStatement.SourceSpan,
            }).ToArray());
        return true;
    }

    private static bool TryNormalizeAwaitSwitch(FunctionDeclaration function)
    {
        if (function.Body is not { Count: >= 1 } body)
        {
            return false;
        }
        var switchIndex = body.ToList().FindIndex(statement => statement is SwitchStatement);
        if (switchIndex < 0 || body.Skip(switchIndex + 1)
                .Any(statement => statement is SwitchStatement))
        {
            return false;
        }
        var switchStatement = (SwitchStatement)body[switchIndex];
        var prefix = body.Take(switchIndex).ToArray();
        var trailing = body.Skip(switchIndex + 1).ToArray();
        if (function.GenericTypeNames.Length != 0 ||
            function.ReturnType is not NamedType { TypeArguments.Count: 1 } ||
            prefix.Any(CoroutineSyntaxAnalysis.StatementContainsAwait) ||
            trailing.Any(CoroutineSyntaxAnalysis.StatementContainsAwait) ||
            switchStatement.Sections.Count == 0)
        {
            return false;
        }

        var arms = new List<(SwitchSection Section, AwaitExpression Await, string Local)>();
        var defaultIndex = -1;
        for (var index = 0; index < switchStatement.Sections.Count; index++)
        {
            var section = switchStatement.Sections[index];
            var defaults = section.Labels.Count(label => label.IsDefault);
            if (defaults != 0)
            {
                if (defaults != 1 || section.Labels.Count != 1 || defaultIndex >= 0)
                {
                    return false;
                }
                defaultIndex = index;
            }
            else if (section.Labels.Count == 0 || section.Labels.Any(label =>
                         label.Filter is not null ||
                         label.Value is not LiteralExpression { IsString: false }))
            {
                return false;
            }

            var statements = section.Statements switch
            {
                [BlockStatement block] => block.Statements,
                var values => values,
            };
            if (statements.Count == 0 || statements[0] is not
                    LocalVariableDeclarationStatement
                    {
                        DeclaredType: AutoType { Const: false },
                        Declarators: [{ Initializer: AwaitExpression awaited } declarator],
                    } || CoroutineSyntaxAnalysis.ContainsAwait(awaited.Operand) ||
                statements.Skip(1).Any(CoroutineSyntaxAnalysis.StatementContainsAwait))
            {
                return false;
            }

            arms.Add((section, awaited, declarator.Name));
        }

        if (defaultIndex < 0 || arms.Count != switchStatement.Sections.Count)
        {
            return false;
        }

        var names = function.Parameters.Select(parameter => parameter.Name)
            .Concat(EnumerateStatements(function.Body)
                .OfType<LocalVariableDeclarationStatement>()
                .SelectMany(declaration => declaration.Declarators)
                .Select(declarator => declarator.Name))
            .ToHashSet(StringComparer.Ordinal);
        var selectorName = CreateUniqueName(names, "__cx_async_switch_selector");
        var selectedResultName = CreateUniqueName(names, "__cx_async_switch_result");
        IdentifierExpression Selector() =>
            new(new QualifiedIdentifier(selectorName));
        var selectedTask = arms[defaultIndex].Await.Operand;
        for (var index = arms.Count - 1; index >= 0; index--)
        {
            if (index == defaultIndex)
            {
                continue;
            }

            ExpressionBase? condition = null;
            foreach (var label in arms[index].Section.Labels)
            {
                if (label.IsDefault)
                {
                    continue;
                }

                var equals = new BinaryExpression(Selector(), "==", label.Value!);
                condition = condition is null
                    ? equals
                    : new BinaryExpression(condition, "||", equals);
            }

            if (condition is not null)
            {
                selectedTask = new ConditionalExpression(
                    condition, arms[index].Await.Operand, selectedTask)
                {
                    SourceSpan = switchStatement.SourceSpan ?? function.SourceSpan,
                };
            }
        }

        var returnsFromEachSection = prefix.Length == 0 && trailing.Length == 0 &&
            arms.All(arm =>
            {
                var statements = arm.Section.Statements switch
                {
                    [BlockStatement block] => block.Statements,
                    var values => values,
                };
                return statements.Count == 2 &&
                    statements[1] is ReturnStatement
                    {
                        Expression: IdentifierExpression result,
                    } && result.Identifier.ToString() == arm.Local;
            });
        if (returnsFromEachSection)
        {
            function.SetBody([
                new LocalVariableDeclarationStatement(
                    new AutoType(false),
                    [new LocalVariableDeclarator(selectorName, switchStatement.Expression)]),
                new LocalVariableDeclarationStatement(
                    new AutoType(false),
                    [new LocalVariableDeclarator(selectedResultName,
                        new AwaitExpression(selectedTask)
                        {
                            SourceSpan = switchStatement.SourceSpan ?? function.SourceSpan,
                        })]),
                new ReturnStatement(new IdentifierExpression(
                    new QualifiedIdentifier(selectedResultName))),
            ]);
            return true;
        }

        var resumedSwitch = new SwitchStatement(Selector(),
            arms.Select(arm => new SwitchSection(arm.Section.Labels,
                arm.Section.Statements.Count == 1 && arm.Section.Statements[0] is BlockStatement originalBlock
                    ? [new BlockStatement(ReplaceAwaitedLocal(originalBlock.Statements, arm.Local, selectedResultName))]
                    : ReplaceAwaitedLocal(arm.Section.Statements, arm.Local, selectedResultName))).ToArray())
        {
            SourceSpan = switchStatement.SourceSpan,
        };
        function.SetBody([
            .. prefix,
            new LocalVariableDeclarationStatement(
                new AutoType(false),
                [new LocalVariableDeclarator(selectorName, switchStatement.Expression)]),
            new LocalVariableDeclarationStatement(
                new AutoType(false),
                [new LocalVariableDeclarator(selectedResultName,
                    new AwaitExpression(selectedTask)
                    {
                        SourceSpan = switchStatement.SourceSpan ?? function.SourceSpan,
                    })]),
            resumedSwitch,
            .. trailing,
        ]);
        return true;
    }

    private static IReadOnlyList<StatementBase> ReplaceAwaitedLocal(
        IReadOnlyList<StatementBase> statements,
        string localName,
        string selectedResultName)
    {
        var copy = statements.ToArray();
        var declaration = (LocalVariableDeclarationStatement)copy[0];
        var declarator = declaration.Declarators[0];
        copy[0] = new LocalVariableDeclarationStatement(
            declaration.DeclaredType,
            [new LocalVariableDeclarator(localName,
                new IdentifierExpression(new QualifiedIdentifier(selectedResultName)))])
        {
            SourceSpan = declaration.SourceSpan,
        };
        return copy;
    }

    private static bool TryNormalizeVoidAwaitSwitch(FunctionDeclaration function)
    {
        if (function.GenericTypeNames.Length != 0 ||
            function.ReturnType is not NamedType { TypeArguments.Count: 0 } ||
            function.Body is not { Count: >= 1 } body)
        {
            return false;
        }

        var switchIndex = body.ToList().FindIndex(statement => statement is SwitchStatement);
        if (switchIndex < 0 || body.Skip(switchIndex + 1)
                .Any(statement => statement is SwitchStatement))
        {
            return false;
        }

        var switchStatement = (SwitchStatement)body[switchIndex];
        var prefix = body.Take(switchIndex).ToArray();
        var trailing = body.Skip(switchIndex + 1).ToArray();
        if (prefix.Any(CoroutineSyntaxAnalysis.StatementContainsAwait) ||
            trailing.Any(CoroutineSyntaxAnalysis.StatementContainsAwait) ||
            switchStatement.Sections.Count == 0)
        {
            return false;
        }

        var sections = new List<(SwitchSection Section, bool IsBlock, ExpressionBase Operand)>();
        var defaultIndex = -1;
        for (var index = 0; index < switchStatement.Sections.Count; index++)
        {
            var section = switchStatement.Sections[index];
            var defaults = section.Labels.Count(label => label.IsDefault);
            if (defaults != 0)
            {
                if (defaults != 1 || section.Labels.Count != 1 || defaultIndex >= 0)
                {
                    return false;
                }
                defaultIndex = index;
            }
            else if (section.Labels.Count == 0 || section.Labels.Any(label =>
                         label.Filter is not null ||
                         label.Value is not LiteralExpression { IsString: false }))
            {
                return false;
            }

            var isBlock = section.Statements.Count == 1 &&
                section.Statements[0] is BlockStatement;
            var statements = isBlock
                ? ((BlockStatement)section.Statements[0]).Statements
                : section.Statements;
            if (statements.Count == 0 || statements[0] is not
                    ExpressionStatement { Expression: AwaitExpression awaited } ||
                CoroutineSyntaxAnalysis.ContainsAwait(awaited.Operand) ||
                statements.Skip(1).Any(CoroutineSyntaxAnalysis.StatementContainsAwait))
            {
                return false;
            }
            sections.Add((section, isBlock, awaited.Operand));
        }

        if (defaultIndex < 0 || sections.Count != switchStatement.Sections.Count)
        {
            return false;
        }

        var names = function.Parameters.Select(parameter => parameter.Name)
            .Concat(EnumerateStatements(function.Body)
                .OfType<LocalVariableDeclarationStatement>()
                .SelectMany(declaration => declaration.Declarators)
                .Select(declarator => declarator.Name))
            .ToHashSet(StringComparer.Ordinal);
        var selectorName = CreateUniqueName(names, "__cx_async_switch_selector");
        IdentifierExpression Selector() =>
            new(new QualifiedIdentifier(selectorName));

        var selectedTask = sections[defaultIndex].Operand;
        for (var index = sections.Count - 1; index >= 0; index--)
        {
            if (index == defaultIndex)
            {
                continue;
            }

            ExpressionBase? condition = null;
            foreach (var label in sections[index].Section.Labels)
            {
                if (label.IsDefault)
                {
                    continue;
                }

                var equals = new BinaryExpression(Selector(), "==", label.Value!);
                condition = condition is null
                    ? equals
                    : new BinaryExpression(condition, "||", equals);
            }
            if (condition is not null)
            {
                selectedTask = new ConditionalExpression(
                    condition, sections[index].Operand, selectedTask)
                {
                    SourceSpan = switchStatement.SourceSpan ?? function.SourceSpan,
                };
            }
        }

        var resumedSwitch = new SwitchStatement(Selector(), sections.Select(item =>
        {
            var statements = item.IsBlock
                ? ((BlockStatement)item.Section.Statements[0]).Statements
                : item.Section.Statements;
            var continuation = statements.Skip(1).ToArray();
            return new SwitchSection(item.Section.Labels,
                item.IsBlock
                    ? [new BlockStatement(continuation)
                        { SourceSpan = ((BlockStatement)item.Section.Statements[0]).SourceSpan }]
                    : continuation);
        }).ToArray())
        {
            SourceSpan = switchStatement.SourceSpan,
        };

        function.SetBody([
            .. prefix,
            new LocalVariableDeclarationStatement(
                new AutoType(false),
                [new LocalVariableDeclarator(selectorName, switchStatement.Expression)]),
            new ExpressionStatement(new AwaitExpression(selectedTask)
            {
                SourceSpan = switchStatement.SourceSpan ?? function.SourceSpan,
            }),
            resumedSwitch,
            .. trailing,
        ]);
        return true;
    }

    private static bool TryGetBranchAwait(
        StatementBase statement,
        out AwaitExpression awaitExpression)
    {
        var returned = statement is BlockStatement { Statements: [ReturnStatement item] }
            ? item
            : statement as ReturnStatement;
        if (returned?.Expression is AwaitExpression expression)
        {
            awaitExpression = expression;
            return true;
        }

        awaitExpression = null!;
        return false;
    }

    private static bool TryNormalizeConditionalVoidAwait(FunctionDeclaration function)
    {
        if (function.Body is not [IfStatement conditional] ||
            conditional.ElseStatement is null ||
            !TryGetBranchAwaitStatement(conditional.ThenStatement, out var whenTrue) ||
            !TryGetBranchAwaitStatement(conditional.ElseStatement, out var whenFalse))
        {
            return false;
        }

        var selectedTask = new ConditionalExpression(
            conditional.Condition,
            whenTrue.Operand,
            whenFalse.Operand)
        {
            SourceSpan = conditional.SourceSpan,
        };
        function.SetBody([
            new ExpressionStatement(new AwaitExpression(selectedTask)
            {
                SourceSpan = conditional.SourceSpan,
            }),
        ]);
        return true;
    }

    private static bool TryGetBranchAwaitStatement(
        StatementBase statement,
        out AwaitExpression awaitExpression)
    {
        var expressionStatement = statement is BlockStatement
            { Statements: [ExpressionStatement item] }
            ? item
            : statement as ExpressionStatement;
        if (expressionStatement?.Expression is AwaitExpression expression)
        {
            awaitExpression = expression;
            return true;
        }

        awaitExpression = null!;
        return false;
    }

    private static bool TryNormalizeConditionalAwaitBranchContinuation(
        FunctionDeclaration function)
    {
        if (function.GenericTypeNames.Length != 0 ||
            function.ReturnType is not NamedType { TypeArguments.Count: 1 } ||
            function.Body is not [IfStatement conditional] ||
            conditional.ElseStatement is null ||
            !TryGetAwaitingReturnBranch(
                conditional.ThenStatement,
                out var whenTrueAwaits,
                out var whenTrueLocals,
                out var whenTrueContinuation) ||
            !TryGetAwaitingReturnBranch(
                conditional.ElseStatement,
                out var whenFalseAwaits,
                out var whenFalseLocals,
                out var whenFalseContinuation) ||
            whenTrueAwaits.Length != whenFalseAwaits.Length ||
            whenTrueAwaits.SelectMany((awaited, index) =>
                    CoroutineSyntaxAnalysis.EnumerateAllExpressions(awaited.Operand)
                        .OfType<IdentifierExpression>()
                        .Select(identifier => identifier.Identifier.ToString()))
                .Concat(whenFalseAwaits.SelectMany(awaited =>
                    CoroutineSyntaxAnalysis.EnumerateAllExpressions(awaited.Operand)
                        .OfType<IdentifierExpression>()
                        .Select(identifier => identifier.Identifier.ToString())))
                .Intersect(whenTrueLocals.Concat(whenFalseLocals), StringComparer.Ordinal)
                .Any() ||
            whenTrueAwaits.Concat(whenFalseAwaits)
                .Any(awaited => CoroutineSyntaxAnalysis.ContainsAwait(awaited.Operand)))
        {
            return false;
        }

        var names = function.Parameters.Select(parameter => parameter.Name)
            .Concat(EnumerateStatements(function.Body)
                .OfType<LocalVariableDeclarationStatement>()
                .SelectMany(declaration => declaration.Declarators)
                .Select(declarator => declarator.Name))
            .ToHashSet(StringComparer.Ordinal);
        var conditionName = CreateUniqueName(names, "__cx_async_branch");
        IdentifierExpression Branch() => new(new QualifiedIdentifier(conditionName));
        var branchBody = new List<StatementBase>
        {
            new LocalVariableDeclarationStatement(
                BuiltInSystemTypes.Bool,
                [new LocalVariableDeclarator(conditionName, conditional.Condition)]),
        };
        var selectedResultNames = new List<string>(whenTrueAwaits.Length);
        for (var index = 0; index < whenTrueAwaits.Length; index++)
        {
            var selectedTaskName = CreateUniqueName(names, $"__cx_async_branch_task_{index}");
            var selectedResultName = CreateUniqueName(names, $"__cx_async_branch_result_{index}");
            selectedResultNames.Add(selectedResultName);
            var selectedTask = new ConditionalExpression(
                Branch(), whenTrueAwaits[index].Operand, whenFalseAwaits[index].Operand)
            {
                SourceSpan = conditional.SourceSpan,
            };
            branchBody.Add(new LocalVariableDeclarationStatement(
                new AutoType(false),
                [new LocalVariableDeclarator(selectedTaskName, selectedTask)]));
            branchBody.Add(new LocalVariableDeclarationStatement(
                new AutoType(false),
                [new LocalVariableDeclarator(
                    selectedResultName,
                    new AwaitExpression(new IdentifierExpression(
                        new QualifiedIdentifier(selectedTaskName)))
                    {
                        SourceSpan = whenTrueAwaits[index].SourceSpan ?? conditional.SourceSpan,
                    })]));
        }

        var trueStatements = new List<StatementBase>();
        var falseStatements = new List<StatementBase>();
        for (var index = 0; index < selectedResultNames.Count; index++)
        {
            var selectedResult = new IdentifierExpression(
                new QualifiedIdentifier(selectedResultNames[index]));
            trueStatements.Add(new LocalVariableDeclarationStatement(
                new AutoType(false),
                [new LocalVariableDeclarator(whenTrueLocals[index], selectedResult)]));
            falseStatements.Add(new LocalVariableDeclarationStatement(
                new AutoType(false),
                [new LocalVariableDeclarator(whenFalseLocals[index], selectedResult)]));
        }
        trueStatements.AddRange(whenTrueContinuation);
        falseStatements.AddRange(whenFalseContinuation);
        branchBody.Add(new IfStatement(
            Branch(),
            new BlockStatement(trueStatements),
            new BlockStatement(falseStatements)));
        function.SetBody(branchBody);
        return true;
    }

    private static bool TryGetAwaitingReturnBranch(
        StatementBase statement,
        out AwaitExpression[] awaitExpressions,
        out string[] awaitedLocalNames,
        out StatementBase[] continuation)
    {
        if (statement is BlockStatement { Statements.Count: >= 2 } block)
        {
            var collectedAwaitExpressions = new List<AwaitExpression>();
            var collectedAwaitedLocalNames = new List<string>();
            var index = 0;
            while (index < block.Statements.Count &&
                block.Statements[index] is LocalVariableDeclarationStatement
                {
                    DeclaredType: AutoType { Const: false },
                    Declarators: [{ Initializer: AwaitExpression awaited } local],
                })
            {
                collectedAwaitExpressions.Add(awaited);
                collectedAwaitedLocalNames.Add(local.Name);
                index++;
            }

            var body = block.Statements.Skip(index).ToArray();
            if (collectedAwaitExpressions.Count != 0 &&
                body.Length != 0 &&
                body[^1] is ReturnStatement { Expression: not null } &&
                body.All(item => !CoroutineSyntaxAnalysis.StatementContainsAwait(item)))
            {
                awaitExpressions = collectedAwaitExpressions.ToArray();
                awaitedLocalNames = collectedAwaitedLocalNames.ToArray();
                continuation = body;
                return true;
            }
        }

        awaitExpressions = [];
        awaitedLocalNames = [];
        continuation = [];
        return false;
    }

    private static bool TryLowerAwaitLocalContinuation(
        FunctionDeclaration function,
        NamedType taskType,
        IReadOnlyList<FunctionDeclaration> availableFunctions,
        IReadOnlyList<QualifiedIdentifier> imports)
    {
        if (TryLowerSingleValueTaskResultLocal(function, taskType))
        {
            return true;
        }

        if (HasGenericTypeContext(function) &&
            !IsGenericAwaitLocalChain(function, taskType) ||
            taskType.TypeArguments.Count != 1 ||
            function.Body is not { Count: >= 2 } body)
        {
            return false;
        }

        var terminalStatement = body[^1];
        var hasTerminalResult = terminalStatement switch
        {
            ReturnStatement { Expression: not null } => true,
            IfStatement conditional => AlwaysReturnsTaskResult(conditional),
            _ => false,
        };
        if (!hasTerminalResult ||
            terminalStatement is ReturnStatement { Expression: AwaitExpression })
        {
            return false;
        }

        var awaits = new List<(int Index, AwaitExpression Expression, LocalVariableDeclarator Local)>();
        for (var index = 0; index < body.Count; index++)
        {
            if (body[index] is not LocalVariableDeclarationStatement declaration ||
                declaration.DeclaredType is AutoType { Const: true } ||
                declaration.Declarators is not
                [{ Initializer: AwaitExpression localAwait } localDeclarator])
            {
                continue;
            }
            awaits.Add((index, localAwait, localDeclarator));
        }
        var isGeneratedNestedAwaitSplit = body.Count == 2 && awaits.Count == 1 &&
            awaits[0].Local.Name.StartsWith(
                "__cx_async_nested_await_value", StringComparison.Ordinal) &&
            terminalStatement is ReturnStatement { Expression: { } nestedReturn } &&
            CoroutineSyntaxAnalysis.EnumerateAllExpressions(nestedReturn)
                .OfType<IdentifierExpression>()
                .Any(identifier => identifier.Identifier.ToString() == awaits[0].Local.Name);
        var isGeneratedNestedBinaryAwaitSplit = awaits.Count == 1 &&
            awaits[0].Local.Name.StartsWith(
                "__cx_async_nested_await_value", StringComparison.Ordinal) &&
            body.Take(awaits[0].Index)
                .OfType<LocalVariableDeclarationStatement>()
                .SelectMany(declaration => declaration.Declarators)
                .Any(declarator => declarator.Name.StartsWith(
                    "__cx_async_nested_left", StringComparison.Ordinal));
        var isGeneratedNestedCallAwaitSplit = awaits.Count == 1 &&
            awaits[0].Local.Name.StartsWith(
                "__cx_async_nested_await_value", StringComparison.Ordinal) &&
            terminalStatement is ReturnStatement
            {
                Expression: InvocationExpression nestedInvocation,
            } &&
            nestedInvocation.Arguments.OfType<IdentifierExpression>()
                .Any(identifier => identifier.Identifier.ToString() ==
                    awaits[0].Local.Name);
        if (awaits.Count == 0 || awaits[^1].Index >= body.Count - 1 ||
            (body.Count < 3 && !isGeneratedNestedAwaitSplit &&
             !(awaits.Count == 1 && awaits[0].Index == 0 &&
               awaits[0].Expression.Operand is InvocationExpression singleAwaitCall &&
               (IsTaskResultBranchSource(singleAwaitCall) ||
                GetValueTaskCallSourceType(singleAwaitCall) is not null))))
        {
            return false;
        }

        var prefix = body.Take(awaits[0].Index).ToArray();
        if (EnumerateStatements(prefix).Any(statement => statement is ReturnStatement) ||
            !CanRewriteTaskResultReturns(body.Skip(awaits[0].Index + 1)))
        {
            return false;
        }
        var names = function.Parameters.Select(parameter => parameter.Name)
            .Concat(EnumerateStatements(body)
                .OfType<LocalVariableDeclarationStatement>()
                .SelectMany(statement => statement.Declarators)
                .Select(declarator => declarator.Name))
            .ToHashSet(StringComparer.Ordinal);
        var prefixLocalTypes = new Dictionary<string, TypeBase>(StringComparer.Ordinal);
        foreach (var declaration in prefix.OfType<LocalVariableDeclarationStatement>())
        {
            foreach (var declarator in declaration.Declarators)
            {
                var localType = declarator.Type ?? declaration.DeclaredType;
                if (localType is AutoType &&
                    declarator.Initializer is IdentifierExpression initializerIdentifier)
                {
                    var initializerName = initializerIdentifier.Identifier.ToString();
                    var parameterType = function.Parameters.FirstOrDefault(parameter =>
                        parameter.Name == initializerName)?.ParameterType;
                    localType = parameterType is ConstType constant
                        ? constant.UnderlyingType
                        : parameterType ?? (prefixLocalTypes.TryGetValue(
                            initializerName, out var previousType)
                            ? previousType
                            : localType);
                }
                else if (localType is AutoType &&
                    declarator.Initializer is InvocationExpression initializerCall)
                {
                    localType = GetSupportedInvocationResultType(initializerCall) ??
                        localType;
                }
                prefixLocalTypes[declarator.Name] = localType;
            }
        }
        var prefixCaptureAliases = new Dictionary<string,
            (string Name, TypeBase Type, ExpressionBase Initializer)>(StringComparer.Ordinal);
        var pendingReturn = body[^1];
        var completionSourceType = NamedType.CreateWithTypeArguments(
            "System.Threading.Tasks.TaskCompletionSource", [taskType.TypeArguments[0]]);
        var completionName = CreateUniqueName(names, "__cx_async_completion");
        IdentifierExpression Completion() => new(new QualifiedIdentifier(completionName));
        InvocationExpression CompletionTask() => new(
            new MemberAccessExpression(Completion(), "GetTask"), []);
        var callbackType = new FunctionType(
            QualifiedIdentifier.Empty,
            BuiltInSystemTypes.Void,
            []);
        var callbackOperandAliases = new Dictionary<int,
            (string Name, TypeBase Type, ExpressionBase Operand)>();
        for (var awaitNumber = 1; awaitNumber < awaits.Count; awaitNumber++)
        {
            var awaitOperand = awaits[awaitNumber].Expression.Operand;
            if (awaitOperand is not IdentifierExpression operandIdentifier)
            {
                continue;
            }
            var parameter = function.Parameters.FirstOrDefault(candidate =>
                candidate.Name == operandIdentifier.Identifier.ToString());
            var parameterType = parameter?.ParameterType is ConstType constant
                ? constant.UnderlyingType
                : parameter?.ParameterType;
            if (parameter is not null && parameterType is NamedType valueTaskType &&
                IsValueTaskType(valueTaskType))
            {
                var aliasName = CreateUniqueName(
                    names, $"__cx_async_callback_operand_{awaitNumber}");
                callbackOperandAliases.Add(awaitNumber,
                    (aliasName, parameter.ParameterType,
                     CloneAwaitOperand(awaitOperand)));
            }
        }
        bool IsTaskParameterIdentifier(IdentifierExpression identifier)
        {
            var parameter = function.Parameters.FirstOrDefault(candidate =>
                candidate.Name == identifier.Identifier.ToString());
            var parameterType = parameter?.ParameterType is ConstType constant
                ? constant.UnderlyingType
                : parameter?.ParameterType;
            return parameterType is NamedType sourceType &&
                IsTaskType(sourceType) && sourceType.TypeArguments.Count == 1;
        }

        NamedType? GetSupportedInvocationResultType(
            InvocationExpression invocation)
        {
            if (invocation.ExplicitTypeArguments.Count != 0)
            {
                return null;
            }

            FunctionDeclaration[] candidates;
            if (invocation.Target is IdentifierExpression target &&
                target.Identifier.Parts.Length >= 1)
            {
                var targetParts = target.Identifier.Parts;
                var targetName = targetParts[^1];
                if (targetParts.Length == 1 &&
                    EnumerateStatements(function.Body)
                    .OfType<LocalFunctionDeclarationStatement>()
                    .Any(local => local.Function.Name == targetName))
                {
                    return null;
                }

                var candidateNames = GetCandidateQualifiedNames(
                    target.Identifier.ToString());
                candidates = availableFunctions.Where(candidate =>
                    candidate.ParentClassDeclaration is null &&
                    !candidate.IsExtension &&
                    candidateNames.Contains(candidate.FullName.ToString())).ToArray();
            }
            else if (invocation.Target is MemberAccessExpression qualifiedFunction &&
                     TryGetQualifiedSourceName(qualifiedFunction.Target) is { } namespaceName &&
                     GetCandidateQualifiedNames(
                         $"{namespaceName}.{qualifiedFunction.MemberName}").Any(name =>
                         availableFunctions.Any(candidate =>
                             candidate.ParentClassDeclaration is null &&
                             !candidate.IsExtension &&
                             candidate.FullName.ToString() == name)))
            {
                var candidateNames = GetCandidateQualifiedNames(
                    $"{namespaceName}.{qualifiedFunction.MemberName}");
                candidates = availableFunctions.Where(candidate =>
                    candidate.ParentClassDeclaration is null &&
                    !candidate.IsExtension &&
                    candidateNames.Contains(candidate.FullName.ToString())).ToArray();
            }
            else if (invocation.Target is MemberAccessExpression
                     { } memberTarget &&
                     TryGetQualifiedSourceName(memberTarget.Target) is { } typeSourceName)
            {
                var typeCandidateNames = GetCandidateQualifiedNames(typeSourceName);
                candidates = availableFunctions.Where(candidate =>
                    candidate.ParentClassDeclaration is { } owner &&
                    typeCandidateNames.Contains(owner.FullName.ToString()) &&
                    owner.GenericTypeNames.Length == 0 &&
                    candidate.Name == memberTarget.MemberName &&
                    candidate.IsStatic &&
                    !candidate.IsExtension).ToArray();
            }
            else
            {
                return null;
            }

            if (candidates.Length != 1 ||
                candidates[0].GenericTypeNames.Length != 0 ||
                candidates[0].IsGenericSpecialization ||
                candidates[0].HasUnclassifiedGenericTypeArguments ||
                candidates[0].Parameters.Count != invocation.Arguments.Count)
            {
                return null;
            }

            return candidates[0].ReturnType as NamedType;
        }

        bool IsTaskInvocationResultSource(InvocationExpression invocation)
        {
            var invocationReturnType = GetSupportedInvocationResultType(invocation);
            return invocationReturnType is not null &&
                IsTaskType(invocationReturnType) &&
                invocationReturnType.TypeArguments.Count == 1 &&
                invocationReturnType.TypeArguments[0].ToString() ==
                    taskType.TypeArguments[0].ToString();
        }

        NamedType? GetValueTaskCallSourceType(ExpressionBase source)
        {
            if (source is not InvocationExpression invocation ||
                GetSupportedInvocationResultType(invocation) is not
                    { } invocationReturnType ||
                !IsValueTaskType(invocationReturnType) ||
                invocationReturnType.TypeArguments.Count != 1 ||
                invocationReturnType.TypeArguments[0].ToString() !=
                    taskType.TypeArguments[0].ToString())
            {
                return null;
            }

            return invocationReturnType;
        }

        string? TryGetQualifiedSourceName(ExpressionBase expression) => expression switch
        {
            IdentifierExpression identifier when identifier.Identifier.Parts.Length == 1 =>
                identifier.Identifier.Parts[0],
            MemberAccessExpression access when
                TryGetQualifiedSourceName(access.Target) is { } qualifier =>
                $"{qualifier}.{access.MemberName}",
            _ => null,
        };

        HashSet<string> GetCandidateQualifiedNames(string sourceName)
        {
            var sourceIdentifier = new QualifiedIdentifier(sourceName.Split('.'));
            var candidates = new HashSet<QualifiedIdentifier>
            {
                sourceIdentifier,
                new QualifiedIdentifier(function.Namespace, sourceIdentifier),
            };
            foreach (var import in imports)
            {
                candidates.Add(new QualifiedIdentifier(import, sourceIdentifier));
            }
            return candidates.Select(candidate => candidate.ToString())
                .ToHashSet(StringComparer.Ordinal);
        }

        bool IsMatchingTaskResultTypeReference(TypeBase type)
        {
            var unwrappedType = type is ConstType constant
                ? constant.UnderlyingType
                : type;
            if (unwrappedType is not NamedType namedType ||
                namedType.TypeArguments.Count != 1 ||
                namedType.TypeArguments[0].ToString() !=
                    taskType.TypeArguments[0].ToString())
            {
                return false;
            }

            const string taskNamespace = "System.Threading.Tasks";
            return IsTaskType(namedType) ||
                namedType.Name == $"{taskNamespace}.Task" ||
                namedType.Name == "Task" &&
                    (function.Namespace.ToString() == taskNamespace ||
                     imports.Any(import => import.ToString() == taskNamespace));
        }

        bool IsTaskResultBranchSource(ExpressionBase source)
        {
            switch (source)
            {
                case IdentifierExpression sourceIdentifier:
                    var sourceParameter = function.Parameters.FirstOrDefault(parameter =>
                        parameter.Name == sourceIdentifier.Identifier.ToString());
                    var sourceType = sourceParameter?.ParameterType is ConstType sourceConstant
                        ? sourceConstant.UnderlyingType
                        : sourceParameter?.ParameterType;
                    return sourceType is NamedType namedSource &&
                        IsTaskType(namedSource) &&
                        namedSource.TypeArguments.Count == 1 &&
                        namedSource.TypeArguments[0].ToString() ==
                            taskType.TypeArguments[0].ToString();
                case InvocationExpression invocation:
                    return IsTaskInvocationResultSource(invocation);
                case NullCoalescingExpression coalescing:
                    return IsTaskResultBranchSource(coalescing.Left) &&
                        IsTaskResultBranchSource(coalescing.Right);
                case ConditionalExpression branch:
                    return IsTaskResultBranchSource(branch.WhenTrue) &&
                        IsTaskResultBranchSource(branch.WhenFalse);
                case SwitchExpression selection:
                    return selection.Arms.Count > 0 &&
                        selection.Arms.All(arm => IsTaskResultBranchSource(arm.Value)) &&
                        TryCloneValueTaskResultExpression(selection,
                            new Dictionary<string, string>(StringComparer.Ordinal),
                            out _);
                case CastExpression cast:
                    return IsMatchingTaskResultTypeReference(cast.TargetType) &&
                        IsTaskResultBranchSource(cast.Operand);
                default:
                    return false;
            }
        }

        if (awaits.Count == 1 &&
            awaits[0].Expression.Operand is InvocationExpression valueTaskCallSource &&
            GetValueTaskCallSourceType(valueTaskCallSource) is not null &&
            TryLowerSingleValueTaskResultLocal(
                function, taskType, GetValueTaskCallSourceType))
        {
            return true;
        }

        bool ContainsTaskCallSource(ExpressionBase source) => source switch
        {
            InvocationExpression => true,
            NullCoalescingExpression coalescing =>
                ContainsTaskCallSource(coalescing.Left) ||
                ContainsTaskCallSource(coalescing.Right),
            ConditionalExpression branch =>
                ContainsTaskCallSource(branch.WhenTrue) ||
                ContainsTaskCallSource(branch.WhenFalse),
            SwitchExpression selection => selection.Arms.Any(arm =>
                ContainsTaskCallSource(arm.Value)),
            CastExpression cast => ContainsTaskCallSource(cast.Operand),
            _ => false,
        };

        bool IsGeneratedConditionalTaskOperand(IdentifierExpression identifier)
        {
            var operandName = identifier.Identifier.ToString();
            if (!operandName.StartsWith(
                    "__cx_async_branch_task_", StringComparison.Ordinal))
            {
                return false;
            }

            var selectedTask = prefix.OfType<LocalVariableDeclarationStatement>()
                .SelectMany(declaration => declaration.Declarators)
                .FirstOrDefault(declarator => declarator.Name == operandName);
            if (selectedTask?.Initializer is not ConditionalExpression branch)
            {
                return false;
            }
            var containsCallSource = branch.WhenTrue is InvocationExpression ||
                branch.WhenFalse is InvocationExpression;
            return (!containsCallSource || awaits.Count == 1) &&
                IsTaskResultBranchSource(branch.WhenTrue) &&
                IsTaskResultBranchSource(branch.WhenFalse);
        }

        bool IsLaterTaskCallSource(ExpressionBase source) => source switch
        {
            InvocationExpression => IsTaskResultBranchSource(source),
            NullCoalescingExpression coalescing =>
                ContainsTaskCallSource(coalescing) &&
                IsTaskResultBranchSource(coalescing),
            ConditionalExpression branch =>
                ContainsTaskCallSource(branch) &&
                IsTaskResultBranchSource(branch),
            SwitchExpression selection =>
                ContainsTaskCallSource(selection) &&
                IsTaskResultBranchSource(selection),
            CastExpression cast =>
                ContainsTaskCallSource(cast) &&
                IsTaskResultBranchSource(cast),
            _ => false,
        };

        bool HasCloneableStatementsBetweenAwaitAndCall(
            int awaitIndex,
            int callSourceDeclarationIndex)
        {
            for (var statementIndex = awaitIndex + 1;
                statementIndex < callSourceDeclarationIndex;
                statementIndex++)
            {
                var statement = body[statementIndex];
                if (statement is not (ExpressionStatement or
                        LocalVariableDeclarationStatement or IfStatement or BlockStatement or
                        WhileStatement or DoWhileStatement or ForStatement or SwitchStatement or
                        TryStatement or ForeachStatement) ||
                    CoroutineSyntaxAnalysis.StatementContainsAwait(statement) ||
                    !TryCloneAwaitCallbackStatement(statement,
                        new Dictionary<string, string>(StringComparer.Ordinal), out _))
                {
                    return false;
                }
            }

            return true;
        }

        bool HasLaterTaskCallSourceShape()
        {
            if (awaits.Count != 2 || awaits[0].Index != prefix.Length ||
                awaits[1].Index != awaits[0].Index + 2 ||
                awaits[1].Index != body.Count - 2 ||
                body[awaits[0].Index + 1] is not LocalVariableDeclarationStatement
                    sourceDeclaration ||
                sourceDeclaration.Declarators.Count != 1 ||
                sourceDeclaration.Declarators[0].Initializer is not { } source ||
                awaits[1].Expression.Operand is not IdentifierExpression selectedSource ||
                sourceDeclaration.Declarators[0].Name !=
                    selectedSource.Identifier.ToString() ||
                body[^1] is not ReturnStatement)
            {
                return false;
            }

            return IsLaterTaskCallSource(source);
        }

        bool HasLaterMultiAwaitTaskCallSourceShape()
        {
            if (awaits.Count < 3 || awaits[0].Index != prefix.Length ||
                awaits[^1].Index != body.Count - 2 ||
                body[^1] is not ReturnStatement ||
                awaits[0].Expression.Operand is not IdentifierExpression firstSource ||
                !IsTaskParameterIdentifier(firstSource))
            {
                return false;
            }

            for (var awaitNumber = 1; awaitNumber < awaits.Count; awaitNumber++)
            {
                var callSourceDeclarationIndex = awaits[awaitNumber].Index - 1;
                if (callSourceDeclarationIndex <= awaits[awaitNumber - 1].Index ||
                    !HasCloneableStatementsBetweenAwaitAndCall(
                        awaits[awaitNumber - 1].Index, callSourceDeclarationIndex) ||
                    body[callSourceDeclarationIndex] is not
                        LocalVariableDeclarationStatement
                        { Declarators: [{ Initializer: { } laterSource }] } declaration ||
                    awaits[awaitNumber].Expression.Operand is not IdentifierExpression selected ||
                    declaration.Declarators[0].Name != selected.Identifier.ToString() ||
                    !IsLaterTaskCallSource(laterSource))
                {
                    return false;
                }
            }

            return true;
        }

        var hasLaterTaskCallSourceShape = HasLaterTaskCallSourceShape() ||
            HasLaterMultiAwaitTaskCallSourceShape();
        var hasSupportedFirstValueTaskPrefix = prefix.All(
            IsSupportedDirectTaskAwaitPrefixStatement);
        var hasFirstValueTaskCallThenTaskParameterAwait =
            hasSupportedFirstValueTaskPrefix &&
            body.Count == prefix.Length + 3 && awaits.Count == 2 &&
            awaits[0].Index == prefix.Length &&
            awaits[1].Index == prefix.Length + 1 &&
            awaits[0].Expression.Operand is InvocationExpression firstValueTaskCall &&
            GetValueTaskCallSourceType(firstValueTaskCall) is not null &&
            awaits[1].Expression.Operand is IdentifierExpression secondTaskParameter &&
            IsTaskResultBranchSource(secondTaskParameter) &&
            body[^1] is ReturnStatement;
        var hasFirstCallThenTaskParameterAwait = awaits.Count == 2 &&
            awaits[0].Index == prefix.Length &&
            awaits[0].Expression.Operand is InvocationExpression firstAwaitCall &&
            (IsTaskResultBranchSource(firstAwaitCall) ||
             hasFirstValueTaskCallThenTaskParameterAwait) &&
            awaits[1].Index == awaits[0].Index + 1 &&
            awaits[1].Expression.Operand is IdentifierExpression secondAwaitSource &&
            IsTaskParameterIdentifier(secondAwaitSource);
        bool HasFirstCallMultiAwaitCallSourceShape()
        {
            if (awaits.Count < 3 || awaits[0].Index != prefix.Length ||
                awaits[0].Expression.Operand is not InvocationExpression firstCallSource ||
                !IsTaskResultBranchSource(firstCallSource) ||
                awaits[^1].Index != body.Count - 2 ||
                body[^1] is not ReturnStatement)
            {
                return false;
            }

            for (var awaitNumber = 1; awaitNumber < awaits.Count; awaitNumber++)
            {
                var sourceDeclarationIndex = awaits[awaitNumber].Index - 1;
                if (sourceDeclarationIndex <= awaits[awaitNumber - 1].Index ||
                    !HasCloneableStatementsBetweenAwaitAndCall(
                        awaits[awaitNumber - 1].Index, sourceDeclarationIndex) ||
                    body[sourceDeclarationIndex] is not LocalVariableDeclarationStatement
                    {
                        Declarators: [{ Initializer: { } laterSource }],
                    } sourceDeclaration ||
                    awaits[awaitNumber].Expression.Operand is not IdentifierExpression selected ||
                    sourceDeclaration.Declarators[0].Name != selected.Identifier.ToString() ||
                    !IsLaterTaskCallSource(laterSource))
                {
                    return false;
                }
            }

            return true;
        }
        bool HasLaterTaskParameterLocalSourceShape()
        {
            if (awaits.Count != 2 || awaits[0].Index != prefix.Length ||
                awaits[1].Index != awaits[0].Index + 2 ||
                body[awaits[0].Index + 1] is not LocalVariableDeclarationStatement
                {
                    Declarators: [{ Initializer: IdentifierExpression laterSource }],
                } sourceDeclaration ||
                awaits[1].Expression.Operand is not IdentifierExpression selectedSource ||
                sourceDeclaration.Declarators[0].Name !=
                    selectedSource.Identifier.ToString())
            {
                return false;
            }

            return IsTaskParameterIdentifier(laterSource);
        }

        bool HasMultiAwaitTaskParameterLocalSourceShape()
        {
            if (awaits.Count < 3 || awaits[0].Index != prefix.Length ||
                awaits[^1].Index != body.Count - 2 ||
                body[^1] is not ReturnStatement)
            {
                return false;
            }

            var hasDirectTaskParameterLocal = false;
            for (var awaitNumber = 1; awaitNumber < awaits.Count; awaitNumber++)
            {
                var sourceDeclarationIndex = awaits[awaitNumber].Index - 1;
                if (sourceDeclarationIndex <= awaits[awaitNumber - 1].Index ||
                    !HasCloneableStatementsBetweenAwaitAndCall(
                        awaits[awaitNumber - 1].Index, sourceDeclarationIndex) ||
                    body[sourceDeclarationIndex] is not LocalVariableDeclarationStatement
                    {
                        Declarators: [{ Initializer: { } laterSource }],
                    } sourceDeclaration ||
                    awaits[awaitNumber].Expression.Operand is not IdentifierExpression selected ||
                    sourceDeclaration.Declarators[0].Name !=
                        selected.Identifier.ToString() ||
                    !IsTaskResultBranchSource(laterSource))
                {
                    return false;
                }

                if (laterSource is IdentifierExpression laterParameter &&
                    IsTaskParameterIdentifier(laterParameter))
                {
                    hasDirectTaskParameterLocal = true;
                }
            }

            return hasDirectTaskParameterLocal;
        }

        var hasDirectTaskOrValueTaskAwaitParameter = awaits.Any(awaited =>
        {
            if (awaited.Expression.Operand is not IdentifierExpression identifier)
            {
                return false;
            }
            var parameter = function.Parameters.FirstOrDefault(candidate =>
                candidate.Name == identifier.Identifier.ToString());
            var parameterType = parameter?.ParameterType is ConstType constant
                ? constant.UnderlyingType
                : parameter?.ParameterType;
            var isExplicitTaskLocal = prefixLocalTypes.TryGetValue(
                    identifier.Identifier.ToString(), out var localType) &&
                (localType is ConstType localConstant
                    ? localConstant.UnderlyingType
                    : localType) is NamedType localTaskType &&
                localTaskType.Name is "Task" or "System.Threading.Tasks.Task" &&
                localTaskType.TypeArguments.Count == 1 &&
                localTaskType.TypeArguments[0].ToString() ==
                    taskType.TypeArguments[0].ToString();
            var isBoundedNonGenericTaskParameterAwait =
                !HasGenericTypeContext(function) &&
                ((prefix.Length == 0 &&
                  ((awaits.Count == 2 && body.Count == 3 &&
                    awaits[0].Index == 0 && awaits[1].Index == 1) ||
                   (awaits.Count == 1 && body.Count == 2 &&
                    awaits[0].Index == 0 &&
                    body[^1] is ReturnStatement
                    {
                        Expression: IdentifierExpression returnedLocal,
                    } && returnedLocal.Identifier.ToString() ==
                        awaits[0].Local.Name))) ||
                 (prefix.Length > 0 && prefix.All(
                    IsSupportedDirectTaskAwaitPrefixStatement) &&
                    awaits.Count == 1 && awaits[0].Index == prefix.Length &&
                    body.Count == prefix.Length + 2 &&
                    body[^1] is ReturnStatement
                    {
                        Expression: { } returnedPrefixExpression,
                    } && CoroutineSyntaxAnalysis.EnumerateAllExpressions(
                        returnedPrefixExpression).OfType<IdentifierExpression>()
                        .Any(returnedIdentifier => returnedIdentifier.Identifier.ToString() ==
                            awaits[0].Local.Name)));
            var isTaskAwaitParameterForBoundedChain =
                (HasGenericTypeContext(function) ||
                    isBoundedNonGenericTaskParameterAwait) &&
                IsTaskParameterIdentifier(identifier) &&
                awaits.Any(awaited => awaited.Expression.Operand is IdentifierExpression source &&
                    source.Identifier.ToString() == identifier.Identifier.ToString());
            return isExplicitTaskLocal || isTaskAwaitParameterForBoundedChain ||
                (parameterType is NamedType sourceType &&
                (IsValueTaskType(sourceType) ||
                 (IsTaskType(sourceType) &&
                  ((hasLaterTaskCallSourceShape &&
                    awaited.Index == awaits[0].Index) ||
                   (hasFirstCallThenTaskParameterAwait &&
                    awaited.Index == awaits[1].Index) ||
                   isGeneratedNestedBinaryAwaitSplit ||
                    isGeneratedNestedCallAwaitSplit)))) ||
                IsGeneratedConditionalTaskOperand(identifier);
        });
        if (HasFirstCallMultiAwaitCallSourceShape() ||
            HasMultiAwaitTaskParameterLocalSourceShape() ||
            HasLaterTaskParameterLocalSourceShape())
        {
            hasDirectTaskOrValueTaskAwaitParameter = true;
        }
        if (hasDirectTaskOrValueTaskAwaitParameter)
        {
            foreach (var identifier in CoroutineSyntaxAnalysis
                .EnumerateBodyExpressions(body.Skip(awaits[0].Index))
                .OfType<IdentifierExpression>())
            {
                var localName = identifier.Identifier.ToString();
                if (prefixCaptureAliases.ContainsKey(localName))
                {
                    continue;
                }
                var localType = prefixLocalTypes.TryGetValue(localName, out var prefixType)
                    ? prefixType
                    : function.Parameters.FirstOrDefault(parameter =>
                        parameter.Name == localName)?.ParameterType;
                if (localType is null)
                {
                    continue;
                }
                var aliasName = CreateUniqueName(names,
                    $"__cx_async_callback_prefix_{localName}");
                prefixCaptureAliases.Add(localName,
                    (aliasName, localType, new IdentifierExpression(
                        new QualifiedIdentifier([.. identifier.Identifier.Parts]))));
            }
        }
        if (awaits.Count > 1 &&
            awaits[0].Expression.Operand is IdentifierExpression firstBranchOperand &&
            IsGeneratedConditionalTaskOperand(firstBranchOperand))
        {
            var firstBranchTaskName = firstBranchOperand.Identifier.ToString();
            var branchConditionName = prefix
                .OfType<LocalVariableDeclarationStatement>()
                .SelectMany(declaration => declaration.Declarators)
                .Where(declarator => declarator.Name == firstBranchTaskName)
                .Select(declarator => declarator.Initializer)
                .OfType<ConditionalExpression>()
                .Select(branch => (branch.Condition as IdentifierExpression)?
                    .Identifier.ToString())
                .FirstOrDefault();
            if (branchConditionName is not null)
            {
                foreach (var selectedTask in body
                    .OfType<LocalVariableDeclarationStatement>()
                    .SelectMany(declaration => declaration.Declarators)
                    .Where(declarator => declarator.Name.StartsWith(
                        "__cx_async_branch_task_", StringComparison.Ordinal) &&
                        declarator.Name != firstBranchTaskName))
                {
                    if (selectedTask.Initializer is not ConditionalExpression branch ||
                        branch.Condition is not IdentifierExpression branchCondition ||
                        branchCondition.Identifier.ToString() != branchConditionName)
                    {
                        continue;
                    }

                    foreach (var source in new[] { branch.WhenTrue, branch.WhenFalse }
                        .OfType<IdentifierExpression>())
                    {
                        var sourceName = source.Identifier.ToString();
                        if (prefixCaptureAliases.ContainsKey(sourceName))
                        {
                            continue;
                        }
                        var parameter = function.Parameters.FirstOrDefault(candidate =>
                            candidate.Name == sourceName);
                        if (parameter is null || !IsTaskParameterIdentifier(source))
                        {
                            continue;
                        }

                        var aliasName = CreateUniqueName(names,
                            $"__cx_async_callback_source_{sourceName}");
                        prefixCaptureAliases.Add(sourceName,
                            (aliasName, parameter.ParameterType,
                             CloneAwaitOperand(source)));
                    }
                }
            }
        }
        if (body[^1] is ReturnStatement { Expression: { } pendingReturnExpression } &&
            prefixCaptureAliases.Count != 0)
        {
            var substitutions = prefixCaptureAliases.ToDictionary(
                item => item.Key, item => item.Value.Name, StringComparer.Ordinal);
            if (!TryCloneValueTaskResultExpression(
                pendingReturnExpression, substitutions, out var clonedPendingReturn))
            {
                return false;
            }
            pendingReturn = new ReturnStatement(clonedPendingReturn)
            {
                SourceSpan = body[^1].SourceSpan,
            };
        }
        var useCallbackOnlyAliases = callbackOperandAliases.Count != 0 ||
            prefixCaptureAliases.Count != 0;

        var callbackRewriteFailed = false;
        List<StatementBase> LowerAwaitContinuation(
            int awaitNumber,
            string? parentCallbackCompletionName = null,
            IReadOnlyDictionary<string, string>? parentResultAliases = null)
        {
            var (awaitIndex, awaitExpression, awaitedLocal) = awaits[awaitNumber];
            var nextAwaitIndex = awaitNumber + 1 < awaits.Count
                ? awaits[awaitNumber + 1].Index
                : body.Count;
            var sourceName = CreateUniqueName(names, $"__cx_async_source_{awaitNumber}");
            var callbackSourceName = CreateUniqueName(
                names, $"__cx_async_callback_source_{awaitNumber}");
            var callbackCompletionName = CreateUniqueName(
                names, $"__cx_async_callback_completion_{awaitNumber}");
            var continuationName = CreateUniqueName(names, $"__cx_async_continuation_{awaitNumber}");
            IdentifierExpression Source() => new(new QualifiedIdentifier(sourceName));
            IdentifierExpression CallbackSource() => useCallbackOnlyAliases
                ? new(new QualifiedIdentifier(callbackSourceName))
                : Source();
            IdentifierExpression CallbackCompletion() => useCallbackOnlyAliases
                ? new(new QualifiedIdentifier(callbackCompletionName))
                : Completion();
            IdentifierExpression Continuation() =>
                new(new QualifiedIdentifier(continuationName));

            var callbackResultAliases = parentResultAliases is null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(parentResultAliases,
                    StringComparer.Ordinal);
            var callbackAliasName = awaitNumber + 1 < awaits.Count &&
                (IsAwaitedLocalMutatedLater(
                     awaitedLocal.Name, body.Skip(awaitIndex + 1)) ||
                 IsAwaitedLocalUsedLater(
                     awaitedLocal.Name, body.Skip(nextAwaitIndex)))
                ? CreateUniqueName(
                    names, $"__cx_async_callback_result_{awaitedLocal.Name}")
                : null;
            if (callbackAliasName is not null)
            {
                callbackResultAliases[awaitedLocal.Name] = callbackAliasName;
            }
            var callbackStatements = new List<StatementBase>
            {
                new LocalVariableDeclarationStatement(
                    ((LocalVariableDeclarationStatement)body[awaitIndex]).DeclaredType,
                    [new LocalVariableDeclarator(
                        awaitedLocal.Name,
                        new InvocationExpression(
                            new MemberAccessExpression(CallbackSource(), "GetResult"), []))]),
            };
            if (callbackAliasName is not null)
            {
                callbackStatements.Add(new LocalVariableDeclarationStatement(
                    new AutoType(false),
                    [new LocalVariableDeclarator(
                        callbackAliasName,
                        new IdentifierExpression(new QualifiedIdentifier(
                            awaitedLocal.Name)))]));
            }
            foreach (var statement in body.Skip(awaitIndex + 1)
                .Take(nextAwaitIndex - awaitIndex - 1))
            {
                var callbackStatement = statement;
                if (ReferenceEquals(statement, body[^1]))
                {
                    callbackStatement = pendingReturn;
                }
                var callbackStatementAliases = new Dictionary<string, string>(
                    callbackResultAliases, StringComparer.Ordinal);
                foreach (var (localName, alias) in prefixCaptureAliases)
                {
                    callbackStatementAliases[localName] = alias.Name;
                }
                if (callbackStatementAliases.Count != 0 &&
                    !TryCloneAwaitCallbackStatement(callbackStatement,
                        callbackStatementAliases, out callbackStatement))
                {
                    callbackRewriteFailed = true;
                }
                callbackStatements.AddRange(
                    RewriteTaskResultReturns(callbackStatement,
                        useCallbackOnlyAliases ? callbackCompletionName : completionName));
            }
            if (awaitNumber + 1 < awaits.Count)
            {
                callbackStatements.AddRange(LowerAwaitContinuation(
                    awaitNumber + 1,
                    useCallbackOnlyAliases ? callbackCompletionName : null,
                    callbackResultAliases));
            }

            var cancellationExceptionName = CreateUniqueName(
                names, $"__cx_async_canceled_{awaitNumber}");
            var canceledClause = new CatchClause(
                new NamedType("System.Threading.Tasks.OperationCanceledException", []),
                cancellationExceptionName,
                null,
                new BlockStatement([new ExpressionStatement(new InvocationExpression(
                    new MemberAccessExpression(CallbackCompletion(), "SetCanceled"), []))]));
            var exceptionName = CreateUniqueName(names, $"__cx_async_exception_{awaitNumber}");
            var catchClause = new CatchClause(
                new NamedType("System.Exception", []),
                exceptionName,
                null,
                new BlockStatement([
                    new ExpressionStatement(new InvocationExpression(
                        new MemberAccessExpression(CallbackCompletion(), "SetException"),
                        [new IdentifierExpression(new QualifiedIdentifier(exceptionName))])),
                ]));
            var callback = new LambdaExpression(
                [],
                null,
                [new TryStatement(new BlockStatement(callbackStatements),
                    [canceledClause, catchClause], null)])
            {
                SourceSpan = awaitExpression.SourceSpan ?? function.SourceSpan,
            };

            var sourceOperand = callbackOperandAliases.TryGetValue(
                awaitNumber, out var operandAlias)
                ? new InvocationExpression(
                    new MemberAccessExpression(
                        new IdentifierExpression(new QualifiedIdentifier(operandAlias.Name)),
                        "AsTask"), [])
                : awaitExpression.Operand is IdentifierExpression sourceIdentifier &&
                  prefixCaptureAliases.TryGetValue(
                      sourceIdentifier.Identifier.ToString(), out var sourceAlias)
                    ? new IdentifierExpression(new QualifiedIdentifier(sourceAlias.Name))
                : hasFirstValueTaskCallThenTaskParameterAwait && awaitNumber == 0
                    ? new InvocationExpression(new MemberAccessExpression(
                        CloneAwaitOperand(awaitExpression.Operand), "AsTask"), [])
                : NormalizeParameterValueTaskAwaitOperand(
                    function, CloneAwaitOperand(awaitExpression.Operand));
            var completionForCallback = parentCallbackCompletionName is null
                ? Completion()
                : new IdentifierExpression(
                    new QualifiedIdentifier(parentCallbackCompletionName));
            var loweredAwait = new List<StatementBase>
            {
                new LocalVariableDeclarationStatement(new AutoType(false),
                [new LocalVariableDeclarator(sourceName, sourceOperand)]),
            };
            if (useCallbackOnlyAliases)
            {
                loweredAwait.AddRange([
                    new LocalVariableDeclarationStatement(new AutoType(false),
                    [new LocalVariableDeclarator(callbackSourceName, Source())]),
                    new LocalVariableDeclarationStatement(completionSourceType,
                    [new LocalVariableDeclarator(
                        callbackCompletionName, completionForCallback)]),
                ]);
            }
            loweredAwait.AddRange([
                new LocalVariableDeclarationStatement(callbackType,
                [new LocalVariableDeclarator(continuationName, callback)]),
                new IfStatement(
                    new MemberAccessExpression(Source(), "IsCompleted"),
                    new ExpressionStatement(new InvocationExpression(Continuation(), [])
                    {
                        SourceSpan = awaitExpression.SourceSpan ?? function.SourceSpan,
                    }),
                    new ExpressionStatement(new InvocationExpression(
                        new MemberAccessExpression(Source(), "OnCompleted"),
                        [Continuation()]))),
            ]);
            return loweredAwait;
        }

        var loweredBody = new List<StatementBase>();
        loweredBody.AddRange(prefix);
        loweredBody.Add(new LocalVariableDeclarationStatement(completionSourceType,
            [new LocalVariableDeclarator(
                completionName,
                new ObjectCreationExpression(completionSourceType, []))]));
        if (useCallbackOnlyAliases)
        {
            loweredBody.AddRange(callbackOperandAliases.Values.Select(alias =>
                (StatementBase)new LocalVariableDeclarationStatement(alias.Type,
                [new LocalVariableDeclarator(alias.Name, alias.Operand)])));
            loweredBody.AddRange(prefixCaptureAliases.Values.Select(alias =>
                (StatementBase)new LocalVariableDeclarationStatement(alias.Type,
                [new LocalVariableDeclarator(alias.Name, alias.Initializer)])));
        }
        loweredBody.AddRange(LowerAwaitContinuation(0));
        if (callbackRewriteFailed)
        {
            throw new CompilationErrorException(
                "A mutated await-local crosses a statement that cannot be cloned into the continuation callback.",
                function.SourceSpan);
        }
        loweredBody.Add(new ReturnStatement(
            CompletionTask()));
        var pendingContinuationBody = loweredBody.Skip(prefix.Length).ToArray();
        StatementBase? allInline = awaits.Count == 1 &&
            awaits[0].Expression.Operand is InvocationExpression firstCallSource &&
            IsTaskResultBranchSource(firstCallSource)
                ? CreateAllInlineAwaitLocalBranchWithFirstCallSource(
                    taskType, body, awaits, prefix, pendingContinuationBody,
                    CreateUniqueName(names, "__cx_async_first_task_source"),
                    firstCallSource)
                : CreateAllInlineAwaitLocalBranch(
                    function, taskType, body, awaits, prefix,
                    pendingContinuationBody, IsTaskResultBranchSource,
                    GetSupportedInvocationResultType);
        bool HasOptimizableTwoAwaitCallSourceShape()
        {
            if (awaits.Count != 2 || awaits[0].Index != prefix.Length ||
                awaits[1].Index != body.Count - 2 ||
                body[^1] is not ReturnStatement ||
                awaits[1].Expression.Operand is not IdentifierExpression secondSource)
            {
                return false;
            }

            if (awaits[0].Expression.Operand is InvocationExpression firstCall &&
                awaits[1].Index == awaits[0].Index + 1)
            {
                return (IsTaskResultBranchSource(firstCall) ||
                    hasFirstValueTaskCallThenTaskParameterAwait) &&
                    IsTaskResultBranchSource(awaits[1].Expression.Operand);
            }

            return awaits[1].Index == awaits[0].Index + 2 &&
                body[awaits[0].Index + 1] is LocalVariableDeclarationStatement
                {
                    Declarators: [{ Initializer: { } laterSource }],
                } sourceDeclaration &&
                sourceDeclaration.Declarators[0].Name ==
                    secondSource.Identifier.ToString() &&
                IsTaskResultBranchSource(laterSource);
        }

        if (allInline is null && HasOptimizableTwoAwaitCallSourceShape())
        {
            var pendingSecondContinuation = new List<StatementBase>
            {
                new LocalVariableDeclarationStatement(completionSourceType,
                [new LocalVariableDeclarator(
                    completionName,
                    new ObjectCreationExpression(completionSourceType, []))]),
            };
            if (useCallbackOnlyAliases)
            {
                pendingSecondContinuation.AddRange(callbackOperandAliases.Values.Select(alias =>
                    (StatementBase)new LocalVariableDeclarationStatement(alias.Type,
                    [new LocalVariableDeclarator(alias.Name, alias.Operand)])));
                pendingSecondContinuation.AddRange(prefixCaptureAliases.Values.Select(alias =>
                    (StatementBase)new LocalVariableDeclarationStatement(alias.Type,
                    [new LocalVariableDeclarator(alias.Name, alias.Initializer)])));
            }
            var originalPendingReturn = pendingReturn;
            if (body[^1] is not ReturnStatement { Expression: { } pendingExpression } returnStatement ||
                !TryCloneValueTaskResultExpression(
                    pendingExpression,
                    new Dictionary<string, string>(StringComparer.Ordinal),
                    out var clonedPendingExpression))
            {
                pendingSecondContinuation.Clear();
                allInline = null;
            }
            else
            {
                pendingReturn = new ReturnStatement(clonedPendingExpression)
                {
                    SourceSpan = returnStatement.SourceSpan,
                };
                var pendingReturnLocalNames = CoroutineSyntaxAnalysis
                    .EnumerateAllExpressions(pendingExpression)
                    .OfType<IdentifierExpression>()
                    .Select(identifier => identifier.Identifier.ToString())
                    .ToHashSet(StringComparer.Ordinal);
                var deferredResultAliases = new Dictionary<string, string>(
                    StringComparer.Ordinal);
                foreach (var earlierAwait in awaits.Take(1))
                {
                    if (!pendingReturnLocalNames.Contains(earlierAwait.Local.Name))
                    {
                        continue;
                    }

                    var resultDeclaration =
                        (LocalVariableDeclarationStatement)body[earlierAwait.Index];
                    var aliasName = CreateUniqueName(names,
                        $"__cx_async_callback_result_{earlierAwait.Local.Name}");
                    deferredResultAliases.Add(earlierAwait.Local.Name, aliasName);
                    pendingSecondContinuation.Add(
                        new LocalVariableDeclarationStatement(
                            resultDeclaration.DeclaredType,
                            [new LocalVariableDeclarator(aliasName,
                                new IdentifierExpression(new QualifiedIdentifier(
                                    earlierAwait.Local.Name)))]));
                }
                pendingSecondContinuation.AddRange(LowerAwaitContinuation(
                    1, parentResultAliases: deferredResultAliases));
                var deferredContinuationSpan =
                    awaits[1].Expression.SourceSpan ?? function.SourceSpan;
                var deferredContinuationIdentity = deferredContinuationSpan is { } span
                    ? $"{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                        System.Text.Encoding.UTF8.GetBytes(span.FilePath)))[..10]}_" +
                      $"{span.StartLine}_{span.StartColumn}"
                    : function.Name;
                foreach (var lambda in CoroutineSyntaxAnalysis
                    .EnumerateBodyExpressions(pendingSecondContinuation)
                    .OfType<LambdaExpression>()
                    .Where(lambda => lambda.SourceSpan == deferredContinuationSpan))
                {
                    lambda.GeneratedCName = CreateUniqueName(names,
                        $"__cx_async_deferred_{deferredContinuationIdentity}");
                }
                pendingSecondContinuation.Add(new ReturnStatement(
                    CompletionTask()));
                pendingReturn = originalPendingReturn;
                var firstCallSourceName = awaits[0].Expression.Operand is InvocationExpression
                    ? CreateUniqueName(names, "__cx_async_first_task_source")
                    : null;
                allInline = CreateAllInlineAwaitLocalBranchWithLaterSource(
                    taskType, body, awaits, prefix,
                    pendingContinuationBody, pendingSecondContinuation,
                    IsTaskResultBranchSource, firstCallSourceName,
                    hasFirstValueTaskCallThenTaskParameterAwait);
            }
        }
        if (allInline is null && awaits.Count >= 3 &&
            awaits[0].Index == prefix.Length &&
            body[^1] is ReturnStatement)
        {
            var pendingContinuationCreationFailed = false;
            IEnumerable<LambdaExpression> EnumerateContinuationLambdasFromBody(
                IEnumerable<StatementBase> statements)
            {
                foreach (var lambda in CoroutineSyntaxAnalysis
                    .EnumerateBodyExpressions(statements)
                    .OfType<LambdaExpression>())
                {
                    yield return lambda;
                    if (lambda.StatementBody is { } nestedBody)
                    {
                        foreach (var nested in EnumerateContinuationLambdasFromBody(nestedBody))
                        {
                            yield return nested;
                        }
                    }
                    if (lambda.ExpressionBody is { } nestedExpression)
                    {
                        foreach (var nested in EnumerateContinuationLambdasFromExpression(nestedExpression))
                        {
                            yield return nested;
                        }
                    }
                }
            }
            IEnumerable<LambdaExpression> EnumerateContinuationLambdasFromExpression(
                ExpressionBase expression)
            {
                foreach (var lambda in CoroutineSyntaxAnalysis
                    .EnumerateAllExpressions(expression)
                    .OfType<LambdaExpression>())
                {
                    yield return lambda;
                    if (lambda.StatementBody is { } nestedBody)
                    {
                        foreach (var nested in EnumerateContinuationLambdasFromBody(nestedBody))
                        {
                            yield return nested;
                        }
                    }
                    if (lambda.ExpressionBody is { } nestedExpression)
                    {
                        foreach (var nested in EnumerateContinuationLambdasFromExpression(nestedExpression))
                        {
                            yield return nested;
                        }
                    }
                }
            }
            void AssignDeferredContinuationNames(
                IEnumerable<StatementBase> statements,
                int awaitNumber)
            {
                var continuationSpan =
                    awaits[awaitNumber].Expression.SourceSpan ?? function.SourceSpan;
                var continuationIdentity = continuationSpan is { } span
                    ? $"{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                        System.Text.Encoding.UTF8.GetBytes(span.FilePath)))[..10]}_" +
                      $"{span.StartLine}_{span.StartColumn}_{awaitNumber}"
                    : $"{function.Name}_{awaitNumber}";
                var deferredLambdaIndex = 0;
                foreach (var lambda in EnumerateContinuationLambdasFromBody(statements))
                {
                    lambda.GeneratedCName = CreateUniqueName(names,
                        $"__cx_async_deferred_{continuationIdentity}_" +
                        $"{deferredLambdaIndex++}");
                }
            }

            AssignDeferredContinuationNames(pendingContinuationBody, 0);
            List<StatementBase> CreatePendingContinuation(int awaitNumber)
            {
                var pendingContinuation = new List<StatementBase>
                {
                    new LocalVariableDeclarationStatement(completionSourceType,
                    [new LocalVariableDeclarator(
                        completionName,
                        new ObjectCreationExpression(completionSourceType, []))]),
                };
                if (useCallbackOnlyAliases)
                {
                    pendingContinuation.AddRange(callbackOperandAliases.Values.Select(alias =>
                        (StatementBase)new LocalVariableDeclarationStatement(alias.Type,
                        [new LocalVariableDeclarator(alias.Name, alias.Operand)])));
                    pendingContinuation.AddRange(prefixCaptureAliases.Values.Select(alias =>
                        (StatementBase)new LocalVariableDeclarationStatement(alias.Type,
                        [new LocalVariableDeclarator(alias.Name, alias.Initializer)])));
                }

                var originalPendingReturn = pendingReturn;
                if (body[^1] is not ReturnStatement { Expression: { } pendingExpression }
                        returnStatement ||
                    !TryCloneValueTaskResultExpression(
                        pendingExpression,
                        new Dictionary<string, string>(StringComparer.Ordinal),
                        out var clonedPendingExpression))
                {
                    pendingContinuation.Clear();
                    pendingContinuationCreationFailed = true;
                    return pendingContinuation;
                }

                pendingReturn = new ReturnStatement(clonedPendingExpression)
                {
                    SourceSpan = returnStatement.SourceSpan,
                };
                var pendingReturnLocalNames = CoroutineSyntaxAnalysis
                    .EnumerateAllExpressions(pendingExpression)
                    .OfType<IdentifierExpression>()
                    .Select(identifier => identifier.Identifier.ToString())
                    .ToHashSet(StringComparer.Ordinal);
                var deferredResultAliases = new Dictionary<string, string>(
                    StringComparer.Ordinal);
                foreach (var earlierAwait in awaits.Take(awaitNumber))
                {
                    if (!pendingReturnLocalNames.Contains(earlierAwait.Local.Name))
                    {
                        continue;
                    }

                    var resultDeclaration =
                        (LocalVariableDeclarationStatement)body[earlierAwait.Index];
                    var aliasName = CreateUniqueName(names,
                        $"__cx_async_callback_result_{earlierAwait.Local.Name}");
                    deferredResultAliases.Add(earlierAwait.Local.Name, aliasName);
                    pendingContinuation.Add(
                        new LocalVariableDeclarationStatement(
                            resultDeclaration.DeclaredType,
                            [new LocalVariableDeclarator(aliasName,
                                new IdentifierExpression(new QualifiedIdentifier(
                                    earlierAwait.Local.Name)))]));
                }
                pendingContinuation.AddRange(LowerAwaitContinuation(
                    awaitNumber, parentResultAliases: deferredResultAliases));

                AssignDeferredContinuationNames(pendingContinuation, awaitNumber);
                pendingContinuation.Add(new ReturnStatement(
                CompletionTask()));
                pendingReturn = originalPendingReturn;
                return pendingContinuation;
            }

            allInline = CreateAllInlineAwaitLocalBranchWithLaterCallSources(
                taskType, body, awaits, prefix, pendingContinuationBody,
                CreatePendingContinuation, IsTaskResultBranchSource,
                awaits[0].Expression.Operand is InvocationExpression
                    ? CreateUniqueName(names, "__cx_async_first_task_source")
                    : null);
            if (pendingContinuationCreationFailed)
            {
                allInline = null;
            }
        }
        function.SetBody(allInline is null
            ? loweredBody
            : [.. prefix, allInline]);
        return true;
    }

    private static bool IsGenericAwaitLocalChain(
        FunctionDeclaration function,
        NamedType taskType)
    {
        if (!HasGenericTypeContext(function) ||
            taskType.TypeArguments.Count != 1 || function.Body is null)
        {
            return false;
        }

        var awaitedValues = function.Body
            .OfType<LocalVariableDeclarationStatement>()
            .Where(declaration => declaration.Declarators is
                [{ Initializer: AwaitExpression }])
            .Select(declaration => (AwaitExpression)declaration.Declarators[0].Initializer!)
            .ToArray();
        if (awaitedValues.Length < 2)
        {
            return false;
        }

        var resultType = taskType.TypeArguments[0];
        return awaitedValues.All(awaited =>
        {
            TypeBase? sourceType = awaited.Operand.InferredType;
            if (sourceType is null && awaited.Operand is IdentifierExpression identifier)
            {
                var name = identifier.Identifier.ToString();
                sourceType = function.Parameters.FirstOrDefault(parameter =>
                        parameter.Name == name)?.ParameterType ??
                    function.Body.OfType<LocalVariableDeclarationStatement>()
                        .FirstOrDefault(declaration => declaration.Declarators.Any(
                            declarator => declarator.Name == name))?.DeclaredType;
            }
            sourceType = sourceType is ConstType constant
                ? constant.UnderlyingType
                : sourceType;
            return sourceType is NamedType awaitableType &&
                (IsTaskType(awaitableType) || IsValueTaskType(awaitableType)) &&
                awaitableType.TypeArguments.Count == 1 &&
                awaitableType.TypeArguments[0].FullName == resultType.FullName;
        });
    }

    private static bool HasGenericTypeContext(FunctionDeclaration function) =>
        function.GenericTypeNames.Length != 0 ||
        function.ParentClassDeclaration?.GenericTypeNames.Length > 0;

    private static bool IsSupportedDirectTaskAwaitPrefixStatement(
        StatementBase statement)
    {
        if (CoroutineSyntaxAnalysis.StatementContainsAwait(statement))
        {
            return false;
        }

        return statement switch
        {
            LocalVariableDeclarationStatement
            {
                Declarators.Count: > 0,
            } declaration => declaration.DeclaredType is not AutoType ||
                declaration.Declarators.All(
                    declarator => declarator.Initializer is not null),
            ExpressionStatement => true,
            BlockStatement block => block.Statements.All(
                IsSupportedDirectTaskAwaitPrefixStatement),
            IfStatement conditional =>
                IsSupportedDirectTaskAwaitPrefixStatement(conditional.ThenStatement) &&
                (conditional.ElseStatement is null ||
                 IsSupportedDirectTaskAwaitPrefixStatement(conditional.ElseStatement)),
            _ => false,
        };
    }

    private static IfStatement? CreateAllInlineAwaitLocalBranch(
        FunctionDeclaration function,
        NamedType taskType,
        IReadOnlyList<StatementBase> body,
        IReadOnlyList<(int Index, AwaitExpression Expression, LocalVariableDeclarator Local)> awaits,
        IReadOnlyList<StatementBase> prefix,
        IReadOnlyList<StatementBase> pendingBody,
        Func<ExpressionBase, bool> isTaskResultBranchSource,
        Func<InvocationExpression, NamedType?> getSupportedInvocationResultType)
    {
        var terminalStatement = body.Count == 0 ? null : body[^1];
        if (awaits.Count == 0 || awaits[0].Index != prefix.Count ||
            body.Count < prefix.Count + awaits.Count + 1 ||
            taskType.TypeArguments.Count != 1 ||
            terminalStatement is null ||
            !TryCreateAllInlineTaskResultReturns(
                terminalStatement, taskType, out var inlineTerminalStatement))
        {
            return null;
        }

        // A later conditional Task source may contain calls. Its local is
        // deliberately materialized only after the preceding await completes,
        // so it cannot participate in the outer all-ready guard. Let the
        // continuation lowering below preserve that source order instead of
        // emitting an out-of-scope or eagerly evaluated readiness check.
        if (awaits.Skip(1).Any(awaited =>
                awaited.Expression.Operand is IdentifierExpression identifier &&
                body.Take(awaited.Index)
                    .OfType<LocalVariableDeclarationStatement>()
                    .SelectMany(declaration => declaration.Declarators)
                    .Any(declarator => declarator.Name == identifier.Identifier.ToString() &&
                        declarator.Initializer is ConditionalExpression branch &&
                        (branch.WhenTrue is InvocationExpression ||
                         branch.WhenFalse is InvocationExpression))))
        {
            return null;
        }

        var operands = new List<(
            ExpressionBase Operand, ExpressionBase CompletionOperand,
            string ParameterName, bool IsValueTask)>();
        string? generatedBranchConditionName = null;
        TypeBase? ResolvePrefixLocalType(string localName)
        {
            var local = prefix
                .OfType<LocalVariableDeclarationStatement>()
                .SelectMany(declaration => declaration.Declarators.Select(declarator =>
                    (declarator, declaration.DeclaredType)))
                .FirstOrDefault(candidate => candidate.declarator.Name == localName);
            if (local.declarator is null)
            {
                return null;
            }

            var localType = local.declarator.Type ?? local.DeclaredType;
            if (localType is AutoType &&
                local.declarator.Initializer is IdentifierExpression initializer)
            {
                var initializerName = initializer.Identifier.ToString();
                var parameterType = function.Parameters.FirstOrDefault(parameter =>
                    parameter.Name == initializerName)?.ParameterType;
                localType = parameterType is ConstType constant
                    ? constant.UnderlyingType
                    : parameterType ?? ResolvePrefixLocalType(initializerName) ?? localType;
            }
            else if (localType is AutoType &&
                local.declarator.Initializer is InvocationExpression initializerCall)
            {
                localType = getSupportedInvocationResultType(initializerCall) ?? localType;
            }
            return localType;
        }

        foreach (var (awaitIndex, awaitExpression, awaitedLocal) in awaits)
        {
            if (awaitExpression.Operand is not IdentifierExpression identifier)
            {
                return null;
            }

            var parameterName = identifier.Identifier.ToString();
            var parameterType = function.Parameters.FirstOrDefault(parameter =>
                parameter.Name == parameterName)?.ParameterType;
            parameterType = parameterType is ConstType constant
                ? constant.UnderlyingType
                : parameterType;
            // A Task<T> local has already evaluated its initializer in the
            // prefix. Resolve the narrow inferred-identifier form as well, so
            // readiness reads the materialized local without re-running it.
            var isTaskLocalAwaitSource = ResolvePrefixLocalType(parameterName) is
                    NamedType localTaskType &&
                localTaskType.Name is "Task" or "System.Threading.Tasks.Task" &&
                localTaskType.TypeArguments.Count == 1 &&
                localTaskType.TypeArguments[0].ToString() ==
                    taskType.TypeArguments[0].ToString();
            var isBoundedNonGenericTaskParameterAwait =
                !HasGenericTypeContext(function) &&
                ((prefix.Count == 0 &&
                  ((awaits.Count == 2 && body.Count == 3 &&
                    awaits[0].Index == 0 && awaits[1].Index == 1) ||
                   (awaits.Count == 1 && body.Count == 2 &&
                    awaits[0].Index == 0 &&
                    terminalStatement is ReturnStatement
                    {
                        Expression: IdentifierExpression returnedLocal,
                    } && returnedLocal.Identifier.ToString() == awaitedLocal.Name))) ||
                 (prefix.Count > 0 && prefix.All(
                    IsSupportedDirectTaskAwaitPrefixStatement) &&
                    awaits.Count == 1 && awaits[0].Index == prefix.Count &&
                    body.Count == prefix.Count + 2 &&
                    terminalStatement is ReturnStatement
                    {
                        Expression: { } returnedPrefixExpression,
                    } && CoroutineSyntaxAnalysis.EnumerateAllExpressions(
                        returnedPrefixExpression).OfType<IdentifierExpression>()
                        .Any(returnedIdentifier => returnedIdentifier.Identifier.ToString() ==
                            awaitedLocal.Name))) &&
                function.Parameters.Any(parameter =>
                    parameter.Name == parameterName);
            var isTaskParameterAwaitSource =
                (HasGenericTypeContext(function) ||
                    isBoundedNonGenericTaskParameterAwait) &&
                function.Parameters.Any(parameter =>
                parameter.Name == parameterName) &&
                parameterType is NamedType taskParameterType &&
                IsTaskType(taskParameterType) &&
                taskParameterType.TypeArguments.Count == 1 &&
                taskParameterType.TypeArguments[0].ToString() ==
                    taskType.TypeArguments[0].ToString();
            var isGeneratedConditionalBranchTask = false;
            ExpressionBase completionOperand = awaitExpression.Operand;
            if (awaitedLocal.Name.StartsWith(
                    "__cx_async_branch_result_", StringComparison.Ordinal) &&
                parameterName.StartsWith(
                    "__cx_async_branch_task_", StringComparison.Ordinal))
            {
                var selectedTask = body.Take(awaitIndex)
                    .OfType<LocalVariableDeclarationStatement>()
                    .SelectMany(declaration => declaration.Declarators.Select(
                        declarator => (declaration, declarator)))
                    .FirstOrDefault(item => item.declarator.Name == parameterName);
                if (selectedTask.declaration is not null &&
                    selectedTask.declarator.Initializer is ConditionalExpression branch &&
                    branch.Condition is IdentifierExpression branchCondition &&
                    branch.WhenTrue is not null &&
                    branch.WhenFalse is not null)
                {
                    if (generatedBranchConditionName is not null &&
                        generatedBranchConditionName !=
                            branchCondition.Identifier.ToString())
                    {
                        return null;
                    }

                    isGeneratedConditionalBranchTask =
                        isTaskResultBranchSource(branch.WhenTrue) &&
                        isTaskResultBranchSource(branch.WhenFalse);
                    if (isGeneratedConditionalBranchTask)
                    {
                        generatedBranchConditionName =
                            branchCondition.Identifier.ToString();
                        if (branch.WhenTrue is InvocationExpression ||
                            branch.WhenFalse is InvocationExpression)
                        {
                            // The selected branch expression has already run in
                            // the generated local initializer. Test that local
                            // so call sources are never evaluated a second time.
                            completionOperand = awaitExpression.Operand;
                        }
                        else
                        {
                            // Preserve the existing direct-parameter branch
                            // checks, including later sources that are only
                            // initialized after an earlier await.
                            completionOperand = branch;
                        }
                    }
                }
            }
            var isGeneratedTaskBinaryAwait =
                awaitedLocal.Name.StartsWith(
                    "__cx_async_nested_await_value", StringComparison.Ordinal) &&
                prefix.OfType<LocalVariableDeclarationStatement>()
                    .SelectMany(declaration => declaration.Declarators)
                    .Any(declarator => declarator.Name.StartsWith(
                        "__cx_async_nested_left", StringComparison.Ordinal)) &&
                parameterType is NamedType taskSourceType &&
                IsTaskType(taskSourceType);
            var isGeneratedTaskCallAwait =
                awaitedLocal.Name.StartsWith(
                    "__cx_async_nested_await_value", StringComparison.Ordinal) &&
                terminalStatement is ReturnStatement
                {
                    Expression: InvocationExpression nestedInvocation,
                } &&
                nestedInvocation.Arguments.OfType<IdentifierExpression>()
                    .Any(identifier => identifier.Identifier.ToString() ==
                        awaitedLocal.Name) &&
                parameterType is NamedType callTaskSourceType &&
                IsTaskType(callTaskSourceType);
            if (!isGeneratedConditionalBranchTask && !isTaskLocalAwaitSource &&
                !isTaskParameterAwaitSource &&
                (parameterType is not NamedType sourceType ||
                 (!IsValueTaskType(sourceType) && !isGeneratedTaskBinaryAwait &&
                  !isGeneratedTaskCallAwait)))
            {
                return null;
            }
            operands.Add((awaitExpression.Operand, completionOperand, parameterName,
                parameterType is NamedType namedParameterType &&
                    IsValueTaskType(namedParameterType)));
        }

        if (operands.Select(operand => operand.ParameterName)
            .Distinct(StringComparer.Ordinal).Count() != operands.Count)
        {
            return null;
        }

        ExpressionBase condition = new MemberAccessExpression(
            operands[0].CompletionOperand, "IsCompleted");
        foreach (var operand in operands.Skip(1))
        {
            condition = new BinaryExpression(
                condition, "&&",
                new MemberAccessExpression(
                    operand.CompletionOperand, "IsCompleted"));
        }

        var awaitByIndex = awaits.ToDictionary(awaited => awaited.Index);
        var inlineStatements = new List<StatementBase>();
        for (var index = awaits[0].Index; index < body.Count - 1; index++)
        {
            if (awaitByIndex.TryGetValue(index, out var awaited))
            {
                var result = new InvocationExpression(
                    new MemberAccessExpression(
                        awaited.Expression.Operand, "GetResult"), []);
                var localType = ((LocalVariableDeclarationStatement)
                    body[awaited.Index]).DeclaredType;
                inlineStatements.Add(new LocalVariableDeclarationStatement(
                    localType,
                    [new LocalVariableDeclarator(awaited.Local.Name, result)]));
            }
            else
            {
                if (CoroutineSyntaxAnalysis.StatementContainsAwait(body[index]))
                {
                    return null;
                }
                inlineStatements.Add(body[index]);
            }
        }

        inlineStatements.Add(inlineTerminalStatement);
        return new IfStatement(
            condition,
            new BlockStatement(inlineStatements),
            new BlockStatement(pendingBody));
    }

    private static StatementBase? CreateAllInlineAwaitLocalBranchWithFirstCallSource(
        NamedType taskType,
        IReadOnlyList<StatementBase> body,
        IReadOnlyList<(int Index, AwaitExpression Expression, LocalVariableDeclarator Local)> awaits,
        IReadOnlyList<StatementBase> prefix,
        IReadOnlyList<StatementBase> pendingFirstContinuation,
        string firstCallSourceName,
        InvocationExpression firstCallSource)
    {
        if (awaits.Count != 1 || awaits[0].Index != prefix.Count ||
            !ReferenceEquals(awaits[0].Expression.Operand, firstCallSource) ||
            body.Count != awaits[0].Index + 2 ||
            body[^1] is not ReturnStatement ||
            !TryCreateAllInlineTaskResultReturns(
                body[^1], taskType, out var inlineTerminal) ||
            !TryCloneValueTaskResultExpression(
                firstCallSource,
                new Dictionary<string, string>(StringComparer.Ordinal),
                out var clonedFirstCallSource))
        {
            return null;
        }

        var pendingSource = pendingFirstContinuation
            .Select((statement, index) => (statement, index))
            .Where(item => item.statement is LocalVariableDeclarationStatement)
            .Select(item => (Declaration: (LocalVariableDeclarationStatement)item.statement,
                item.index))
            .FirstOrDefault(item => item.Declaration.Declarators.Count == 1 &&
                item.Declaration.Declarators[0].Initializer is InvocationExpression);
        if (pendingSource.Declaration is null)
        {
            return null;
        }

        var sourceDeclarator = pendingSource.Declaration.Declarators[0];
        var pendingBody = pendingFirstContinuation.ToArray();
        pendingBody[pendingSource.index] = new LocalVariableDeclarationStatement(
            pendingSource.Declaration.DeclaredType,
            [new LocalVariableDeclarator(sourceDeclarator.Name,
                new IdentifierExpression(new QualifiedIdentifier(firstCallSourceName)))])
        {
            SourceSpan = pendingSource.Declaration.SourceSpan,
        };

        var resultDeclaration = (LocalVariableDeclarationStatement)body[awaits[0].Index];
        var inlineResult = new LocalVariableDeclarationStatement(
            resultDeclaration.DeclaredType,
            [new LocalVariableDeclarator(awaits[0].Local.Name,
                new InvocationExpression(new MemberAccessExpression(
                    new IdentifierExpression(new QualifiedIdentifier(firstCallSourceName)),
                    "GetResult"), []))])
        {
            SourceSpan = awaits[0].Expression.SourceSpan,
        };
        var ready = new IfStatement(
            new MemberAccessExpression(
                new IdentifierExpression(new QualifiedIdentifier(firstCallSourceName)),
                "IsCompleted"),
            new BlockStatement([inlineResult, inlineTerminal]),
            new BlockStatement(pendingBody))
        {
            SourceSpan = awaits[0].Expression.SourceSpan,
        };
        var sourceDeclaration = new LocalVariableDeclarationStatement(
            new AutoType(false),
            [new LocalVariableDeclarator(firstCallSourceName, clonedFirstCallSource)])
        {
            SourceSpan = awaits[0].Expression.SourceSpan,
        };
        return new BlockStatement([sourceDeclaration, ready]);
    }

    private static StatementBase? CreateAllInlineAwaitLocalBranchWithLaterSource(
        NamedType taskType,
        IReadOnlyList<StatementBase> body,
        IReadOnlyList<(int Index, AwaitExpression Expression, LocalVariableDeclarator Local)> awaits,
        IReadOnlyList<StatementBase> prefix,
        IReadOnlyList<StatementBase> pendingFirstContinuation,
        IReadOnlyList<StatementBase> pendingSecondContinuation,
        Func<ExpressionBase, bool> isTaskResultBranchSource,
        string? firstCallSourceName,
        bool firstCallSourceIsValueTask = false)
    {
        var firstSourceOperand = awaits.Count == 0 ? null : awaits[0].Expression.Operand;
        if (firstCallSourceName is not null && firstSourceOperand is not InvocationExpression)
        {
            return null;
        }
        if (awaits.Count != 2 || awaits[0].Index != prefix.Count ||
            firstSourceOperand is null ||
            (firstSourceOperand is not IdentifierExpression &&
             (firstSourceOperand is not InvocationExpression ||
              firstCallSourceName is null ||
              (!firstCallSourceIsValueTask &&
               !isTaskResultBranchSource(firstSourceOperand)))) ||
            awaits[1].Expression.Operand is not IdentifierExpression selectedSource ||
            awaits[1].Index != body.Count - 2 ||
            body[^1] is not ReturnStatement ||
            !TryCreateAllInlineTaskResultReturns(
                body[^1], taskType, out var inlineTerminal))
        {
            return null;
        }

        LocalVariableDeclarationStatement? selectedSourceDeclaration = null;
        ExpressionBase? selectedSourceInitializer = null;
        if (awaits[1].Index == awaits[0].Index + 2 &&
            body[awaits[0].Index + 1] is LocalVariableDeclarationStatement
            {
                Declarators: [{ Initializer: { } laterSource }],
            } candidateDeclaration &&
            candidateDeclaration.Declarators[0].Name ==
                selectedSource.Identifier.ToString() &&
            isTaskResultBranchSource(laterSource))
        {
            selectedSourceDeclaration = candidateDeclaration;
            if (!TryCloneValueTaskResultExpression(
                    laterSource,
                    new Dictionary<string, string>(StringComparer.Ordinal),
                    out var clonedTaskCallSource))
            {
                return null;
            }
            selectedSourceInitializer = clonedTaskCallSource;
        }
        else if (firstCallSourceName is null ||
                 awaits[1].Index != awaits[0].Index + 1 ||
                 !isTaskResultBranchSource(awaits[1].Expression.Operand))
        {
            return null;
        }

        var firstReadySource = firstCallSourceName is null
            ? CloneAwaitOperand(firstSourceOperand)
            : new IdentifierExpression(new QualifiedIdentifier(firstCallSourceName));
        IReadOnlyList<StatementBase> firstPendingBody = pendingFirstContinuation;
        LocalVariableDeclarationStatement? firstCallSourceDeclaration = null;
        if (firstCallSourceName is not null)
        {
            var pendingSourceDeclarationIndex = pendingFirstContinuation
                .Select((statement, index) => (statement, index))
                .Where(item => item.statement is LocalVariableDeclarationStatement)
                .Select(item => (Declaration: (LocalVariableDeclarationStatement)item.statement,
                    item.index))
                .FirstOrDefault(item => item.Declaration.Declarators.Count == 1 &&
                    item.Declaration.Declarators[0].Initializer is InvocationExpression);
            if (pendingSourceDeclarationIndex.Declaration is null)
            {
                return null;
            }

            var pendingSourceDeclaration = pendingSourceDeclarationIndex.Declaration;
            var pendingSourceDeclarator = pendingSourceDeclaration.Declarators[0];
            ExpressionBase pendingFirstSource =
                new IdentifierExpression(new QualifiedIdentifier(firstCallSourceName));
            if (firstCallSourceIsValueTask)
            {
                pendingFirstSource = new InvocationExpression(
                    new MemberAccessExpression(pendingFirstSource, "AsTask"), []);
            }
            var rewrittenPendingBody = pendingFirstContinuation.ToArray();
            rewrittenPendingBody[pendingSourceDeclarationIndex.index] =
                new LocalVariableDeclarationStatement(
                    pendingSourceDeclaration.DeclaredType,
                    [new LocalVariableDeclarator(pendingSourceDeclarator.Name,
                        pendingFirstSource)])
                {
                    SourceSpan = pendingSourceDeclaration.SourceSpan,
                };
            firstPendingBody = rewrittenPendingBody;
            firstCallSourceDeclaration = new LocalVariableDeclarationStatement(
                new AutoType(false),
                [new LocalVariableDeclarator(firstCallSourceName,
                    CloneAwaitOperand(firstSourceOperand))])
            {
                SourceSpan = awaits[0].Expression.SourceSpan,
            };
        }

        var firstResult = new LocalVariableDeclarationStatement(
            ((LocalVariableDeclarationStatement)body[awaits[0].Index]).DeclaredType,
            [new LocalVariableDeclarator(awaits[0].Local.Name,
                new InvocationExpression(
                    new MemberAccessExpression(CloneAwaitOperand(firstReadySource), "GetResult"),
                    []))]);
        LocalVariableDeclarationStatement? selectedSourceDeclarationClone = null;
        if (selectedSourceDeclaration is not null)
        {
            var selectedDeclarator = selectedSourceDeclaration.Declarators.Single();
            selectedSourceDeclarationClone = new LocalVariableDeclarationStatement(
                selectedSourceDeclaration.DeclaredType,
                [new LocalVariableDeclarator(
                    selectedDeclarator.Name, selectedSourceInitializer)])
            {
                SourceSpan = selectedSourceDeclaration.SourceSpan,
            };
        }
        var secondResult = new LocalVariableDeclarationStatement(
            ((LocalVariableDeclarationStatement)body[awaits[1].Index]).DeclaredType,
            [new LocalVariableDeclarator(awaits[1].Local.Name,
                new InvocationExpression(
                    new MemberAccessExpression(CloneAwaitOperand(selectedSource), "GetResult"),
                    []))]);

        var secondReady = new IfStatement(
            new MemberAccessExpression(CloneAwaitOperand(selectedSource), "IsCompleted"),
            new BlockStatement([secondResult, inlineTerminal]),
            new BlockStatement(pendingSecondContinuation));
        var firstReadyBody = new List<StatementBase> { firstResult };
        if (selectedSourceDeclarationClone is not null)
        {
            firstReadyBody.Add(selectedSourceDeclarationClone);
        }
        firstReadyBody.Add(secondReady);
        var firstReady = new IfStatement(
            new MemberAccessExpression(CloneAwaitOperand(firstReadySource), "IsCompleted"),
            new BlockStatement(firstReadyBody),
            new BlockStatement(firstPendingBody))
        {
            SourceSpan = awaits[0].Expression.SourceSpan,
        };
        return firstCallSourceDeclaration is null
            ? firstReady
            : new BlockStatement([firstCallSourceDeclaration, firstReady]);
    }

    private static StatementBase? CreateAllInlineAwaitLocalBranchWithLaterCallSources(
        NamedType taskType,
        IReadOnlyList<StatementBase> body,
        IReadOnlyList<(int Index, AwaitExpression Expression, LocalVariableDeclarator Local)> awaits,
        IReadOnlyList<StatementBase> prefix,
        IReadOnlyList<StatementBase> pendingFirstContinuation,
        Func<int, List<StatementBase>> createPendingContinuation,
        Func<ExpressionBase, bool> isLaterTaskCallSource,
        string? firstCallSourceName)
    {
        var firstSourceOperand = awaits.Count == 0
            ? null
            : awaits[0].Expression.Operand;
        if (awaits.Count < 3 || awaits[0].Index != prefix.Count ||
            awaits[^1].Index != body.Count - 2 ||
            body[^1] is not ReturnStatement ||
            firstSourceOperand is null ||
            (firstSourceOperand is not IdentifierExpression &&
             (firstSourceOperand is not InvocationExpression ||
              firstCallSourceName is null ||
              !isLaterTaskCallSource(firstSourceOperand))) ||
            !TryCreateAllInlineTaskResultReturns(
                body[^1], taskType, out var inlineTerminal))
        {
            return null;
        }

        var firstReadySource = firstCallSourceName is null
            ? CloneAwaitOperand(firstSourceOperand)
            : new IdentifierExpression(new QualifiedIdentifier(firstCallSourceName));
        IReadOnlyList<StatementBase> firstPendingBody = pendingFirstContinuation;
        LocalVariableDeclarationStatement? firstCallSourceDeclaration = null;
        if (firstCallSourceName is not null)
        {
            if (firstSourceOperand is not InvocationExpression firstCallSource ||
                !TryCloneValueTaskResultExpression(firstCallSource,
                    new Dictionary<string, string>(StringComparer.Ordinal),
                    out var clonedFirstCallSource))
            {
                return null;
            }

            var pendingSource = pendingFirstContinuation
                .Select((statement, index) => (statement, index))
                .Where(item => item.statement is LocalVariableDeclarationStatement)
                .Select(item => (Declaration: (LocalVariableDeclarationStatement)item.statement,
                    item.index))
                .FirstOrDefault(item => item.Declaration.Declarators.Count == 1 &&
                    item.Declaration.Declarators[0].Initializer is InvocationExpression);
            if (pendingSource.Declaration is null)
            {
                return null;
            }

            var sourceDeclarator = pendingSource.Declaration.Declarators[0];
            var rewrittenPending = pendingFirstContinuation.ToArray();
            rewrittenPending[pendingSource.index] = new LocalVariableDeclarationStatement(
                pendingSource.Declaration.DeclaredType,
                [new LocalVariableDeclarator(sourceDeclarator.Name,
                    new IdentifierExpression(new QualifiedIdentifier(firstCallSourceName)))])
            {
                SourceSpan = pendingSource.Declaration.SourceSpan,
            };
            firstPendingBody = rewrittenPending;
            firstCallSourceDeclaration = new LocalVariableDeclarationStatement(
                new AutoType(false),
                [new LocalVariableDeclarator(firstCallSourceName, clonedFirstCallSource)])
            {
                SourceSpan = awaits[0].Expression.SourceSpan,
            };
        }

        var selectedDeclarations = new LocalVariableDeclarationStatement[awaits.Count - 1];
        var selectedInitializers = new ExpressionBase[awaits.Count - 1];
        var statementsBeforeSelectedCalls = new StatementBase[awaits.Count - 1][];
        for (var index = 1; index < awaits.Count; index++)
        {
            var callSourceDeclarationIndex = awaits[index].Index - 1;
            if (callSourceDeclarationIndex <= awaits[index - 1].Index ||
                body[callSourceDeclarationIndex] is not LocalVariableDeclarationStatement
                {
                    Declarators: [{ Initializer: { } callSource }],
                } declaration ||
                awaits[index].Expression.Operand is not IdentifierExpression selectedSource ||
                declaration.Declarators[0].Name != selectedSource.Identifier.ToString() ||
                !isLaterTaskCallSource(callSource) ||
                !TryCloneValueTaskResultExpression(
                    callSource,
                    new Dictionary<string, string>(StringComparer.Ordinal),
                    out var clonedCallSource))
            {
                return null;
            }

            var statementsBeforeSelectedCall = new List<StatementBase>();
            for (var statementIndex = awaits[index - 1].Index + 1;
                statementIndex < callSourceDeclarationIndex;
                statementIndex++)
            {
                var statement = body[statementIndex];
                if (statement is not (ExpressionStatement or
                        LocalVariableDeclarationStatement or IfStatement or BlockStatement or
                        WhileStatement or DoWhileStatement or ForStatement or SwitchStatement or
                        TryStatement or ForeachStatement) ||
                    CoroutineSyntaxAnalysis.StatementContainsAwait(statement) ||
                    !TryCloneAwaitCallbackStatement(statement,
                        new Dictionary<string, string>(StringComparer.Ordinal),
                        out var clonedStatement))
                {
                    return null;
                }
                statementsBeforeSelectedCall.Add(clonedStatement);
            }

            selectedDeclarations[index - 1] = declaration;
            selectedInitializers[index - 1] = clonedCallSource;
            statementsBeforeSelectedCalls[index - 1] =
                [.. statementsBeforeSelectedCall];
        }

        LocalVariableDeclarationStatement CreateAwaitResult(int awaitNumber) =>
            new(((LocalVariableDeclarationStatement)
                body[awaits[awaitNumber].Index]).DeclaredType,
                [new LocalVariableDeclarator(
                    awaits[awaitNumber].Local.Name,
                    new InvocationExpression(new MemberAccessExpression(
                        awaitNumber == 0
                            ? CloneAwaitOperand(firstReadySource)
                            : CloneAwaitOperand(awaits[awaitNumber].Expression.Operand),
                        "GetResult"), []))]);

        LocalVariableDeclarationStatement CloneSelectedDeclaration(int index)
        {
            var declaration = selectedDeclarations[index];
            var declarator = declaration.Declarators[0];
            return new LocalVariableDeclarationStatement(declaration.DeclaredType,
                [new LocalVariableDeclarator(declarator.Name,
                    selectedInitializers[index])])
            {
                SourceSpan = declaration.SourceSpan,
            };
        }

        StatementBase? nextReadyCheck = null;
        for (var awaitNumber = awaits.Count - 1; awaitNumber >= 1; awaitNumber--)
        {
            var selectedSource = awaits[awaitNumber].Expression.Operand;
            var readyStatements = new List<StatementBase>
            {
                CreateAwaitResult(awaitNumber),
            };
            if (awaitNumber < statementsBeforeSelectedCalls.Length)
            {
                readyStatements.AddRange(statementsBeforeSelectedCalls[awaitNumber]);
            }
            if (awaitNumber == awaits.Count - 1)
            {
                readyStatements.Add(inlineTerminal);
            }
            else
            {
                readyStatements.Add(CloneSelectedDeclaration(awaitNumber));
                readyStatements.Add(nextReadyCheck!);
            }

            nextReadyCheck = new IfStatement(
                new MemberAccessExpression(CloneAwaitOperand(selectedSource), "IsCompleted"),
                new BlockStatement(readyStatements),
                new BlockStatement(createPendingContinuation(awaitNumber)))
            {
                SourceSpan = awaits[awaitNumber].Expression.SourceSpan,
            };
        }

        var firstReadyStatements = new List<StatementBase>
        {
            CreateAwaitResult(0),
        };
        firstReadyStatements.AddRange(statementsBeforeSelectedCalls[0]);
        firstReadyStatements.Add(CloneSelectedDeclaration(0));
        firstReadyStatements.Add(nextReadyCheck!);
        var firstReady = new IfStatement(
            new MemberAccessExpression(CloneAwaitOperand(firstReadySource), "IsCompleted"),
            new BlockStatement(firstReadyStatements),
            new BlockStatement(firstPendingBody))
        {
            SourceSpan = awaits[0].Expression.SourceSpan,
        };
        return firstCallSourceDeclaration is null
            ? firstReady
            : new BlockStatement([firstCallSourceDeclaration, firstReady]);
    }

    private static bool TryCreateAllInlineTaskResultReturns(
        StatementBase statement,
        NamedType taskType,
        out StatementBase inlineStatement)
    {
        switch (statement)
        {
            case ReturnStatement { Expression: { } expression }
                when !CoroutineSyntaxAnalysis.ContainsAwait(expression) &&
                     TryCloneValueTaskResultExpression(expression,
                         new Dictionary<string, string>(StringComparer.Ordinal),
                         out var inlineExpression):
                inlineStatement = new ReturnStatement(new ObjectCreationExpression(
                    taskType, [inlineExpression])
                {
                    SourceSpan = expression.SourceSpan,
                })
                {
                    SourceSpan = statement.SourceSpan,
                };
                return true;

            case IfStatement conditional when conditional.ElseStatement is not null:
                if (!TryCloneValueTaskResultExpression(conditional.Condition,
                        new Dictionary<string, string>(StringComparer.Ordinal),
                        out var condition) ||
                    CoroutineSyntaxAnalysis.ContainsAwait(conditional.Condition) ||
                    !TryCreateAllInlineTaskResultReturns(
                        conditional.ThenStatement, taskType, out var thenStatement) ||
                    !TryCreateAllInlineTaskResultReturns(
                        conditional.ElseStatement, taskType, out var elseStatement))
                {
                    inlineStatement = statement;
                    return false;
                }

                inlineStatement = new IfStatement(
                    condition, thenStatement, elseStatement)
                {
                    SourceSpan = conditional.SourceSpan,
                };
                return true;

            case BlockStatement block:
                var statements = new List<StatementBase>(block.Statements.Count);
                foreach (var child in block.Statements)
                {
                    if (!TryCreateAllInlineTaskResultReturns(
                            child, taskType, out var inlineChild))
                    {
                        inlineStatement = statement;
                        return false;
                    }
                    statements.Add(inlineChild);
                }

                inlineStatement = new BlockStatement(statements, block.IsUnsafe)
                {
                    SourceSpan = block.SourceSpan,
                };
                return true;

            default:
                if (CoroutineSyntaxAnalysis.EnumerateStatements([statement])
                    .Skip(1).Any(child => child is ReturnStatement))
                {
                    inlineStatement = statement;
                    return false;
                }

                inlineStatement = statement;
                return true;
        }
    }

    private static bool IsAwaitedLocalMutatedLater(
        string localName,
        IEnumerable<StatementBase> statements) =>
        EnumerateStatements(statements)
            .SelectMany(EnumerateAwaitedStatementExpressions)
            .SelectMany(CoroutineSyntaxAnalysis.EnumerateAllExpressions)
            .Any(expression => expression switch
            {
                AssignmentExpression assignment =>
                    CoroutineSyntaxAnalysis.EnumerateAllExpressions(assignment)
                        .OfType<IdentifierExpression>()
                        .Any(identifier => identifier.Identifier.ToString() == localName),
                UnaryExpression unary when unary.Operator is "++" or "--" =>
                    unary.Operand is IdentifierExpression identifier &&
                    identifier.Identifier.ToString() == localName,
                _ => false,
            });

    private static bool IsAwaitedLocalUsedLater(
        string localName,
        IEnumerable<StatementBase> statements) =>
        CoroutineSyntaxAnalysis.EnumerateBodyExpressions(statements)
            .OfType<IdentifierExpression>()
            .Any(identifier =>
                identifier.Identifier.ToString() == localName);

    private static IEnumerable<ExpressionBase> EnumerateAwaitedStatementExpressions(
        StatementBase statement) => statement switch
    {
        ExpressionStatement expressionStatement => [expressionStatement.Expression],
        LocalVariableDeclarationStatement declaration => declaration.Declarators
            .Where(declarator => declarator.Initializer is not null)
            .Select(declarator => declarator.Initializer!),
        ReturnStatement { Expression: { } expression } => [expression],
        ThrowStatement { Expression: { } expression } => [expression],
        IfStatement conditional => [conditional.Condition],
        WhileStatement loop => [loop.Condition],
        DoWhileStatement loop => [loop.Condition],
        ForStatement loop => loop.InitializerExpressions
            .Concat(loop.Condition is null ? [] : [loop.Condition])
            .Concat(loop.Iterators)
            .Concat(loop.DeclarationInitializer?.Declarators
                .Where(declarator => declarator.Initializer is not null)
                .Select(declarator => declarator.Initializer!) ?? []),
        ForeachStatement loop => [loop.Collection],
        SwitchStatement switchStatement => [switchStatement.Expression],
        TryStatement tryStatement => tryStatement.CatchClauses
            .Where(clause => clause.Filter is not null)
            .Select(clause => clause.Filter!),
        _ => [],
    };

    private static bool TryCloneAwaitCallbackStatement(
        StatementBase statement,
        IReadOnlyDictionary<string, string> substitutions,
        out StatementBase clone)
    {
        if (statement is BlockStatement block)
        {
            var clonedStatements = new List<StatementBase>(block.Statements.Count);
            foreach (var child in block.Statements)
            {
                if (!TryCloneAwaitCallbackStatement(child, substitutions,
                        out var clonedChild))
                {
                    clone = statement;
                    return false;
                }
                clonedStatements.Add(clonedChild);
            }
            clone = new BlockStatement(clonedStatements, block.IsUnsafe)
            {
                SourceSpan = block.SourceSpan,
            };
            return true;
        }

        if (statement is LocalVariableDeclarationStatement declaration)
        {
            var declarationReferencesAlias = EnumerateAwaitedStatementExpressions(statement)
                .SelectMany(CoroutineSyntaxAnalysis.EnumerateAllExpressions)
                .OfType<IdentifierExpression>()
                .Any(identifier => substitutions.ContainsKey(
                    identifier.Identifier.ToString()));
            if (!declarationReferencesAlias &&
                declaration.Declarators.All(declarator => declarator.Initializer is not null))
            {
                clone = statement;
                return true;
            }

            var clonedDeclarators = new List<LocalVariableDeclarator>(
                declaration.Declarators.Count);
            foreach (var declarator in declaration.Declarators)
            {
                var initializer = declarator.Initializer;
                if (initializer is not null &&
                    !TryCloneValueTaskResultExpression(
                        initializer, substitutions, out initializer))
                {
                    clone = statement;
                    return false;
                }

                var clonedDeclarator = new LocalVariableDeclarator(
                    declarator.Name, initializer);
                if (declarator.Type is { } localType)
                {
                    if (declarator.Symbol is { } symbol)
                    {
                        clonedDeclarator.BindType(localType, symbol);
                    }
                    else
                    {
                        clonedDeclarator.CloseType(localType);
                    }
                }
                clonedDeclarators.Add(clonedDeclarator);
            }
            clone = new LocalVariableDeclarationStatement(
                declaration.DeclaredType, clonedDeclarators)
            {
                SourceSpan = declaration.SourceSpan,
            };
            return true;
        }

        if (statement is IfStatement conditional)
        {
            var ifReferencesSubstitutedLocal = EnumerateStatements([statement])
                .SelectMany(EnumerateAwaitedStatementExpressions)
                .SelectMany(CoroutineSyntaxAnalysis.EnumerateAllExpressions)
                .OfType<IdentifierExpression>()
                .Any(identifier => substitutions.ContainsKey(
                    identifier.Identifier.ToString()));
            if (!ifReferencesSubstitutedLocal)
            {
                clone = statement;
                return true;
            }

            if (!TryCloneValueTaskResultExpression(
                    conditional.Condition, substitutions, out var clonedCondition) ||
                !TryCloneAwaitCallbackStatement(
                    conditional.ThenStatement, substitutions, out var clonedThen))
            {
                clone = statement;
                return false;
            }

            StatementBase? clonedElse = null;
            if (conditional.ElseStatement is not null &&
                !TryCloneAwaitCallbackStatement(
                    conditional.ElseStatement, substitutions, out clonedElse))
            {
                clone = statement;
                return false;
            }
            clone = new IfStatement(clonedCondition, clonedThen, clonedElse)
            {
                SourceSpan = conditional.SourceSpan,
            };
            return true;
        }

        if (statement is WhileStatement loop)
        {
            var loopReferencesSubstitutedLocal = EnumerateStatements([statement])
                .SelectMany(EnumerateAwaitedStatementExpressions)
                .SelectMany(CoroutineSyntaxAnalysis.EnumerateAllExpressions)
                .OfType<IdentifierExpression>()
                .Any(identifier => substitutions.ContainsKey(
                    identifier.Identifier.ToString()));
            if (!loopReferencesSubstitutedLocal)
            {
                clone = statement;
                return true;
            }

            if (!TryCloneValueTaskResultExpression(
                    loop.Condition, substitutions, out var clonedCondition) ||
                !TryCloneAwaitCallbackStatement(
                    loop.Body, substitutions, out var clonedBody))
            {
                clone = statement;
                return false;
            }

            clone = new WhileStatement(clonedCondition, clonedBody)
            {
                SourceSpan = loop.SourceSpan,
            };
            return true;
        }

        if (statement is DoWhileStatement doWhile)
        {
            var loopReferencesSubstitutedLocal = EnumerateStatements([statement])
                .SelectMany(EnumerateAwaitedStatementExpressions)
                .SelectMany(CoroutineSyntaxAnalysis.EnumerateAllExpressions)
                .OfType<IdentifierExpression>()
                .Any(identifier => substitutions.ContainsKey(
                    identifier.Identifier.ToString()));
            if (!loopReferencesSubstitutedLocal)
            {
                clone = statement;
                return true;
            }

            if (!TryCloneAwaitCallbackStatement(
                    doWhile.Body, substitutions, out var clonedBody) ||
                !TryCloneValueTaskResultExpression(
                    doWhile.Condition, substitutions, out var clonedCondition))
            {
                clone = statement;
                return false;
            }

            clone = new DoWhileStatement(clonedBody, clonedCondition)
            {
                SourceSpan = doWhile.SourceSpan,
            };
            return true;
        }

        if (statement is ForStatement forLoop)
        {
            var forReferencesSubstitutedLocal = EnumerateStatements([statement])
                .SelectMany(EnumerateAwaitedStatementExpressions)
                .SelectMany(CoroutineSyntaxAnalysis.EnumerateAllExpressions)
                .OfType<IdentifierExpression>()
                .Any(identifier => substitutions.ContainsKey(
                    identifier.Identifier.ToString()));
            if (!forReferencesSubstitutedLocal)
            {
                clone = statement;
                return true;
            }

            LocalVariableDeclarationStatement? clonedDeclarationInitializer = null;
            if (forLoop.DeclarationInitializer is not null)
            {
                if (!TryCloneAwaitCallbackStatement(
                        forLoop.DeclarationInitializer, substitutions,
                        out var clonedInitializerStatement) ||
                    clonedInitializerStatement is not LocalVariableDeclarationStatement
                        clonedDeclaration)
                {
                    clone = statement;
                    return false;
                }
                clonedDeclarationInitializer = clonedDeclaration;
            }

            var clonedInitializerExpressions = new List<ExpressionBase>(
                forLoop.InitializerExpressions.Count);
            foreach (var initializer in forLoop.InitializerExpressions)
            {
                if (!TryCloneValueTaskResultExpression(
                        initializer, substitutions, out var clonedInitializer))
                {
                    clone = statement;
                    return false;
                }
                clonedInitializerExpressions.Add(clonedInitializer);
            }

            ExpressionBase? clonedCondition = null;
            if (forLoop.Condition is not null &&
                !TryCloneValueTaskResultExpression(
                    forLoop.Condition, substitutions, out clonedCondition))
            {
                clone = statement;
                return false;
            }

            var clonedIterators = new List<ExpressionBase>(forLoop.Iterators.Count);
            foreach (var iterator in forLoop.Iterators)
            {
                if (!TryCloneValueTaskResultExpression(
                        iterator, substitutions, out var clonedIterator))
                {
                    clone = statement;
                    return false;
                }
                clonedIterators.Add(clonedIterator);
            }

            if (!TryCloneAwaitCallbackStatement(
                    forLoop.Body, substitutions, out var clonedBody))
            {
                clone = statement;
                return false;
            }

            clone = new ForStatement(clonedDeclarationInitializer,
                clonedInitializerExpressions, clonedCondition, clonedIterators,
                clonedBody)
            {
                SourceSpan = forLoop.SourceSpan,
            };
            return true;
        }

        if (statement is SwitchStatement switchStatement)
        {
            var switchReferencesSubstitutedLocal = EnumerateStatements([statement])
                .SelectMany(EnumerateAwaitedStatementExpressions)
                .Concat(switchStatement.Sections.SelectMany(section => section.Labels)
                    .SelectMany(label => new[] { label.Value, label.Filter }
                        .Where(expression => expression is not null)
                        .Select(expression => expression!)))
                .SelectMany(CoroutineSyntaxAnalysis.EnumerateAllExpressions)
                .OfType<IdentifierExpression>()
                .Any(identifier => substitutions.ContainsKey(
                    identifier.Identifier.ToString()));
            if (!switchReferencesSubstitutedLocal)
            {
                clone = statement;
                return true;
            }

            if (!TryCloneValueTaskResultExpression(
                    switchStatement.Expression, substitutions,
                    out var clonedSwitchExpression))
            {
                clone = statement;
                return false;
            }

            var clonedSections = new List<SwitchSection>(
                switchStatement.Sections.Count);
            foreach (var section in switchStatement.Sections)
            {
                var clonedLabels = new List<SwitchLabel>(section.Labels.Count);
                foreach (var label in section.Labels)
                {
                    ExpressionBase? clonedValue = null;
                    if (label.Value is not null &&
                        !TryCloneValueTaskResultExpression(
                            label.Value, substitutions, out clonedValue))
                    {
                        clone = statement;
                        return false;
                    }
                    ExpressionBase? clonedFilter = null;
                    if (label.Filter is not null &&
                        !TryCloneValueTaskResultExpression(
                            label.Filter, substitutions, out clonedFilter))
                    {
                        clone = statement;
                        return false;
                    }
                    clonedLabels.Add(new SwitchLabel(clonedValue, clonedFilter));
                }

                var clonedSectionStatements = new List<StatementBase>(
                    section.Statements.Count);
                foreach (var sectionStatement in section.Statements)
                {
                    if (!TryCloneAwaitCallbackStatement(
                            sectionStatement, substitutions,
                            out var clonedSectionStatement))
                    {
                        clone = statement;
                        return false;
                    }
                    clonedSectionStatements.Add(clonedSectionStatement);
                }
                clonedSections.Add(new SwitchSection(
                    clonedLabels, clonedSectionStatements));
            }

            var clonedSwitch = new SwitchStatement(
                clonedSwitchExpression, clonedSections)
            {
                SourceSpan = switchStatement.SourceSpan,
            };
            if (switchStatement.SelectorTemporaryName is { } selectorTemporary &&
                switchStatement.CaseIndexTemporaryName is { } caseIndexTemporary)
            {
                clonedSwitch.BindTemporaries(selectorTemporary, caseIndexTemporary);
            }
            clone = clonedSwitch;
            return true;
        }

        if (statement is ForeachStatement foreachStatement)
        {
            // Synchronous foreach binding runs after async lowering, so preserve
            // the source declaration and let each clone bind its own loop locals.
            if (foreachStatement.IsAwait)
            {
                clone = statement;
                return false;
            }

            var foreachReferencesSubstitutedLocal = EnumerateStatements([statement])
                .SelectMany(EnumerateAwaitedStatementExpressions)
                .SelectMany(CoroutineSyntaxAnalysis.EnumerateAllExpressions)
                .OfType<IdentifierExpression>()
                .Any(identifier => substitutions.ContainsKey(
                    identifier.Identifier.ToString()));
            if (!foreachReferencesSubstitutedLocal)
            {
                clone = statement;
                return true;
            }

            if (!TryCloneValueTaskResultExpression(
                    foreachStatement.Collection, substitutions,
                    out var clonedCollection) ||
                !TryCloneAwaitCallbackStatement(
                    foreachStatement.Body, substitutions, out var clonedBody))
            {
                clone = statement;
                return false;
            }

            var clonedForeach = new ForeachStatement(
                foreachStatement.DeclaredType, foreachStatement.VariableName,
                clonedCollection, clonedBody)
            {
                SourceSpan = foreachStatement.SourceSpan,
            };
            clone = clonedForeach;
            return true;
        }

        if (statement is TryStatement tryStatement)
        {
            var tryReferencesSubstitutedLocal = EnumerateStatements([statement])
                .SelectMany(EnumerateAwaitedStatementExpressions)
                .SelectMany(CoroutineSyntaxAnalysis.EnumerateAllExpressions)
                .OfType<IdentifierExpression>()
                .Any(identifier => substitutions.ContainsKey(
                    identifier.Identifier.ToString()));
            if (!tryReferencesSubstitutedLocal)
            {
                clone = statement;
                return true;
            }

            if (!TryCloneAwaitCallbackStatement(
                    tryStatement.Body, substitutions, out var clonedTryBody))
            {
                clone = statement;
                return false;
            }

            var clonedCatchClauses = new List<CatchClause>(
                tryStatement.CatchClauses.Count);
            foreach (var clause in tryStatement.CatchClauses)
            {
                ExpressionBase? clonedFilter = null;
                if (clause.Filter is not null &&
                    !TryCloneValueTaskResultExpression(
                        clause.Filter, substitutions, out clonedFilter))
                {
                    clone = statement;
                    return false;
                }
                if (!TryCloneAwaitCallbackStatement(
                        clause.Body, substitutions, out var clonedCatchBody))
                {
                    clone = statement;
                    return false;
                }

                var clonedClause = new CatchClause(
                    clause.ExceptionType, clause.VariableName,
                    clonedFilter, clonedCatchBody);
                if (clause.VariableSymbol is { } variableSymbol)
                {
                    clonedClause.BindVariableSymbol(variableSymbol);
                }
                clonedCatchClauses.Add(clonedClause);
            }

            StatementBase? clonedFinallyBody = null;
            if (tryStatement.FinallyBody is not null &&
                !TryCloneAwaitCallbackStatement(
                    tryStatement.FinallyBody, substitutions,
                    out clonedFinallyBody))
            {
                clone = statement;
                return false;
            }

            clone = new TryStatement(
                clonedTryBody, clonedCatchClauses, clonedFinallyBody)
            {
                SourceSpan = tryStatement.SourceSpan,
            };
            return true;
        }

        if (statement is ThrowStatement { Expression: { } exceptionExpression } throwStatement)
        {
            var throwReferencesSubstitutedLocal =
                CoroutineSyntaxAnalysis.EnumerateAllExpressions(exceptionExpression)
                    .OfType<IdentifierExpression>()
                    .Any(identifier => substitutions.ContainsKey(
                        identifier.Identifier.ToString()));
            if (!throwReferencesSubstitutedLocal)
            {
                clone = statement;
                return true;
            }

            if (!TryCloneValueTaskResultExpression(
                    exceptionExpression, substitutions, out var clonedExceptionExpression))
            {
                clone = statement;
                return false;
            }

            clone = new ThrowStatement(clonedExceptionExpression)
            {
                SourceSpan = throwStatement.SourceSpan,
            };
            return true;
        }

        var expression = statement switch
        {
            ExpressionStatement expressionStatement => expressionStatement.Expression,
            ReturnStatement { Expression: { } returnExpression } => returnExpression,
            _ => null,
        };
        if (expression is null)
        {
            clone = statement;
            var otherStatementReferencesSubstitutedLocal = EnumerateStatements([statement])
                .SelectMany(EnumerateAwaitedStatementExpressions)
                .SelectMany(CoroutineSyntaxAnalysis.EnumerateAllExpressions)
                .OfType<IdentifierExpression>()
                .Any(identifier => substitutions.ContainsKey(
                    identifier.Identifier.ToString()));
            return !otherStatementReferencesSubstitutedLocal;
        }

        var referencesSubstitutedLocal = CoroutineSyntaxAnalysis
            .EnumerateAllExpressions(expression)
            .OfType<IdentifierExpression>()
            .Any(identifier => substitutions.ContainsKey(
                identifier.Identifier.ToString()));
        if (!referencesSubstitutedLocal)
        {
            clone = statement;
            return true;
        }

        if (!TryCloneValueTaskResultExpression(
            expression, substitutions, out var clonedExpression))
        {
            clone = statement;
            return false;
        }

        clone = statement switch
        {
            ExpressionStatement expressionStatement =>
                new ExpressionStatement(clonedExpression)
                {
                    SourceSpan = expressionStatement.SourceSpan,
                },
            ReturnStatement returnStatement => new ReturnStatement(clonedExpression)
            {
                SourceSpan = returnStatement.SourceSpan,
            },
            _ => statement,
        };
        return true;
    }

    private static ExpressionBase CloneAwaitOperand(ExpressionBase operand) =>
        operand is IdentifierExpression identifier
            ? new IdentifierExpression(new QualifiedIdentifier(
                [.. identifier.Identifier.Parts]))
            {
                SourceSpan = identifier.SourceSpan,
            }
            : operand;

    private static bool TryLowerSingleValueTaskResultLocal(
        FunctionDeclaration function,
        NamedType taskType,
        Func<ExpressionBase, NamedType?>? valueTaskCallSourceType = null)
    {
        if (function.GenericTypeNames.Length != 0 ||
            taskType.TypeArguments.Count != 1 ||
            function.Body is not
            [
                LocalVariableDeclarationStatement
                {
                    DeclaredType: not AutoType { Const: true },
                    Declarators:
                    [
                        { Initializer: AwaitExpression awaitExpression } awaitedLocal,
                    ],
                } awaitedDeclaration,
                ReturnStatement { Expression: { } returnedExpression },
            ] ||
            !CoroutineSyntaxAnalysis.EnumerateAllExpressions(returnedExpression)
                .OfType<IdentifierExpression>()
                .Any(identifier => identifier.Identifier.ToString() == awaitedLocal.Name))
        {
            return false;
        }

        var operand = awaitExpression.Operand;
        NamedType? valueTaskType = null;
        string? hoistedSourceName = null;
        if (operand is IdentifierExpression operandIdentifier)
        {
            var parameter = function.Parameters.FirstOrDefault(candidate =>
                candidate.Name == operandIdentifier.Identifier.ToString());
            var parameterType = parameter?.ParameterType is ConstType constant
                ? constant.UnderlyingType
                : parameter?.ParameterType;
            if (parameterType is NamedType parameterValueTaskType &&
                IsValueTaskType(parameterValueTaskType))
            {
                valueTaskType = parameterValueTaskType;
            }
        }
        if (valueTaskType is null && valueTaskCallSourceType is not null &&
            valueTaskCallSourceType(operand) is { } callValueTaskType)
        {
            valueTaskType = callValueTaskType;
            hoistedSourceName = CreateUniqueName(
                function.Parameters.Select(candidate => candidate.Name)
                    .Append(awaitedLocal.Name)
                    .ToHashSet(StringComparer.Ordinal),
                "__cx_async_value_task_call");
            operand = new IdentifierExpression(new QualifiedIdentifier(hoistedSourceName));
        }
        if (valueTaskType is null || valueTaskType.TypeArguments.Count != 1 ||
            valueTaskType.TypeArguments[0].ToString() !=
                taskType.TypeArguments[0].ToString())
        {
            return false;
        }

        var resultType = taskType.TypeArguments[0];
        var sourceTaskType = NamedType.CreateWithTypeArguments(
            "System.Threading.Tasks.Task", [resultType]);
        var completionSourceType = NamedType.CreateWithTypeArguments(
            "System.Threading.Tasks.TaskCompletionSource", [resultType]);
        var names = function.Parameters.Select(candidate => candidate.Name)
            .Append(awaitedLocal.Name)
            .ToHashSet(StringComparer.Ordinal);
        var sourceName = CreateUniqueName(names, "__cx_async_value_task_source");
        var resultTaskName = CreateUniqueName(names, "__cx_async_result_task");
        var callbackSourceName = CreateUniqueName(names, "__cx_async_callback_source");
        var completionName = CreateUniqueName(names, "__cx_async_completion");
        var callbackCompletionName = CreateUniqueName(
            names, "__cx_async_callback_completion");
        var continuationName = CreateUniqueName(names, "__cx_async_continuation");
        var inlineResultName = CreateUniqueName(names, "__cx_async_inline_result");
        var pendingResultName = CreateUniqueName(names, "__cx_async_pending_result");
        var cancellationName = CreateUniqueName(names, "__cx_async_canceled");
        var exceptionName = CreateUniqueName(names, "__cx_async_exception");
        if (!TryCloneValueTaskResultExpression(returnedExpression, awaitedLocal.Name,
                inlineResultName, out var inlineResultExpression) ||
            !TryCloneValueTaskResultExpression(returnedExpression, awaitedLocal.Name,
                pendingResultName, out var pendingResultExpression))
        {
            return false;
        }

        IdentifierExpression Id(string name) =>
            new(new QualifiedIdentifier(name));
        var inlineBlock = new BlockStatement([
            new LocalVariableDeclarationStatement(
                awaitedDeclaration.DeclaredType,
                [new LocalVariableDeclarator(inlineResultName,
                    new InvocationExpression(
                        new MemberAccessExpression(operand, "GetResult"), []))]),
            new ReturnStatement(new ObjectCreationExpression(
                taskType, [inlineResultExpression])),
        ]);

        var callbackSource = Id(callbackSourceName);
        var callbackCompletion = Id(callbackCompletionName);
        var callbackBody = new BlockStatement([
            new LocalVariableDeclarationStatement(
                awaitedDeclaration.DeclaredType,
                [new LocalVariableDeclarator(pendingResultName,
                    new InvocationExpression(
                        new MemberAccessExpression(callbackSource, "GetResult"), []))]),
            new ExpressionStatement(new InvocationExpression(
                new MemberAccessExpression(callbackCompletion, "SetResult"),
                [pendingResultExpression])),
        ]);
        var callback = new LambdaExpression(
            [], null,
            [new TryStatement(
                callbackBody,
                [
                    new CatchClause(
                        new NamedType(
                            "System.Threading.Tasks.OperationCanceledException", []),
                        cancellationName,
                        null,
                        new BlockStatement([new ExpressionStatement(
                            new InvocationExpression(
                                new MemberAccessExpression(
                                    callbackCompletion, "SetCanceled"), []))])),
                    new CatchClause(
                        new NamedType("System.Exception", []),
                        exceptionName,
                        null,
                        new BlockStatement([new ExpressionStatement(
                            new InvocationExpression(
                                new MemberAccessExpression(
                                    callbackCompletion, "SetException"),
                                [Id(exceptionName)]))])),
                ], null)])
        {
            SourceSpan = awaitExpression.SourceSpan ?? function.SourceSpan,
        };

        var pendingBlock = new BlockStatement([
            new LocalVariableDeclarationStatement(
                sourceTaskType,
                [new LocalVariableDeclarator(sourceName,
                    new InvocationExpression(
                        new MemberAccessExpression(operand, "AsTask"), []))]),
            new LocalVariableDeclarationStatement(
                completionSourceType,
                [new LocalVariableDeclarator(completionName,
                    new ObjectCreationExpression(completionSourceType, []))]),
            new LocalVariableDeclarationStatement(
                sourceTaskType,
                [new LocalVariableDeclarator(callbackSourceName, Id(sourceName))]),
            new LocalVariableDeclarationStatement(
                completionSourceType,
                [new LocalVariableDeclarator(callbackCompletionName,
                    Id(completionName))]),
            new LocalVariableDeclarationStatement(
                sourceTaskType,
                [new LocalVariableDeclarator(resultTaskName,
                    new MemberAccessExpression(Id(completionName), "Task"))]),
            new LocalVariableDeclarationStatement(
                new FunctionType(QualifiedIdentifier.Empty,
                    BuiltInSystemTypes.Void, []),
                [new LocalVariableDeclarator(continuationName, callback)]),
            new ExpressionStatement(new InvocationExpression(
                new MemberAccessExpression(Id(sourceName), "OnCompleted"),
                [Id(continuationName)])),
            new ReturnStatement(Id(resultTaskName)),
        ]);

        var loweredBody = new List<StatementBase>();
        if (hoistedSourceName is not null)
        {
            loweredBody.Add(new LocalVariableDeclarationStatement(
                valueTaskType,
                [new LocalVariableDeclarator(hoistedSourceName, awaitExpression.Operand)]));
        }
        loweredBody.Add(
            new IfStatement(
                new MemberAccessExpression(operand, "IsCompleted"),
                inlineBlock,
                pendingBlock)
            {
                SourceSpan = awaitExpression.SourceSpan ?? function.SourceSpan,
            });
        function.SetBody(loweredBody);
        return true;
    }

    private static bool TryCloneValueTaskResultExpression(
        ExpressionBase expression,
        string awaitedLocalName,
        string resultLocalName,
        out ExpressionBase clone) =>
        TryCloneValueTaskResultExpression(
            expression,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [awaitedLocalName] = resultLocalName,
            },
            out clone);

    private static bool TryCloneValueTaskResultExpression(
        ExpressionBase expression,
        IReadOnlyDictionary<string, string> substitutions,
        out ExpressionBase clone)
    {
        ExpressionBase? result = expression switch
        {
            IdentifierExpression identifier => new IdentifierExpression(
                substitutions.TryGetValue(identifier.Identifier.ToString(),
                    out var replacementName)
                    ? new QualifiedIdentifier(replacementName)
                    : new QualifiedIdentifier([.. identifier.Identifier.Parts])),
            LiteralExpression literal => new LiteralExpression(literal.SourceText),
            TypeOfExpression typeOf => new TypeOfExpression(typeOf.Type),
            BinaryExpression binary when
                TryCloneValueTaskResultExpression(binary.Left, substitutions, out var left) &&
                TryCloneValueTaskResultExpression(binary.Right, substitutions, out var right) =>
                    new BinaryExpression(left, binary.Operator, right),
            AssignmentExpression assignment when
                TryCloneValueTaskResultExpression(
                    assignment.Target, substitutions, out var target) &&
                TryCloneValueTaskResultExpression(
                    assignment.Value, substitutions, out var value) =>
                    new AssignmentExpression(target, assignment.Operator, value),
            UnaryExpression unary when
                TryCloneValueTaskResultExpression(unary.Operand, substitutions,
                    out var operand) =>
                    new UnaryExpression(unary.Operator, operand, unary.Postfix),
            MemberAccessExpression member when
                TryCloneValueTaskResultExpression(member.Target, substitutions,
                    out var target) =>
                    new MemberAccessExpression(target, member.MemberName),
            InvocationExpression invocation when
                TryCloneValueTaskResultExpression(invocation.Target, substitutions,
                    out var invocationTarget) &&
                TryCloneValueTaskResultExpressions(invocation.Arguments, substitutions,
                    out var arguments) =>
                    new InvocationExpression(invocationTarget, arguments,
                        invocation.ExplicitTypeArguments),
            ConditionalExpression conditional when
                TryCloneValueTaskResultExpression(conditional.Condition, substitutions,
                    out var condition) &&
                TryCloneValueTaskResultExpression(conditional.WhenTrue, substitutions,
                    out var whenTrue) &&
                TryCloneValueTaskResultExpression(conditional.WhenFalse, substitutions,
                    out var whenFalse) =>
                    new ConditionalExpression(condition, whenTrue, whenFalse),
            NullCoalescingExpression coalescing when
                TryCloneValueTaskResultExpression(coalescing.Left, substitutions,
                    out var left) &&
                TryCloneValueTaskResultExpression(coalescing.Right, substitutions,
                    out var right) =>
                    new NullCoalescingExpression(left, right),
            SwitchExpression selection when
                TryCloneValueTaskResultExpression(selection.Selector, substitutions,
                    out var selector) &&
                TryCloneValueTaskResultSwitchArms(selection.Arms, substitutions,
                    out var arms) =>
                    CloneValueTaskResultSwitchExpression(selection, selector, arms),
            CastExpression cast when
                TryCloneValueTaskResultExpression(cast.Operand, substitutions,
                    out var castOperand) =>
                    new CastExpression(cast.TargetType, castOperand, cast.IsSafe),
            _ => null,
        };

        if (result is null)
        {
            clone = null!;
            return false;
        }

        result.SourceSpan = expression.SourceSpan;
        clone = result;
        return true;
    }

    private static SwitchExpression CloneValueTaskResultSwitchExpression(
        SwitchExpression original,
        ExpressionBase selector,
        IReadOnlyList<SwitchExpressionArm> arms)
    {
        var clone = new SwitchExpression(selector, arms);
        if (original.TemporaryName is { } temporaryName)
        {
            clone.BindTemporary(temporaryName);
        }
        return clone;
    }

    private static bool TryCloneValueTaskResultSwitchArms(
        IReadOnlyList<SwitchExpressionArm> arms,
        IReadOnlyDictionary<string, string> substitutions,
        out SwitchExpressionArm[] clones)
    {
        var result = new List<SwitchExpressionArm>(arms.Count);
        foreach (var arm in arms)
        {
            ExpressionBase? label = null;
            ExpressionBase? filter = null;
            if ((arm.Label is not null &&
                    !TryCloneValueTaskResultExpression(arm.Label, substitutions,
                        out label)) ||
                (arm.Filter is not null &&
                    !TryCloneValueTaskResultExpression(arm.Filter, substitutions,
                        out filter)) ||
                !TryCloneValueTaskResultExpression(arm.Value, substitutions,
                    out var value))
            {
                clones = [];
                return false;
            }
            result.Add(new SwitchExpressionArm(label, filter, value));
        }
        clones = result.ToArray();
        return true;
    }

    private static bool TryCloneValueTaskResultExpressions(
        IReadOnlyList<ExpressionBase> expressions,
        IReadOnlyDictionary<string, string> substitutions,
        out ExpressionBase[] clones)
    {
        var result = new List<ExpressionBase>(expressions.Count);
        foreach (var expression in expressions)
        {
            if (!TryCloneValueTaskResultExpression(expression, substitutions, out var clone))
            {
                clones = [];
                return false;
            }
            result.Add(clone);
        }

        clones = result.ToArray();
        return true;
    }

    private static bool TryLowerAwaitLoopContinuation(
        FunctionDeclaration function,
        NamedType taskType,
        bool hasResult,
        IReadOnlyList<FunctionDeclaration> availableFunctions,
        IReadOnlyList<QualifiedIdentifier> imports)
    {
        if (!hasResult && function.Body is { Count: > 0 } implicitReturnBody &&
            implicitReturnBody[^1] is WhileStatement or DoWhileStatement or ForStatement)
        {
            function.SetBody(implicitReturnBody.Append(new ReturnStatement(null)).ToArray());
        }

        if (function.GenericTypeNames.Length != 0 ||
            taskType.TypeArguments.Count != (hasResult ? 1 : 0) ||
            function.Body is not { Count: >= 2 } body ||
            body[^1] is not ReturnStatement terminalReturn ||
            (hasResult
                ? terminalReturn.Expression is null
                : terminalReturn.Expression is not null) ||
            body.Take(body.Count - 2).Any(statement =>
                CoroutineSyntaxAnalysis.StatementContainsAwait(statement)))
        {
            return false;
        }

        var loopStatement = body[^2];
        StatementBase? loopBody = loopStatement switch
        {
            WhileStatement loop => loop.Body,
            DoWhileStatement loop => loop.Body,
            ForStatement loop => loop.Body,
            _ => null,
        };
        ExpressionBase? loopCondition = loopStatement switch
        {
            WhileStatement loop => loop.Condition,
            DoWhileStatement loop => loop.Condition,
            ForStatement loop => loop.Condition ?? new LiteralExpression("true"),
            _ => null,
        };
        var isDoWhile = loopStatement is DoWhileStatement;
        var loopIterators = loopStatement is ForStatement forStatement
            ? forStatement.Iterators
            : Array.Empty<ExpressionBase>();
        var loopDeclarationInitializer = loopStatement is ForStatement forLoop
            ? forLoop.DeclarationInitializer
            : null;
        var loopInitializerExpressions = loopStatement is ForStatement initializedFor
            ? initializedFor.InitializerExpressions
            : Array.Empty<ExpressionBase>();
        if (loopBody is null || loopCondition is null ||
            CoroutineSyntaxAnalysis.ContainsAwait(loopCondition))
        {
            return false;
        }

        AwaitExpression awaitExpression;
        LocalVariableDeclarator? awaitedLocal;
        StatementBase[] continuationStatements;
        if (hasResult)
        {
            if (TryGetAwaitingLoopBody(loopBody, out awaitExpression,
                    out var resultLocal, out continuationStatements))
            {
                awaitedLocal = resultLocal;
            }
            else if (!TryGetStandaloneAwaitLoopBody(loopBody, out awaitExpression,
                         out awaitedLocal, out continuationStatements))
            {
                return false;
            }
        }
        else if (!TryGetStandaloneAwaitLoopBody(loopBody, out awaitExpression,
                     out awaitedLocal, out continuationStatements))
        {
            return false;
        }
        if (CoroutineSyntaxAnalysis.ContainsAwait(awaitExpression.Operand) ||
            !CanLowerAwaitLoopControlTransfers(continuationStatements, hasResult))
        {
            return false;
        }

        NamedType? GetAwaitableType(ExpressionBase expression)
        {
            var inferredType = expression.InferredType is ConstType constant
                ? constant.UnderlyingType as NamedType
                : expression.InferredType as NamedType;
            if (inferredType is not null)
            {
                return inferredType;
            }

            if (expression is IdentifierExpression identifier)
            {
                var parameter = function.Parameters.FirstOrDefault(candidate =>
                    candidate.Name == identifier.Identifier.ToString());
                return parameter?.ParameterType is ConstType parameterConstant
                    ? parameterConstant.UnderlyingType as NamedType
                    : parameter?.ParameterType as NamedType;
            }

            if (expression is ConditionalExpression conditional &&
                GetAwaitableType(conditional.WhenTrue) is { } whenTrue &&
                GetAwaitableType(conditional.WhenFalse) is { } whenFalse &&
                whenTrue.ToString() == whenFalse.ToString())
            {
                return whenTrue;
            }

            if (expression is InvocationExpression invocation &&
                invocation.Target is IdentifierExpression callTarget &&
                callTarget.Identifier.Parts.Length == 1)
            {
                var callName = callTarget.Identifier.Parts[0];
                var candidateNames = new HashSet<string>(StringComparer.Ordinal)
                {
                    new QualifiedIdentifier(function.Namespace,
                        new QualifiedIdentifier(callName)).ToString(),
                };
                foreach (var import in imports)
                {
                    candidateNames.Add(new QualifiedIdentifier(import,
                        new QualifiedIdentifier(callName)).ToString());
                }

                var candidates = availableFunctions.Where(candidate =>
                    candidate.ParentClassDeclaration is null &&
                    candidate.Name == callName &&
                    candidate.GenericTypeNames.Length == 0 &&
                    candidate.Parameters.Count == invocation.Arguments.Count &&
                    candidateNames.Contains(candidate.FullName.ToString())).ToArray();
                if (candidates.Length == 1)
                {
                    return candidates[0].ReturnType is ConstType returnConstant
                        ? returnConstant.UnderlyingType as NamedType
                        : candidates[0].ReturnType as NamedType;
                }
            }

            return null;
        }

        var awaitableType = GetAwaitableType(awaitExpression.Operand);
        if (awaitableType is null)
        {
            return false;
        }
        var isValueTaskAwait = IsValueTaskType(awaitableType);
        var callbackSourceType = isValueTaskAwait
            ? awaitableType.TypeArguments.Count == 1
                ? NamedType.CreateWithTypeArguments(
                    "System.Threading.Tasks.Task", [awaitableType.TypeArguments[0]])
                : new NamedType("System.Threading.Tasks.Task", [])
            : awaitableType;

        var names = function.Parameters.Select(parameter => parameter.Name)
            .Concat(EnumerateStatements(body)
                .OfType<LocalVariableDeclarationStatement>()
                .SelectMany(declaration => declaration.Declarators)
                .Select(declarator => declarator.Name))
            .ToHashSet(StringComparer.Ordinal);
        var identitySubstitutions = CoroutineSyntaxAnalysis
            .EnumerateBodyExpressions(body)
            .OfType<IdentifierExpression>()
            .Select(identifier => identifier.Identifier.ToString())
            .Distinct(StringComparer.Ordinal)
            .ToDictionary(name => name, name => name, StringComparer.Ordinal);

        var callbackLiveNames = CoroutineSyntaxAnalysis.EnumerateBodyExpressions(
                continuationStatements.Append(new ExpressionStatement(loopCondition))
                    .Concat(loopIterators.Select(iterator =>
                        (StatementBase)new ExpressionStatement(iterator)))
                    .Append(new ExpressionStatement(awaitExpression.Operand))
                    .Append(new ExpressionStatement(
                        terminalReturn.Expression ?? new LiteralExpression("0"))))
            .OfType<IdentifierExpression>()
            .Select(identifier => identifier.Identifier.ToString())
            .ToHashSet(StringComparer.Ordinal);
        var callbackAliases = new Dictionary<string, (string Alias, TypeBase Type)>(
            StringComparer.Ordinal);
        foreach (var parameter in function.Parameters)
        {
            if (callbackLiveNames.Contains(parameter.Name))
            {
                callbackAliases.TryAdd(
                    parameter.Name,
                    ($"__cx_async_saved_{parameter.Name}", parameter.ParameterType));
            }
        }
        foreach (var declaration in body.Take(body.Count - 2)
                     .OfType<LocalVariableDeclarationStatement>()
                     .Concat(loopStatement is ForStatement { DeclarationInitializer: { } initializer }
                         ? [initializer]
                         : []))
        {
            foreach (var declarator in declaration.Declarators)
            {
                var type = declarator.Type ?? declaration.DeclaredType;
                if (callbackLiveNames.Contains(declarator.Name) && type is not AutoType)
                {
                    callbackAliases.TryAdd(
                        declarator.Name,
                        ($"__cx_async_saved_{declarator.Name}", type));
                }
            }
        }
        var callbackSubstitutions = identitySubstitutions.ToDictionary(
            pair => pair.Key,
            pair => callbackAliases.TryGetValue(pair.Key, out var alias)
                ? alias.Alias
                : pair.Value,
            StringComparer.Ordinal);

        bool TryCloneStatements(
            IEnumerable<StatementBase> statements,
            IReadOnlyDictionary<string, string> substitutions,
            out StatementBase[] clones)
        {
            var result = new List<StatementBase>();
            foreach (var statement in statements)
            {
                if (!TryCloneAwaitCallbackStatement(
                        statement, substitutions, out var clone))
                {
                    clones = [];
                    return false;
                }
                result.Add(clone);
            }
            clones = result.ToArray();
            return true;
        }

        bool TryCloneExpressions(
            IEnumerable<ExpressionBase> expressions,
            IReadOnlyDictionary<string, string> substitutions,
            out ExpressionBase[] clones)
        {
            var result = new List<ExpressionBase>();
            foreach (var expression in expressions)
            {
                if (!TryCloneValueTaskResultExpression(
                        expression, substitutions, out var clone))
                {
                    clones = [];
                    return false;
                }
                clone.SourceSpan = expression.SourceSpan;
                result.Add(clone);
            }
            clones = result.ToArray();
            return true;
        }

        if (!TryCloneStatements(continuationStatements, identitySubstitutions,
                out var inlineContinuationStatements) ||
            !TryCloneStatements(continuationStatements, callbackSubstitutions,
                out var resumedContinuationStatements) ||
            !TryCloneStatements(continuationStatements, callbackSubstitutions,
                out var firstResumedContinuationStatements) ||
            !TryCloneStatements(continuationStatements, callbackSubstitutions,
                out var resumedInlineContinuationStatements) ||
            !TryCloneExpressions(loopIterators, identitySubstitutions,
                out var inlineLoopIterators) ||
            !TryCloneExpressions(loopIterators, callbackSubstitutions,
                out var resumedLoopIterators) ||
            !TryCloneExpressions(loopIterators, callbackSubstitutions,
                out var firstResumedLoopIterators) ||
            !TryCloneExpressions(loopIterators, callbackSubstitutions,
                out var resumedInlineLoopIterators) ||
            !TryCloneValueTaskResultExpression(
                loopCondition, identitySubstitutions, out var inlineLoopCondition) ||
            !TryCloneValueTaskResultExpression(
                terminalReturn.Expression ?? new LiteralExpression("0"),
                identitySubstitutions, out var inlineTerminalResult) ||
            !TryCloneValueTaskResultExpression(
                loopCondition, callbackSubstitutions, out var callbackLoopCondition) ||
            !TryCloneValueTaskResultExpression(
                terminalReturn.Expression ?? new LiteralExpression("0"),
                callbackSubstitutions, out var callbackTerminalResult) ||
            !TryCloneValueTaskResultExpression(
                awaitExpression.Operand, callbackSubstitutions,
                out var callbackAwaitOperand))
        {
            return false;
        }
        inlineLoopCondition.SourceSpan = loopCondition.SourceSpan;
        inlineTerminalResult.SourceSpan = terminalReturn.Expression?.SourceSpan;

        var completionName = CreateUniqueName(names, "__cx_async_completion");
        var stepName = CreateUniqueName(names, "__cx_async_while_step");
        var breakName = CreateUniqueName(names, "__cx_async_while_break");
        var firstIterationName = CreateUniqueName(names, "__cx_async_loop_first");
        var runningName = CreateUniqueName(names, "__cx_async_while_running");
        var requestedName = CreateUniqueName(names, "__cx_async_while_requested");
        var sourceName = CreateUniqueName(names, "__cx_async_while_source");
        var callbackSourceName = CreateUniqueName(
            names, "__cx_async_while_callback_source");
        var firstCallbackSourceName = CreateUniqueName(
            names, "__cx_async_first_callback_source");
        var continuationName = CreateUniqueName(names, "__cx_async_while_continuation");
        var firstContinuationName = CreateUniqueName(
            names, "__cx_async_first_continuation");
        var cancellationName = CreateUniqueName(names, "__cx_async_while_canceled");
        var exceptionName = CreateUniqueName(names, "__cx_async_while_exception");
        IdentifierExpression Completion() => new(new QualifiedIdentifier(completionName));
        IdentifierExpression Step() => new(new QualifiedIdentifier(stepName));
        IdentifierExpression BreakRequested() => new(new QualifiedIdentifier(breakName));
        IdentifierExpression FirstIteration() =>
            new(new QualifiedIdentifier(firstIterationName));
        IdentifierExpression StepRunning() => new(new QualifiedIdentifier(runningName));
        IdentifierExpression StepRequested() => new(new QualifiedIdentifier(requestedName));
        IdentifierExpression Source() => new(new QualifiedIdentifier(sourceName));
        IdentifierExpression CallbackSource() =>
            new(new QualifiedIdentifier(callbackSourceName));
        IdentifierExpression FirstCallbackSource() =>
            new(new QualifiedIdentifier(firstCallbackSourceName));
        IdentifierExpression Continuation() =>
            new(new QualifiedIdentifier(continuationName));
        var resumeSpan = awaitExpression.SourceSpan ?? function.SourceSpan;
        var firstResumeSpan = loopStatement.SourceSpan is { } loopSpan
            ? loopSpan with { StartColumn = loopSpan.StartColumn + 1 }
            : function.SourceSpan;

        string SyntheticLambdaName(string role, SourceSpan? span)
        {
            var location = span ?? function.SourceSpan
                ?? throw new InvalidOperationException(
                    "A synthesized awaited-loop lambda requires a source location.");
            var pathHash = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(location.FilePath)))[..10];
            return $"__cx_async_loop_{pathHash}_{location.StartLine}_{location.StartColumn}_{role}";
        }

        var callbackType = new FunctionType(
            QualifiedIdentifier.Empty,
            BuiltInSystemTypes.Void,
            []);
        var resumedStatements = new List<StatementBase>();
        if (awaitedLocal is not null)
        {
            resumedStatements.Add(new LocalVariableDeclarationStatement(
                new AutoType(false),
                [new LocalVariableDeclarator(
                    awaitedLocal.Name,
                    new InvocationExpression(
                        new MemberAccessExpression(CallbackSource(), "GetResult"), []))]));
        }
        else
        {
            resumedStatements.Add(new ExpressionStatement(new InvocationExpression(
                new MemberAccessExpression(CallbackSource(), "GetResult"), [])));
        }
        resumedStatements.AddRange(RewriteAwaitLoopControlTransfers(
            resumedContinuationStatements,
            completionName,
            stepName,
            breakName,
            resumedLoopIterators,
            hasResult));
        resumedStatements.AddRange(resumedLoopIterators.Select(iterator =>
            (StatementBase)new ExpressionStatement(iterator)));
        resumedStatements.Add(new ExpressionStatement(
            new InvocationExpression(Step(), []) { SourceSpan = resumeSpan }));
        var resumedCallback = new LambdaExpression(
            [],
            null,
            [new TryStatement(
                new BlockStatement(resumedStatements),
                [
                    new CatchClause(
                        new NamedType("System.Threading.Tasks.OperationCanceledException", []),
                        cancellationName,
                        null,
                        new BlockStatement([new ExpressionStatement(
                            new InvocationExpression(
                                new MemberAccessExpression(Completion(), "SetCanceled"), []))])),
                    new CatchClause(
                        new NamedType("System.Exception", []),
                        exceptionName,
                        null,
                        new BlockStatement([new ExpressionStatement(
                            new InvocationExpression(
                                new MemberAccessExpression(Completion(), "SetException"),
                                [new IdentifierExpression(new QualifiedIdentifier(exceptionName))]))])),
                ],
                null)])
        {
            SourceSpan = awaitExpression.SourceSpan ?? function.SourceSpan,
            GeneratedCName = SyntheticLambdaName("resume", awaitExpression.SourceSpan),
        };

        var firstResumedStatements = new List<StatementBase>();
        if (awaitedLocal is not null)
        {
            firstResumedStatements.Add(new LocalVariableDeclarationStatement(
                awaitedLocal.Type ?? new AutoType(false),
                [new LocalVariableDeclarator(
                    awaitedLocal.Name,
                    new InvocationExpression(
                        new MemberAccessExpression(FirstCallbackSource(), "GetResult"), []))]));
        }
        else
        {
            firstResumedStatements.Add(new ExpressionStatement(new InvocationExpression(
                new MemberAccessExpression(FirstCallbackSource(), "GetResult"), [])));
        }
        firstResumedStatements.AddRange(RewriteAwaitLoopControlTransfers(
            firstResumedContinuationStatements,
            completionName,
            stepName,
            breakName,
            firstResumedLoopIterators,
            hasResult));
        firstResumedStatements.AddRange(firstResumedLoopIterators.Select(iterator =>
            (StatementBase)new ExpressionStatement(iterator)));
        firstResumedStatements.Add(new ExpressionStatement(
            new InvocationExpression(Step(), []) { SourceSpan = resumeSpan }));
        var firstResumedCallback = new LambdaExpression(
            [],
            null,
            [new TryStatement(
                new BlockStatement(firstResumedStatements),
                [
                    new CatchClause(
                        new NamedType("System.Threading.Tasks.OperationCanceledException", []),
                        cancellationName,
                        null,
                        new BlockStatement([new ExpressionStatement(
                            new InvocationExpression(
                                new MemberAccessExpression(Completion(), "SetCanceled"), []))])),
                    new CatchClause(
                        new NamedType("System.Exception", []),
                        exceptionName,
                        null,
                        new BlockStatement([new ExpressionStatement(
                            new InvocationExpression(
                                new MemberAccessExpression(Completion(), "SetException"),
                                [new IdentifierExpression(new QualifiedIdentifier(exceptionName))]))]))
                ],
                null)])
        {
            SourceSpan = firstResumeSpan,
            GeneratedCName = SyntheticLambdaName("first_resume", firstResumeSpan),
        };

        var scheduleOrRun = new IfStatement(
            new MemberAccessExpression(Source(), "IsCompleted"),
            new ExpressionStatement(new InvocationExpression(Continuation(), [])
            {
                SourceSpan = resumeSpan,
            }),
                new ExpressionStatement(new InvocationExpression(
                    new MemberAccessExpression(Source(), "OnCompleted"),
                    [Continuation()])));
        var resumedInlineStatements = new List<StatementBase>();
        if (awaitedLocal is not null)
        {
            resumedInlineStatements.Add(new LocalVariableDeclarationStatement(
                awaitedLocal.Type ?? new AutoType(false),
                [new LocalVariableDeclarator(
                    awaitedLocal.Name,
                    new InvocationExpression(
                        new MemberAccessExpression(Source(), "GetResult"), []))]));
        }
        else
        {
            resumedInlineStatements.Add(new ExpressionStatement(new InvocationExpression(
                new MemberAccessExpression(Source(), "GetResult"), [])));
        }
        resumedInlineStatements.AddRange(RewriteAwaitLoopControlTransfers(
            resumedInlineContinuationStatements,
            completionName,
            stepName,
            breakName,
            resumedInlineLoopIterators,
            hasResult,
            insideStepLoop: true));
        resumedInlineStatements.AddRange(resumedInlineLoopIterators.Select(iterator =>
            (StatementBase)new ExpressionStatement(iterator)));
        resumedInlineStatements.Add(new ExpressionStatement(new AssignmentExpression(
            StepRequested(), "=", new LiteralExpression("true"))));
        var pendingAwaitIteration = new BlockStatement([
            new LocalVariableDeclarationStatement(
                callbackSourceType,
                [new LocalVariableDeclarator(
                    callbackSourceName,
                    isValueTaskAwait
                        ? new InvocationExpression(
                            new MemberAccessExpression(Source(), "AsTask"), [])
                        : Source())]),
            new LocalVariableDeclarationStatement(
                callbackType,
                [new LocalVariableDeclarator(continuationName, resumedCallback)]),
            new IfStatement(
                new MemberAccessExpression(CallbackSource(), "IsCompleted"),
                new ExpressionStatement(new InvocationExpression(Continuation(), [])
                {
                    SourceSpan = resumeSpan,
                }),
                new ExpressionStatement(new InvocationExpression(
                    new MemberAccessExpression(CallbackSource(), "OnCompleted"),
                    [Continuation()]))),
        ]);
        var awaitIteration = new BlockStatement([
            new LocalVariableDeclarationStatement(
                new AutoType(false),
                [new LocalVariableDeclarator(sourceName, callbackAwaitOperand)]),
            new IfStatement(
                new MemberAccessExpression(Source(), "IsCompleted"),
                new BlockStatement(resumedInlineStatements),
                pendingAwaitIteration),
        ]);
        var completeLoop = new BlockStatement([
            new ExpressionStatement(new AssignmentExpression(
                StepRunning(), "=", new LiteralExpression("false"))),
            new ExpressionStatement(new InvocationExpression(
                new MemberAccessExpression(Completion(), "SetResult"),
                hasResult ? [callbackTerminalResult] : [])),
            new ReturnStatement(null),
        ]);
        ExpressionBase continueCondition = isDoWhile
            ? new BinaryExpression(
                new UnaryExpression("!", FirstIteration()),
                "&&",
                new UnaryExpression("!", callbackLoopCondition))
            : new UnaryExpression("!", callbackLoopCondition);
        var beforeAwait = new List<StatementBase>();
        if (isDoWhile)
        {
            beforeAwait.Add(new ExpressionStatement(new AssignmentExpression(
                FirstIteration(), "=", new LiteralExpression("false"))));
        }
        beforeAwait.Add(awaitIteration);
        var runNextIteration = new BlockStatement([
            new ExpressionStatement(new AssignmentExpression(
                StepRequested(), "=", new LiteralExpression("false"))),
            new IfStatement(
                new BinaryExpression(
                    BreakRequested(),
                    "||",
                    continueCondition),
                completeLoop,
                new BlockStatement(beforeAwait)),
            new IfStatement(
                new UnaryExpression("!", StepRequested()),
                new BlockStatement([
                    new ExpressionStatement(new AssignmentExpression(
                        StepRunning(), "=", new LiteralExpression("false"))),
                    new ReturnStatement(null),
                ]),
                null),
        ]);
        var step = new LambdaExpression(
            [],
            null,
            [new IfStatement(
                StepRunning(),
                new BlockStatement([
                    new ExpressionStatement(new AssignmentExpression(
                        StepRequested(), "=", new LiteralExpression("true"))),
                    new ReturnStatement(null),
                ]),
                null),
             new ExpressionStatement(new AssignmentExpression(
                 StepRunning(), "=", new LiteralExpression("true"))),
             new WhileStatement(
                 new LiteralExpression("true"),
                 new BlockStatement(runNextIteration.Statements))])
        {
            SourceSpan = loopStatement.SourceSpan ?? function.SourceSpan,
            GeneratedCName = SyntheticLambdaName("step", loopStatement.SourceSpan),
        };

        var completionSourceType = hasResult
            ? NamedType.CreateWithTypeArguments(
                "System.Threading.Tasks.TaskCompletionSource",
                [taskType.TypeArguments[0]])
            : new NamedType("System.Threading.Tasks.TaskCompletionSource", []);

        ExpressionBase pendingCallbackSource = isValueTaskAwait
            ? new InvocationExpression(
                new MemberAccessExpression(Source(), "AsTask"), [])
            : Source();
        var pendingStatements = callbackAliases.Select(pair =>
            (StatementBase)new LocalVariableDeclarationStatement(
                pair.Value.Type,
                [new LocalVariableDeclarator(
                    pair.Value.Alias,
                    new IdentifierExpression(new QualifiedIdentifier(pair.Key)))]))
            .ToList();
        pendingStatements.AddRange([
            new LocalVariableDeclarationStatement(
                completionSourceType,
                [new LocalVariableDeclarator(
                    completionName,
                    new ObjectCreationExpression(completionSourceType, []))]),
            new LocalVariableDeclarationStatement(
                BuiltInSystemTypes.Bool,
                [new LocalVariableDeclarator(breakName, new LiteralExpression("false"))]),
        ]);
        if (isDoWhile)
        {
            pendingStatements.Add(new LocalVariableDeclarationStatement(
                BuiltInSystemTypes.Bool,
                [new LocalVariableDeclarator(
                    firstIterationName, new LiteralExpression("false"))]));
        }
        pendingStatements.AddRange([
            new LocalVariableDeclarationStatement(
                BuiltInSystemTypes.Bool,
                [new LocalVariableDeclarator(runningName, new LiteralExpression("false"))]),
            new LocalVariableDeclarationStatement(
                BuiltInSystemTypes.Bool,
                [new LocalVariableDeclarator(requestedName, new LiteralExpression("false"))]),
            new LocalVariableDeclarationStatement(
                callbackType,
                [new LocalVariableDeclarator(stepName, null)]),
            new ExpressionStatement(new AssignmentExpression(Step(), "=", step)),
            new LocalVariableDeclarationStatement(
                callbackSourceType,
                [new LocalVariableDeclarator(
                    firstCallbackSourceName, pendingCallbackSource)]),
            new LocalVariableDeclarationStatement(
                callbackType,
                [new LocalVariableDeclarator(firstContinuationName, firstResumedCallback)]),
            new ExpressionStatement(new InvocationExpression(
                new MemberAccessExpression(FirstCallbackSource(), "OnCompleted"),
                [new IdentifierExpression(new QualifiedIdentifier(firstContinuationName))])),
            new ReturnStatement(new MemberAccessExpression(Completion(), "Task")),
        ]);

        var inlineAwaitStatements = new List<StatementBase>();
        if (awaitedLocal is not null)
        {
            inlineAwaitStatements.Add(new LocalVariableDeclarationStatement(
                awaitedLocal.Type ?? new AutoType(false),
                [new LocalVariableDeclarator(
                    awaitedLocal.Name,
                    new InvocationExpression(
                        new MemberAccessExpression(Source(), "GetResult"), []))]));
        }
        else
        {
            inlineAwaitStatements.Add(new ExpressionStatement(new InvocationExpression(
                new MemberAccessExpression(Source(), "GetResult"), [])));
        }
        inlineAwaitStatements.AddRange(RewriteInlineAwaitLoopReturns(
            inlineContinuationStatements, taskType, hasResult));
        var inlineAwaitBranch = new IfStatement(
            new MemberAccessExpression(Source(), "IsCompleted"),
            new BlockStatement(inlineAwaitStatements),
            new BlockStatement(pendingStatements))
        {
            SourceSpan = awaitExpression.SourceSpan ?? function.SourceSpan,
        };
        var inlineAwaitIteration = new BlockStatement([
            new LocalVariableDeclarationStatement(
                new AutoType(false),
                [new LocalVariableDeclarator(
                    sourceName, CloneAwaitOperand(awaitExpression.Operand))]),
            inlineAwaitBranch,
        ]);
        StatementBase inlineLoop = loopStatement switch
        {
            WhileStatement loop => new WhileStatement(
                inlineLoopCondition, inlineAwaitIteration) { SourceSpan = loop.SourceSpan },
            DoWhileStatement loop => new DoWhileStatement(
                inlineAwaitIteration, inlineLoopCondition) { SourceSpan = loop.SourceSpan },
            ForStatement loop => new ForStatement(
                loop.DeclarationInitializer,
                loop.InitializerExpressions,
                loop.Condition is null ? null : inlineLoopCondition,
                inlineLoopIterators,
                inlineAwaitIteration) { SourceSpan = loop.SourceSpan },
            _ => loopStatement,
        };

        var loweredBody = new List<StatementBase>();
        loweredBody.AddRange(body.Take(body.Count - 2));
        loweredBody.Add(inlineLoop);
        loweredBody.Add(new ReturnStatement(new ObjectCreationExpression(
            taskType,
            hasResult ? [inlineTerminalResult] : []))
        {
            SourceSpan = terminalReturn.SourceSpan,
        });
        function.SetBody(loweredBody);
        return true;
    }

    private static IReadOnlyList<StatementBase> RewriteInlineAwaitLoopReturns(
        IEnumerable<StatementBase> statements,
        NamedType taskType,
        bool hasResult) => statements.SelectMany(statement =>
            RewriteInlineAwaitLoopReturns(statement, taskType, hasResult)).ToArray();

    private static IReadOnlyList<StatementBase> RewriteInlineAwaitLoopReturns(
        StatementBase statement,
        NamedType taskType,
        bool hasResult)
    {
        IReadOnlyList<StatementBase> RewriteMany(IEnumerable<StatementBase> nested) =>
            nested.SelectMany(item =>
                RewriteInlineAwaitLoopReturns(item, taskType, hasResult)).ToArray();

        return statement switch
        {
            ReturnStatement { Expression: { } expression } returned when hasResult =>
            [new ReturnStatement(new ObjectCreationExpression(taskType, [expression]))
            {
                SourceSpan = returned.SourceSpan,
            }],
            ReturnStatement { Expression: null } returned when !hasResult =>
            [new ReturnStatement(new ObjectCreationExpression(taskType, []))
            {
                SourceSpan = returned.SourceSpan,
            }],
            BlockStatement block =>
            [new BlockStatement(RewriteMany(block.Statements), block.IsUnsafe)
            {
                SourceSpan = block.SourceSpan,
            }],
            IfStatement conditional =>
            [new IfStatement(
                conditional.Condition,
                RewriteInlineAwaitLoopReturns(
                    conditional.ThenStatement, taskType, hasResult).Single(),
                conditional.ElseStatement is null
                    ? null
                    : RewriteInlineAwaitLoopReturns(
                        conditional.ElseStatement, taskType, hasResult).Single())
            {
                SourceSpan = conditional.SourceSpan,
            }],
            _ => [statement],
        };
    }

    private static bool TryNormalizeLoopTransferAwaitedFinally(
        FunctionDeclaration function,
        bool hasResult)
    {
        var body = function.Body;
        if (body is null || body.Count != (hasResult ? 2 : 1) ||
            (hasResult
                ? body[1] is not ReturnStatement { Expression: not null }
                : false))
        {
            return false;
        }

        var loopStatement = body[0];
        var loopBody = loopStatement switch
        {
            WhileStatement loop => loop.Body,
            DoWhileStatement loop => loop.Body,
            ForStatement loop => loop.Body,
            _ => null,
        };
        if (loopBody is not BlockStatement { Statements: [TryStatement protectedTransfer] } ||
            protectedTransfer.CatchClauses.Count != 0 ||
            protectedTransfer.Body is not BlockStatement { Statements: [BreakStatement or ContinueStatement] } transferBody ||
            protectedTransfer.FinallyBody is not BlockStatement
            {
                Statements:
                [ExpressionStatement { Expression: AwaitExpression
                    { Operand: IdentifierExpression } awaitedCleanup }],
            })
        {
            return false;
        }

        var resumedLoopBody = new BlockStatement([
            new ExpressionStatement(awaitedCleanup)
            {
                SourceSpan = awaitedCleanup.SourceSpan ?? protectedTransfer.SourceSpan,
            },
            transferBody.Statements[0],
        ])
        {
            SourceSpan = protectedTransfer.SourceSpan,
        };
        StatementBase normalizedLoop = loopStatement switch
        {
            WhileStatement loop => new WhileStatement(loop.Condition, resumedLoopBody)
            {
                SourceSpan = loop.SourceSpan,
            },
            DoWhileStatement loop => new DoWhileStatement(resumedLoopBody, loop.Condition)
            {
                SourceSpan = loop.SourceSpan,
            },
            ForStatement loop => new ForStatement(
                loop.DeclarationInitializer,
                loop.InitializerExpressions,
                loop.Condition,
                loop.Iterators,
                resumedLoopBody)
            {
                SourceSpan = loop.SourceSpan,
            },
            _ => throw new InvalidOperationException(),
        };
        function.SetBody(hasResult ? [normalizedLoop, body[1]] : [normalizedLoop]);
        return true;
    }

    private static bool CanLowerAwaitLoopControlTransfers(
        IEnumerable<StatementBase> statements,
        bool hasResult)
    {
        foreach (var statement in EnumerateStatements(statements))
        {
            if (statement is WhileStatement or DoWhileStatement or ForStatement or ForeachStatement or
                SwitchStatement or TryStatement or LocalFunctionDeclarationStatement ||
                statement is ReturnStatement returned &&
                (hasResult ? returned.Expression is null : returned.Expression is not null))
            {
                return false;
            }
        }

        return true;
    }

    private static IReadOnlyList<StatementBase> RewriteAwaitLoopControlTransfers(
        IEnumerable<StatementBase> statements,
        string completionName,
        string stepName,
        string breakName,
        IReadOnlyList<ExpressionBase> iterators,
        bool hasResult,
        bool insideStepLoop = false)
    {
        return statements.SelectMany(Rewrite).ToArray();

        IReadOnlyList<StatementBase> Rewrite(StatementBase statement)
        {
            IdentifierExpression Completion() =>
                new(new QualifiedIdentifier(completionName));
            IdentifierExpression Step() => new(new QualifiedIdentifier(stepName));
            IdentifierExpression BreakRequested() => new(new QualifiedIdentifier(breakName));
            var sourceSpan = statement.SourceSpan;

            switch (statement)
            {
                case ReturnStatement { Expression: { } result }:
                    return
                    [
                        new ExpressionStatement(new InvocationExpression(
                            new MemberAccessExpression(Completion(), "SetResult"), [result]))
                        {
                            SourceSpan = sourceSpan,
                        },
                        new ReturnStatement(null) { SourceSpan = sourceSpan },
                    ];
                case ReturnStatement { Expression: null } when !hasResult:
                    return
                    [
                        new ExpressionStatement(new InvocationExpression(
                            new MemberAccessExpression(Completion(), "SetResult"), []))
                        {
                            SourceSpan = sourceSpan,
                        },
                        new ReturnStatement(null) { SourceSpan = sourceSpan },
                    ];
                case BreakStatement:
                    var breakTransfer = new List<StatementBase>
                    {
                        new ExpressionStatement(new AssignmentExpression(
                            BreakRequested(), "=", new LiteralExpression("true")))
                        {
                            SourceSpan = sourceSpan,
                        },
                    };
                    if (insideStepLoop)
                    {
                        breakTransfer.Add(new ContinueStatement { SourceSpan = sourceSpan });
                        return breakTransfer;
                    }
                    breakTransfer.Add(new ExpressionStatement(new InvocationExpression(Step(), [])
                    {
                        SourceSpan = sourceSpan,
                    }));
                    breakTransfer.Add(new ReturnStatement(null) { SourceSpan = sourceSpan });
                    return breakTransfer;
                case ContinueStatement:
                    var continueTransfer = iterators
                        .Select(iterator => (StatementBase)new ExpressionStatement(iterator))
                        .ToList();
                    if (insideStepLoop)
                    {
                        continueTransfer.Add(new ContinueStatement { SourceSpan = sourceSpan });
                        return continueTransfer;
                    }
                    continueTransfer.Add(new ExpressionStatement(new InvocationExpression(Step(), [])
                    {
                        SourceSpan = sourceSpan,
                    }));
                    continueTransfer.Add(new ReturnStatement(null) { SourceSpan = sourceSpan });
                    return continueTransfer;
                case BlockStatement block:
                    return
                    [
                        new BlockStatement(
                            block.Statements.SelectMany(Rewrite).ToArray(),
                            block.IsUnsafe)
                        {
                            SourceSpan = sourceSpan,
                        },
                    ];
                case IfStatement conditional:
                    return
                    [
                        new IfStatement(
                            conditional.Condition,
                            AsSingle(Rewrite(conditional.ThenStatement), conditional.ThenStatement),
                            conditional.ElseStatement is null
                                ? null
                                : AsSingle(Rewrite(conditional.ElseStatement), conditional.ElseStatement))
                        {
                            SourceSpan = sourceSpan,
                        },
                    ];
                default:
                    return [statement];
            }
        }

        static StatementBase AsSingle(
            IReadOnlyList<StatementBase> rewritten,
            StatementBase original) => rewritten.Count == 1
                ? rewritten[0]
                : new BlockStatement(rewritten) { SourceSpan = original.SourceSpan };
    }

    private static bool TryGetAwaitingLoopBody(
        StatementBase statement,
        out AwaitExpression awaitExpression,
        out LocalVariableDeclarator awaitedLocal,
        out StatementBase[] continuation)
    {
        if (statement is BlockStatement { Statements.Count: >= 1 } block &&
            block.Statements[0] is LocalVariableDeclarationStatement declaration &&
            declaration.DeclaredType is not AutoType { Const: true } &&
            declaration.Declarators is [{ Initializer: AwaitExpression awaited } local] &&
            block.Statements.Skip(1).All(item =>
                !CoroutineSyntaxAnalysis.StatementContainsAwait(item)))
        {
            awaitExpression = awaited;
            awaitedLocal = local;
            continuation = block.Statements.Skip(1).ToArray();
            return true;
        }

        awaitExpression = null!;
        awaitedLocal = null!;
        continuation = [];
        return false;
    }

    private static bool TryGetStandaloneAwaitLoopBody(
        StatementBase statement,
        out AwaitExpression awaitExpression,
        out LocalVariableDeclarator? awaitedLocal,
        out StatementBase[] continuation)
    {
        if (statement is BlockStatement { Statements.Count: >= 1 } block &&
            block.Statements[0] is ExpressionStatement { Expression: AwaitExpression awaited } &&
            block.Statements.Skip(1).All(item =>
                !CoroutineSyntaxAnalysis.StatementContainsAwait(item)))
        {
            awaitExpression = awaited;
            awaitedLocal = null;
            continuation = block.Statements.Skip(1).ToArray();
            return true;
        }

        awaitExpression = null!;
        awaitedLocal = null;
        continuation = [];
        return false;
    }

    private static bool AlwaysReturnsTaskResult(IfStatement conditional) =>
        conditional.ElseStatement is not null &&
        StatementAlwaysReturnsTaskResult(conditional.ThenStatement) &&
        StatementAlwaysReturnsTaskResult(conditional.ElseStatement);

    private static bool StatementAlwaysReturnsTaskResult(StatementBase statement) => statement switch
    {
        ReturnStatement { Expression: not null } => true,
        BlockStatement { Statements.Count: > 0 } block =>
            StatementAlwaysReturnsTaskResult(block.Statements[^1]),
        IfStatement conditional => AlwaysReturnsTaskResult(conditional),
        _ => false,
    };

    private static bool CanRewriteTaskResultReturns(
        IEnumerable<StatementBase> statements) =>
        statements.All(CanRewriteTaskResultReturns);

    private static bool CanRewriteTaskResultReturns(StatementBase statement) => statement switch
    {
        ReturnStatement { Expression: not null } => true,
        ReturnStatement => false,
        BlockStatement block => CanRewriteTaskResultReturns(block.Statements),
        IfStatement conditional =>
            CanRewriteTaskResultReturns([conditional.ThenStatement]) &&
            (conditional.ElseStatement is null ||
             CanRewriteTaskResultReturns([conditional.ElseStatement])),
        WhileStatement loop => CanRewriteTaskResultReturns([loop.Body]),
        DoWhileStatement loop => CanRewriteTaskResultReturns([loop.Body]),
        ForStatement loop => CanRewriteTaskResultReturns([loop.Body]),
        SwitchStatement switchStatement => switchStatement.Sections.All(section =>
            CanRewriteTaskResultReturns(section.Statements)),
        TryStatement tryStatement =>
            CanRewriteTaskResultReturns([tryStatement.Body]) &&
            tryStatement.CatchClauses.All(clause =>
                CanRewriteTaskResultReturns([clause.Body])) &&
            (tryStatement.FinallyBody is null ||
             (!EnumerateStatements([tryStatement.FinallyBody])
                 .Any(nested => nested is ReturnStatement) &&
              CanRewriteTaskResultReturns([tryStatement.FinallyBody]))) &&
            (tryStatement.FinallyBody is null ||
             (!EnumerateStatements([tryStatement.Body])
                  .Concat(tryStatement.CatchClauses.SelectMany(clause =>
                      EnumerateStatements([clause.Body])))
                  .Any(nested => nested is ReturnStatement))),
        ForeachStatement loop =>
            !EnumerateStatements([loop.Body]).Any(nested => nested is ReturnStatement),
        LocalFunctionDeclarationStatement => true,
        _ => true,
    };

    private static IReadOnlyList<StatementBase> RewriteTaskResultReturns(
        StatementBase statement,
        string completionName)
    {
        IdentifierExpression Completion() =>
            new(new QualifiedIdentifier(completionName));
        IReadOnlyList<StatementBase> RewriteMany(IEnumerable<StatementBase> statements) =>
            statements.SelectMany(nested =>
                RewriteTaskResultReturns(nested, completionName)).ToArray();
        StatementBase RewriteSingle(StatementBase nested)
        {
            var rewritten = RewriteTaskResultReturns(nested, completionName);
            return rewritten.Count == 1
                ? rewritten[0]
                : new BlockStatement(rewritten)
                {
                    SourceSpan = nested.SourceSpan,
                };
        }

        switch (statement)
        {
            case ReturnStatement { Expression: { } expression } returnStatement:
                return
                [
                    new ExpressionStatement(new InvocationExpression(
                        new MemberAccessExpression(Completion(), "SetResult"), [expression]))
                    {
                        SourceSpan = returnStatement.SourceSpan,
                    },
                    new ReturnStatement(null)
                    {
                        SourceSpan = returnStatement.SourceSpan,
                    },
                ];
            case BlockStatement block:
                return
                [
                    new BlockStatement(RewriteMany(block.Statements), block.IsUnsafe)
                    {
                        SourceSpan = block.SourceSpan,
                    },
                ];
            case IfStatement conditional:
                return
                [
                    new IfStatement(
                        conditional.Condition,
                        RewriteSingle(conditional.ThenStatement),
                        conditional.ElseStatement is null
                            ? null
                            : RewriteSingle(conditional.ElseStatement))
                    {
                        SourceSpan = conditional.SourceSpan,
                    },
                ];
            case WhileStatement loop:
                return
                [
                    new WhileStatement(loop.Condition, RewriteSingle(loop.Body))
                    {
                        SourceSpan = loop.SourceSpan,
                    },
                ];
            case DoWhileStatement loop:
                return
                [
                    new DoWhileStatement(RewriteSingle(loop.Body), loop.Condition)
                    {
                        SourceSpan = loop.SourceSpan,
                    },
                ];
            case ForStatement loop:
                return
                [
                    new ForStatement(
                        loop.DeclarationInitializer,
                        loop.InitializerExpressions,
                        loop.Condition,
                        loop.Iterators,
                        RewriteSingle(loop.Body))
                    {
                        SourceSpan = loop.SourceSpan,
                    },
                ];
            case SwitchStatement switchStatement:
                return
                [
                    new SwitchStatement(
                        switchStatement.Expression,
                        switchStatement.Sections.Select(section => new SwitchSection(
                            section.Labels,
                            RewriteMany(section.Statements))).ToArray())
                    {
                        SourceSpan = switchStatement.SourceSpan,
                    },
                ];
            case TryStatement tryStatement:
                return
                [
                    new TryStatement(
                        RewriteSingle(tryStatement.Body),
                        tryStatement.CatchClauses.Select(clause => new CatchClause(
                            clause.ExceptionType,
                            clause.VariableName,
                            clause.Filter,
                            RewriteSingle(clause.Body))).ToArray(),
                        tryStatement.FinallyBody)
                    {
                        SourceSpan = tryStatement.SourceSpan,
                    },
                ];
            default:
                return [statement];
        }
    }

    private static bool TryGetReturnAwait(
        FunctionDeclaration function,
        out AwaitExpression awaitExpression)
    {
        if (function.Body is
            [ReturnStatement { Expression: AwaitExpression directAwait }])
        {
            awaitExpression = directAwait;
            return true;
        }

        if (function.Body is
            [
                LocalVariableDeclarationStatement
                {
                    DeclaredType: AutoType { Const: false },
                    Declarators:
                    [
                        {
                            Initializer: AwaitExpression localAwait,
                        } localDeclarator,
                    ],
                },
                ReturnStatement
                {
                    Expression: IdentifierExpression returnedIdentifier,
                },
            ] && returnedIdentifier.Identifier.ToString() == localDeclarator.Name &&
            !function.Parameters.Any(parameter => parameter.Name == localDeclarator.Name))
        {
            awaitExpression = localAwait;
            return true;
        }

        awaitExpression = null!;
        return false;
    }

    private static bool TryLowerVoidAwait(
        FunctionDeclaration function,
        NamedType taskType)
    {
        if (TryLowerSingleAwaitVoidTail(function, taskType))
        {
            return true;
        }

        if (TryLowerVoidAwaitedBodyFinally(function))
        {
            return true;
        }
        if (TryLowerVoidAwaitedReturnFinally(function))
        {
            return true;
        }
        if (TryLowerVoidAwaitedFinally(function))
        {
            return true;
        }
        if (TryLowerVoidAwaitContinuation(function))
        {
            return true;
        }
        if (function.Body is not
            [ExpressionStatement { Expression: AwaitExpression awaitExpression }])
        {
            return false;
        }

        var awaitedTaskType = new NamedType("System.Threading.Tasks.Task", []);
        var completionSourceType = new NamedType(
            "System.Threading.Tasks.TaskCompletionSource", []);
        var names = function.Parameters.Select(parameter => parameter.Name)
            .ToHashSet(StringComparer.Ordinal);
        var sourceName = CreateUniqueName(names, "__cx_async_source");
        var completionName = CreateUniqueName(names, "__cx_async_completion");
        var source = new IdentifierExpression(new QualifiedIdentifier(sourceName));
        var completion = new IdentifierExpression(
            new QualifiedIdentifier(completionName));
        var getResult = new ExpressionStatement(new InvocationExpression(
            new MemberAccessExpression(source, "GetResult"), []));
        var body = new StatementBase[]
        {
            CreateInlineValueTaskCompletionBranch(
                function, taskType, awaitExpression)!,
            new LocalVariableDeclarationStatement(awaitedTaskType,
            [new LocalVariableDeclarator(
                sourceName,
                NormalizeParameterValueTaskAwaitOperand(function, awaitExpression.Operand))]),
            new IfStatement(
                new MemberAccessExpression(source, "IsCompleted"),
                new BlockStatement([
                    getResult,
                    new ReturnStatement(new ObjectCreationExpression(taskType, [])),
                ]),
                null),
            new LocalVariableDeclarationStatement(completionSourceType,
            [new LocalVariableDeclarator(
                completionName,
                new ObjectCreationExpression(completionSourceType, []))]),
            new ExpressionStatement(new InvocationExpression(
                new MemberAccessExpression(
                    new IdentifierExpression(new QualifiedIdentifier(
                        "System", "Threading", "Tasks", "TaskRuntime")),
                    "ForwardVoid"), [source, completion])),
            new ReturnStatement(new CastExpression(
                taskType,
                new InvocationExpression(
                    new MemberAccessExpression(
                        new IdentifierExpression(new QualifiedIdentifier(
                            "System", "Threading", "Tasks", "TaskRuntime")),
                        "GetVoidTask"),
                    [completion]))),
        };
        if (body[0] is not IfStatement { ElseStatement: null })
        {
            body = body.Skip(1).ToArray();
        }
        function.SetBody(body);
        return true;
    }

    private static bool TryLowerSingleAwaitVoidTail(
        FunctionDeclaration function,
        NamedType taskType)
    {
        if (function.GenericTypeNames.Length != 0 ||
            function.Body is not
            [
                ExpressionStatement { Expression: AwaitExpression awaitExpression },
                ReturnStatement { Expression: null },
            ])
        {
            return false;
        }

        var operandType = awaitExpression.Operand.InferredType;
        if (operandType is null &&
            awaitExpression.Operand is IdentifierExpression operandIdentifier)
        {
            operandType = function.Parameters.FirstOrDefault(parameter =>
                parameter.Name == operandIdentifier.Identifier.ToString())?.ParameterType;
        }
        operandType = operandType is ConstType constant
            ? constant.UnderlyingType
            : operandType;
        var usesValueTask = operandType is NamedType valueTaskType &&
            IsValueTaskType(valueTaskType);
        if (usesValueTask && awaitExpression.Operand is not IdentifierExpression)
        {
            return false;
        }
        var taskProducingInvocation = operandType is null &&
            awaitExpression.Operand is InvocationExpression;
        if (!usesValueTask && !taskProducingInvocation &&
            (operandType is not NamedType taskOperandType ||
             !IsNonGenericTaskType(taskOperandType)))
        {
            return false;
        }

        var sourceTaskType = new NamedType("System.Threading.Tasks.Task", []);
        var completionSourceType = new NamedType(
            "System.Threading.Tasks.TaskCompletionSource", []);
        var names = function.Parameters.Select(candidate => candidate.Name)
            .ToHashSet(StringComparer.Ordinal);
        var sourceName = CreateUniqueName(names, "__cx_async_void_value_source");
        var completionName = CreateUniqueName(names, "__cx_async_void_completion");
        var resultTaskName = CreateUniqueName(names, "__cx_async_void_result");
        var callbackSourceName = CreateUniqueName(names, "__cx_async_void_callback_source");
        var callbackTaskName = CreateUniqueName(names, "__cx_async_void_callback_task");
        var continuationName = CreateUniqueName(names, "__cx_async_void_continuation");
        var cancellationName = CreateUniqueName(names, "__cx_async_void_canceled");
        var exceptionName = CreateUniqueName(names, "__cx_async_void_exception");
        IdentifierExpression Id(string name) =>
            new(new QualifiedIdentifier(name));

        var inlineBlock = new BlockStatement([
            new ExpressionStatement(new InvocationExpression(
                new MemberAccessExpression(
                    usesValueTask ? awaitExpression.Operand : Id(sourceName),
                    "GetResult"), [])),
            new ReturnStatement(new ObjectCreationExpression(taskType, [])),
        ]);

        var callbackSource = Id(callbackSourceName);
        var callbackTask = Id(callbackTaskName);
        var callback = new LambdaExpression(
            [], null,
            [new TryStatement(
                new BlockStatement([
                    new ExpressionStatement(new InvocationExpression(
                        new MemberAccessExpression(callbackSource, "GetResult"), [])),
                    new ExpressionStatement(new InvocationExpression(
                        new MemberAccessExpression(callbackTask, "SetResult"), [])),
                ]),
                [
                    new CatchClause(
                        new NamedType(
                            "System.Threading.Tasks.OperationCanceledException", []),
                        cancellationName,
                        null,
                        new BlockStatement([new ExpressionStatement(
                            new InvocationExpression(
                                new MemberAccessExpression(
                                    callbackTask, "SetCanceled"), []))])),
                    new CatchClause(
                        new NamedType("System.Exception", []),
                        exceptionName,
                        null,
                        new BlockStatement([new ExpressionStatement(
                            new InvocationExpression(
                                new MemberAccessExpression(
                                    callbackTask, "SetFault"),
                                [Id(exceptionName)]))])),
                ], null)])
        {
            SourceSpan = awaitExpression.SourceSpan ?? function.SourceSpan,
        };

        var pendingStatements = new List<StatementBase>();
        if (usesValueTask)
        {
            pendingStatements.Add(new LocalVariableDeclarationStatement(
                sourceTaskType,
                [new LocalVariableDeclarator(sourceName,
                    new InvocationExpression(
                        new MemberAccessExpression(awaitExpression.Operand, "AsTask"), []))]));
        }
        pendingStatements.AddRange([
            new LocalVariableDeclarationStatement(
                completionSourceType,
                [new LocalVariableDeclarator(completionName,
                    new ObjectCreationExpression(completionSourceType, []))]),
            new LocalVariableDeclarationStatement(
                sourceTaskType,
                [new LocalVariableDeclarator(callbackSourceName, Id(sourceName))]),
            new LocalVariableDeclarationStatement(
                sourceTaskType,
                [new LocalVariableDeclarator(callbackTaskName,
                    new MemberAccessExpression(Id(completionName), "Task"))]),
            new LocalVariableDeclarationStatement(
                sourceTaskType,
                [new LocalVariableDeclarator(resultTaskName,
                    new MemberAccessExpression(Id(completionName), "Task"))]),
            new LocalVariableDeclarationStatement(
                new FunctionType(QualifiedIdentifier.Empty,
                    BuiltInSystemTypes.Void, []),
                [new LocalVariableDeclarator(continuationName, callback)]),
            new ExpressionStatement(new InvocationExpression(
                new MemberAccessExpression(Id(sourceName), "OnCompleted"),
                [Id(continuationName)])),
            new ReturnStatement(Id(resultTaskName)),
        ]);

        var loweredBody = new List<StatementBase>();
        if (!usesValueTask)
        {
            loweredBody.Add(new LocalVariableDeclarationStatement(
                sourceTaskType,
                [new LocalVariableDeclarator(sourceName, awaitExpression.Operand)]));
        }
        loweredBody.Add(
            new IfStatement(
                new MemberAccessExpression(
                    usesValueTask ? awaitExpression.Operand : Id(sourceName),
                    "IsCompleted"),
                inlineBlock,
                new BlockStatement(pendingStatements))
            {
                SourceSpan = awaitExpression.SourceSpan ?? function.SourceSpan,
            });
        function.SetBody(loweredBody);
        return true;
    }

    private static bool TryLowerVoidAwaitedReturnFinally(FunctionDeclaration function)
    {
        if (function.Body is not
            [TryStatement
            {
                Body: BlockStatement { Statements: [ReturnStatement { Expression: null }] },
                CatchClauses.Count: 0,
                FinallyBody: BlockStatement finallyBody,
            }] ||
            finallyBody.Statements.Count is < 1 or > 2 ||
            finallyBody.Statements.Any(statement => statement is not
                ExpressionStatement { Expression: AwaitExpression { Operand: IdentifierExpression } }))
        {
            return false;
        }

        // A return at the end of this exact protected body transfers to the
        // awaited finally and then completes the void task. Removing the source
        // return is safe because there are no statements after it to skip.
        function.SetBody([
            new TryStatement(new BlockStatement([]), [], finallyBody),
        ]);
        return TryLowerVoidAwaitedFinally(function);
    }

    private static bool TryLowerTaskResultReturnAwaitedFinally(
        FunctionDeclaration function,
        NamedType taskType)
    {
        if (TryLowerNestedTaskResultReturnAwaitedFinally(function, taskType))
        {
            return true;
        }

        if (taskType.TypeArguments.Count != 1 ||
            function.Body is not
            [TryStatement
            {
                Body: BlockStatement { Statements: [ReturnStatement { Expression: { } resultExpression }] },
                CatchClauses.Count: 0,
                FinallyBody: BlockStatement
                {
                    Statements:
                    [ExpressionStatement { Expression: AwaitExpression
                        { Operand: IdentifierExpression } cleanupAwait }],
                },
            }])
        {
            return false;
        }

        var names = function.Parameters.Select(parameter => parameter.Name)
            .ToHashSet(StringComparer.Ordinal);
        var resultName = CreateUniqueName(names, "__cx_async_cleanup_result");
        var pendingExceptionName = CreateUniqueName(names, "__cx_async_cleanup_return_exception");
        var resultExceptionName = CreateUniqueName(names, "__cx_async_cleanup_return_thrown");
        var sourceName = CreateUniqueName(names, "__cx_async_cleanup_source");
        var completionName = CreateUniqueName(names, "__cx_async_cleanup_completion");
        var continuationName = CreateUniqueName(names, "__cx_async_cleanup_continuation");
        var canceledExceptionName = CreateUniqueName(names, "__cx_async_cleanup_canceled");
        var cleanupExceptionName = CreateUniqueName(names, "__cx_async_cleanup_failure");

        IdentifierExpression Result() => new(new QualifiedIdentifier(resultName));
        IdentifierExpression PendingException() =>
            new(new QualifiedIdentifier(pendingExceptionName));
        IdentifierExpression Source() => new(new QualifiedIdentifier(sourceName));
        IdentifierExpression Completion() => new(new QualifiedIdentifier(completionName));
        IdentifierExpression Continuation() => new(new QualifiedIdentifier(continuationName));

        var resultType = taskType.TypeArguments[0];
        var completionSourceType = NamedType.CreateWithTypeArguments(
            "System.Threading.Tasks.TaskCompletionSource", [resultType]);
        var callbackType = new FunctionType(
            QualifiedIdentifier.Empty,
            BuiltInSystemTypes.Void,
            []);
        var callback = new LambdaExpression(
            [],
            null,
            [new TryStatement(
                new BlockStatement([
                    new ExpressionStatement(new InvocationExpression(
                        new MemberAccessExpression(Source(), "GetResult"), [])),
                    new IfStatement(
                        new BinaryExpression(PendingException(), "!=", new LiteralExpression("null")),
                        new ExpressionStatement(new InvocationExpression(
                            new MemberAccessExpression(Completion(), "SetException"),
                            [PendingException()])),
                        new ExpressionStatement(new InvocationExpression(
                            new MemberAccessExpression(Completion(), "SetResult"),
                            [Result()]))),
                ]),
                [
                    new CatchClause(
                        new NamedType("System.Threading.Tasks.OperationCanceledException", []),
                        canceledExceptionName,
                        null,
                        new BlockStatement([new ExpressionStatement(new InvocationExpression(
                            new MemberAccessExpression(Completion(), "SetCanceled"), []))])),
                    new CatchClause(
                        new NamedType("System.Exception", []),
                        cleanupExceptionName,
                        null,
                        new BlockStatement([new ExpressionStatement(new InvocationExpression(
                            new MemberAccessExpression(Completion(), "SetException"),
                            [new IdentifierExpression(new QualifiedIdentifier(cleanupExceptionName))]))])),
                ],
                null)])
        {
            SourceSpan = cleanupAwait.SourceSpan ?? function.SourceSpan,
        };

        function.SetBody([
            new LocalVariableDeclarationStatement(
                resultType,
                [new LocalVariableDeclarator(resultName, null)]),
            new LocalVariableDeclarationStatement(
                new NamedType("System.Exception", []),
                [new LocalVariableDeclarator(pendingExceptionName, new LiteralExpression("null"))]),
            new TryStatement(
                new BlockStatement([new ExpressionStatement(new AssignmentExpression(
                    Result(), "=", resultExpression))]),
                [new CatchClause(
                    new NamedType("System.Exception", []),
                    resultExceptionName,
                    null,
                    new BlockStatement([new ExpressionStatement(new AssignmentExpression(
                        PendingException(), "=",
                        new IdentifierExpression(new QualifiedIdentifier(resultExceptionName))))]))],
                null),
            new LocalVariableDeclarationStatement(
                completionSourceType,
                [new LocalVariableDeclarator(
                    completionName,
                    new ObjectCreationExpression(completionSourceType, []))]),
            new LocalVariableDeclarationStatement(
                new AutoType(false),
                [new LocalVariableDeclarator(sourceName, cleanupAwait.Operand)]),
            new LocalVariableDeclarationStatement(
                callbackType,
                [new LocalVariableDeclarator(continuationName, callback)]),
            new IfStatement(
                new MemberAccessExpression(Source(), "IsCompleted"),
                new ExpressionStatement(new InvocationExpression(Continuation(), [])
                {
                    SourceSpan = cleanupAwait.SourceSpan ?? function.SourceSpan,
                }),
            new ExpressionStatement(new InvocationExpression(
                new MemberAccessExpression(Source(), "OnCompleted"),
                [Continuation()]))),
            new ReturnStatement(new MemberAccessExpression(Completion(), "Task")),
        ]);
        return true;
    }

    private static bool TryLowerNestedTaskResultReturnAwaitedFinally(
        FunctionDeclaration function,
        NamedType taskType)
    {
        if (taskType.TypeArguments.Count != 1 ||
            function.Body is not [TryStatement outer] || outer.CatchClauses.Count != 0 ||
            outer.Body is not BlockStatement { Statements: [TryStatement inner] } ||
            inner.CatchClauses.Count != 0 ||
            inner.Body is not BlockStatement
            {
                Statements: [ReturnStatement { Expression: { } resultExpression }],
            } ||
            inner.FinallyBody is not BlockStatement
            {
                Statements: [ExpressionStatement { Expression: AwaitExpression
                    { Operand: IdentifierExpression } innerAwait }],
            } ||
            outer.FinallyBody is not BlockStatement
            {
                Statements: [ExpressionStatement { Expression: AwaitExpression
                    { Operand: IdentifierExpression } outerAwait }],
            })
        {
            return false;
        }

        var names = function.Parameters.Select(parameter => parameter.Name)
            .ToHashSet(StringComparer.Ordinal);
        var resultName = CreateUniqueName(names, "__cx_async_nested_return_result");
        var pendingExceptionName = CreateUniqueName(names, "__cx_async_nested_return_exception");
        var pendingCanceledName = CreateUniqueName(names, "__cx_async_nested_return_canceled");
        var resultExceptionName = CreateUniqueName(names, "__cx_async_nested_result_thrown");
        var innerSourceName = CreateUniqueName(names, "__cx_async_nested_inner_source");
        var innerContinuationName = CreateUniqueName(names, "__cx_async_nested_inner_continuation");
        var innerCanceledName = CreateUniqueName(names, "__cx_async_nested_inner_canceled");
        var innerFailureName = CreateUniqueName(names, "__cx_async_nested_inner_failure");
        var outerSourceName = CreateUniqueName(names, "__cx_async_nested_outer_source");
        var outerContinuationName = CreateUniqueName(names, "__cx_async_nested_outer_continuation");
        var outerCanceledName = CreateUniqueName(names, "__cx_async_nested_outer_canceled");
        var outerFailureName = CreateUniqueName(names, "__cx_async_nested_outer_failure");
        var outerSetupCanceledName = CreateUniqueName(names, "__cx_async_nested_outer_setup_canceled");
        var outerSetupFailureName = CreateUniqueName(names, "__cx_async_nested_outer_setup_failure");
        var completionName = CreateUniqueName(names, "__cx_async_nested_return_completion");

        IdentifierExpression Result() => new(new QualifiedIdentifier(resultName));
        IdentifierExpression PendingException() =>
            new(new QualifiedIdentifier(pendingExceptionName));
        IdentifierExpression PendingCanceled() =>
            new(new QualifiedIdentifier(pendingCanceledName));
        IdentifierExpression InnerSource() => new(new QualifiedIdentifier(innerSourceName));
        IdentifierExpression InnerContinuation() => new(new QualifiedIdentifier(innerContinuationName));
        IdentifierExpression OuterSource() => new(new QualifiedIdentifier(outerSourceName));
        IdentifierExpression OuterContinuation() => new(new QualifiedIdentifier(outerContinuationName));
        IdentifierExpression Completion() => new(new QualifiedIdentifier(completionName));

        var resultType = taskType.TypeArguments[0];
        var completionSourceType = NamedType.CreateWithTypeArguments(
            "System.Threading.Tasks.TaskCompletionSource", [resultType]);
        var callbackType = new FunctionType(
            QualifiedIdentifier.Empty,
            BuiltInSystemTypes.Void,
            []);

        ExpressionStatement AssignPendingException(string name) =>
            new(new AssignmentExpression(
                PendingException(), "=", new IdentifierExpression(new QualifiedIdentifier(name))));
        ExpressionStatement AssignPendingCanceled(bool value) =>
            new(new AssignmentExpression(PendingCanceled(), "=", new LiteralExpression(value ? "true" : "false")));
        ExpressionStatement ClearPendingException() =>
            new(new AssignmentExpression(PendingException(), "=", new LiteralExpression("null")));
        ExpressionStatement SetCanceled() =>
            new(new InvocationExpression(new MemberAccessExpression(Completion(), "SetCanceled"), []));
        ExpressionStatement SetException(ExpressionBase exception) =>
            new(new InvocationExpression(new MemberAccessExpression(Completion(), "SetException"), [exception]));
        ExpressionStatement SetResult() =>
            new(new InvocationExpression(new MemberAccessExpression(Completion(), "SetResult"), [Result()]));

        StatementBase[] CompletePendingOutcome() =>
        [
            new IfStatement(
                PendingCanceled(),
                SetCanceled(),
                new IfStatement(
                    new BinaryExpression(PendingException(), "!=", new LiteralExpression("null")),
                    SetException(PendingException()),
                    SetResult())),
        ];

        ExpressionStatement InvokeContinuation(IdentifierExpression continuation, SourceSpan? span) =>
            new(new InvocationExpression(continuation, []) { SourceSpan = span ?? function.SourceSpan });

        var outerCallback = new LambdaExpression(
            [],
            null,
            [new TryStatement(
                new BlockStatement([
                    new ExpressionStatement(new InvocationExpression(
                        new MemberAccessExpression(OuterSource(), "GetResult"), [])),
                    .. CompletePendingOutcome(),
                ]),
                [
                    new CatchClause(
                        new NamedType("System.Threading.Tasks.OperationCanceledException", []),
                        outerCanceledName,
                        null,
                        new BlockStatement([SetCanceled()])),
                    new CatchClause(
                        new NamedType("System.Exception", []),
                        outerFailureName,
                        null,
                        new BlockStatement([SetException(
                            new IdentifierExpression(new QualifiedIdentifier(outerFailureName)))])),
                ],
                null)])
        {
            SourceSpan = outerAwait.SourceSpan ?? function.SourceSpan,
        };

        var outerSetup = new BlockStatement([
            new LocalVariableDeclarationStatement(
                new AutoType(false),
                [new LocalVariableDeclarator(outerSourceName, outerAwait.Operand)]),
            new LocalVariableDeclarationStatement(
                callbackType,
                [new LocalVariableDeclarator(outerContinuationName, outerCallback)]),
            new IfStatement(
                new MemberAccessExpression(OuterSource(), "IsCompleted"),
                InvokeContinuation(OuterContinuation(), outerAwait.SourceSpan),
                new ExpressionStatement(new InvocationExpression(
                    new MemberAccessExpression(OuterSource(), "OnCompleted"),
                    [OuterContinuation()]))),
        ]);

        var innerCallback = new LambdaExpression(
            [],
            null,
            [new TryStatement(
                new BlockStatement([
                    new TryStatement(
                        new BlockStatement([new ExpressionStatement(new InvocationExpression(
                            new MemberAccessExpression(InnerSource(), "GetResult"), []))]),
                        [
                            new CatchClause(
                                new NamedType("System.Threading.Tasks.OperationCanceledException", []),
                                innerCanceledName,
                                null,
                                new BlockStatement([
                                    AssignPendingCanceled(true),
                                    ClearPendingException(),
                                ])),
                            new CatchClause(
                                new NamedType("System.Exception", []),
                                innerFailureName,
                                null,
                                new BlockStatement([
                                    AssignPendingCanceled(false),
                                    AssignPendingException(innerFailureName),
                                ])),
                        ],
                        null),
                    new TryStatement(
                        outerSetup,
                        [
                            new CatchClause(
                                new NamedType("System.Threading.Tasks.OperationCanceledException", []),
                                outerSetupCanceledName,
                                null,
                                new BlockStatement([SetCanceled()])),
                            new CatchClause(
                                new NamedType("System.Exception", []),
                                outerSetupFailureName,
                                null,
                                new BlockStatement([SetException(
                                    new IdentifierExpression(new QualifiedIdentifier(outerSetupFailureName)))])),
                        ],
                        null),
                ]),
                [],
                null)])
        {
            SourceSpan = innerAwait.SourceSpan ?? function.SourceSpan,
        };

        function.SetBody([
            new LocalVariableDeclarationStatement(
                resultType,
                [new LocalVariableDeclarator(resultName, null)]),
            new LocalVariableDeclarationStatement(
                new NamedType("System.Exception", []),
                [new LocalVariableDeclarator(pendingExceptionName, new LiteralExpression("null"))]),
            new LocalVariableDeclarationStatement(
                BuiltInSystemTypes.Bool,
                [new LocalVariableDeclarator(pendingCanceledName, new LiteralExpression("false"))]),
            new TryStatement(
                new BlockStatement([new ExpressionStatement(new AssignmentExpression(
                    Result(), "=", resultExpression))]),
                [
                    new CatchClause(
                        new NamedType("System.Threading.Tasks.OperationCanceledException", []),
                        resultExceptionName,
                        null,
                        new BlockStatement([
                            AssignPendingCanceled(true),
                            ClearPendingException(),
                        ])),
                    new CatchClause(
                        new NamedType("System.Exception", []),
                        resultExceptionName,
                        null,
                        new BlockStatement([
                            AssignPendingCanceled(false),
                            AssignPendingException(resultExceptionName),
                        ])),
                ],
                null),
            new LocalVariableDeclarationStatement(
                completionSourceType,
                [new LocalVariableDeclarator(
                    completionName,
                    new ObjectCreationExpression(completionSourceType, []))]),
            new LocalVariableDeclarationStatement(
                new AutoType(false),
                [new LocalVariableDeclarator(innerSourceName, innerAwait.Operand)]),
            new LocalVariableDeclarationStatement(
                callbackType,
                [new LocalVariableDeclarator(innerContinuationName, innerCallback)]),
            new IfStatement(
                new MemberAccessExpression(InnerSource(), "IsCompleted"),
                InvokeContinuation(InnerContinuation(), innerAwait.SourceSpan),
                new ExpressionStatement(new InvocationExpression(
                    new MemberAccessExpression(InnerSource(), "OnCompleted"),
                    [InnerContinuation()]))),
            new ReturnStatement(new MemberAccessExpression(Completion(), "Task")),
        ]);
        return true;
    }

    private static bool TryLowerVoidAwaitedBodyFinally(FunctionDeclaration function)
    {
        if (function.Body is not
            [TryStatement
            {
                Body: BlockStatement
                {
                    Statements:
                    [ExpressionStatement { Expression: AwaitExpression awaited }],
                },
                CatchClauses.Count: 0,
                FinallyBody: BlockStatement finallyBody,
            }] ||
            awaited.Operand is not IdentifierExpression ||
            finallyBody.Statements.Any(CoroutineSyntaxAnalysis.StatementContainsAwait) ||
            finallyBody.Statements.Any(statement => statement is ReturnStatement or BreakStatement or ContinueStatement))
        {
            return false;
        }

        var names = function.Parameters.Select(parameter => parameter.Name)
            .Concat(finallyBody.Statements.OfType<LocalVariableDeclarationStatement>()
                .SelectMany(statement => statement.Declarators)
                .Select(declarator => declarator.Name))
            .ToHashSet(StringComparer.Ordinal);
        var sourceName = CreateUniqueName(names, "__cx_async_protected_source");
        var completionName = CreateUniqueName(names, "__cx_async_protected_completion");
        var continuationName = CreateUniqueName(names, "__cx_async_protected_continuation");
        var canceledName = CreateUniqueName(names, "__cx_async_protected_canceled");
        var exceptionName = CreateUniqueName(names, "__cx_async_protected_exception");
        IdentifierExpression Source() => new(new QualifiedIdentifier(sourceName));
        IdentifierExpression Completion() => new(new QualifiedIdentifier(completionName));
        IdentifierExpression Continuation() => new(new QualifiedIdentifier(continuationName));

        var callbackType = new FunctionType(QualifiedIdentifier.Empty, BuiltInSystemTypes.Void, []);
        var protectedAwait = new TryStatement(
            new BlockStatement([new ExpressionStatement(new InvocationExpression(
                new MemberAccessExpression(Source(), "GetResult"), []))]),
            [],
            finallyBody);
        var callback = new LambdaExpression(
            [],
            null,
            [new TryStatement(
                new BlockStatement([
                    protectedAwait,
                    new ExpressionStatement(new InvocationExpression(
                        new MemberAccessExpression(Completion(), "SetResult"), [])),
                ]),
                [
                    new CatchClause(
                        new NamedType("System.Threading.Tasks.OperationCanceledException", []),
                        canceledName,
                        null,
                        new BlockStatement([new ExpressionStatement(new InvocationExpression(
                            new MemberAccessExpression(Completion(), "SetCanceled"), []))])),
                    new CatchClause(
                        new NamedType("System.Exception", []),
                        exceptionName,
                        null,
                        new BlockStatement([new ExpressionStatement(new InvocationExpression(
                            new MemberAccessExpression(Completion(), "SetException"),
                            [new IdentifierExpression(new QualifiedIdentifier(exceptionName))]))])),
                ],
                null)])
        {
            SourceSpan = awaited.SourceSpan ?? function.SourceSpan,
        };

        var completionSourceType = new NamedType("System.Threading.Tasks.TaskCompletionSource", []);
        var taskType = new NamedType("System.Threading.Tasks.Task", []);
        function.SetBody([
            new LocalVariableDeclarationStatement(
                completionSourceType,
                [new LocalVariableDeclarator(completionName,
                    new ObjectCreationExpression(completionSourceType, []))]),
            new LocalVariableDeclarationStatement(
                new AutoType(false),
                [new LocalVariableDeclarator(sourceName, awaited.Operand)]),
            new LocalVariableDeclarationStatement(
                callbackType,
                [new LocalVariableDeclarator(continuationName, callback)]),
            new IfStatement(
                new MemberAccessExpression(Source(), "IsCompleted"),
                new ExpressionStatement(new InvocationExpression(Continuation(), [])
                {
                    SourceSpan = awaited.SourceSpan ?? function.SourceSpan,
                }),
                new ExpressionStatement(new InvocationExpression(
                    new MemberAccessExpression(Source(), "OnCompleted"),
                    [Continuation()]))),
            new ReturnStatement(new CastExpression(
                taskType,
                new InvocationExpression(
                    new MemberAccessExpression(
                        new IdentifierExpression(new QualifiedIdentifier(
                            "System", "Threading", "Tasks", "TaskRuntime")),
                        "GetVoidTask"),
                    [Completion()]))),
        ]);
        return true;
    }

    private static bool TryLowerVoidAwaitedFinally(FunctionDeclaration function)
    {
        if (TryLowerNestedVoidAwaitedFinally(function))
        {
            return true;
        }
        if (TryLowerVoidAwaitedFinallyTwice(function))
        {
            return true;
        }
        if (function.Body is not
            [TryStatement
            {
                Body: BlockStatement tryBody,
                CatchClauses.Count: 0,
                FinallyBody: BlockStatement
                {
                    Statements:
                    [ExpressionStatement { Expression: AwaitExpression awaitExpression }],
                },
            }])
        {
            return false;
        }

        var protectedStatements = EnumerateStatements([tryBody]).ToArray();
        if (protectedStatements.Any(statement => statement is ReturnStatement) ||
            protectedStatements.Any(StatementContainsAwait))
        {
            return false;
        }

        var names = function.Parameters.Select(parameter => parameter.Name)
            .Concat(protectedStatements.OfType<LocalVariableDeclarationStatement>()
                .SelectMany(statement => statement.Declarators)
                .Select(declarator => declarator.Name))
            .ToHashSet(StringComparer.Ordinal);
        var completionName = CreateUniqueName(names, "__cx_async_cleanup_completion");
        var sourceName = CreateUniqueName(names, "__cx_async_cleanup_source");
        var pendingSourceName = CreateUniqueName(names, "__cx_async_cleanup_pending_source");
        var pendingExceptionAliasName = CreateUniqueName(
            names, "__cx_async_cleanup_pending_exception");
        var continuationName = CreateUniqueName(names, "__cx_async_cleanup_continuation");
        var tryExceptionName = CreateUniqueName(names, "__cx_async_try_exception");
        var pendingExceptionName = CreateUniqueName(names, "__cx_async_pending_exception");
        var canceledCompletionName = CreateUniqueName(
            names, "__cx_async_cleanup_canceled_completion");
        var cleanupExceptionName = CreateUniqueName(names, "__cx_async_cleanup_exception");
        var canceledExceptionName = CreateUniqueName(names, "__cx_async_cleanup_canceled");
        var inlineCanceledExceptionName = CreateUniqueName(
            names, "__cx_async_cleanup_inline_canceled");

        IdentifierExpression Completion() => new(new QualifiedIdentifier(completionName));
        IdentifierExpression Source() => new(new QualifiedIdentifier(sourceName));
        IdentifierExpression PendingSource() =>
            new(new QualifiedIdentifier(pendingSourceName));
        IdentifierExpression Continuation() => new(new QualifiedIdentifier(continuationName));
        IdentifierExpression PendingException() =>
            new(new QualifiedIdentifier(pendingExceptionName));
        IdentifierExpression PendingExceptionAlias() =>
            new(new QualifiedIdentifier(pendingExceptionAliasName));
        IdentifierExpression CanceledCompletion() =>
            new(new QualifiedIdentifier(canceledCompletionName));

        var completionSourceType = new NamedType(
            "System.Threading.Tasks.TaskCompletionSource", []);
        var callbackType = new FunctionType(
            QualifiedIdentifier.Empty,
            BuiltInSystemTypes.Void,
            []);
        var callback = new LambdaExpression(
            [],
            null,
            [new TryStatement(
                new BlockStatement([
                    new ExpressionStatement(new InvocationExpression(
                        new MemberAccessExpression(PendingSource(), "GetResult"), [])),
                    new IfStatement(
                        new BinaryExpression(PendingExceptionAlias(), "!=", new LiteralExpression("null")),
                        new ExpressionStatement(new InvocationExpression(
                            new MemberAccessExpression(Completion(), "SetException"),
                            [PendingExceptionAlias()])),
                        new ExpressionStatement(new InvocationExpression(
                            new MemberAccessExpression(Completion(), "SetResult"), []))),
                ]),
                [
                    new CatchClause(
                        new NamedType("System.Threading.Tasks.OperationCanceledException", []),
                        canceledExceptionName,
                        null,
                        new BlockStatement([new ExpressionStatement(new InvocationExpression(
                            new MemberAccessExpression(Completion(), "SetCanceled"), []))])),
                    new CatchClause(
                        new NamedType("System.Exception", []),
                        cleanupExceptionName,
                        null,
                        new BlockStatement([new ExpressionStatement(new InvocationExpression(
                            new MemberAccessExpression(Completion(), "SetException"),
                            [new IdentifierExpression(new QualifiedIdentifier(cleanupExceptionName))]))])),
                ],
                null)])
        {
            SourceSpan = awaitExpression.SourceSpan ?? function.SourceSpan,
        };

        var loweredBody = new StatementBase[]
        {
            new LocalVariableDeclarationStatement(
                new NamedType("System.Exception", []),
                [new LocalVariableDeclarator(pendingExceptionName, new LiteralExpression("null"))]),
            new TryStatement(
                tryBody,
                [new CatchClause(
                    new NamedType("System.Exception", []),
                    tryExceptionName,
                    null,
                    new BlockStatement([new ExpressionStatement(new AssignmentExpression(
                        PendingException(), "=",
                        new IdentifierExpression(new QualifiedIdentifier(tryExceptionName))))]))],
                null),
            new LocalVariableDeclarationStatement(
                new AutoType(false),
                [new LocalVariableDeclarator(sourceName, awaitExpression.Operand)]),
            new IfStatement(
                new MemberAccessExpression(Source(), "IsCompleted"),
                new BlockStatement([
                    new TryStatement(
                        new BlockStatement([new ExpressionStatement(new InvocationExpression(
                            new MemberAccessExpression(Source(), "GetResult"), []))]),
                        [new CatchClause(
                            new NamedType(
                                "System.Threading.Tasks.OperationCanceledException", []),
                            inlineCanceledExceptionName,
                            null,
                            new BlockStatement([
                                new LocalVariableDeclarationStatement(
                                    completionSourceType,
                                    [new LocalVariableDeclarator(
                                        canceledCompletionName,
                                        new ObjectCreationExpression(completionSourceType, []))]),
                                new ExpressionStatement(new InvocationExpression(
                                    new MemberAccessExpression(
                                        CanceledCompletion(), "SetCanceled"), [])),
                                new ReturnStatement(new MemberAccessExpression(
                                    CanceledCompletion(), "Task")),
                            ]))],
                        null),
                    new IfStatement(
                        new BinaryExpression(
                            PendingException(), "!=", new LiteralExpression("null")),
                        new ThrowStatement(PendingException()),
                        null),
                    new ReturnStatement(new ObjectCreationExpression(
                        new NamedType("System.Threading.Tasks.Task", []), [])),
                ]),
                new BlockStatement([
                    new LocalVariableDeclarationStatement(
                        new AutoType(false),
                        [new LocalVariableDeclarator(pendingSourceName, Source())]),
                    new LocalVariableDeclarationStatement(
                        new NamedType("System.Exception", []),
                        [new LocalVariableDeclarator(
                            pendingExceptionAliasName, PendingException())]),
                    new LocalVariableDeclarationStatement(
                        completionSourceType,
                        [new LocalVariableDeclarator(
                            completionName,
                            new ObjectCreationExpression(completionSourceType, []))]),
                    new LocalVariableDeclarationStatement(
                        callbackType,
                        [new LocalVariableDeclarator(continuationName, callback)]),
                    new ExpressionStatement(new InvocationExpression(
                        new MemberAccessExpression(PendingSource(), "OnCompleted"),
                        [Continuation()])),
                    new ReturnStatement(new CastExpression(
                        new NamedType("System.Threading.Tasks.Task", []),
                        new InvocationExpression(
                            new MemberAccessExpression(
                                new IdentifierExpression(new QualifiedIdentifier(
                                    "System", "Threading", "Tasks", "TaskRuntime")),
                                "GetVoidTask"),
                            [Completion()]))),
                ])),
            new ReturnStatement(new ObjectCreationExpression(
                new NamedType("System.Threading.Tasks.Task", []), [])),
        };
        function.SetBody(loweredBody);
        return true;
    }

    private static bool TryLowerNestedVoidAwaitedFinally(FunctionDeclaration function)
    {
        if (function.Body is not [TryStatement outer] ||
            outer.CatchClauses.Count != 0 ||
            outer.Body is not BlockStatement { Statements: [TryStatement inner] } ||
            inner.CatchClauses.Count != 0 ||
            inner.FinallyBody is not BlockStatement
            {
                Statements: [ExpressionStatement { Expression: AwaitExpression innerAwait }],
            } ||
            outer.FinallyBody is not BlockStatement
            {
                Statements: [ExpressionStatement { Expression: AwaitExpression outerAwait }],
            } ||
            innerAwait.Operand is not IdentifierExpression ||
            outerAwait.Operand is not IdentifierExpression ||
            inner.Body is not BlockStatement innerBody)
        {
            return false;
        }

        var protectedStatements = EnumerateStatements([innerBody]).ToArray();
        if (protectedStatements.Any(StatementContainsAwait) ||
            protectedStatements.Any(statement => statement is ReturnStatement or BreakStatement or ContinueStatement))
        {
            return false;
        }

        var names = function.Parameters.Select(parameter => parameter.Name)
            .Concat(protectedStatements.OfType<LocalVariableDeclarationStatement>()
                .SelectMany(statement => statement.Declarators)
                .Select(declarator => declarator.Name))
            .ToHashSet(StringComparer.Ordinal);
        var completionName = CreateUniqueName(names, "__cx_async_nested_completion");
        var pendingExceptionName = CreateUniqueName(names, "__cx_async_nested_exception");
        var pendingCanceledName = CreateUniqueName(names, "__cx_async_nested_canceled");
        var innerSourceName = CreateUniqueName(names, "__cx_async_nested_inner_source");
        var innerContinuationName = CreateUniqueName(names, "__cx_async_nested_inner_continuation");
        var outerSourceName = CreateUniqueName(names, "__cx_async_nested_outer_source");
        var outerContinuationName = CreateUniqueName(names, "__cx_async_nested_outer_continuation");
        var innerExceptionName = CreateUniqueName(names, "__cx_async_nested_inner_exception");
        var outerExceptionName = CreateUniqueName(names, "__cx_async_nested_outer_exception");
        var sourceExceptionName = CreateUniqueName(names, "__cx_async_nested_source_exception");
        var sourceCanceledName = CreateUniqueName(names, "__cx_async_nested_source_canceled");

        IdentifierExpression Completion() => new(new QualifiedIdentifier(completionName));
        IdentifierExpression PendingException() => new(new QualifiedIdentifier(pendingExceptionName));
        IdentifierExpression PendingCanceled() => new(new QualifiedIdentifier(pendingCanceledName));
        IdentifierExpression InnerSource() => new(new QualifiedIdentifier(innerSourceName));
        IdentifierExpression InnerContinuation() => new(new QualifiedIdentifier(innerContinuationName));
        IdentifierExpression OuterSource() => new(new QualifiedIdentifier(outerSourceName));
        IdentifierExpression OuterContinuation() => new(new QualifiedIdentifier(outerContinuationName));
        IdentifierExpression OuterAwaitOperand() => new(
            ((IdentifierExpression)outerAwait.Operand).Identifier)
        {
            SourceSpan = outerAwait.Operand.SourceSpan,
        };

        var taskType = new NamedType("System.Threading.Tasks.Task", []);
        var completionSourceType = new NamedType("System.Threading.Tasks.TaskCompletionSource", []);
        var callbackType = new FunctionType(QualifiedIdentifier.Empty, BuiltInSystemTypes.Void, []);
        var exceptionType = new NamedType("System.Exception", []);
        var canceledExceptionType = new NamedType("System.Threading.Tasks.OperationCanceledException", []);

        ExpressionStatement SetException(IdentifierExpression exception) =>
            new(new InvocationExpression(new MemberAccessExpression(Completion(), "SetException"), [exception]));
        ExpressionStatement SetCanceled() =>
            new(new InvocationExpression(new MemberAccessExpression(Completion(), "SetCanceled"), []));
        ExpressionStatement SetResult() =>
            new(new InvocationExpression(new MemberAccessExpression(Completion(), "SetResult"), []));

        CatchClause StorePendingCatch(string exceptionName, bool canceled) =>
            new(canceled ? canceledExceptionType : exceptionType,
                exceptionName,
                null,
                new BlockStatement([
                    new ExpressionStatement(new AssignmentExpression(
                        PendingException(), "=", new IdentifierExpression(new QualifiedIdentifier(exceptionName)))),
                    new ExpressionStatement(new AssignmentExpression(
                        PendingCanceled(), "=", new LiteralExpression(canceled ? "true" : "false"))),
                ]));

        StatementBase CompletePending() => new IfStatement(
            PendingCanceled(),
            SetCanceled(),
            new IfStatement(
                new BinaryExpression(PendingException(), "!=", new LiteralExpression("null")),
                SetException(PendingException()),
                SetResult()));

        var outerCallback = new LambdaExpression(
            [], null,
            [new TryStatement(
                new BlockStatement([
                    new ExpressionStatement(new InvocationExpression(
                        new MemberAccessExpression(OuterSource(), "GetResult"), [])),
                    CompletePending(),
                ]),
                [
                    new CatchClause(canceledExceptionType, outerExceptionName, null,
                        new BlockStatement([SetCanceled()])),
                    new CatchClause(exceptionType, sourceExceptionName, null,
                        new BlockStatement([SetException(
                            new IdentifierExpression(new QualifiedIdentifier(sourceExceptionName)))])),
                ],
                null)])
        {
            SourceSpan = outerAwait.SourceSpan ?? function.SourceSpan,
        };

        var startOuter = new BlockStatement([
                new TryStatement(
                    new BlockStatement([
                    new LocalVariableDeclarationStatement(new AutoType(false),
                        [new LocalVariableDeclarator(outerSourceName, OuterAwaitOperand())]),
                    new LocalVariableDeclarationStatement(callbackType,
                        [new LocalVariableDeclarator(outerContinuationName, outerCallback)]),
                    new IfStatement(
                        new MemberAccessExpression(OuterSource(), "IsCompleted"),
                        new ExpressionStatement(new InvocationExpression(OuterContinuation(), [])
                        {
                            SourceSpan = outerAwait.SourceSpan ?? function.SourceSpan,
                        }),
                        new ExpressionStatement(new InvocationExpression(
                            new MemberAccessExpression(OuterSource(), "OnCompleted"),
                            [OuterContinuation()]))),
                    ]),
                    [
                    new CatchClause(canceledExceptionType, sourceCanceledName, null,
                        new BlockStatement([SetCanceled()])),
                    new CatchClause(exceptionType, outerExceptionName, null,
                        new BlockStatement([SetException(
                            new IdentifierExpression(new QualifiedIdentifier(outerExceptionName)))])),
                    ],
                    null),
            ]);

        var innerCallback = new LambdaExpression(
            [], null,
            [new TryStatement(
                new BlockStatement([
                    new ExpressionStatement(new InvocationExpression(
                        new MemberAccessExpression(InnerSource(), "GetResult"), [])),
                ]),
                [
                    StorePendingCatch(innerExceptionName, true),
                    StorePendingCatch(sourceExceptionName, false),
                ],
                null),
                startOuter])
        {
            SourceSpan = innerAwait.SourceSpan ?? function.SourceSpan,
        };

        var innerCleanup = new BlockStatement([
                new LocalVariableDeclarationStatement(new AutoType(false),
                    [new LocalVariableDeclarator(innerSourceName, innerAwait.Operand)]),
                new LocalVariableDeclarationStatement(callbackType,
                    [new LocalVariableDeclarator(innerContinuationName, innerCallback)]),
                new IfStatement(
                    new MemberAccessExpression(InnerSource(), "IsCompleted"),
                    new ExpressionStatement(new InvocationExpression(InnerContinuation(), [])
                    {
                        SourceSpan = innerAwait.SourceSpan ?? function.SourceSpan,
                    }),
                    new ExpressionStatement(new InvocationExpression(
                        new MemberAccessExpression(InnerSource(), "OnCompleted"),
                        [InnerContinuation()]))),
            ]);

        var loweredBody = new StatementBase[]
        {
            new LocalVariableDeclarationStatement(exceptionType,
                [new LocalVariableDeclarator(pendingExceptionName, new LiteralExpression("null"))]),
            new LocalVariableDeclarationStatement(BuiltInSystemTypes.Bool,
                [new LocalVariableDeclarator(pendingCanceledName, new LiteralExpression("false"))]),
            new LocalVariableDeclarationStatement(completionSourceType,
                [new LocalVariableDeclarator(completionName,
                    new ObjectCreationExpression(completionSourceType, []))]),
            new TryStatement(
                innerBody,
                [
                    StorePendingCatch(innerExceptionName, true),
                    StorePendingCatch(sourceExceptionName, false),
                ],
                null),
            innerCleanup,
            new ReturnStatement(new CastExpression(taskType,
                new InvocationExpression(new MemberAccessExpression(
                    new IdentifierExpression(new QualifiedIdentifier(
                        "System", "Threading", "Tasks", "TaskRuntime")), "GetVoidTask"),
                    [Completion()]))),
        };

        function.SetBody(loweredBody);
        return true;
    }

    private static bool TryLowerVoidAwaitedFinallyTwice(FunctionDeclaration function)
    {
        if (function.Body is not
            [TryStatement
            {
                Body: BlockStatement tryBody,
                CatchClauses.Count: 0,
                FinallyBody: BlockStatement
                {
                    Statements:
                    [
                        ExpressionStatement { Expression: AwaitExpression firstAwait },
                        ExpressionStatement { Expression: AwaitExpression secondAwait },
                    ],
                } finallyBody,
            }])
        {
            return false;
        }

        var protectedStatements = EnumerateStatements([tryBody]).ToArray();
        if (protectedStatements.Any(statement => statement is ReturnStatement) ||
            protectedStatements.Any(StatementContainsAwait))
        {
            return false;
        }

        var names = function.Parameters.Select(parameter => parameter.Name)
            .Concat(protectedStatements.OfType<LocalVariableDeclarationStatement>()
                .SelectMany(statement => statement.Declarators)
                .Select(declarator => declarator.Name))
            .ToHashSet(StringComparer.Ordinal);
        var completionName = CreateUniqueName(names, "__cx_async_cleanup_completion");
        var pendingExceptionName = CreateUniqueName(names, "__cx_async_pending_exception");
        var tryExceptionName = CreateUniqueName(names, "__cx_async_try_exception");
        var sourceNames = new[]
        {
            CreateUniqueName(names, "__cx_async_cleanup_source_0"),
            CreateUniqueName(names, "__cx_async_cleanup_source_1"),
        };
        var continuationNames = new[]
        {
            CreateUniqueName(names, "__cx_async_cleanup_continuation_0"),
            CreateUniqueName(names, "__cx_async_cleanup_continuation_1"),
        };
        var cleanupExceptionNames = new[]
        {
            CreateUniqueName(names, "__cx_async_cleanup_exception_0"),
            CreateUniqueName(names, "__cx_async_cleanup_exception_1"),
        };
        var canceledExceptionNames = new[]
        {
            CreateUniqueName(names, "__cx_async_cleanup_canceled_0"),
            CreateUniqueName(names, "__cx_async_cleanup_canceled_1"),
        };
        IdentifierExpression Completion() => new(new QualifiedIdentifier(completionName));
        IdentifierExpression PendingException() =>
            new(new QualifiedIdentifier(pendingExceptionName));
        IdentifierExpression Source(int index) =>
            new(new QualifiedIdentifier(sourceNames[index]));
        IdentifierExpression Continuation(int index) =>
            new(new QualifiedIdentifier(continuationNames[index]));
        var completionSourceType = new NamedType("System.Threading.Tasks.TaskCompletionSource", []);
        var callbackType = new FunctionType(QualifiedIdentifier.Empty, BuiltInSystemTypes.Void, []);
        var awaitExpressions = new[] { firstAwait, secondAwait };

        List<StatementBase> LowerCleanupAwait(int index)
        {
            var canceledCatch = new CatchClause(
                new NamedType("System.Threading.Tasks.OperationCanceledException", []),
                canceledExceptionNames[index], null,
                new BlockStatement([new ExpressionStatement(new InvocationExpression(
                    new MemberAccessExpression(Completion(), "SetCanceled"), []))]));
            var exceptionCatch = new CatchClause(
                new NamedType("System.Exception", []),
                cleanupExceptionNames[index], null,
                new BlockStatement([new ExpressionStatement(new InvocationExpression(
                    new MemberAccessExpression(Completion(), "SetException"),
                    [new IdentifierExpression(new QualifiedIdentifier(
                        cleanupExceptionNames[index]))]))]));

            var successStatements = new List<StatementBase>
            {
                new ExpressionStatement(new InvocationExpression(
                    new MemberAccessExpression(Source(index), "GetResult"), [])),
            };
            if (index == 0)
            {
                successStatements.AddRange(LowerCleanupAwait(1));
            }
            else
            {
                successStatements.Add(new IfStatement(
                    new BinaryExpression(PendingException(), "!=", new LiteralExpression("null")),
                    new ExpressionStatement(new InvocationExpression(
                        new MemberAccessExpression(Completion(), "SetException"),
                        [PendingException()])),
                    new ExpressionStatement(new InvocationExpression(
                        new MemberAccessExpression(Completion(), "SetResult"), []))));
            }

            var callback = new LambdaExpression(
                [], null,
                [new TryStatement(
                    new BlockStatement(successStatements),
                    [canceledCatch, exceptionCatch], null)])
            {
                SourceSpan = awaitExpressions[index].SourceSpan ?? function.SourceSpan,
            };
            return
            [
                new LocalVariableDeclarationStatement(new AutoType(false),
                    [new LocalVariableDeclarator(sourceNames[index], awaitExpressions[index].Operand)]),
                new LocalVariableDeclarationStatement(callbackType,
                    [new LocalVariableDeclarator(continuationNames[index], callback)]),
                new IfStatement(
                    new MemberAccessExpression(Source(index), "IsCompleted"),
                    new ExpressionStatement(new InvocationExpression(Continuation(index), [])
                    {
                        SourceSpan = awaitExpressions[index].SourceSpan ?? function.SourceSpan,
                    }),
                new ExpressionStatement(new InvocationExpression(
                    new MemberAccessExpression(Source(index), "OnCompleted"),
                    [Continuation(index)]))),
            ];
        }

        var loweredBody = new List<StatementBase>
        {
            new LocalVariableDeclarationStatement(new NamedType("System.Exception", []),
                [new LocalVariableDeclarator(pendingExceptionName, new LiteralExpression("null"))]),
            new TryStatement(
                tryBody,
                [new CatchClause(new NamedType("System.Exception", []), tryExceptionName, null,
                    new BlockStatement([new ExpressionStatement(new AssignmentExpression(
                        PendingException(), "=",
                        new IdentifierExpression(new QualifiedIdentifier(tryExceptionName))))]))],
                null),
            new LocalVariableDeclarationStatement(completionSourceType,
                [new LocalVariableDeclarator(completionName,
                    new ObjectCreationExpression(completionSourceType, []))]),
        };
        loweredBody.AddRange(LowerCleanupAwait(0));
        loweredBody.Add(new ReturnStatement(new CastExpression(
            new NamedType("System.Threading.Tasks.Task", []),
            new InvocationExpression(new MemberAccessExpression(
                new IdentifierExpression(new QualifiedIdentifier(
                    "System", "Threading", "Tasks", "TaskRuntime")), "GetVoidTask"),
                [Completion()]))));
        function.SetBody(loweredBody);
        return true;
    }

    private static bool TryLowerVoidAwaitContinuation(
        FunctionDeclaration function)
    {
        if (function.Body is not { Count: >= 2 } body ||
            body is [ExpressionStatement { Expression: AwaitExpression }])
        {
            return false;
        }

        var awaits = new List<(int Index, AwaitExpression Expression)>();
        for (var index = 0; index < body.Count; index++)
        {
            if (body[index] is ExpressionStatement
                { Expression: AwaitExpression awaitExpression })
            {
                awaits.Add((index, awaitExpression));
            }
        }
        if (awaits.Count == 0)
        {
            return false;
        }

        var hasFinalReturn = body[^1] is ReturnStatement { Expression: null };
        var finalStatementIndex = hasFinalReturn ? body.Count - 1 : body.Count;
        if (awaits[^1].Index >= finalStatementIndex ||
            EnumerateStatements(body.Take(awaits[0].Index))
                .Any(statement => statement is ReturnStatement) ||
            !CanRewriteVoidReturns(body.Skip(awaits[0].Index + 1)
                .Take(finalStatementIndex - awaits[0].Index - 1)) ||
            body.Skip(finalStatementIndex).Any(statement => statement is not ReturnStatement { Expression: null }))
        {
            return false;
        }

        var names = function.Parameters.Select(parameter => parameter.Name)
            .Concat(EnumerateStatements(body)
                .OfType<LocalVariableDeclarationStatement>()
                .SelectMany(statement => statement.Declarators)
                .Select(declarator => declarator.Name))
            .ToHashSet(StringComparer.Ordinal);
        var completionName = CreateUniqueName(names, "__cx_async_completion");
        var completionSourceType = new NamedType(
            "System.Threading.Tasks.TaskCompletionSource", []);
        var sourceTaskType = new NamedType("System.Threading.Tasks.Task", []);
        var callbackType = new FunctionType(
            QualifiedIdentifier.Empty,
            BuiltInSystemTypes.Void,
            []);
        IdentifierExpression Completion() =>
            new(new QualifiedIdentifier(completionName));

        var prefixCount = awaits[0].Index;
        var allInlineBranch = CreateAllInlineVoidAwaitBranch(
            function, body, awaits);
        var callbackTaskAliases = new Dictionary<int,
            (string Name, TypeBase Type, ExpressionBase Initializer)>();
        if (allInlineBranch is not null)
        {
            for (var awaitNumber = 0; awaitNumber < awaits.Count; awaitNumber++)
            {
                if (awaits[awaitNumber].Expression.Operand is not IdentifierExpression identifier)
                {
                    continue;
                }
                var parameterName = identifier.Identifier.ToString();
                var parameter = function.Parameters.First(candidate =>
                    candidate.Name == parameterName);
                var aliasName = CreateUniqueName(names,
                    $"__cx_async_pending_task_{awaitNumber}");
                callbackTaskAliases.Add(awaitNumber,
                    (aliasName, parameter.ParameterType,
                     CloneAwaitOperand(awaits[awaitNumber].Expression.Operand)));
            }
        }

        var callbackValueAliases = new Dictionary<string,
            (string Name, TypeBase Type, ExpressionBase Initializer)>(StringComparer.Ordinal);
        var callbackSubstitutions = new Dictionary<string, string>(StringComparer.Ordinal);
        var prefixLocalTypes = new Dictionary<string, TypeBase>(StringComparer.Ordinal);
        foreach (var declaration in body.Take(prefixCount)
                     .OfType<LocalVariableDeclarationStatement>())
        {
            foreach (var declarator in declaration.Declarators)
            {
                var localType = declarator.Type ?? declaration.DeclaredType;
                if (localType is AutoType &&
                    declarator.Initializer is IdentifierExpression initializerIdentifier)
                {
                    var initializerName = initializerIdentifier.Identifier.ToString();
                    localType = function.Parameters.FirstOrDefault(parameter =>
                        parameter.Name == initializerName)?.ParameterType ??
                        (prefixLocalTypes.TryGetValue(initializerName, out var previousType)
                            ? previousType
                            : localType);
                }
                if (localType is not AutoType)
                {
                    prefixLocalTypes[declarator.Name] = localType;
                }
            }
        }
        var callbackLiveNames = CoroutineSyntaxAnalysis.EnumerateBodyExpressions(
                body.Skip(awaits[0].Index + 1).Take(finalStatementIndex - awaits[0].Index - 1))
            .OfType<IdentifierExpression>()
            .Select(identifier => identifier.Identifier.ToString())
            .ToHashSet(StringComparer.Ordinal);
        if (allInlineBranch is not null)
        {
            foreach (var declaration in body.Take(prefixCount)
                         .OfType<LocalVariableDeclarationStatement>())
            {
                foreach (var declarator in declaration.Declarators)
                {
                    var type = prefixLocalTypes.TryGetValue(
                        declarator.Name, out var prefixType)
                        ? prefixType
                        : declarator.Type ?? declaration.DeclaredType;
                    if (!callbackLiveNames.Contains(declarator.Name) ||
                        type is null or AutoType || declarator.Initializer is null)
                    {
                        continue;
                    }

                    var aliasName = CreateUniqueName(
                        names, $"__cx_async_saved_{declarator.Name}");
                    callbackValueAliases.Add(
                        declarator.Name,
                        (aliasName, type,
                         new IdentifierExpression(new QualifiedIdentifier(declarator.Name))));
                    callbackSubstitutions.Add(declarator.Name, aliasName);
                }
            }
        }

        var callbackBodySegments = new List<StatementBase[]>(awaits.Count);
        for (var awaitNumber = 0; awaitNumber < awaits.Count; awaitNumber++)
        {
            var awaitIndex = awaits[awaitNumber].Index;
            var nextAwaitIndex = awaitNumber + 1 < awaits.Count
                ? awaits[awaitNumber + 1].Index
                : finalStatementIndex;
            var clonedSegment = new List<StatementBase>();
            foreach (var statement in body.Skip(awaitIndex + 1)
                         .Take(nextAwaitIndex - awaitIndex - 1))
            {
                foreach (var rewritten in RewriteVoidReturns(statement, completionName))
                {
                    if (!TryCloneAwaitCallbackStatement(
                            rewritten, callbackSubstitutions, out var clonedStatement))
                    {
                        return false;
                    }
                    clonedSegment.Add(clonedStatement);
                }
            }
            callbackBodySegments.Add(clonedSegment.ToArray());
        }

        List<StatementBase> LowerAwaitContinuation(int awaitNumber)
        {
            var (awaitIndex, awaitExpression) = awaits[awaitNumber];
            var sourceName = CreateUniqueName(names, $"__cx_async_source_{awaitNumber}");
            var continuationName = CreateUniqueName(names, $"__cx_async_continuation_{awaitNumber}");
            IdentifierExpression Source() => new(new QualifiedIdentifier(sourceName));
            IdentifierExpression Continuation() =>
                new(new QualifiedIdentifier(continuationName));

            var callbackStatements = new List<StatementBase>
            {
                new ExpressionStatement(new InvocationExpression(
                    new MemberAccessExpression(Source(), "GetResult"), [])),
            };
            callbackStatements.AddRange(callbackBodySegments[awaitNumber]);
            if (awaitNumber + 1 < awaits.Count)
            {
                callbackStatements.AddRange(LowerAwaitContinuation(awaitNumber + 1));
            }
            else
            {
                callbackStatements.Add(new ExpressionStatement(new InvocationExpression(
                    new MemberAccessExpression(Completion(), "SetResult"), [])));
            }

            var cancellationExceptionName = CreateUniqueName(
                names, $"__cx_async_canceled_{awaitNumber}");
            var canceledClause = new CatchClause(
                new NamedType("System.Threading.Tasks.OperationCanceledException", []),
                cancellationExceptionName,
                null,
                new BlockStatement([new ExpressionStatement(new InvocationExpression(
                    new MemberAccessExpression(Completion(), "SetCanceled"), []))]));
            var exceptionName = CreateUniqueName(names, $"__cx_async_exception_{awaitNumber}");
            var catchClause = new CatchClause(
                new NamedType("System.Exception", []),
                exceptionName,
                null,
                new BlockStatement([
                    new ExpressionStatement(new InvocationExpression(
                        new MemberAccessExpression(Completion(), "SetException"),
                        [new IdentifierExpression(new QualifiedIdentifier(exceptionName))])),
                ]));
            var callback = new LambdaExpression(
                [],
                null,
                [new TryStatement(new BlockStatement(callbackStatements),
                    [canceledClause, catchClause], null)])
            {
                SourceSpan = awaitExpression.SourceSpan ?? function.SourceSpan,
            };

            return
            [
                new LocalVariableDeclarationStatement(sourceTaskType,
                [new LocalVariableDeclarator(sourceName,
                    callbackTaskAliases.TryGetValue(awaitNumber, out var taskAlias)
                        ? new IdentifierExpression(new QualifiedIdentifier(taskAlias.Name))
                        : awaitExpression.Operand)]),
                new LocalVariableDeclarationStatement(callbackType,
                [new LocalVariableDeclarator(continuationName, callback)]),
                new IfStatement(
                    new MemberAccessExpression(Source(), "IsCompleted"),
                    new ExpressionStatement(new InvocationExpression(Continuation(), [])
                    {
                        SourceSpan = awaitExpression.SourceSpan ?? function.SourceSpan,
                    }),
                    new ExpressionStatement(new InvocationExpression(
                        new MemberAccessExpression(Source(), "OnCompleted"),
                        [Continuation()]))),
            ];
        }

        var loweredBody = new List<StatementBase>();
        loweredBody.AddRange(body.Take(awaits[0].Index));
        loweredBody.AddRange(callbackTaskAliases.Values.Select(alias =>
            (StatementBase)new LocalVariableDeclarationStatement(
                alias.Type,
                [new LocalVariableDeclarator(alias.Name, alias.Initializer)])));
        loweredBody.AddRange(callbackValueAliases.Values.Select(alias =>
            (StatementBase)new LocalVariableDeclarationStatement(
                alias.Type,
                [new LocalVariableDeclarator(alias.Name, alias.Initializer)])));
        loweredBody.Add(new LocalVariableDeclarationStatement(completionSourceType,
            [new LocalVariableDeclarator(
                completionName,
                new ObjectCreationExpression(completionSourceType, []))]));
        loweredBody.AddRange(LowerAwaitContinuation(0));
        loweredBody.Add(new ReturnStatement(
            new MemberAccessExpression(Completion(), "Task")));
        if (allInlineBranch is null)
        {
            function.SetBody(loweredBody);
        }
        else
        {
            function.SetBody([
                .. body.Take(prefixCount),
                new IfStatement(
                    allInlineBranch.Condition,
                    allInlineBranch.ThenStatement,
                    new BlockStatement(loweredBody.Skip(prefixCount).ToArray()))
                {
                    SourceSpan = allInlineBranch.SourceSpan,
                },
            ]);
        }
        return true;
    }

    private static IfStatement? CreateAllInlineVoidAwaitBranch(
        FunctionDeclaration function,
        IReadOnlyList<StatementBase> body,
        IReadOnlyList<(int Index, AwaitExpression Expression)> awaits)
    {
        if (awaits.Count == 0 ||
            CoroutineSyntaxAnalysis.EnumerateBodyExpressions(body)
                .OfType<AwaitExpression>().Count() != awaits.Count ||
            EnumerateStatements(body.Skip(awaits[0].Index))
                .Any(statement => statement is ReturnStatement))
        {
            return null;
        }

        var awaitByIndex = awaits.ToDictionary(awaited => awaited.Index);
        var inlineSubstitutions = CoroutineSyntaxAnalysis
            .EnumerateBodyExpressions(body)
            .OfType<IdentifierExpression>()
            .Select(identifier => identifier.Identifier.ToString())
            .Distinct(StringComparer.Ordinal)
            .ToDictionary(name => name, name => name, StringComparer.Ordinal);
        ExpressionBase? condition = null;
        var inlineStatements = new List<StatementBase>();
        for (var index = awaits[0].Index; index < body.Count; index++)
        {
            if (awaitByIndex.TryGetValue(index, out var awaited))
            {
                if (awaited.Expression.Operand is not IdentifierExpression identifier)
                {
                    return null;
                }

                var source = CloneAwaitOperand(awaited.Expression.Operand);
                var parameterName = identifier.Identifier.ToString();
                var parameter = function.Parameters.FirstOrDefault(candidate =>
                    candidate.Name == parameterName);
                var parameterType = parameter?.ParameterType is ConstType constant
                    ? constant.UnderlyingType
                    : parameter?.ParameterType;
                if (parameterType is not NamedType namedSource ||
                    (!IsTaskType(namedSource) && !IsNonGenericTaskType(namedSource)))
                {
                    return null;
                }

                var completed = new MemberAccessExpression(
                    CloneAwaitOperand(awaited.Expression.Operand), "IsCompleted");
                condition = condition is null
                    ? completed
                    : new BinaryExpression(condition, "&&", completed);
                inlineStatements.Add(new ExpressionStatement(new InvocationExpression(
                    new MemberAccessExpression(source, "GetResult"), []))
                {
                    SourceSpan = awaited.Expression.SourceSpan,
                });
            }
            else
            {
                if (StatementContainsAwait(body[index]))
                {
                    return null;
                }
                if (!TryCloneAwaitCallbackStatement(
                        body[index], inlineSubstitutions, out var clonedStatement))
                {
                    return null;
                }
                inlineStatements.Add(clonedStatement);
            }
        }

        inlineStatements.Add(new ReturnStatement(new ObjectCreationExpression(
            new NamedType("System.Threading.Tasks.Task", []), [])));
        return new IfStatement(condition!, new BlockStatement(inlineStatements), null)
        {
            SourceSpan = awaits[0].Expression.SourceSpan,
        };
    }

    private static bool CanRewriteVoidReturns(
        IEnumerable<StatementBase> statements) =>
        statements.All(CanRewriteVoidReturns);

    private static bool CanRewriteVoidReturns(StatementBase statement) => statement switch
    {
        ReturnStatement { Expression: null } => true,
        ReturnStatement => false,
        BlockStatement block => CanRewriteVoidReturns(block.Statements),
        IfStatement conditional =>
            CanRewriteVoidReturns([conditional.ThenStatement]) &&
            (conditional.ElseStatement is null ||
             CanRewriteVoidReturns([conditional.ElseStatement])),
        WhileStatement loop => CanRewriteVoidReturns([loop.Body]),
        DoWhileStatement loop => CanRewriteVoidReturns([loop.Body]),
        ForStatement loop => CanRewriteVoidReturns([loop.Body]),
        SwitchStatement switchStatement => switchStatement.Sections.All(section =>
            CanRewriteVoidReturns(section.Statements)),
        TryStatement tryStatement =>
            CanRewriteVoidReturns([tryStatement.Body]) &&
            tryStatement.CatchClauses.All(clause =>
                CanRewriteVoidReturns([clause.Body])) &&
            (tryStatement.FinallyBody is null ||
             (!EnumerateStatements([tryStatement.FinallyBody])
                 .Any(nested => nested is ReturnStatement) &&
              CanRewriteVoidReturns([tryStatement.FinallyBody]))) &&
            (tryStatement.FinallyBody is null ||
             (!EnumerateStatements([tryStatement.Body])
                  .Concat(tryStatement.CatchClauses.SelectMany(clause =>
                      EnumerateStatements([clause.Body])))
                  .Any(nested => nested is ReturnStatement))),
        ForeachStatement loop =>
            !EnumerateStatements([loop.Body]).Any(nested => nested is ReturnStatement),
        LocalFunctionDeclarationStatement => true,
        _ => true,
    };

    private static IReadOnlyList<StatementBase> RewriteVoidReturns(
        StatementBase statement,
        string completionName)
    {
        IdentifierExpression Completion() =>
            new(new QualifiedIdentifier(completionName));
        IReadOnlyList<StatementBase> RewriteMany(IEnumerable<StatementBase> statements) =>
            statements.SelectMany(nested =>
                RewriteVoidReturns(nested, completionName)).ToArray();
        StatementBase RewriteSingle(StatementBase nested)
        {
            var rewritten = RewriteVoidReturns(nested, completionName);
            return rewritten.Count == 1
                ? rewritten[0]
                : new BlockStatement(rewritten)
                {
                    SourceSpan = nested.SourceSpan,
                };
        }

        switch (statement)
        {
            case ReturnStatement { Expression: null } returnStatement:
                return
                [
                    new ExpressionStatement(new InvocationExpression(
                        new MemberAccessExpression(Completion(), "SetResult"), []))
                    {
                        SourceSpan = returnStatement.SourceSpan,
                    },
                    new ReturnStatement(null)
                    {
                        SourceSpan = returnStatement.SourceSpan,
                    },
                ];
            case BlockStatement block:
                return
                [
                    new BlockStatement(RewriteMany(block.Statements), block.IsUnsafe)
                    {
                        SourceSpan = block.SourceSpan,
                    },
                ];
            case IfStatement conditional:
                return
                [
                    new IfStatement(
                        conditional.Condition,
                        RewriteSingle(conditional.ThenStatement),
                        conditional.ElseStatement is null
                            ? null
                            : RewriteSingle(conditional.ElseStatement))
                    {
                        SourceSpan = conditional.SourceSpan,
                    },
                ];
            case WhileStatement loop:
                return
                [
                    new WhileStatement(loop.Condition, RewriteSingle(loop.Body))
                    {
                        SourceSpan = loop.SourceSpan,
                    },
                ];
            case DoWhileStatement loop:
                return
                [
                    new DoWhileStatement(RewriteSingle(loop.Body), loop.Condition)
                    {
                        SourceSpan = loop.SourceSpan,
                    },
                ];
            case ForStatement loop:
                return
                [
                    new ForStatement(
                        loop.DeclarationInitializer,
                        loop.InitializerExpressions,
                        loop.Condition,
                        loop.Iterators,
                        RewriteSingle(loop.Body))
                    {
                        SourceSpan = loop.SourceSpan,
                    },
                ];
            case SwitchStatement switchStatement:
                return
                [
                    new SwitchStatement(
                        switchStatement.Expression,
                        switchStatement.Sections.Select(section => new SwitchSection(
                            section.Labels,
                            RewriteMany(section.Statements))).ToArray())
                    {
                        SourceSpan = switchStatement.SourceSpan,
                    },
                ];
            case TryStatement tryStatement:
                return
                [
                    new TryStatement(
                        RewriteSingle(tryStatement.Body),
                        tryStatement.CatchClauses.Select(clause => new CatchClause(
                            clause.ExceptionType,
                            clause.VariableName,
                            clause.Filter,
                            RewriteSingle(clause.Body))).ToArray(),
                        tryStatement.FinallyBody)
                    {
                        SourceSpan = tryStatement.SourceSpan,
                    },
                ];
            default:
                return [statement];
        }
    }

    private static void WrapExceptions(
        FunctionDeclaration function,
        NamedType taskType,
        bool hasResult)
    {
        var exceptionType = new NamedType("System.Exception", []);
        var exceptionIdentifier = new IdentifierExpression(
            new QualifiedIdentifier("__cx_async_exception"));
        IReadOnlyList<StatementBase> faultHandlerStatements;
        if (hasResult)
        {
            const string faultedTaskName = "__cx_async_faulted_task";
            var faultedTaskIdentifier = new IdentifierExpression(
                new QualifiedIdentifier(faultedTaskName));
            var faultedTask = new ObjectCreationExpression(taskType, []);
            var setFault = new InvocationExpression(
                new MemberAccessExpression(faultedTaskIdentifier, "SetFault"),
                [exceptionIdentifier]);
            faultHandlerStatements =
            [
                new LocalVariableDeclarationStatement(taskType,
                [new LocalVariableDeclarator(faultedTaskName, faultedTask)]),
                new ExpressionStatement(setFault),
                new ReturnStatement(faultedTaskIdentifier),
            ];
        }
        else
        {
            var faultFactory = new MemberAccessExpression(
                new IdentifierExpression(new QualifiedIdentifier(
                    "System", "Threading", "Tasks", "Task")),
                "Faulted");
            faultHandlerStatements =
            [
                new ReturnStatement(new InvocationExpression(
                    faultFactory, [exceptionIdentifier])),
            ];
        }
        var catchClause = new CatchClause(
            exceptionType,
            "__cx_async_exception",
            null,
            new BlockStatement(faultHandlerStatements));
        // Inline GetResult must publish the same canceled state as a resumed
        // await. Ordinary faults continue through the existing fault handler.
        var canceledOwnerType = hasResult && taskType.TypeArguments.Count == 1
            ? taskType
            : new NamedType("System.Threading.Tasks.TaskCompletionSource", []);
        const string canceledOwnerName = "__cx_async_canceled_completion";
        IdentifierExpression CanceledOwner() =>
            new(new QualifiedIdentifier(canceledOwnerName));
        var canceledClause = new CatchClause(
            new NamedType("System.Threading.Tasks.OperationCanceledException", []),
            "__cx_async_canceled_exception",
            null,
            new BlockStatement([
                new LocalVariableDeclarationStatement(canceledOwnerType,
                [new LocalVariableDeclarator(canceledOwnerName,
                    new ObjectCreationExpression(canceledOwnerType, []))]),
                new ExpressionStatement(new InvocationExpression(
                    new MemberAccessExpression(CanceledOwner(), "SetCanceled"), [])),
                new ReturnStatement(hasResult && taskType.TypeArguments.Count == 1
                    ? CanceledOwner()
                    : new MemberAccessExpression(CanceledOwner(), "Task")),
            ]));
        function.SetBody([
            new TryStatement(
                new BlockStatement(function.Body ?? []),
                [canceledClause, catchClause],
                null)
            {
                SourceSpan = function.SourceSpan,
            },
        ]);
    }

    private static string CreateUniqueName(HashSet<string> names, string baseName)
    {
        var name = baseName;
        var index = 0;
        while (!names.Add(name))
        {
            name = $"{baseName}_{++index}";
        }
        return name;
    }

    internal static bool IsTaskType(NamedType type)
    {
        var parts = type.ResolvedTypeFullName.Parts;
        return parts.Length >= 4 &&
            parts[^4].Equals("System", StringComparison.Ordinal) &&
            parts[^3].Equals("Threading", StringComparison.Ordinal) &&
            parts[^2].Equals("Tasks", StringComparison.Ordinal) &&
            parts[^1].Equals("Task", StringComparison.Ordinal);
    }

    internal static bool IsValueTaskType(NamedType type)
    {
        var parts = type.ResolvedTypeFullName.Parts;
        return parts.Length >= 4 &&
            parts[^4].Equals("System", StringComparison.Ordinal) &&
            parts[^3].Equals("Threading", StringComparison.Ordinal) &&
            parts[^2].Equals("Tasks", StringComparison.Ordinal) &&
            (parts[^1].Equals("ValueTask", StringComparison.Ordinal) ||
             parts[^1].Equals("ValueTaskHandle", StringComparison.Ordinal)) &&
            type.TypeArguments.Count <= 1;
    }

    internal static bool IsAsyncEnumerableType(TypeBase type) =>
        (type is ConstType constant ? constant.UnderlyingType : type) is NamedType named &&
        named.TypeArguments.Count == 1 &&
        named.ResolvedTypeFullName.Parts is var parts &&
        parts.Length >= 3 &&
        parts[^3].Equals("System", StringComparison.Ordinal) &&
        parts[^2].Equals("Collections", StringComparison.Ordinal) &&
        parts[^1].Equals("IAsyncEnumerable", StringComparison.Ordinal);

    internal static bool IsNonGenericTaskType(NamedType type)
    {
        var parts = type.ResolvedTypeFullName.Parts;
        var sourceParts = type.Name.Split('.', StringSplitOptions.RemoveEmptyEntries);
        return type.TypeArguments.Count == 0 &&
            ((parts.Length >= 4 &&
              parts[^4].Equals("System", StringComparison.Ordinal) &&
              parts[^3].Equals("Threading", StringComparison.Ordinal) &&
              parts[^2].Equals("Tasks", StringComparison.Ordinal) &&
              parts[^1].Equals("Task", StringComparison.Ordinal)) ||
             (sourceParts.Length >= 4 &&
              sourceParts[^4].Equals("System", StringComparison.Ordinal) &&
              sourceParts[^3].Equals("Threading", StringComparison.Ordinal) &&
              sourceParts[^2].Equals("Tasks", StringComparison.Ordinal) &&
              sourceParts[^1].Equals("Task", StringComparison.Ordinal)) ||
             sourceParts[^1].Equals("Task", StringComparison.Ordinal));
    }

    private static ObjectCreationExpression CreateCompletedTask(
        NamedType taskType,
        ExpressionBase? result,
        SourceSpan? sourceSpan)
    {
        var completedType = taskType.TypeArguments.Count == 0
            ? new NamedType("System.Threading.Tasks.Task", [])
            : NamedType.CreateWithTypeArguments(
                "System.Threading.Tasks.Task", [taskType.TypeArguments[0]]);
        return new ObjectCreationExpression(
            completedType,
            result is null ? [] : [result])
        {
            SourceSpan = sourceSpan,
        };
    }

    private static IEnumerable<StatementBase> EnumerateStatements(
        IEnumerable<StatementBase> statements)
    {
        foreach (var statement in statements)
        {
            yield return statement;
            switch (statement)
            {
                case BlockStatement block:
                    foreach (var nested in EnumerateStatements(block.Statements)) yield return nested;
                    break;
                case IfStatement conditional:
                    foreach (var nested in EnumerateStatements([conditional.ThenStatement])) yield return nested;
                    if (conditional.ElseStatement is not null)
                        foreach (var nested in EnumerateStatements([conditional.ElseStatement])) yield return nested;
                    break;
                case WhileStatement loop:
                    foreach (var nested in EnumerateStatements([loop.Body])) yield return nested;
                    break;
                case DoWhileStatement loop:
                    foreach (var nested in EnumerateStatements([loop.Body])) yield return nested;
                    break;
                case ForStatement loop:
                    foreach (var nested in EnumerateStatements([loop.Body])) yield return nested;
                    break;
                case ForeachStatement loop:
                    foreach (var nested in EnumerateStatements([loop.Body])) yield return nested;
                    break;
                case SwitchStatement switchStatement:
                    foreach (var section in switchStatement.Sections)
                        foreach (var nested in EnumerateStatements(section.Statements)) yield return nested;
                    break;
                case TryStatement tryStatement:
                    foreach (var nested in EnumerateStatements([tryStatement.Body])) yield return nested;
                    foreach (var clause in tryStatement.CatchClauses)
                        foreach (var nested in EnumerateStatements([clause.Body])) yield return nested;
                    if (tryStatement.FinallyBody is not null)
                        foreach (var nested in EnumerateStatements([tryStatement.FinallyBody])) yield return nested;
                    break;
            }
        }
    }

    private static bool StatementContainsAwait(StatementBase statement) =>
        CoroutineSyntaxAnalysis.StatementContainsAwait(statement);
}


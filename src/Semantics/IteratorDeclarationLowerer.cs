using System.Security.Cryptography;
using System.Text;
using CxCompiler.Model;
using CxCompiler.Model.Common;
using CxCompiler.Model.Errors;
using CxCompiler.Model.Expressions;
using CxCompiler.Model.Statements;
using CxCompiler.Model.Types;
using CxCompiler.Model.Types.BuiltInTypes;
using CxCompiler.OutputGenerators;

namespace CxCompiler.Semantics;

/// <summary>
/// Builds ordinary CX classes for iterator state-machine lowering.
/// The generated MoveNext method is a state machine; its frame fields and
/// interface implementations then use the normal binder, C emitter, and GC.
/// </summary>
internal static class IteratorDeclarationLowerer
{
    public static IReadOnlyList<ClassDeclaration> Lower(
        CompilationContext context,
        IReadOnlyList<CompilationContext> availableContexts)
    {
        var generated = new List<ClassDeclaration>();
        foreach (var function in EnumerateFunctions(context.DeclarationScope.Declarations)
            .Where(function => function.Body is not null)
            .ToArray())
        {
            var isAsyncIterator = function.IsAsync &&
                AsyncFunctionLowerer.IsAsyncEnumerableType(function.ReturnType);
            var yields = EnumerateYields(function.Body!).ToArray();
            if (yields.Length == 0)
            {
                continue;
            }

            if (function.IsAsync && !isAsyncIterator)
            {
                throw new CompilationErrorException(
                    "A method cannot be both async and an iterator.",
                    function.SourceSpan);
            }

            if (isAsyncIterator)
            {
                ValidateAsyncIteratorAwaitForms(function);
            }

            if (!function.IsStatic && function.ParentClassDeclaration is
                { ClassType: ClassType.Struct })
            {
                throw new CompilationErrorException(
                    "Instance iterator methods on structs are not supported yet.",
                    function.SourceSpan);
            }

            var sequenceType = function.ReturnType is ConstType constant
                ? constant.UnderlyingType as NamedType
                : function.ReturnType as NamedType;
            if (sequenceType is null || sequenceType.TypeArguments.Count != 1 ||
                (isAsyncIterator
                    ? !AsyncFunctionLowerer.IsAsyncEnumerableType(sequenceType)
                    : !IsEnumerableType(sequenceType)))
            {
                continue;
            }

            if (!TryPrepareBody(function, isAsyncIterator, out var localDeclarations, out var localTypes, out var blocks,
                    out var cleanupRegions, out var entryState))
            {
                throw new CompilationErrorException(
                    "This iterator body shape is not supported by the current C state-machine lowering.",
                    function.SourceSpan);
            }

            var iteratorName = CreateIteratorName(function);
            var asyncOperationTypes = isAsyncIterator
                ? ResolveAsyncIteratorOperationTypes(
                    sequenceType, availableContexts)
                : null;
            var iterator = CreateIteratorType(
                context, function, iteratorName, sequenceType.TypeArguments[0],
                localDeclarations, localTypes, blocks, cleanupRegions, entryState,
                isAsyncIterator,
                asyncOperationTypes?.MoveNextReturnType,
                asyncOperationTypes?.DisposeReturnType);
            generated.Add(iterator);
            context.DeclarationScope.AddDeclaration(iterator);
            context.AddImport(new QualifiedIdentifier("System", "Collections"));
            function.MarkIteratorFactory(sequenceType.TypeArguments[0], iterator);
            var constructorArguments = new List<ExpressionBase>();
            if (!function.IsStatic && function.ParentClassDeclaration is not null)
            {
                constructorArguments.Add(new ThisExpression());
            }
            constructorArguments.AddRange(function.Parameters.Select(parameter =>
                (ExpressionBase)new IdentifierExpression(
                    new QualifiedIdentifier(parameter.Name))));
            if (isAsyncIterator)
            {
                constructorArguments.Add(new MemberAccessExpression(
                    new IdentifierExpression(new QualifiedIdentifier(
                        "System", "Threading", "Tasks", "CancellationToken")),
                    "None"));
            }
            var frameGenericTypeNames = GetFrameGenericTypeNames(function);
            function.SetBody([
                new ReturnStatement(new ObjectCreationExpression(
                    NamedType.CreateWithTypeArguments(
                        iteratorName,
                        frameGenericTypeNames.Select(name =>
                            (TypeBase)new GenericType(name)).ToArray()),
                    constructorArguments)),
            ]);
        }
        return generated;
    }

    private static void ValidateAsyncIteratorAwaitForms(FunctionDeclaration function)
    {
        var awaitedYieldCleanup = EnumerateStatements(function.Body!)
            .OfType<TryStatement>()
            .FirstOrDefault(statement => statement.FinallyBody is not null &&
                EnumerateYields([statement.Body]).Any() &&
                CoroutineSyntaxAnalysis.EnumerateBodyExpressions(
                    [statement.FinallyBody]).OfType<AwaitExpression>().Any());
        if (awaitedYieldCleanup is not null)
        {
            throw new CompilationErrorException(
                "Async iterator finally blocks that await while protecting a yield are not supported yet.",
                awaitedYieldCleanup.SourceSpan ?? function.SourceSpan);
        }

        var supportedAwaits = CoroutineSyntaxAnalysis.EnumerateStatements(function.Body!)
            .Select(statement => statement switch
            {
                ExpressionStatement { Expression: AwaitExpression awaitExpression } =>
                    awaitExpression,
                YieldReturnStatement { Expression: AwaitExpression awaitExpression } =>
                    awaitExpression,
                LocalVariableDeclarationStatement
                {
                    Declarators: [{ Initializer: AwaitExpression awaitExpression,
                        Type: not null }],
                } => awaitExpression,
                _ => null,
            })
            .Where(awaitExpression => awaitExpression is not null)
            .ToHashSet(ReferenceEqualityComparer.Instance);
        if (CoroutineSyntaxAnalysis.EnumerateBodyExpressions(function.Body!)
            .OfType<AwaitExpression>()
            .Any(awaitExpression => !supportedAwaits.Contains(awaitExpression)))
        {
            throw new CompilationErrorException(
                "Async iterator await must be a standalone statement or initialize one typed or inferred local.",
                function.SourceSpan);
        }
    }

    private static bool TryPrepareBody(
        FunctionDeclaration function,
        bool isAsyncIterator,
        out IReadOnlyList<LocalVariableDeclarator> locals,
        out IReadOnlyDictionary<string, TypeBase> localTypes,
        out IReadOnlyList<IteratorBlock> blocks,
        out IReadOnlyList<IteratorCleanupRegion> cleanupRegions,
        out int entryState)
    {
        if (!isAsyncIterator && CoroutineSuspensionPlanBuilder.TryBuildLinearYieldPlan(
                function.Body!, out var plan))
        {
            locals = [];
            localTypes = new Dictionary<string, TypeBase>(StringComparer.Ordinal);
            blocks = plan.Blocks.Select(block => new IteratorBlock(
                block.State,
                block.Operations,
                block.Terminator switch
                {
                    CoroutinePlanNext next => new IteratorNext(next.State),
                    CoroutinePlanBranch branch => new IteratorBranch(
                        branch.Condition, branch.WhenTrue, branch.WhenFalse),
                    CoroutinePlanYield yielded => new IteratorYield(
                        yielded.Expression, yielded.NextState, yielded.SourceSpan),
                    CoroutinePlanComplete => new IteratorComplete(),
                    _ => throw new InternalCompilerException(
                        "Unsupported terminator in linear coroutine plan."),
                })).ToArray();
            cleanupRegions = [];
            entryState = plan.EntryState;
            return true;
        }

        var collectedLocals = new List<LocalVariableDeclarator>();
        var collectedLocalTypes = new Dictionary<string, TypeBase>(StringComparer.Ordinal);
        var names = function.Parameters.Select(parameter => parameter.Name)
            .ToHashSet(StringComparer.Ordinal);
        var builder = new IteratorControlFlowBuilder(
            collectedLocals, collectedLocalTypes, names, isAsyncIterator,
            function.FullName.ToString());
        if (!builder.TryBuild(function.Body!, out entryState))
        {
            locals = [];
            localTypes = new Dictionary<string, TypeBase>();
            blocks = [];
            cleanupRegions = [];
            entryState = 0;
            return false;
        }
        locals = collectedLocals;
        localTypes = collectedLocalTypes;
        blocks = builder.Blocks;
        cleanupRegions = builder.CleanupRegions;
        return builder.HasYield;
    }

    private static (TypeBase MoveNextReturnType, TypeBase DisposeReturnType)?
        ResolveAsyncIteratorOperationTypes(
            NamedType sequenceType,
            IReadOnlyList<CompilationContext> contexts)
    {
        var classes = contexts.SelectMany(context =>
                context.DeclarationScope.Declarations.OfType<ClassDeclaration>()
                    .Select(declaration => (Declaration: declaration,
                        ProjectName: context.ProjectName)))
            .ToArray();
        bool MatchesType(
            ClassDeclaration declaration,
            string? contextProjectName,
            NamedType type)
        {
            var declarationName = declaration.FullName.ToString();
            var resolvedName = type.ResolvedTypeFullName.ToString();
            var projectName = declaration.ProjectName ?? contextProjectName;
            return declarationName == resolvedName ||
                (projectName is { Length: > 0 } &&
                 resolvedName.EndsWith("/" + projectName + "." + declarationName,
                    StringComparison.Ordinal)) ||
                resolvedName.EndsWith("/" + declarationName,
                    StringComparison.Ordinal) ||
                resolvedName.EndsWith("." + declarationName,
                    StringComparison.Ordinal) ||
                declarationName == type.FullName.ToString();
        }
        ClassDeclaration? FindType(NamedType type) =>
            classes.FirstOrDefault(candidate => MatchesType(
                candidate.Declaration, candidate.ProjectName, type))
                .Declaration;

        var sequenceInterface = FindType(sequenceType);
        var getEnumerator = sequenceInterface?.MemberDeclarations.Declarations
            .OfType<FunctionDeclaration>()
            .SingleOrDefault(function => function.Name == "GetAsyncEnumerator");
        var enumeratorType = getEnumerator?.ReturnType as NamedType;
        var enumeratorInterface = enumeratorType is null
            ? null
            : FindType(enumeratorType);
        var moveNext = enumeratorInterface?.MemberDeclarations.Declarations
            .OfType<FunctionDeclaration>()
            .SingleOrDefault(function => function.Name == "MoveNextAsync");
        if (moveNext is null || enumeratorInterface is null)
        {
            return null;
        }

        var visited = new HashSet<ClassDeclaration>();
        IEnumerable<FunctionDeclaration> EnumerateInterfaceFunctions(
            ClassDeclaration declaration)
        {
            if (!visited.Add(declaration))
            {
                yield break;
            }
            foreach (var function in declaration.MemberDeclarations.Declarations
                .OfType<FunctionDeclaration>())
            {
                yield return function;
            }
            foreach (var baseType in declaration.BaseTypes.OfType<NamedType>())
            {
                if (FindType(baseType) is { } baseDeclaration)
                {
                    foreach (var function in EnumerateInterfaceFunctions(baseDeclaration))
                    {
                        yield return function;
                    }
                }
            }
        }

        var dispose = EnumerateInterfaceFunctions(enumeratorInterface)
            .SingleOrDefault(function => function.Name == "DisposeAsync");
        return dispose is null
            ? null
            : (moveNext.ReturnType, dispose.ReturnType);
    }

    private static ClassDeclaration CreateIteratorType(
        CompilationContext context,
        FunctionDeclaration function,
        string iteratorName,
        TypeBase elementType,
        IReadOnlyList<LocalVariableDeclarator> localDeclarations,
        IReadOnlyDictionary<string, TypeBase> localTypes,
        IReadOnlyList<IteratorBlock> blocks,
        IReadOnlyList<IteratorCleanupRegion> cleanupRegions,
        int entryState,
        bool isAsyncIterator,
        TypeBase? asyncMoveNextReturnType,
        TypeBase? asyncDisposeReturnType)
    {
        var scope = new DeclarationScope(@namespace: context.Namespace);
        var iterator = new ClassDeclaration(
            [ClassModifier.Internal], false, ClassType.Class, null,
            iteratorName, GetFrameGenericTypeNames(function), scope,
            GetFrameGenericConstraints(function), GetFrameConceptConstraints(function))
        {
            ProjectName = context.ProjectName,
        };
        var sequenceInterface = Constructed(isAsyncIterator
            ? "System.Collections.IAsyncEnumerable"
            : "System.Collections.IEnumerable", elementType);
        var enumeratorInterface = Constructed(isAsyncIterator
            ? "System.Collections.IAsyncEnumerator"
            : "System.Collections.IEnumerator", elementType);
        iterator.AddBaseType(sequenceInterface);
        iterator.AddBaseType(enumeratorInterface);

        AddField("__cx_state", BuiltInSystemTypes.UInt);
        AddField("__cx_hasCurrent", BuiltInSystemTypes.Bool);
        AddField("__cx_current", elementType);
        if (!isAsyncIterator)
        {
            AddField("__cx_advancing", BuiltInSystemTypes.Bool);
        }
        var cancellationTokenType = new NamedType(
            "System.Threading.Tasks.CancellationToken", []);
        if (isAsyncIterator)
        {
            AddField("__cx_cancellationToken", cancellationTokenType);
            AddField("__cx_async_move_source", Constructed(
                "System.Threading.Tasks.ReusableValueTaskSource", BuiltInSystemTypes.Bool));
            AddField("__cx_async_move_started", BuiltInSystemTypes.Bool);
            AddField("__cx_async_suspended", BuiltInSystemTypes.Bool);
            AddField("__cx_async_fault_cleanup_pending", BuiltInSystemTypes.Bool);
            AddField("__cx_async_cancellation_registration",
                new NamedType("System.Threading.Tasks.CancellationTokenRegistration", []));
        }
        var receiverType = !function.IsStatic && function.ParentClassDeclaration is { } owner
            ? owner.GenericTypeNames.Length == 0
                ? new NamedType(owner.FullName.ToString(), [])
                : NamedType.CreateWithTypeArguments(
                    owner.FullName.ToString(),
                    owner.GenericTypeNames.Select(name =>
                        (TypeBase)new GenericType(name)).ToArray())
            : null;
        if (receiverType is not null)
        {
            AddField("__cx_receiver", receiverType);
        }
        foreach (var region in cleanupRegions)
        {
            AddField(region.ActiveFieldName, BuiltInSystemTypes.Bool);
        }

        var parameterFields = function.Parameters
            .Select((parameter, index) => new FrameLocal(
                parameter.Name, $"__cx_parameter_{index}", parameter.ParameterType,
                parameter.LocalSymbol))
            .ToArray();
        var localFields = localDeclarations
            .Select((local, index) => new FrameLocal(
                local.Name, $"__cx_local_{index}",
                localTypes[local.Name], local.Symbol))
            .ToArray();
        foreach (var frameLocal in parameterFields.Concat(localFields))
        {
            AddField(frameLocal.FieldName, frameLocal.Type);
        }

        var constructor = new ConstructorDeclaration(
            iterator.FullName,
            [MemberModifier.Public], iterator);
        var constructorStatements = new List<StatementBase>();
        constructorStatements.Add(Assign(Member("__cx_state"),
            new LiteralExpression($"{entryState}u")));
        if (receiverType is not null)
        {
            constructor.AddParameter(new FunctionParameter("__cx_receiver", receiverType, null));
            constructorStatements.Add(Assign(Member("__cx_receiver"), Id("__cx_receiver")));
        }
        foreach (var parameter in function.Parameters)
        {
            constructor.AddParameter(new FunctionParameter(
                parameter.Name, parameter.ParameterType, null));
            constructorStatements.Add(Assign(
                Member("__cx_parameter_" + function.Parameters
                    .TakeWhile(candidate => candidate.Name != parameter.Name).Count()),
                Id(parameter.Name)));
        }
        if (isAsyncIterator)
        {
            constructor.AddParameter(new FunctionParameter(
                "__cx_cancellationToken", cancellationTokenType, null));
            constructorStatements.Add(Assign(
                Member("__cx_cancellationToken"), Id("__cx_cancellationToken")));
            constructorStatements.Add(Assign(
                Member("__cx_async_move_source"),
                new ObjectCreationExpression(Constructed(
                    "System.Threading.Tasks.ReusableValueTaskSource",
                    BuiltInSystemTypes.Bool), [])));
            constructorStatements.Add(Assign(
                Member("__cx_async_move_started"),
                new LiteralExpression("false")));
        }
        constructor.SetBody(constructorStatements);
        scope.AddDeclaration(constructor);

        var cloneArguments = (receiverType is null
                ? Enumerable.Empty<ExpressionBase>()
                : [Member("__cx_receiver")])
            .Concat(parameterFields.Select(frameLocal =>
                (ExpressionBase)Member(frameLocal.FieldName)))
            .ToList();
        var cloneName = isAsyncIterator ? "GetAsyncEnumerator" : "GetEnumerator";
        var cloneMethod = new FunctionDeclaration(
            cloneName, iterator.FullName,
            enumeratorInterface, [MemberModifier.Public], iterator,
            @const: !isAsyncIterator);
        if (isAsyncIterator)
        {
            cloneMethod.AddParameter(new FunctionParameter(
                "cancellationToken", cancellationTokenType, null));
            cloneArguments.Add(Id("cancellationToken"));
        }
        var clonedIterator = new ObjectCreationExpression(
            Constructed(iteratorName, GetFrameGenericTypeNames(function)
                .Select(name => (TypeBase)new GenericType(name)).ToArray()),
            cloneArguments.ToArray());
        cloneMethod.SetBody([new ReturnStatement(clonedIterator)]);
        scope.AddDeclaration(cloneMethod);

        AddCurrentProperty(iterator, elementType);
        var moveNextCore = new FunctionDeclaration(
            isAsyncIterator ? "MoveNextCore" : "MoveNext", iterator.FullName,
            BuiltInSystemTypes.Bool,
            [isAsyncIterator ? MemberModifier.Private : MemberModifier.Public], iterator);
        moveNextCore.SetIteratorCaptureCells(localFields
            .Where(frameLocal => frameLocal.Symbol?.IsCaptured == true)
            .ToDictionary(frameLocal => frameLocal.Symbol!, frameLocal => frameLocal.FieldName));
        moveNextCore.SetBody(BuildMoveNextBody(
            blocks, parameterFields.Concat(localFields).ToArray(),
            localDeclarations, cleanupRegions, elementType, isAsyncIterator,
            function.FullName.ToString()));
        scope.AddDeclaration(moveNextCore);

        var disposeCore = new FunctionDeclaration(
            isAsyncIterator ? "DisposeCore" : "Dispose", iterator.FullName,
            BuiltInSystemTypes.Void,
            [isAsyncIterator ? MemberModifier.Private : MemberModifier.Public], iterator);
        var disposeCoreBody = BuildCleanupChain(
            cleanupRegions, ClearEnumeratorState(localFields));
        disposeCore.SetBody(isAsyncIterator
            ? [new ExpressionStatement(new InvocationExpression(
                    new MemberAccessExpression(
                        Member("__cx_async_cancellation_registration"), "Dispose"), [])),
                .. disposeCoreBody]
            : disposeCoreBody);
        scope.AddDeclaration(disposeCore);

        if (isAsyncIterator)
        {
            var taskType = Constructed(
                "System.Threading.Tasks.Task", BuiltInSystemTypes.Bool);
            var valueTaskType = Constructed(
                "System.Threading.Tasks.ValueTask", BuiltInSystemTypes.Bool);
            var moveNextReturnType = asyncMoveNextReturnType ?? valueTaskType;
            var usesTaskMoveNext = AsyncFunctionLowerer.IsTaskType(
                moveNextReturnType as NamedType ?? valueTaskType);
            var moveNextAsync = new FunctionDeclaration(
                "MoveNextAsync", iterator.FullName, moveNextReturnType,
                [MemberModifier.Public], iterator);
            moveNextAsync.SetBody(BuildAsyncMoveNextBody(usesTaskMoveNext));
            scope.AddDeclaration(moveNextAsync);

            var resumeAsyncMove = new FunctionDeclaration(
                "ResumeAsyncMove", iterator.FullName, BuiltInSystemTypes.Void,
                [MemberModifier.Private], iterator);
            resumeAsyncMove.SetBody(BuildResumeAsyncMoveBody());
            scope.AddDeclaration(resumeAsyncMove);

            var taskHandleType = new NamedType("System.Threading.Tasks.TaskHandle", []);
            var valueTaskHandleType = new NamedType("System.Threading.Tasks.ValueTaskHandle", []);
            var disposeReturnType = asyncDisposeReturnType ?? valueTaskHandleType;
            var disposeNamedType = disposeReturnType as NamedType ?? valueTaskHandleType;
            var usesTaskDispose = AsyncFunctionLowerer.IsNonGenericTaskType(
                disposeNamedType) ||
                disposeNamedType.ResolvedTypeFullName.Parts.LastOrDefault() ==
                    "TaskHandle" ||
                disposeNamedType.Name.Split('.', StringSplitOptions.RemoveEmptyEntries)
                    .LastOrDefault() == "TaskHandle";
            var moveSource = Member("__cx_async_move_source");
            var disposeAsync = new FunctionDeclaration(
                "DisposeAsync", iterator.FullName, disposeReturnType,
                [MemberModifier.Public], iterator);
            var disposeExceptionName = "__cx_async_dispose_exception";
            disposeAsync.SetBody([
                new TryStatement(
                    new BlockStatement([
                        Call("DisposeCore"),
                        new IfStatement(
                            new BinaryExpression(Member("__cx_async_move_started"), "==",
                                new LiteralExpression("true")),
                            new BlockStatement([
                                new IfStatement(
                                    new UnaryExpression("!", IsAsyncMoveSourceCompleted()),
                                    new BlockStatement([InvokeOn(
                                        moveSource, "SetCanceled")]),
                                    null),
                            ]),
                            null),
                        new ReturnStatement(usesTaskDispose
                            ? new ObjectCreationExpression(taskHandleType, [])
                            : new ObjectCreationExpression(valueTaskHandleType, [])),
                    ]),
                    [new CatchClause(
                        new NamedType("System.Exception", []),
                        disposeExceptionName,
                        null,
                        new BlockStatement([
                            new ReturnStatement(usesTaskDispose
                                ? new InvocationExpression(
                                    new MemberAccessExpression(
                                        new IdentifierExpression(new QualifiedIdentifier(
                                            "System", "Threading", "Tasks", "TaskHandle")),
                                        "Faulted"),
                                    [Id(disposeExceptionName)])
                                : new ObjectCreationExpression(valueTaskHandleType, [
                                    new InvocationExpression(
                                        new MemberAccessExpression(
                                            new IdentifierExpression(new QualifiedIdentifier(
                                                "System", "Threading", "Tasks", "TaskHandle")),
                                            "Faulted"),
                                        [Id(disposeExceptionName)]),
                                ])),
                        ]))],
                    null),
            ]);
            scope.AddDeclaration(disposeAsync);
        }

        return iterator;

        void AddField(string name, TypeBase type) => scope.AddDeclaration(new FieldDeclaration(
            name, type, [MemberModifier.Private], iterator));

        ExpressionBase IsAsyncMoveSourceCompleted() => new InvocationExpression(
            new MemberAccessExpression(new IdentifierExpression(new QualifiedIdentifier(
                "System", "Threading", "Tasks", "TaskRuntime")),
                "ReusableSourceIsCompleted"),
            [Member("__cx_async_move_source"), new InvocationExpression(
                new MemberAccessExpression(Member("__cx_async_move_source"), "GetVersion"), [])]);

        IReadOnlyList<StatementBase> BuildAsyncMoveNextBody(bool returnTask)
        {
            var asyncTaskType = Constructed(
                "System.Threading.Tasks.Task", BuiltInSystemTypes.Bool);
            var valueTaskType = Constructed(
                "System.Threading.Tasks.ValueTask", BuiltInSystemTypes.Bool);
            var sourceField = Member("__cx_async_move_source");
            const string concurrentTaskName = "__cx_concurrent_move_task";
            const string concurrentValueTaskName = "__cx_concurrent_move_value_task";
            const string returnedValueTaskName = "__cx_move_value_task";
            var concurrentTask = Id(concurrentTaskName);
            var concurrentValueTask = Id(concurrentValueTaskName);
            var returnedValueTask = Id(returnedValueTaskName);
            var returnedValueTaskDeclaration = new LocalVariableDeclarationStatement(
                valueTaskType,
                [new LocalVariableDeclarator(returnedValueTaskName,
                    new ObjectCreationExpression(valueTaskType, []))]);
            var concurrentFailureStatements = new List<StatementBase>
            {
                new LocalVariableDeclarationStatement(
                    asyncTaskType,
                    [new LocalVariableDeclarator(concurrentTaskName,
                        new ObjectCreationExpression(asyncTaskType, []))]),
            };
            if (!returnTask)
            {
                concurrentFailureStatements.Add(
                    new LocalVariableDeclarationStatement(
                        valueTaskType,
                        [new LocalVariableDeclarator(concurrentValueTaskName,
                            new ObjectCreationExpression(valueTaskType, []))]));
            }
            concurrentFailureStatements.Add(InvokeOn(concurrentTask, "SetFault",
                new ObjectCreationExpression(
                    new NamedType("System.Exception", []), [])));
            if (returnTask)
            {
                concurrentFailureStatements.Add(new ReturnStatement(concurrentTask));
            }
            else
            {
                concurrentFailureStatements.Add(InvokeOn(
                    concurrentValueTask, "SetTask", concurrentTask));
                concurrentFailureStatements.Add(
                    new ReturnStatement(concurrentValueTask));
            }
            var concurrentTaskCheck = new IfStatement(
                new BinaryExpression(Member("__cx_async_move_started"), "==",
                    new LiteralExpression("true")),
                new BlockStatement([
                    new IfStatement(
                        new UnaryExpression("!", IsAsyncMoveSourceCompleted()),
                        new BlockStatement(concurrentFailureStatements),
                        null),
                    new ExpressionStatement(new InvocationExpression(
                        new MemberAccessExpression(sourceField, "Reset"), [])),
                ]),
                new BlockStatement([Assign(Member("__cx_async_move_started"),
                    new LiteralExpression("true"))]));
            var taskReturnStatement = new IfStatement(
                new MemberAccessExpression(returnedValueTask, "IsCompleted"),
                new BlockStatement([
                    new TryStatement(
                        new BlockStatement([new ReturnStatement(
                            new ObjectCreationExpression(asyncTaskType, [
                                new InvocationExpression(
                                    new MemberAccessExpression(
                                        returnedValueTask, "GetResult"), []),
                            ]))]),
                        [
                            new CatchClause(
                                new NamedType(
                                    "System.Threading.Tasks.OperationCanceledException", []),
                                "__cx_async_move_canceled",
                                null,
                                new BlockStatement([
                                    new LocalVariableDeclarationStatement(
                                        asyncTaskType,
                                        [new LocalVariableDeclarator(
                                            "__cx_async_canceled_task",
                                            new ObjectCreationExpression(asyncTaskType, []))]),
                                    InvokeOn(Id("__cx_async_canceled_task"), "SetCanceled"),
                                    new ReturnStatement(Id("__cx_async_canceled_task")),
                                ])),
                            new CatchClause(
                                new NamedType("System.Exception", []),
                                "__cx_async_move_fault",
                                null,
                                new BlockStatement([
                                    new LocalVariableDeclarationStatement(
                                        asyncTaskType,
                                        [new LocalVariableDeclarator(
                                            "__cx_async_faulted_task",
                                            new ObjectCreationExpression(asyncTaskType, []))]),
                                    InvokeOn(Id("__cx_async_faulted_task"), "SetFault",
                                        Id("__cx_async_move_fault")),
                                    new ReturnStatement(Id("__cx_async_faulted_task")),
                                ])),
                        ], null),
                ]),
                new ReturnStatement(new InvocationExpression(
                    new MemberAccessExpression(returnedValueTask, "AsTask"), [])));
            return [
                returnedValueTaskDeclaration,
                concurrentTaskCheck,
                Assign(Member("__cx_async_suspended"),
                    new LiteralExpression("false")),
                Call("ResumeAsyncMove"),
                InvokeOn(returnedValueTask, "SetSource", sourceField,
                    new InvocationExpression(new MemberAccessExpression(
                        sourceField, "GetVersion"), [])),
                returnTask
                    ? taskReturnStatement
                    : new ReturnStatement(returnedValueTask),
            ];
        }

        IReadOnlyList<StatementBase> BuildResumeAsyncMoveBody()
        {
            const string exceptionName = "__cx_async_move_exception";
            var source = Member("__cx_async_move_source");
            var canceledClause = new CatchClause(
                new NamedType("System.Threading.Tasks.OperationCanceledException", []),
                null, null,
                new BlockStatement([
                    new ExpressionStatement(new InvocationExpression(
                        new MemberAccessExpression(
                            Member("__cx_async_cancellation_registration"), "Dispose"), [])),
                    InvokeOn(source, "SetCanceled"),
                ]));
            var exceptionClause = new CatchClause(
                new NamedType("System.Exception", []),
                exceptionName, null,
                new BlockStatement([
                    new ExpressionStatement(new InvocationExpression(
                        new MemberAccessExpression(
                            Member("__cx_async_cancellation_registration"), "Dispose"), [])),
                    InvokeOn(source, "SetException", Id(exceptionName)),
                ]));
            return [new TryStatement(
                new BlockStatement([
                    new IfStatement(
                        IsAsyncMoveSourceCompleted(),
                        new BlockStatement([new ReturnStatement(null)]),
                        null),
                    new IfStatement(
                        new BinaryExpression(
                            new MemberAccessExpression(
                                Member("__cx_cancellationToken"), "IsCancellationRequested"),
                            "&&",
                            new UnaryExpression("!",
                                Member("__cx_async_fault_cleanup_pending"))),
                        new BlockStatement([
                            Call("DisposeCore"),
                            InvokeOn(source, "SetCanceled"),
                            new ReturnStatement(null),
                        ]),
                        null),
                    Assign(Member("__cx_async_suspended"),
                        new LiteralExpression("false")),
                    new LocalVariableDeclarationStatement(BuiltInSystemTypes.Bool,
                        [new LocalVariableDeclarator(
                            "__cx_async_move_has_value", CallExpression("MoveNextCore"))]),
                    new IfStatement(
                        new UnaryExpression("!", Member("__cx_async_suspended")),
                        new BlockStatement([InvokeOn(source, "SetResult",
                            Id("__cx_async_move_has_value"))]),
                        null),
                    new ReturnStatement(null),
                ]),
                [canceledClause, exceptionClause], null)];
        }

        ExpressionStatement Call(string method) =>
            new(new InvocationExpression(Member(method), []));

        ExpressionStatement InvokeOn(ExpressionBase target, string method,
            params ExpressionBase[] arguments) =>
            new(new InvocationExpression(
                new MemberAccessExpression(target, method), arguments));

        InvocationExpression CallExpression(string method) =>
            new(Member(method), []);
    }

    private static IReadOnlyList<StatementBase> BuildMoveNextBody(
        IReadOnlyList<IteratorBlock> blocks,
        IReadOnlyList<FrameLocal> frameLocals,
        IReadOnlyList<LocalVariableDeclarator> localDeclarations,
        IReadOnlyList<IteratorCleanupRegion> cleanupRegions,
        TypeBase elementType,
        bool isAsyncIterator,
        string functionIdentity)
    {
        if (isAsyncIterator && blocks.Count != 0)
        {
            var nextResumeState = blocks.Max(block => block.State) + 1;
            var initialBlocks = new List<IteratorBlock>(blocks.Count);
            var resumeBlocks = new List<IteratorBlock>();
            foreach (var block in blocks)
            {
                if (block.Terminator is not IteratorAwait awaited)
                {
                    initialBlocks.Add(block);
                    continue;
                }

                var resumeState = nextResumeState++;
                var resumeSuffix = $"#awaitresume:{functionIdentity}:{block.State}";
                SourceSpan? resumeSpan = awaited.SourceSpan is { } sourceSpan
                    ? sourceSpan with
                    {
                        FilePath = $"{sourceSpan.FilePath}{resumeSuffix}",
                        StartLine = 1,
                        StartColumn = 1,
                    }
                    : null;
                SourceSpan? resumeFaultSpan = block.FaultSourceSpan is { } faultSpan
                    ? faultSpan with
                    {
                        FilePath = $"{faultSpan.FilePath}{resumeSuffix}",
                        StartLine = 1,
                        StartColumn = 1,
                    }
                    : null;
                initialBlocks.Add(block with
                {
                    Terminator = awaited with { ResumeState = resumeState },
                });
                resumeBlocks.Add(block with
                {
                    State = resumeState,
                    Operations = [],
                    Terminator = awaited with
                    {
                        SourceSpan = resumeSpan,
                        ResumeState = null,
                    },
                    FaultSourceSpan = resumeFaultSpan,
                });
            }
            blocks = [.. initialBlocks, .. resumeBlocks];
        }

        var statements = new List<StatementBase>();
        foreach (var local in frameLocals)
        {
            var declaration = new LocalVariableDeclarator(
                local.Name, Member(local.FieldName));
            if (local.Symbol is not null)
            {
                declaration.BindType(local.Type, local.Symbol);
            }
            statements.Add(new LocalVariableDeclarationStatement(
                local.Type, [declaration]));
        }

        var sections = new List<SwitchSection>();
        foreach (var block in blocks)
        {
            var body = new List<StatementBase>();
            if (block.FaultState is { } operationFaultState &&
                block.FaultExceptionLocalName is { } operationFaultExceptionLocal)
            {
                var operationCaughtName = $"__cx_async_foreach_caught_ops_{block.State}";
                body.Add(new LocalVariableDeclarationStatement(BuiltInSystemTypes.Bool,
                    [new LocalVariableDeclarator(operationCaughtName,
                        new LiteralExpression("false"))]));
                body.Add(new TryStatement(
                    new BlockStatement(block.Operations),
                    [new CatchClause(
                        new NamedType("System.Exception", []),
                        "__cx_async_foreach_operation_exception",
                        null,
                        new BlockStatement([
                            Assign(Id(operationFaultExceptionLocal),
                                Id("__cx_async_foreach_operation_exception")),
                            Assign(Id(operationCaughtName),
                                new LiteralExpression("true")),
                        ]))],
                    null));
                body.Add(new IfStatement(Id(operationCaughtName),
                    new BlockStatement(BuildAsyncFaultRedirect(
                        frameLocals, operationFaultExceptionLocal,
                        operationFaultState, block.FaultSourceSpan)), null));
            }
            else
            {
                body.AddRange(block.Operations);
            }
            switch (block.Terminator)
            {
                case IteratorNext next:
                    body.AddRange(ContinueAt(next.State));
                    break;
                case IteratorBranch branch:
                    if (block.FaultState is { } branchFaultState &&
                        block.FaultExceptionLocalName is { } branchFaultExceptionLocal)
                    {
                        var conditionName = $"__cx_async_foreach_condition_{block.State}";
                        var caughtName = $"__cx_async_foreach_caught_condition_{block.State}";
                        body.Add(new LocalVariableDeclarationStatement(
                            BuiltInSystemTypes.Bool,
                            [new LocalVariableDeclarator(conditionName,
                                new LiteralExpression("false"))]));
                        body.Add(new LocalVariableDeclarationStatement(
                            BuiltInSystemTypes.Bool,
                            [new LocalVariableDeclarator(caughtName,
                                new LiteralExpression("false"))]));
                        body.Add(new TryStatement(
                            new BlockStatement([Assign(Id(conditionName), branch.Condition)]),
                            [new CatchClause(
                                new NamedType("System.Exception", []),
                                "__cx_async_foreach_condition_exception",
                                null,
                                new BlockStatement([
                                    Assign(Id(branchFaultExceptionLocal),
                                        Id("__cx_async_foreach_condition_exception")),
                                    Assign(Id(caughtName), new LiteralExpression("true")),
                                ]))],
                            null));
                        body.Add(new IfStatement(Id(caughtName),
                            new BlockStatement(BuildAsyncFaultRedirect(
                                frameLocals, branchFaultExceptionLocal,
                                branchFaultState,
                                block.FaultSourceSpan is { } faultBranchSpan
                                    ? faultBranchSpan with
                                    {
                                        StartColumn = faultBranchSpan.StartColumn + 1,
                                    }
                                    : null)), null));
                        body.Add(new IfStatement(Id(conditionName),
                            new BlockStatement(ContinueAt(branch.WhenTrue)),
                            new BlockStatement(ContinueAt(branch.WhenFalse))));
                    }
                    else
                    {
                        body.Add(new IfStatement(branch.Condition,
                            new BlockStatement(ContinueAt(branch.WhenTrue)),
                            new BlockStatement(ContinueAt(branch.WhenFalse))));
                    }
                    break;
                case IteratorDispatch dispatch:
                    if (block.FaultState is { } dispatchFaultState &&
                        block.FaultExceptionLocalName is { } dispatchFaultExceptionLocal)
                    {
                        var dispatchCaughtName =
                            $"__cx_async_foreach_caught_dispatch_{block.State}";
                        body.Add(new LocalVariableDeclarationStatement(
                            BuiltInSystemTypes.Bool,
                            [new LocalVariableDeclarator(dispatchCaughtName,
                                new LiteralExpression("false"))]));
                        body.Add(new TryStatement(
                            new BlockStatement([BuildAsyncDispatch(dispatch.Selection)]),
                            [new CatchClause(
                                new NamedType("System.Exception", []),
                                "__cx_async_foreach_dispatch_exception",
                                null,
                                new BlockStatement([
                                    Assign(Id(dispatchFaultExceptionLocal),
                                        Id("__cx_async_foreach_dispatch_exception")),
                                    Assign(Id(dispatchCaughtName),
                                        new LiteralExpression("true")),
                                ]))],
                            null));
                        body.Add(new IfStatement(Id(dispatchCaughtName),
                            new BlockStatement(BuildAsyncFaultRedirect(
                                frameLocals, dispatchFaultExceptionLocal,
                                dispatchFaultState,
                                block.FaultSourceSpan is { } dispatchFaultSpan
                                    ? dispatchFaultSpan with
                                    {
                                        StartColumn = dispatchFaultSpan.StartColumn + 3,
                                    }
                                    : null)), null));
                        body.Add(new ContinueStatement());
                    }
                    else
                    {
                        body.Add(dispatch.Selection);
                    }
                    break;
                case IteratorYield yielded:
                    if (block.FaultState is { } yieldFaultState &&
                        block.FaultExceptionLocalName is { } yieldFaultExceptionLocal)
                    {
                        var caughtName = $"__cx_async_foreach_caught_yield_{block.State}";
                        body.Add(new LocalVariableDeclarationStatement(
                            BuiltInSystemTypes.Bool,
                            [new LocalVariableDeclarator(caughtName,
                                new LiteralExpression("false"))]));
                        body.Add(new TryStatement(
                            new BlockStatement([
                                new ExpressionStatement(new AssignmentExpression(
                                    Member("__cx_current"), "=", yielded.Expression))
                                {
                                    SourceSpan = yielded.SourceSpan,
                                },
                            ]),
                            [new CatchClause(
                                new NamedType("System.Exception", []),
                                "__cx_async_foreach_yield_exception",
                                null,
                                new BlockStatement([
                                    Assign(Id(yieldFaultExceptionLocal),
                                        Id("__cx_async_foreach_yield_exception")),
                                    Assign(Id(caughtName), new LiteralExpression("true")),
                                ]))],
                            null));
                        body.Add(new IfStatement(Id(caughtName),
                            new BlockStatement(BuildAsyncFaultRedirect(
                                frameLocals, yieldFaultExceptionLocal,
                                yieldFaultState,
                                block.FaultSourceSpan is { } faultYieldSpan
                                    ? faultYieldSpan with
                                    {
                                        StartColumn = faultYieldSpan.StartColumn + 2,
                                    }
                                    : null)), null));
                    }
                    else
                    {
                        body.Add(new ExpressionStatement(new AssignmentExpression(
                            Member("__cx_current"), "=", yielded.Expression))
                        {
                            SourceSpan = yielded.SourceSpan,
                        });
                    }
                    body.Add(Assign(Member("__cx_hasCurrent"),
                        new LiteralExpression("true")));
                    foreach (var frameLocal in frameLocals)
                    {
                        body.Add(Assign(Member(frameLocal.FieldName), Id(frameLocal.Name)));
                    }
                    body.Add(Assign(Member("__cx_state"),
                        new LiteralExpression($"{yielded.NextState}u")));
                    body.Add(new ReturnStatement(new LiteralExpression("true")));
                    break;
                case IteratorAwait awaited:
                    var awaitedTask = Id(awaited.TaskLocalName);
                    var getResultStatements = new List<StatementBase>();
                    if (awaited.ResultLocalName is not null)
                    {
                        getResultStatements.Add(Assign(Id(awaited.ResultLocalName),
                            new InvocationExpression(
                                new MemberAccessExpression(awaitedTask, "GetResult"), [])));
                    }
                    else
                    {
                        getResultStatements.Add(new ExpressionStatement(
                            new InvocationExpression(
                                new MemberAccessExpression(awaitedTask, "GetResult"), [])));
                    }
                    var completedAwait = new List<StatementBase>
                    {
                        new ExpressionStatement(new InvocationExpression(
                            new MemberAccessExpression(
                                Member("__cx_async_cancellation_registration"), "Dispose"), [])),
                    };
                    if (block.FaultState is { } awaitFaultState &&
                        block.FaultExceptionLocalName is { } awaitFaultExceptionLocal)
                    {
                        var getResultCaughtName =
                            $"__cx_async_foreach_caught_await_{block.State}";
                        completedAwait.Add(new LocalVariableDeclarationStatement(
                            BuiltInSystemTypes.Bool,
                            [new LocalVariableDeclarator(getResultCaughtName,
                                new LiteralExpression("false"))]));
                        completedAwait.Add(new TryStatement(
                            new BlockStatement(getResultStatements),
                            [new CatchClause(
                                new NamedType("System.Exception", []),
                                "__cx_async_foreach_await_exception",
                                null,
                                new BlockStatement([
                                    Assign(Id(awaitFaultExceptionLocal),
                                        Id("__cx_async_foreach_await_exception")),
                                    Assign(Id(getResultCaughtName),
                                        new LiteralExpression("true")),
                                ]))],
                            null));
                        completedAwait.Add(new IfStatement(Id(getResultCaughtName),
                            new BlockStatement(BuildAsyncFaultRedirect(
                                frameLocals, awaitFaultExceptionLocal,
                                awaitFaultState,
                                block.FaultSourceSpan is { } faultAwaitSpan
                                    ? faultAwaitSpan with
                                    {
                                        StartColumn = faultAwaitSpan.StartColumn + 1,
                                    }
                                    : null)), null));
                    }
                    else
                    {
                        completedAwait.AddRange(getResultStatements);
                    }
                    if (awaited.IsFaultCleanup)
                    {
                        completedAwait.Add(Assign(
                            Member("__cx_async_fault_cleanup_pending"),
                            new LiteralExpression("false")));
                    }
                    completedAwait.AddRange(ContinueAt(awaited.NextState));
                    var suspended = new List<StatementBase>
                    {
                        Assign(Member("__cx_state"),
                            new LiteralExpression($"{awaited.ResumeState ?? block.State}u")),
                        Assign(Member("__cx_async_suspended"),
                            new LiteralExpression("true")),
                    };
                    suspended.AddRange(frameLocals.Select(frameLocal =>
                        (StatementBase)Assign(Member(frameLocal.FieldName), Id(frameLocal.Name))));
                    var cancellationCallback = new LambdaExpression([], null, [
                        new ExpressionStatement(new InvocationExpression(
                            Member("ResumeAsyncMove"), [])),
                    ])
                    {
                        SourceSpan = awaited.SourceSpan is { } cancellationSpan
                            ? cancellationSpan with
                            {
                                StartColumn = cancellationSpan.StartColumn + 2,
                            }
                            : null,
                    };
                    ExpressionBase resumeContinuation;
                    if (!awaited.IsFaultCleanup)
                    {
                        var callbackName = $"__cx_async_resume_callback_{block.State}";
                        var callbackType = new FunctionType(
                            QualifiedIdentifier.Empty, BuiltInSystemTypes.Void, []);
                        suspended.Add(new LocalVariableDeclarationStatement(
                            callbackType,
                            [new LocalVariableDeclarator(
                                callbackName, cancellationCallback)]));
                        suspended.Add(Assign(Member("__cx_async_cancellation_registration"),
                            new InvocationExpression(new MemberAccessExpression(
                                Member("__cx_cancellationToken"), "Register"),
                                [Id(callbackName)])));
                        resumeContinuation = Id(callbackName);
                    }
                    else
                    {
                        resumeContinuation = new LambdaExpression([], null, [new ExpressionStatement(
                                new InvocationExpression(Member("ResumeAsyncMove"), []))])
                        {
                            SourceSpan = awaited.SourceSpan is { } continuationSpan
                                ? continuationSpan with
                                {
                                    StartColumn = continuationSpan.StartColumn + 1,
                                }
                                : null,
                        };
                    }
                    suspended.Add(new ExpressionStatement(new InvocationExpression(
                        new MemberAccessExpression(awaitedTask, "OnCompleted"),
                        [resumeContinuation])));
                    suspended.Add(new ReturnStatement(new LiteralExpression("false")));
                    body.Add(new IfStatement(
                        new MemberAccessExpression(awaitedTask, "IsCompleted"),
                        new BlockStatement(completedAwait),
                        new BlockStatement(suspended)));
                    break;
                case IteratorComplete:
                    body.AddRange(ClearEnumeratorState(frameLocals));
                    body.Add(new ReturnStatement(new LiteralExpression("false")));
                    break;
            }
            sections.Add(new SwitchSection(
                [new SwitchLabel(new LiteralExpression($"{block.State}u"))], body));
        }
        sections.Add(new SwitchSection([new SwitchLabel(null)], [
            .. ClearEnumeratorState(frameLocals),
            new ReturnStatement(new LiteralExpression("false")),
        ]));
        statements.Add(Assign(Member("__cx_hasCurrent"),
            new LiteralExpression("false")));
        if (isAsyncIterator)
        {
            statements.Add(Assign(Member("__cx_async_suspended"),
                new LiteralExpression("false")));
        }
        statements.Add(new WhileStatement(new LiteralExpression("true"),
            new BlockStatement([new SwitchStatement(Member("__cx_state"), sections)])));
        statements.Add(new ReturnStatement(new LiteralExpression("false")));
        var exception = new IdentifierExpression(new QualifiedIdentifier(
            "__cx_iterator_exception"));
        var catchBody = BuildCleanupChain(cleanupRegions,
            ClearEnumeratorState(frameLocals).Concat<StatementBase>([
                new ThrowStatement(exception),
                new ReturnStatement(new LiteralExpression("false")),
            ]).ToArray());
        StatementBase moveBody = new TryStatement(
            new BlockStatement(statements),
            [new CatchClause(
                new NamedType("System.Exception", []),
                "__cx_iterator_exception",
                null,
                new BlockStatement(catchBody))],
            null);
        if (!isAsyncIterator)
        {
            moveBody = new BlockStatement([
                new IfStatement(Member("__cx_advancing"),
                    new BlockStatement([new ThrowStatement(
                        new ObjectCreationExpression(
                            new NamedType("System.Exception", []), []))]), null),
                Assign(Member("__cx_advancing"), new LiteralExpression("true")),
                new TryStatement(
                    new BlockStatement([moveBody]),
                    [],
                    new BlockStatement([Assign(Member("__cx_advancing"),
                        new LiteralExpression("false"))])),
            ]);
        }
        return [moveBody];

        static IReadOnlyList<StatementBase> ContinueAt(int state) => [
            Assign(Member("__cx_state"), new LiteralExpression($"{state}u")),
            new ContinueStatement(),
        ];

        static SwitchStatement BuildAsyncDispatch(SwitchStatement selection) =>
            new(selection.Expression, selection.Sections.Select(section =>
            {
                var statements = section.Statements.ToList();
                if (statements.Count > 0 && statements[^1] is ContinueStatement)
                {
                    statements[^1] = new BreakStatement();
                }
                else
                {
                    statements.Add(new BreakStatement());
                }
                return new SwitchSection(section.Labels, statements);
            }).ToArray());
    }

    private static IReadOnlyList<StatementBase> BuildAsyncFaultRedirect(
        IReadOnlyList<FrameLocal> frameLocals,
        string exceptionLocalName,
        int faultState,
        SourceSpan? sourceSpan)
    {
        return [
            Assign(Member("__cx_async_fault_cleanup_pending"),
                new LiteralExpression("true")),
            .. frameLocals.Select(frameLocal => (StatementBase)Assign(
                Member(frameLocal.FieldName), Id(frameLocal.Name))),
            Assign(Member("__cx_state"), new LiteralExpression($"{faultState}u")),
            Assign(Member("__cx_async_suspended"), new LiteralExpression("true")),
            new ExpressionStatement(new InvocationExpression(
                new MemberAccessExpression(new IdentifierExpression(
                    new QualifiedIdentifier("System", "Threading", "Tasks", "TaskScheduler")),
                    "Post"),
                [new LambdaExpression([], null, [new ExpressionStatement(
                    new InvocationExpression(Member("ResumeAsyncMove"), []))])
                {
                    SourceSpan = sourceSpan,
                }])),
            new ReturnStatement(new LiteralExpression("false")),
        ];
    }

    private static IReadOnlyList<StatementBase> ClearEnumeratorState(
        IReadOnlyList<FrameLocal> frameLocals)
    {
        var clear = new List<StatementBase>
        {
            Assign(Member("__cx_state"), new LiteralExpression("4294967295u")),
            Assign(Member("__cx_hasCurrent"), new LiteralExpression("false")),
            Assign(Member("__cx_current"), new LiteralExpression("default")),
        };
        clear.AddRange(frameLocals
            .Where(local =>
                !local.FieldName.StartsWith("__cx_parameter_", StringComparison.Ordinal) &&
                local.Symbol?.IsCaptured != true)
            .Select(local => (StatementBase)Assign(
                Member(local.FieldName), new LiteralExpression("default"))));
        return clear;
    }

    private static IReadOnlyList<StatementBase> BuildCleanupChain(
        IReadOnlyList<IteratorCleanupRegion> cleanupRegions,
        IReadOnlyList<StatementBase> terminalBody)
    {
        IReadOnlyList<StatementBase> body = terminalBody;
        foreach (var region in cleanupRegions)
        {
            var runCleanup = new BlockStatement([
                Assign(Member(region.ActiveFieldName), new LiteralExpression("false")),
                region.FinallyBody,
            ]);
            body = [new TryStatement(
                new BlockStatement([
                    new IfStatement(Member(region.ActiveFieldName), runCleanup, null),
                ]),
                [],
                new BlockStatement(body))];
        }
        return body;
    }

    private static void AddCurrentProperty(
        ClassDeclaration iterator,
        TypeBase elementType)
    {
        var property = new PropertyDeclaration(
            "Current", iterator.FullName, elementType,
            [MemberModifier.Public], iterator);
        var getter = new PropertyAccessorDeclaration(property, "get", false, true);
        getter.SetBody([
            new IfStatement(
                new UnaryExpression("!", Member("__cx_hasCurrent")),
                new BlockStatement([new ThrowStatement(
                    new ObjectCreationExpression(new NamedType("System.Exception", []), []))]),
                null),
            new ReturnStatement(Member("__cx_current")),
        ]);
        property.AddAccessor(getter);
        iterator.MemberDeclarations.AddDeclaration(property);
    }

    private static IEnumerable<StatementBase> EnumerateStatements(
        IEnumerable<StatementBase> statements)
    {
        foreach (var statement in statements)
        {
            yield return statement;
            var children = statement switch
            {
                BlockStatement block => block.Statements,
                IfStatement conditional => conditional.ElseStatement is null
                    ? [conditional.ThenStatement]
                    : [conditional.ThenStatement, conditional.ElseStatement],
                WhileStatement loop => [loop.Body],
                DoWhileStatement loop => [loop.Body],
                ForStatement loop => loop.DeclarationInitializer is null
                    ? [loop.Body]
                    : [loop.DeclarationInitializer, loop.Body],
                ForeachStatement loop => [loop.Body],
                SwitchStatement selection => selection.Sections
                    .SelectMany(section => section.Statements),
                TryStatement tryStatement => new[] { tryStatement.Body }
                    .Concat(tryStatement.CatchClauses.Select(clause => clause.Body))
                    .Concat(tryStatement.FinallyBody is null
                        ? [] : [tryStatement.FinallyBody]),
                _ => [],
            };
            foreach (var child in EnumerateStatements(children))
            {
                yield return child;
            }
        }
    }

    private static IEnumerable<FunctionDeclaration> EnumerateFunctions(
        IEnumerable<DeclarationBase> declarations)
    {
        foreach (var declaration in declarations)
        {
            switch (declaration)
            {
                case FunctionDeclaration function:
                    yield return function;
                    break;
                case ClassDeclaration classDeclaration:
                    foreach (var function in EnumerateFunctions(
                        classDeclaration.MemberDeclarations.Declarations))
                    {
                        yield return function;
                    }
                    break;
                case ExtensionDeclaration extension:
                    foreach (var function in EnumerateFunctions(
                        extension.Members.MemberDeclarations.Declarations))
                    {
                        yield return function;
                    }
                    break;
            }
        }
    }

    private static NamedType Constructed(string name, TypeBase argument) =>
        NamedType.CreateWithTypeArguments(name, [argument]);

    private static bool IsEnumerableType(NamedType type)
    {
        var parts = type.ResolvedTypeFullName.Parts;
        return type.TypeArguments.Count == 1 && parts.Length >= 3 &&
            parts[^3].Equals("System", StringComparison.Ordinal) &&
            parts[^2].Equals("Collections", StringComparison.Ordinal) &&
            parts[^1].Equals("IEnumerable", StringComparison.Ordinal);
    }

    private static NamedType Constructed(
        string name,
        IReadOnlyList<TypeBase> arguments) =>
        NamedType.CreateWithTypeArguments(name, arguments);

    private static string[] GetFrameGenericTypeNames(FunctionDeclaration function) =>
        (function.ParentClassDeclaration?.GenericTypeNames ?? [])
        .Concat(function.GenericTypeNames)
        .Distinct(StringComparer.Ordinal)
        .ToArray();

    private static GenericTypeConstraint[] GetFrameGenericConstraints(
        FunctionDeclaration function) =>
        (function.ParentClassDeclaration?.GenericConstraints ?? [])
        .Concat(function.GenericConstraints)
        .ToArray();

    private static GenericConceptConstraint[] GetFrameConceptConstraints(
        FunctionDeclaration function) =>
        (function.ParentClassDeclaration?.ConceptConstraints ?? [])
        .Concat(function.ConceptConstraints)
        .ToArray();

    private static IdentifierExpression Id(string name) =>
        new(new QualifiedIdentifier(name));

    private static MemberAccessExpression Member(string name) =>
        new(new ThisExpression(), name);

    private static ExpressionBase CreateCurrentExpression(
        ExpressionBase source,
        string enumeratorName) => source is InvocationExpression
        ? new InvocationExpression(
            new MemberAccessExpression(Id(enumeratorName), "Current"), [])
        : new MemberAccessExpression(Id(enumeratorName), "Current");

    private static ExpressionStatement Assign(ExpressionBase target, ExpressionBase value) =>
        new(new AssignmentExpression(target, "=", value));

    private static IEnumerable<StatementBase> EnumerateYields(
        IEnumerable<StatementBase> statements)
    {
        foreach (var statement in statements)
        {
            if (statement is YieldReturnStatement or YieldBreakStatement)
            {
                yield return statement;
            }
            IEnumerable<StatementBase> nested = statement switch
            {
                BlockStatement block => block.Statements,
                IfStatement branch => branch.ElseStatement is null
                    ? [branch.ThenStatement]
                    : [branch.ThenStatement, branch.ElseStatement],
                WhileStatement loop => [loop.Body],
                DoWhileStatement loop => [loop.Body],
                ForStatement loop => [loop.Body],
                ForeachStatement loop => [loop.Body],
                TryStatement tryStatement => new[] { tryStatement.Body }
                    .Concat(tryStatement.CatchClauses.Select(clause => clause.Body))
                    .Concat(tryStatement.FinallyBody is null
                        ? [] : [tryStatement.FinallyBody]),
                SwitchStatement selection => selection.Sections
                    .SelectMany(section => section.Statements),
                _ => [],
            };
            foreach (var child in EnumerateYields(nested))
            {
                yield return child;
            }
        }
    }

    private static string CreateIteratorName(FunctionDeclaration function)
    {
        var span = function.SourceSpan;
        var identity = $"{function.FullName}:{span?.FilePath}:{span?.StartLine}:{span?.StartColumn}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..12];
        return $"__CxIterator_{function.Name}_{hash}";
    }

    private sealed record FrameLocal(
        string Name,
        string FieldName,
        TypeBase Type,
        LocalVariableSymbol? Symbol = null);
    private sealed record IteratorCleanupRegion(int Id, StatementBase FinallyBody)
    {
        public string ActiveFieldName => $"__cx_finally_active_{Id}";
    }

    private sealed record IteratorBlock(int State, IReadOnlyList<StatementBase> Operations,
        IteratorTerminator Terminator, int? FaultState = null,
        string? FaultExceptionLocalName = null,
        SourceSpan? FaultSourceSpan = null);

    private abstract record IteratorTerminator;
    private sealed record IteratorNext(int State) : IteratorTerminator;
    private sealed record IteratorBranch(ExpressionBase Condition, int WhenTrue,
        int WhenFalse) : IteratorTerminator;
    private sealed record IteratorDispatch(SwitchStatement Selection) : IteratorTerminator;
    private sealed record IteratorAwait(string TaskLocalName, string? ResultLocalName,
        int NextState, CxCompiler.Model.Common.SourceSpan? SourceSpan,
        bool IsFaultCleanup = false, int? ResumeState = null) : IteratorTerminator;
    private sealed record IteratorYield(ExpressionBase Expression, int NextState,
        SourceSpan? SourceSpan = null)
        : IteratorTerminator;
    private sealed record IteratorComplete : IteratorTerminator;

    private sealed class IteratorControlFlowBuilder(
        List<LocalVariableDeclarator> locals,
        Dictionary<string, TypeBase> localTypes,
        HashSet<string> names,
        bool isAsyncIterator,
        string functionIdentity)
    {
        private readonly List<IteratorBlock?> _blocks = [];
        private readonly List<IteratorCleanupRegion> _cleanupRegions = [];
        private readonly bool _isAsyncIterator = isAsyncIterator;
        private readonly string _functionIdentity = functionIdentity;
        public IReadOnlyList<IteratorBlock> Blocks => _blocks
            .Select(block => block ?? throw new InternalCompilerException(
                "An iterator continuation block was not completed."))
            .ToArray();
        public IReadOnlyList<IteratorCleanupRegion> CleanupRegions => _cleanupRegions;
        public bool HasYield { get; private set; }

        public bool TryBuild(IReadOnlyList<StatementBase> statements, out int entryState)
        {
            var complete = Add([], new IteratorComplete());
            if (!TryBuildSequence(statements, complete, null, null, complete, out entryState))
            {
                return false;
            }
            return HasYield;
        }

        private bool TryBuildSequence(
            IReadOnlyList<StatementBase> statements,
            int continuation,
            int? breakState,
            int? continueState,
            int exitState,
            out int state)
        {
            state = continuation;
            for (var index = statements.Count - 1; index >= 0; index--)
            {
                if (!TryBuildStatement(statements[index], state, breakState,
                        continueState, exitState, out state))
                {
                    return false;
                }
            }
            return true;
        }

        private bool TryBuildStatement(
            StatementBase statement,
            int continuation,
            int? breakState,
            int? continueState,
            int exitState,
            out int state)
        {
            switch (statement)
            {
                case BlockStatement block:
                    return TryBuildSequence(block.Statements, continuation,
                        breakState, continueState, exitState, out state);
                case IfStatement conditional:
                    if (!TryBuildStatement(conditional.ThenStatement, continuation,
                            breakState, continueState, exitState, out var whenTrue))
                    {
                        state = 0;
                        return false;
                    }
                    var whenFalse = continuation;
                    if (conditional.ElseStatement is not null &&
                        !TryBuildStatement(conditional.ElseStatement, continuation,
                            breakState, continueState, exitState, out whenFalse))
                    {
                        state = 0;
                        return false;
                    }
                    state = Add([], new IteratorBranch(
                        conditional.Condition, whenTrue, whenFalse));
                    return true;
                case WhileStatement loop:
                    var conditionState = Reserve();
                    if (!TryBuildStatement(loop.Body, conditionState,
                            continuation, conditionState, exitState, out var bodyState))
                    {
                        state = 0;
                        return false;
                    }
                    Set(conditionState, [], new IteratorBranch(
                        loop.Condition, bodyState, continuation));
                    state = conditionState;
                    return true;
                case DoWhileStatement loop:
                    var doCondition = Reserve();
                    if (!TryBuildStatement(loop.Body, doCondition,
                            continuation, doCondition, exitState, out var doBody))
                    {
                        state = 0;
                        return false;
                    }
                    Set(doCondition, [], new IteratorBranch(
                        loop.Condition, doBody, continuation));
                    state = doBody;
                    return true;
                case ForStatement loop:
                    var forCondition = Reserve();
                    var iteratorOperations = loop.Iterators
                        .Select(expression => (StatementBase)new ExpressionStatement(expression))
                        .ToArray();
                    if (!TryBuildSequence(iteratorOperations, forCondition,
                            continuation, forCondition, exitState, out var iteratorState) ||
                        !TryBuildStatement(loop.Body, iteratorState,
                            continuation, iteratorState, exitState, out var forBody))
                    {
                        state = 0;
                        return false;
                    }
                    Set(forCondition, [], new IteratorBranch(
                        loop.Condition ?? new LiteralExpression("true"),
                        forBody, continuation));
                    var forInitializers = new List<StatementBase>();
                    if (loop.DeclarationInitializer is not null)
                    {
                        forInitializers.Add(loop.DeclarationInitializer);
                    }
                    forInitializers.AddRange(loop.InitializerExpressions
                        .Select(expression => (StatementBase)new ExpressionStatement(expression)));
                    return TryBuildSequence(forInitializers, forCondition,
                        breakState, continueState, exitState, out state);
                case SwitchStatement selection:
                    var sections = new List<SwitchSection>();
                    foreach (var section in selection.Sections)
                    {
                        if (!TryBuildSequence(section.Statements, continuation,
                                continuation, continueState, exitState, out var sectionState))
                        {
                            state = 0;
                            return false;
                        }
                        sections.Add(new SwitchSection(section.Labels, [
                            Assign(Member("__cx_state"),
                                new LiteralExpression($"{sectionState}u")),
                            new ContinueStatement(),
                        ]));
                    }
                    if (!selection.Sections.SelectMany(section => section.Labels)
                        .Any(label => label.IsDefault))
                    {
                        sections.Add(new SwitchSection([new SwitchLabel(null)], [
                            Assign(Member("__cx_state"),
                                new LiteralExpression($"{continuation}u")),
                            new ContinueStatement(),
                        ]));
                    }
                    if (selection.Expression.InferredType is not { } selectorType)
                    {
                        state = 0;
                        return false;
                    }
                    var selectorName = $"__cx_iterator_switch_selector_{locals.Count}";
                    if (!AddFrameLocal(selectorName, selectorType))
                    {
                        state = 0;
                        return false;
                    }
                    var dispatch = new SwitchStatement(Id(selectorName), sections);
                    if (selection.SelectorTemporaryName is not null &&
                        selection.CaseIndexTemporaryName is not null)
                    {
                        dispatch.BindTemporaries(selection.SelectorTemporaryName,
                            selection.CaseIndexTemporaryName);
                    }
                    state = Add([
                        Assign(Id(selectorName), selection.Expression),
                    ], new IteratorDispatch(dispatch));
                    return true;
                case ForeachStatement loop
                    when loop.IsAwait && _isAsyncIterator:
                    if (loop.VariableType is null || loop.VariableSymbol is null ||
                        loop.EnumeratorSymbol is null ||
                        loop.GetEnumeratorInvocation is null ||
                        loop.MoveNextInvocation is null ||
                        loop.DisposeAsyncInvocation is null ||
                        loop.CurrentExpression is null)
                    {
                        state = 0;
                        return false;
                    }
                    if (!AddFrameLocal(loop.VariableName, loop.VariableType) ||
                        !AddFrameLocal(loop.EnumeratorSymbol.Name,
                            loop.EnumeratorSymbol.Type))
                    {
                        state = 0;
                        return false;
                    }

                    var asyncEnumeratorField = $"__cx_local_{locals.FindIndex(local =>
                        local.Name == loop.EnumeratorSymbol.Name)}";
                    var asyncEnumerator = Member(asyncEnumeratorField);
                    var moveTaskType = loop.MoveNextInvocation.InferredType;
                    var disposeTaskType = loop.DisposeAsyncInvocation.InferredType;
                    if (moveTaskType is null || disposeTaskType is null)
                    {
                        state = 0;
                        return false;
                    }
                    var moveTaskName = $"__cx_await_foreach_move_{locals.Count}";
                    var moveResultName = $"__cx_await_foreach_result_{locals.Count}";
                    var disposeTaskName = $"__cx_await_foreach_dispose_{locals.Count}";
                    if (!AddFrameLocal(moveTaskName, moveTaskType) ||
                        !AddFrameLocal(moveResultName, BuiltInSystemTypes.Bool) ||
                        !AddFrameLocal(disposeTaskName, disposeTaskType))
                    {
                        state = 0;
                        return false;
                    }

                    var moveTask = Id(moveTaskName);
                    var disposeTask = Id(disposeTaskName);
                    var awaitSpanOffset = locals.Count * 8;
                    SourceSpan? AsyncAwaitSpan(int offset) => statement.SourceSpan is { } sourceSpan
                        ? sourceSpan with
                        {
                            FilePath = $"{sourceSpan.FilePath}#asyncforeach:{_functionIdentity}:{awaitSpanOffset + offset}",
                            StartLine = 1,
                            StartColumn = 1,
                        }
                        : null;
                    var loopCheck = Reserve();
                    var disposal = Add([
                        Assign(disposeTask, new InvocationExpression(
                            new MemberAccessExpression(asyncEnumerator,
                                loop.DisposeAsyncInvocation.TargetSymbol!.FullName.Parts[^1]), [])),
                    ], new IteratorAwait(disposeTaskName, null, continuation,
                        AsyncAwaitSpan(3)));
                    var exitDisposal = Add([
                        Assign(disposeTask, new InvocationExpression(
                            new MemberAccessExpression(asyncEnumerator,
                                loop.DisposeAsyncInvocation.TargetSymbol!.FullName.Parts[^1]), [])),
                    ], new IteratorAwait(disposeTaskName, null, exitState,
                        AsyncAwaitSpan(4)));
                    var asyncBodyStatesStart = _blocks.Count;
                    if (!TryBuildStatement(loop.Body, loopCheck, disposal,
                            loopCheck, exitDisposal, out var asyncBodyState))
                    {
                        state = 0;
                        return false;
                    }
                    var asyncBodyStatesEnd = _blocks.Count;
                    var asyncBody = Add([
                        Assign(Id(loop.VariableName), CreateCurrentExpression(
                            loop.CurrentExpression, asyncEnumeratorField)),
                    ], new IteratorNext(asyncBodyState));
                    var afterMove = Add([], new IteratorBranch(
                        Id(moveResultName), asyncBody, disposal));
                    var startMove = Add([
                        Assign(moveTask, new InvocationExpression(
                            new MemberAccessExpression(asyncEnumerator,
                                loop.MoveNextInvocation.TargetSymbol!.FullName.Parts[^1]), [])),
                    ], new IteratorAwait(moveTaskName, moveResultName, afterMove,
                        AsyncAwaitSpan(2)));
                    Set(loopCheck, [], new IteratorNext(startMove));
                    state = Add([
                        Assign(asyncEnumerator, new InvocationExpression(
                            loop.GetEnumeratorInvocation.Target,
                            [Member("__cx_cancellationToken")])),
                        Assign(Id(loop.EnumeratorSymbol.Name), asyncEnumerator),
                    ], new IteratorNext(loopCheck));

                    var faultExceptionName = $"__cx_await_foreach_exception_{locals.Count}";
                    if (!AddFrameLocal(faultExceptionName,
                            new NamedType("System.Exception", [])))
                    {
                        state = 0;
                        return false;
                    }
                    var rethrowFault = Add([
                        new ThrowStatement(Id(faultExceptionName)),
                    ], new IteratorComplete());
                    var disposeAfterFault = Add([
                        Assign(disposeTask, new InvocationExpression(
                            new MemberAccessExpression(asyncEnumerator,
                                loop.DisposeAsyncInvocation.TargetSymbol!.FullName.Parts[^1]), [])),
                    ], new IteratorAwait(disposeTaskName, null, rethrowFault,
                        AsyncAwaitSpan(5), IsFaultCleanup: true));
                    SetFaultHandler(loopCheck, disposeAfterFault, faultExceptionName,
                        AsyncAwaitSpan(6));
                    SetFaultHandler(startMove, disposeAfterFault, faultExceptionName,
                        AsyncAwaitSpan(7));
                    SetFaultHandler(afterMove, disposeAfterFault, faultExceptionName,
                        AsyncAwaitSpan(8));
                    SetFaultHandler(asyncBody, disposeAfterFault, faultExceptionName,
                        AsyncAwaitSpan(9));
                    SetFaultHandler(asyncBodyStatesStart, asyncBodyStatesEnd,
                        disposeAfterFault, faultExceptionName, AsyncAwaitSpan(10));
                    return true;

                case ForeachStatement loop
                    when EnumerateYields([loop.Body]).Any():
                    if (loop.VariableType is null || loop.VariableSymbol is null)
                    {
                        state = 0;
                        return false;
                    }
                    if (!AddFrameLocal(loop.VariableName, loop.VariableType))
                    {
                        state = 0;
                        return false;
                    }

                    if (loop.IsStringIteration)
                    {
                        var collectionName = $"__cx_foreach_collection_{locals.Count}";
                        var indexName = $"__cx_foreach_index_{locals.Count}";
                        if (!AddFrameLocal(collectionName, BuiltInSystemTypes.String) ||
                            !AddFrameLocal(indexName, BuiltInSystemTypes.UInt))
                        {
                            state = 0;
                            return false;
                        }
                        var collection = Id(collectionName);
                        var index = Id(indexName);
                        var stringCurrent = new ArrayAccessExpression(
                            new MemberAccessExpression(collection, "Item"), [index]);
                        var stringCondition = new BinaryExpression(
                            index, "<", new MemberAccessExpression(collection, "Length"));
                        var stringCheck = Reserve();
                        var stringAdvance = Add([
                            new ExpressionStatement(new AssignmentExpression(
                                index, "+=", new LiteralExpression("1u"))),
                        ], new IteratorNext(stringCheck));
                        if (!TryBuildStatement(loop.Body, stringAdvance,
                                continuation, stringAdvance, exitState, out var stringBodyState))
                        {
                            state = 0;
                            return false;
                        }
                        var stringBody = Add([
                            Assign(Id(loop.VariableName), stringCurrent),
                        ], new IteratorNext(stringBodyState));
                        Set(stringCheck, [], new IteratorBranch(
                            stringCondition, stringBody, continuation));
                        state = Add([
                            Assign(collection, loop.Collection),
                            Assign(index, new LiteralExpression("0u")),
                        ], new IteratorNext(stringCheck));
                        return true;
                    }

                    if (loop.EnumeratorSymbol is not null &&
                        loop.GetEnumeratorInvocation is not null &&
                        loop.MoveNextInvocation is not null &&
                        loop.CurrentExpression is not null)
                    {
                        var enumerator = loop.EnumeratorSymbol;
                        if (!AddFrameLocal(enumerator.Name, enumerator.Type))
                        {
                            state = 0;
                            return false;
                        }
                        var enumeratorLocalIndex = locals.FindIndex(local =>
                            local.Name == enumerator.Name);
                        var enumeratorField = $"__cx_local_{enumeratorLocalIndex}";
                        var enumeratorReceiver = Member(enumeratorField);
                        var enumeratorConditionState = Reserve();
                        var enumeratorCleanupState = continuation;
                        var exitCleanupState = exitState;
                        IteratorCleanupRegion? disposeRegion = null;
                        if (loop.DisposeInvocation is { } dispose)
                        {
                            disposeRegion = new IteratorCleanupRegion(
                                _cleanupRegions.Count,
                                new ExpressionStatement(new InvocationExpression(
                                    new MemberAccessExpression(enumeratorReceiver,
                                        dispose.TargetSymbol!.FullName.Parts[^1]), [])));
                            _cleanupRegions.Add(disposeRegion);
                            enumeratorCleanupState = Add([
                                Assign(Member(disposeRegion.ActiveFieldName),
                                    new LiteralExpression("false")),
                                disposeRegion.FinallyBody,
                            ], new IteratorNext(continuation));
                            exitCleanupState = Add([
                                Assign(Member(disposeRegion.ActiveFieldName),
                                    new LiteralExpression("false")),
                                disposeRegion.FinallyBody,
                            ], new IteratorNext(exitState));
                        }

                        if (!TryBuildStatement(loop.Body, enumeratorConditionState,
                                enumeratorCleanupState, enumeratorConditionState, exitCleanupState,
                                out var enumeratorBodyState))
                        {
                            state = 0;
                            return false;
                        }
                        var enumeratorBody = Add([
                            Assign(Id(loop.VariableName), CreateCurrentExpression(
                                loop.CurrentExpression, enumeratorField)),
                        ], new IteratorNext(enumeratorBodyState));
                        Set(enumeratorConditionState, [], new IteratorBranch(
                            new InvocationExpression(
                                new MemberAccessExpression(enumeratorReceiver,
                                    loop.MoveNextInvocation.TargetSymbol!.FullName.Parts[^1]), []),
                            enumeratorBody, enumeratorCleanupState));
                        var initialize = new List<StatementBase>
                        {
                            Assign(enumeratorReceiver, new InvocationExpression(
                                new MemberAccessExpression(loop.Collection,
                                    loop.GetEnumeratorInvocation.TargetSymbol!.FullName.Parts[^1]), [])),
                            Assign(Id(enumerator.Name), enumeratorReceiver),
                        };
                        if (disposeRegion is not null)
                        {
                            initialize.Add(Assign(
                                Member(disposeRegion.ActiveFieldName),
                                new LiteralExpression("true")));
                        }
                        state = Add(initialize, new IteratorNext(enumeratorConditionState));
                        return true;
                    }

                    if (loop.Collection.InferredType is ArrayType arrayType)
                    {
                        var collectionName = $"__cx_foreach_collection_{locals.Count}";
                        var indexName = $"__cx_foreach_index_{locals.Count}";
                        if (!AddFrameLocal(collectionName, arrayType) ||
                            !AddFrameLocal(indexName, BuiltInSystemTypes.UInt))
                        {
                            state = 0;
                            return false;
                        }
                        var collection = Id(collectionName);
                        var index = Id(indexName);
                        var arrayCurrent = new ArrayAccessExpression(collection, [index]);
                        var arrayCondition = new BinaryExpression(
                            index, "<", new MemberAccessExpression(collection, "Length"));
                        var arrayCheck = Reserve();
                        var arrayAdvance = Add([
                            new ExpressionStatement(new AssignmentExpression(
                                index, "+=", new LiteralExpression("1u"))),
                        ], new IteratorNext(arrayCheck));
                        if (!TryBuildStatement(loop.Body, arrayAdvance,
                                continuation, arrayAdvance, exitState, out var arrayBodyState))
                        {
                            state = 0;
                            return false;
                        }
                        var arrayBody = Add([
                            Assign(Id(loop.VariableName), arrayCurrent),
                        ], new IteratorNext(arrayBodyState));
                        Set(arrayCheck, [], new IteratorBranch(
                            arrayCondition, arrayBody, continuation));
                        state = Add([
                            Assign(collection, loop.Collection),
                            Assign(index, new LiteralExpression("0u")),
                        ], new IteratorNext(arrayCheck));
                        return true;
                    }

                    state = 0;
                    return false;
                case TryStatement protectedRegion
                    when EnumerateYields([protectedRegion]).Any():
                    if (protectedRegion.FinallyBody is null ||
                        protectedRegion.CatchClauses.Count != 0 ||
                        EnumerateYields([protectedRegion.FinallyBody]).Any())
                    {
                        state = 0;
                        return false;
                    }
                    var cleanupRegion = new IteratorCleanupRegion(
                        _cleanupRegions.Count, protectedRegion.FinallyBody);
                    _cleanupRegions.Add(cleanupRegion);
                    var cleanupState = Add([
                        Assign(Member(cleanupRegion.ActiveFieldName),
                            new LiteralExpression("false")),
                        protectedRegion.FinallyBody,
                    ], new IteratorNext(continuation));
                    int? protectedBreakState = breakState is { } breakTarget
                        ? Add([
                            Assign(Member(cleanupRegion.ActiveFieldName),
                                new LiteralExpression("false")),
                            protectedRegion.FinallyBody,
                        ], new IteratorNext(breakTarget))
                        : null;
                    int? protectedContinueState = continueState is { } continueTarget
                        ? Add([
                            Assign(Member(cleanupRegion.ActiveFieldName),
                                new LiteralExpression("false")),
                            protectedRegion.FinallyBody,
                        ], new IteratorNext(continueTarget))
                        : null;
                    if (!TryBuildStatement(protectedRegion.Body, cleanupState,
                            protectedBreakState, protectedContinueState, cleanupState,
                            out var tryBodyState))
                    {
                        state = 0;
                        return false;
                    }
                    state = Add([
                        Assign(Member(cleanupRegion.ActiveFieldName),
                            new LiteralExpression("true")),
                    ], new IteratorNext(tryBodyState));
                    return true;
                case BreakStatement when breakState is { } target:
                    state = Add([], new IteratorNext(target));
                    return true;
                case ContinueStatement when continueState is { } target:
                    state = Add([], new IteratorNext(target));
                    return true;
                case YieldReturnStatement yielded:
                    HasYield = true;
                    if (yielded.Expression is AwaitExpression awaitedYield)
                    {
                        if (!_isAsyncIterator ||
                            AddAwaitTaskLocal(awaitedYield) is not { } awaitedYieldTask ||
                            awaitedYield.InferredType is not { } awaitedYieldType)
                        {
                            state = 0;
                            return false;
                        }
                        var awaitedYieldValue = $"__cx_await_yield_{locals.Count}";
                        if (!AddFrameLocal(awaitedYieldValue, awaitedYieldType))
                        {
                            state = 0;
                            return false;
                        }
                        var yieldState = Add([], new IteratorYield(
                            Id(awaitedYieldValue), continuation,
                            awaitedYield.SourceSpan));
                        state = Add([
                            Assign(Id(awaitedYieldTask), awaitedYield.Operand),
                        ], new IteratorAwait(awaitedYieldTask, awaitedYieldValue,
                            yieldState, awaitedYield.SourceSpan));
                        return true;
                    }
                    state = Add([], new IteratorYield(yielded.Expression, continuation,
                        yielded.SourceSpan));
                    return true;
                case YieldBreakStatement:
                    HasYield = true;
                    state = Add([], new IteratorNext(exitState));
                    return true;
                case ReturnStatement { Expression: null }:
                    state = Add([], new IteratorNext(exitState));
                    return true;
                case LocalVariableDeclarationStatement declaration:
                    var isSingleAwaitInitializer = declaration.Declarators is
                        [{ Initializer: AwaitExpression }];
                    if (declaration.DeclaredType is AutoType && !isSingleAwaitInitializer ||
                        declaration.Declarators.Any(declarator =>
                            declarator.Initializer is AwaitExpression) &&
                        (!_isAsyncIterator || !isSingleAwaitInitializer))
                    {
                        state = 0;
                        return false;
                    }
                    if (declaration.Declarators is [{ Initializer: AwaitExpression localAwait } local])
                    {
                        var awaitedLocalType = local.Type ??
                            (declaration.DeclaredType is AutoType
                                ? null
                                : declaration.DeclaredType);
                        if (!_isAsyncIterator || awaitedLocalType is null ||
                            !names.Add(local.Name))
                        {
                            state = 0;
                            return false;
                        }
                        locals.Add(local.Symbol?.IsCaptured == true
                            ? local
                            : new LocalVariableDeclarator(local.Name, null));
                        localTypes.Add(local.Name, awaitedLocalType);
                        var taskLocalName = AddAwaitTaskLocal(localAwait);
                        if (taskLocalName is null)
                        {
                            state = 0;
                            return false;
                        }
                        var taskAssignment = new ExpressionStatement(new AssignmentExpression(
                            Id(taskLocalName), "=", localAwait.Operand))
                        {
                            SourceSpan = localAwait.SourceSpan,
                        };
                        state = Add([taskAssignment], new IteratorAwait(
                            taskLocalName, local.Name, continuation, localAwait.SourceSpan));
                        return true;
                    }
                    var operations = new List<StatementBase>();
                    foreach (var declarator in declaration.Declarators)
                    {
                        if (!names.Add(declarator.Name))
                        {
                            state = 0;
                            return false;
                        }
                        locals.Add(declarator.Symbol?.IsCaptured == true
                            ? declarator
                            : new LocalVariableDeclarator(
                                declarator.Name, declarator.Initializer));
                        localTypes.Add(declarator.Name, declaration.DeclaredType);
                        operations.Add(new ExpressionStatement(new AssignmentExpression(
                            new IdentifierExpression(new QualifiedIdentifier(declarator.Name)),
                            "=", declarator.Initializer ?? new LiteralExpression("default")))
                        {
                            SourceSpan = statement.SourceSpan,
                        });
                    }
                    state = Add(operations, new IteratorNext(continuation));
                    return true;
                case ExpressionStatement { Expression: AwaitExpression awaitExpression }:
                    if (!_isAsyncIterator ||
                        AddAwaitTaskLocal(awaitExpression) is not { } awaitedTaskLocal)
                    {
                        state = 0;
                        return false;
                    }
                    state = Add([
                        new ExpressionStatement(new AssignmentExpression(
                            Id(awaitedTaskLocal), "=", awaitExpression.Operand)),
                    ], new IteratorAwait(awaitedTaskLocal, null, continuation,
                        awaitExpression.SourceSpan));
                    return true;
                case ExpressionStatement or EmptyStatement or ThrowStatement:
                    if (CoroutineSyntaxAnalysis.EnumerateBodyExpressions([statement])
                        .Any(expression => expression is AwaitExpression))
                    {
                        state = 0;
                        return false;
                    }
                    state = Add([statement], new IteratorNext(continuation));
                    return true;
                case ForeachStatement or SwitchStatement or TryStatement
                    when !EnumerateYields([statement]).Any() &&
                         !EnumerateStatements([statement]).OfType<ReturnStatement>().Any():
                    state = Add([statement], new IteratorNext(continuation));
                    return true;
                default:
                    state = 0;
                    return false;
            }
        }

        private int Add(IReadOnlyList<StatementBase> operations,
            IteratorTerminator terminator)
        {
            var state = _blocks.Count;
            _blocks.Add(new IteratorBlock(state, operations, terminator));
            return state;
        }

        private int Reserve()
        {
            var state = _blocks.Count;
            _blocks.Add(null);
            return state;
        }

        private void Set(int state, IReadOnlyList<StatementBase> operations,
            IteratorTerminator terminator) =>
            _blocks[state] = new IteratorBlock(state, operations, terminator);

        private void SetFaultHandler(int state, int faultState,
            string exceptionLocalName, SourceSpan? sourceSpan)
        {
            if (_blocks[state] is { FaultState: null } block)
            {
                _blocks[state] = block with
                {
                    FaultState = faultState,
                    FaultExceptionLocalName = exceptionLocalName,
                    FaultSourceSpan = sourceSpan is { } span
                        ? span with
                        {
                            FilePath = $"{span.FilePath}#fault:{_functionIdentity}:{state}",
                            StartLine = 1,
                            StartColumn = 1,
                        }
                        : null,
                };
            }
        }

        private void SetFaultHandler(int startState, int endState, int faultState,
            string exceptionLocalName, SourceSpan? sourceSpan)
        {
            for (var state = startState; state < endState; state++)
            {
                SetFaultHandler(state, faultState, exceptionLocalName, sourceSpan);
            }
        }

        private bool AddFrameLocal(string name, TypeBase type)
        {
            if (!names.Add(name))
            {
                return false;
            }
            locals.Add(new LocalVariableDeclarator(name, null));
            localTypes.Add(name, type);
            return true;
        }

        private string? AddAwaitTaskLocal(AwaitExpression awaitExpression)
        {
            if (awaitExpression.Operand.InferredType is not { } taskType)
            {
                return null;
            }
            var name = $"__cx_await_task_{locals.Count}";
            while (!names.Add(name))
            {
                name += "_";
            }
            locals.Add(new LocalVariableDeclarator(name, null));
            localTypes.Add(name, taskType);
            return name;
        }
    }
}

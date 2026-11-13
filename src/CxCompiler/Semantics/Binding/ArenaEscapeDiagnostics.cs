using CxCompiler.Model.Common;
using CxCompiler.Model.Errors;
using CxCompiler.Model.Expressions;
using CxCompiler.Model.Statements;
using CxCompiler.Model.Types;
using CxCompiler.Model.Types.BuiltInTypes;

namespace CxCompiler.Semantics;

internal static class ArenaEscapeDiagnostics
{
    private const int MaxLoopAnalysisIterations = 8;
    private const int MaxInlinedCallDepth = 48;
    private const string EscapeMessage =
        "A possibly arena-scoped reference cannot be stored in a static, captured, or durable destination; copy its data into new objects inside Memory.WithDurable first.";
    private const string NativeEscapeMessage =
        "A possibly arena-scoped reference cannot cross a native boundary unless the import has a trusted noEscape contract.";
    private const string ReflectionEscapeMessage =
        "A possibly arena-scoped reference cannot be passed to reflective invocation without a non-retaining contract.";
    private const string OpaqueCallEscapeMessage =
        "A possibly arena-scoped reference cannot cross an opaque call boundary without a trusted non-retaining contract.";
    private const string AsyncArenaCallMessage =
        "An async operation cannot start inside Memory.WithArena because its continuation may outlive the synchronous scope; use Memory.WithArenaAsync or start durable async work inside Memory.WithDurable.";
    private const string DetachedAsyncArenaCallMessage =
        "The compiler cannot prove this async operation is joined to Memory.WithArenaAsync; detached work must not retain arena-scoped data. Return the operation directly from the scope or copy data into durable storage first.";
    private const string CallDepthEscapeMessage =
        "The compiler cannot prove arena lifetime safety because the analyzed call or constructor chain exceeds the supported depth.";

    [Flags]
    private enum Region
    {
        None = 0,
        Arena = 1,
        Durable = 2,
    }

    private readonly record struct StoragePath(
        LocalVariableSymbol Root,
        string Suffix,
        bool Captured)
    {
        public StoragePath Append(string segment) =>
            this with { Suffix = Suffix + "/" + segment };
    }

    private readonly record struct FieldSlot(StoragePath Owner, FieldSymbol Field);
    private readonly record struct PropertySlot(StoragePath Owner, PropertySymbol Property);
    private readonly record struct ArraySlot(StoragePath Owner, string? Index);

    public static void Analyze(
        FunctionSymbol target,
        IReadOnlyList<ExpressionBase> arguments,
        IReadOnlyList<ClassDeclaration> knownTypes,
        Action<string>? progressReporter = null)
    {
        if (target.ModuleName != "cxcore" ||
            target.FullName.Parts.Length < 2 ||
            target.FullName.Parts[^2] != "Memory" ||
            target.FullName.Parts[^1] is not ("WithArena" or "WithArenaAsync"))
        {
            return;
        }

        // The analysis is intentionally conservative and local to the callback.
        // Runtime barriers remain mandatory for indirect values and paths this
        // pass cannot prove.
        foreach (var lambda in arguments.OfType<LambdaExpression>())
        {
            var state = new AnalysisState();
            state.Progress = new AnalysisProgress(progressReporter);
            state.KnownTypes = knownTypes;
            state.ArenaDepth = 1;
            if (target.FullName.Parts[^1] == "WithArenaAsync")
                state.AsyncArenaScopeDepth = 1;
            foreach (var capture in lambda.Captures)
            {
                if (GetReferenceBearingValueStructType(
                    capture.Local.Type, state) is not null)
                {
                    state.Set(capture.Local, Region.None);
                    state.SetPath(
                        capture.Local,
                        new StoragePath(capture.Local, "", Captured: true));
                    continue;
                }

                state.Set(
                    capture.Local,
                    IsReferenceLikeOrReferenceBearing(capture.Local.Type, state)
                        ? Region.Arena | Region.Durable
                        : Region.None);
                state.SetPath(capture.Local, null);
            }
            var activeFunctions = new HashSet<FunctionDeclaration>();
            if (lambda.ExpressionBody is not null)
            {
                if (target.FullName.Parts[^1] == "WithArenaAsync")
                    MarkJoinedTaskCalls(lambda.ExpressionBody, state);
                VisitExpression(lambda.ExpressionBody, state, durableContext: false, activeFunctions);
            }
            if (lambda.StatementBody is not null)
                VisitStatements(lambda.StatementBody, state, durableContext: false, activeFunctions);
        }
    }

    private static void VisitStatements(
        IEnumerable<StatementBase> statements,
        AnalysisState state,
        bool durableContext,
        HashSet<FunctionDeclaration> activeFunctions)
    {
        foreach (var statement in statements)
            VisitStatement(statement, state, durableContext, activeFunctions);
    }

    private static AnalysisState AnalyzeLoop(
        AnalysisState entryState,
        Action<AnalysisState> analyzeIteration)
    {
        var headState = entryState.Clone();
        var iterations = 0;
        while (true)
        {
            iterations++;
            entryState.Progress?.LoopIteration();
            var iterationState = headState.Clone();
            analyzeIteration(iterationState);

            var nextHeadState = entryState.Clone();
            nextHeadState.Merge(entryState, iterationState);
            if (headState.HasSameFlowState(nextHeadState))
                return nextHeadState;

            if (iterations >= MaxLoopAnalysisIterations)
            {
                // A large loop body can repeatedly expand a substantial call
                // graph before the flow state reaches a fixed point. Widen all
                // tracked reference flows, then visit the body once more so
                // escaping stores still produce diagnostics.
                nextHeadState.WidenArenaFlows();
                entryState.Progress?.LoopWidened();
                var widenedIterationState = nextHeadState.Clone();
                analyzeIteration(widenedIterationState);

                var widenedResult = entryState.Clone();
                widenedResult.Merge(entryState, widenedIterationState);
                widenedResult.WidenArenaFlows();
                return widenedResult;
            }

            headState = nextHeadState;
        }
    }

    private static Region GetForeachElementRegion(
        ForeachStatement loop,
        AnalysisState state)
    {
        if (loop.VariableType is not { } variableType ||
            !IsReferenceLikeOrReferenceBearing(variableType, state))
        {
            return Region.None;
        }

        if (GetOwnerPath(loop.Collection, state) is { } owner &&
            state.IsArrayContentsKnown(owner))
        {
            return state.GetElements(owner, index: null);
        }

        if (GetOwnerPath(loop.Collection, state) is { } collectionOwner &&
            state.TryGetCollectionContents(collectionOwner, out var collectionContents))
        {
            return collectionContents;
        }

        return Region.Arena | Region.Durable;
    }

    private static void VisitStatement(
        StatementBase statement,
        AnalysisState state,
        bool durableContext,
        HashSet<FunctionDeclaration> activeFunctions)
    {
        switch (statement)
        {
            case BlockStatement block:
                VisitStatements(block.Statements, state, durableContext, activeFunctions);
                break;
            case ExpressionStatement expression:
                VisitExpression(expression.Expression, state, durableContext, activeFunctions);
                break;
            case LocalVariableDeclarationStatement declaration:
                foreach (var variable in declaration.Declarators)
                {
                    if (variable.Symbol is null) continue;
                    if (variable.Initializer is null)
                    {
                        state.Set(variable.Symbol, Region.None);
                        state.SetAllocationArenaDepth(variable.Symbol, null);
                        if (GetReferenceBearingValueStructType(
                            variable.Symbol.Type, state) is not null)
                        {
                            var defaultPath = new StoragePath(variable.Symbol, "", Captured: false);
                            state.SetPath(variable.Symbol, defaultPath);
                            state.MarkAggregateContentsKnown(defaultPath);
                        }
                        else
                        {
                            state.SetPath(variable.Symbol, null);
                        }
                        continue;
                    }
                    VisitExpression(variable.Initializer, state, durableContext, activeFunctions);
                    state.Set(variable.Symbol, GetValueRegion(
                        variable.Initializer, state, durableContext));
                    state.SetAllocationArenaDepth(
                        variable.Symbol,
                        GetAllocationArenaDepth(variable.Initializer, state, durableContext));
                    var path = GetOwnerPath(variable.Initializer, state);
                    if (path is null && IsReferenceAllocation(variable.Initializer))
                        path = new StoragePath(variable.Symbol, "", Captured: false);
                    state.SetPath(variable.Symbol, path);
                }
                break;
            case IfStatement conditional:
                VisitExpression(conditional.Condition, state, durableContext, activeFunctions);
                var thenState = state.Clone();
                VisitStatement(conditional.ThenStatement, thenState, durableContext, activeFunctions);
                var elseState = state.Clone();
                if (conditional.ElseStatement is not null)
                    VisitStatement(conditional.ElseStatement, elseState, durableContext, activeFunctions);
                state.Merge(thenState, elseState);
                break;
            case ForStatement loop:
                if (loop.DeclarationInitializer is not null)
                    VisitStatement(loop.DeclarationInitializer, state, durableContext, activeFunctions);
                foreach (var initializer in loop.InitializerExpressions)
                    VisitExpression(initializer, state, durableContext, activeFunctions);
                var forEntryState = state.Clone();
                var forLoopState = AnalyzeLoop(forEntryState, iterationState =>
                {
                    if (loop.Condition is not null)
                        VisitExpression(loop.Condition, iterationState, durableContext, activeFunctions);
                    VisitStatement(loop.Body, iterationState, durableContext, activeFunctions);
                    foreach (var iterator in loop.Iterators)
                        VisitExpression(iterator, iterationState, durableContext, activeFunctions);
                });
                state.Merge(state.Clone(), forLoopState);
                break;
            case ForeachStatement loop:
                VisitExpression(loop.Collection, state, durableContext, activeFunctions);
                var foreachEntryState = state.Clone();
                var foreachElementRegion = GetForeachElementRegion(loop, state);
                var foreachLoopState = AnalyzeLoop(foreachEntryState, iterationState =>
                {
                    if (loop.VariableSymbol is { } variable)
                    {
                        iterationState.Set(variable, foreachElementRegion);
                        iterationState.SetPath(variable, null);
                    }
                    VisitStatement(loop.Body, iterationState, durableContext, activeFunctions);
                });
                state.Merge(state.Clone(), foreachLoopState);
                break;
            case WhileStatement loop:
                var whileEntryState = state.Clone();
                var whileLoopState = AnalyzeLoop(whileEntryState, iterationState =>
                {
                    VisitExpression(loop.Condition, iterationState, durableContext, activeFunctions);
                    VisitStatement(loop.Body, iterationState, durableContext, activeFunctions);
                });
                state.Merge(state.Clone(), whileLoopState);
                break;
            case DoWhileStatement loop:
                var doEntryState = state.Clone();
                var doLoopState = AnalyzeLoop(doEntryState, iterationState =>
                {
                    VisitStatement(loop.Body, iterationState, durableContext, activeFunctions);
                    VisitExpression(loop.Condition, iterationState, durableContext, activeFunctions);
                });
                state.Merge(state.Clone(), doLoopState);
                break;
            case SwitchStatement selection:
                VisitExpression(selection.Expression, state, durableContext, activeFunctions);
                var branchStates = new List<AnalysisState>();
                var unmatchedSwitchState = state.Clone();
                foreach (var section in selection.Sections)
                {
                    var sectionMissState = unmatchedSwitchState.Clone();
                    var sectionMatchStates = new List<AnalysisState>(section.Labels.Count);
                    foreach (var label in section.Labels)
                    {
                        var labelState = sectionMissState.Clone();
                        if (label.Value is not null)
                            VisitExpression(label.Value, labelState, durableContext, activeFunctions);
                        if (label.Filter is not null)
                            VisitExpression(label.Filter, labelState, durableContext, activeFunctions);
                        sectionMatchStates.Add(labelState);

                        var nextMissState = sectionMissState.Clone();
                        nextMissState.Merge(sectionMissState, labelState);
                        sectionMissState = nextMissState;
                    }
                    var branch = sectionMatchStates.Count == 0
                        ? unmatchedSwitchState.Clone()
                        : sectionMatchStates[0].Clone();
                    if (sectionMatchStates.Count > 1)
                        branch.Merge(sectionMatchStates.ToArray());
                    VisitStatements(section.Statements, branch, durableContext, activeFunctions);
                    branchStates.Add(branch);
                    unmatchedSwitchState = sectionMissState;
                }
                branchStates.Add(unmatchedSwitchState); // No matching label is possible.
                state.Merge(branchStates.ToArray());
                break;
            case TryStatement guarded:
                var tryEntryState = state.Clone();
                var tryState = state.Clone();
                VisitStatement(guarded.Body, tryState, durableContext, activeFunctions);
                var tryBranches = new List<AnalysisState> { tryState };
                var catchEntryState = tryEntryState.Clone();
                catchEntryState.Merge(tryEntryState, tryState);
                var unmatchedCatchState = catchEntryState.Clone();
                foreach (var clause in guarded.CatchClauses)
                {
                    var catchState = unmatchedCatchState.Clone();
                    if (clause.Filter is not null)
                    {
                        VisitExpression(clause.Filter, catchState, durableContext, activeFunctions);
                        var nextUnmatchedCatchState = unmatchedCatchState.Clone();
                        nextUnmatchedCatchState.Merge(unmatchedCatchState, catchState);
                        unmatchedCatchState = nextUnmatchedCatchState;
                    }
                    VisitStatement(clause.Body, catchState, durableContext, activeFunctions);
                    tryBranches.Add(catchState);
                }
                state.Merge(tryBranches.ToArray());
                if (guarded.FinallyBody is not null)
                    VisitStatement(guarded.FinallyBody, state, durableContext, activeFunctions);
                break;
            case ReturnStatement { Expression: not null } returned:
                MarkJoinedTaskCalls(returned.Expression!, state);
                VisitExpression(returned.Expression!, state, durableContext, activeFunctions);
                state.CurrentReturnAccumulator?.Add(
                    GetValueRegion(returned.Expression!, state, durableContext),
                    GetOwnerPath(returned.Expression!, state),
                    GetAllocationArenaDepth(returned.Expression!, state, durableContext));
                break;
            case ThrowStatement { Expression: not null } thrown:
                VisitExpression(thrown.Expression!, state, durableContext, activeFunctions);
                break;
            case YieldReturnStatement yielded:
                VisitExpression(yielded.Expression, state, durableContext, activeFunctions);
                break;
        }
    }

    private static void VisitExpression(
        ExpressionBase expression,
        AnalysisState state,
        bool durableContext,
        HashSet<FunctionDeclaration> activeFunctions)
    {
        switch (expression)
        {
            case AssignmentExpression assignment:
            {
                if (assignment.Operator == "=" &&
                    assignment.Target is ArrayAccessExpression indexedAssignment &&
                    assignment.TargetProperty is not null)
                {
                    if (indexedAssignment.Target is MemberAccessExpression indexedMember)
                        VisitExpression(
                            indexedMember.Target, state, durableContext, activeFunctions);
                    else
                        VisitExpression(
                            indexedAssignment.Target, state, durableContext, activeFunctions);
                    foreach (var index in indexedAssignment.Indices)
                        VisitExpression(index, state, durableContext, activeFunctions);
                }
                else if (assignment.Operator == "=" && assignment.TargetProperty is not null)
                {
                    if (assignment.Target is MemberAccessExpression propertyTarget)
                        VisitExpression(
                            propertyTarget.Target, state, durableContext, activeFunctions);
                }
                else
                {
                    VisitExpression(assignment.Target, state, durableContext, activeFunctions);
                }
                VisitExpression(assignment.Value, state, durableContext, activeFunctions);
                var assignedValue = assignment.Operator == "="
                    ? new CallResult(
                        GetValueRegion(assignment.Value, state, durableContext),
                        GetOwnerPath(assignment.Value, state))
                    : GetCompoundAssignmentResult(
                        assignment, state, durableContext, activeFunctions);
                if (assignment.Operator != "=" &&
                    assignedValue.Region == Region.None &&
                    IsReferenceLikeOrReferenceBearing(
                        assignment.Target.InferredType ?? BuiltInSystemTypes.Object,
                        state))
                {
                    assignedValue = assignedValue with
                    {
                        Region = Region.Arena | Region.Durable,
                    };
                }
                var valueRegion = assignedValue.Region;
                var ownerAlignedNodeLink = IsLinkedListNodeLinkStore(
                    assignment, activeFunctions);
                var propertyReceiver = assignment.TargetProperty is null
                    ? null
                    : assignment.Target is ArrayAccessExpression propertyArrayTarget
                        ? (propertyArrayTarget.Target as MemberAccessExpression)?.Target
                        : (assignment.Target as MemberAccessExpression)?.Target;
                if ((valueRegion & Region.Arena) != 0 &&
                    ((IsPersistentDestination(assignment.Target, state) &&
                      !ownerAlignedNodeLink) ||
                     IsOutlivedArenaOwnerStore(
                         assignment.Target, assignment.Value, state, durableContext,
                         assignment.TargetProperty, propertyReceiver)))
                {
                    throw new CompilationErrorException(EscapeMessage, assignment.SourceSpan);
                }

                if (GetAssignedLocal(assignment.Target) is { } local)
                {
                    state.Set(local, valueRegion);
                    state.SetAllocationArenaDepth(
                        local,
                        GetAllocationArenaDepth(assignment.Value, state, durableContext));
                    var path = assignedValue.Path;
                    if (path is null && assignment.Operator == "=" &&
                        IsReferenceAllocation(assignment.Value))
                        path = new StoragePath(local, "", Captured: false);
                    state.SetPath(local, path);
                }
                SetAssignmentSlotRegion(assignment, valueRegion, state);
                SetAssignmentSlotAllocationArenaDepth(
                    assignment.Target,
                    GetAllocationArenaDepth(assignment.Value, state, durableContext),
                    state);

                if (assignment.TargetProperty is { } assignmentProperty &&
                    assignment.PropertySetter is { } setter)
                {
                    var propertyTarget = assignment.Target is ArrayAccessExpression indexedTarget
                        ? indexedTarget.Target
                        : assignment.Target;
                    var receiver = (propertyTarget as MemberAccessExpression)?.Target;
                    var indexArguments = assignment.Target is ArrayAccessExpression indexed
                        ? indexed.Indices
                        : [];
                    var accessor = setter.Declaration;
                    var setterFunction = accessor?.BodyFunction;
                    var openDispatch = assignment.InterfaceDispatchSlotIndex is not null ||
                        IsOpenProperty(assignmentProperty, accessor);
                    var unsupportedIndexer =
                        setter.ParameterTypes.Count != indexArguments.Count;
                    if (openDispatch || unsupportedIndexer || setterFunction?.Body is null)
                    {
                        if (receiver is not null &&
                            MayContainArenaReference(receiver, state, durableContext))
                        {
                            throw new CompilationErrorException(
                                $"{OpaqueCallEscapeMessage} ({assignmentProperty.FullName})",
                                receiver.SourceSpan);
                        }
                        foreach (var indexArgument in indexArguments)
                        {
                            if (MayContainArenaReference(
                                indexArgument, state, durableContext))
                            {
                                throw new CompilationErrorException(
                                    $"{OpaqueCallEscapeMessage} ({assignmentProperty.FullName})",
                                    indexArgument.SourceSpan);
                            }
                        }
                        if ((assignment.Operator == "=" &&
                             MayContainArenaReference(
                                 assignment.Value, state, durableContext)) ||
                            (assignedValue.Region & Region.Arena) != 0)
                        {
                            throw new CompilationErrorException(
                                $"{OpaqueCallEscapeMessage} ({assignmentProperty.FullName})",
                                assignment.SourceSpan);
                        }
                    }
                    else
                    {
                        VisitFunctionBody(
                            setterFunction,
                            assignmentProperty.IsStatic ? null : receiver,
                            [.. indexArguments, assignment.Value],
                            state,
                            durableContext,
                            activeFunctions,
                            [.. indexArguments.Select(index =>
                                GetValueRegion(index, state, durableContext)),
                                assignedValue.Region],
                            [.. indexArguments.Select(index =>
                                GetOwnerPath(index, state)),
                                assignedValue.Path]);
                    }
                }

                state.SetResult(assignment, assignedValue);

                break;
            }
            case InvocationExpression invocation:
                if (IsDurableAllocationContext(invocation))
                {
                    VisitExpression(invocation.Target, state, durableContext, activeFunctions);
                    var previousReturnAccumulator = state.CurrentReturnAccumulator;
                    state.CurrentReturnAccumulator = null;
                    foreach (var lambda in invocation.Arguments.OfType<LambdaExpression>())
                    {
                        if (lambda.ExpressionBody is not null)
                            VisitExpression(lambda.ExpressionBody, state, durableContext: true, activeFunctions);
                        if (lambda.StatementBody is not null)
                            VisitStatements(lambda.StatementBody, state, durableContext: true, activeFunctions);
                    }
                    state.CurrentReturnAccumulator = previousReturnAccumulator;
                    break;
                }
                if (IsMemoryMethod(invocation, "WithArena") ||
                    IsMemoryMethod(invocation, "WithArenaAsync"))
                {
                    var previousReturnAccumulator = state.CurrentReturnAccumulator;
                    var previousAsyncArenaScopeDepth = state.AsyncArenaScopeDepth;
                    var previousArenaDepth = state.ArenaDepth;
                    state.ArenaDepth = previousArenaDepth + 1;
                    state.AsyncArenaScopeDepth = IsMemoryMethod(
                        invocation, "WithArenaAsync")
                        ? previousAsyncArenaScopeDepth + 1
                        : 0;
                    state.CurrentReturnAccumulator = null;
                    foreach (var lambda in invocation.Arguments.OfType<LambdaExpression>())
                    {
                        if (lambda.ExpressionBody is not null)
                        {
                            if (IsMemoryMethod(invocation, "WithArenaAsync"))
                                MarkJoinedTaskCalls(lambda.ExpressionBody, state);
                            VisitExpression(lambda.ExpressionBody, state, durableContext: false, activeFunctions);
                        }
                        if (lambda.StatementBody is not null)
                            VisitStatements(lambda.StatementBody, state, durableContext: false, activeFunctions);
                    }
                    state.CurrentReturnAccumulator = previousReturnAccumulator;
                    state.AsyncArenaScopeDepth = previousAsyncArenaScopeDepth;
                    state.ArenaDepth = previousArenaDepth;
                }
                VisitExpression(invocation.Target, state, durableContext, activeFunctions);
                foreach (var argument in invocation.Arguments)
                    if (argument is not LambdaExpression)
                        VisitExpression(argument, state, durableContext, activeFunctions);
                if (invocation.TargetSymbol?.NativeImport is { NoEscape: false })
                {
                    foreach (var argument in invocation.Arguments)
                    {
                        if (MayContainArenaReference(argument, state, durableContext))
                            throw new CompilationErrorException(
                                NativeEscapeMessage, argument.SourceSpan);
                    }
                }
                if (GetReflectionArgumentArrayIndex(invocation) is { } argumentArrayIndex &&
                    argumentArrayIndex < invocation.Arguments.Count &&
                    MayContainArenaArrayReference(
                        invocation.Arguments[argumentArrayIndex], state))
                {
                    throw new CompilationErrorException(
                        ReflectionEscapeMessage,
                        invocation.Arguments[argumentArrayIndex].SourceSpan);
                }
                if (IsOpaqueCallBoundary(invocation, state, activeFunctions))
                {
                    if (invocation.Receiver is { } opaqueReceiver &&
                        MayContainArenaReference(opaqueReceiver, state, durableContext))
                    {
                        throw new CompilationErrorException(
                            $"{OpaqueCallEscapeMessage} ({invocation.TargetSymbol?.FullName})",
                            opaqueReceiver.SourceSpan);
                    }
                    foreach (var argument in invocation.Arguments)
                    {
                        if (MayContainArenaReference(argument, state, durableContext))
                        {
                            throw new CompilationErrorException(
                                $"{OpaqueCallEscapeMessage} ({invocation.TargetSymbol?.FullName})",
                                argument.SourceSpan);
                        }
                    }
                    if (invocation.Receiver is { } opaqueCollectionReceiver &&
                        GetOwnerPath(opaqueCollectionReceiver, state) is { } receiverPath)
                    {
                        state.ForgetCollectionContents(receiverPath);
                    }
                    foreach (var argument in invocation.Arguments)
                    {
                        if (GetOwnerPath(argument, state) is { } argumentPath)
                            state.ForgetCollectionContents(argumentPath);
                    }
                }
                if (IsAsyncCall(invocation) && !durableContext &&
                    state.AsyncArenaScopeDepth == 0)
                {
                    throw new CompilationErrorException(
                        AsyncArenaCallMessage, invocation.SourceSpan);
                }
                if (IsAsyncCall(invocation) && !durableContext &&
                    state.AsyncArenaScopeDepth != 0)
                {
                    if (!state.IsJoinedAsyncCall(invocation))
                    {
                        throw new CompilationErrorException(
                            DetachedAsyncArenaCallMessage, invocation.SourceSpan);
                    }
                    VisitAsyncCallee(invocation, state, durableContext, activeFunctions);
                }
                if (invocation.Receiver is { } collectionReceiver &&
                    !IsRuntimeCheckedCoreCollectionStore(
                        invocation, activeFunctions) &&
                    IsPotentiallyDurableCollectionOwner(
                        collectionReceiver, state, durableContext))
                {
                    foreach (var argumentIndex in GetCollectionStoredArgumentIndices(invocation))
                    {
                        if (argumentIndex < invocation.Arguments.Count &&
                            MayContainArenaReference(
                                invocation.Arguments[argumentIndex], state, durableContext))
                        {
                            throw new CompilationErrorException(
                                EscapeMessage,
                                invocation.Arguments[argumentIndex].SourceSpan);
                        }
                    }
                }
                if (invocation.Receiver is { } mutatedCollection &&
                    GetOwnerPath(mutatedCollection, state) is { } collectionPath)
                {
                    var storedArgumentIndices = GetCollectionStoredArgumentIndices(invocation);
                    if (storedArgumentIndices.Count > 0)
                    {
                        foreach (var argumentIndex in storedArgumentIndices)
                        {
                            if (argumentIndex < invocation.Arguments.Count)
                            {
                                state.AddCollectionContents(
                                    collectionPath,
                                    GetValueRegion(
                                        invocation.Arguments[argumentIndex],
                                        state,
                                        durableContext));
                            }
                        }
                    }
                    else if (IsCollectionMethod(invocation, "List", "AddRange"))
                    {
                        var sourcePath = invocation.Arguments.Count > 0
                            ? GetOwnerPath(invocation.Arguments[0], state)
                            : null;
                        if (sourcePath is { } source &&
                            state.TryGetCollectionContents(source, out var sourceContents))
                        {
                            state.AddCollectionContents(collectionPath, sourceContents);
                        }
                        else
                        {
                            state.ForgetCollectionContents(collectionPath);
                        }
                    }
                    else if (IsCollectionClearCall(invocation))
                    {
                        state.SetCollectionContents(collectionPath, Region.None);
                    }
                }
                if (invocation.Receiver is { } copySource &&
                    TryGetCollectionCopyDestinationIndex(invocation) is { } destinationIndex &&
                    destinationIndex < invocation.Arguments.Count &&
                    IsPotentiallyDurableCollectionOwner(
                        invocation.Arguments[destinationIndex], state, durableContext) &&
                    MayContainArenaCollectionContents(copySource, state, durableContext))
                {
                    throw new CompilationErrorException(
                        EscapeMessage,
                        invocation.Arguments[destinationIndex].SourceSpan);
                }
                if (invocation.Receiver is { } rangeSource &&
                    IsCollectionMethod(invocation, "List", "AddRange") &&
                    invocation.Arguments.Count > 0 &&
                    IsPotentiallyDurableCollectionOwner(
                        rangeSource, state, durableContext) &&
                    MayContainArenaCollectionContents(
                        invocation.Arguments[0], state, durableContext))
                {
                    throw new CompilationErrorException(
                        EscapeMessage, invocation.Arguments[0].SourceSpan);
                }
                if (invocation.Receiver is { } arraySource &&
                    IsCollectionMethod(invocation, "List", "ToArray") &&
                    (durableContext || IsPotentiallyDurableCollectionOwner(
                        arraySource, state, durableContext)) &&
                    MayContainArenaCollectionContents(arraySource, state, durableContext))
                {
                    throw new CompilationErrorException(EscapeMessage, invocation.SourceSpan);
                }
                if (!IsModeledCollectionCall(invocation) &&
                    !IsTaskVoidResultObservation(invocation) &&
                    !IsRetainedAsyncQueueCall(invocation, state) &&
                    !IsDurableArenaHandleCall(invocation))
                {
                    var previousJoinedFactoryDepth = state.JoinedTaskFactoryDepth;
                    if (IsJoinedTaskFactoryCall(invocation, state))
                        state.JoinedTaskFactoryDepth += 1;
                    VisitCallees(invocation, state, durableContext, activeFunctions);
                    state.JoinedTaskFactoryDepth = previousJoinedFactoryDepth;
                }
                if (invocation.Receiver is { } readCollection &&
                    IsCollectionReadCall(invocation) &&
                    (invocation.TargetSymbol?.ReturnType ??
                        invocation.FunctionType?.ReturnType) is { } readReturnType &&
                    IsReferenceLikeOrReferenceBearing(readReturnType, state))
                {
                    var result = state.GetResult(invocation);
                    var resultRegion = GetOwnerPath(readCollection, state) is { } readOwner &&
                        state.TryGetCollectionContents(readOwner, out var readContents)
                            ? result.Region | readContents
                            : MayContainArenaCollectionContents(
                                readCollection, state, durableContext)
                                ? result.Region | Region.Arena
                                : result.Region;
                    state.SetResult(invocation, result with
                    {
                        Region = resultRegion,
                    });
                }
                break;
            case IdentifierExpression { TargetProperty: { } identifierProperty } identifier
                when identifier.PropertyGetter is { } identifierGetter:
                VisitPropertyGetter(
                    identifier, identifierProperty, identifierGetter, receiver: null,
                    interfaceDispatch: false, state, durableContext, activeFunctions);
                break;
            case MemberAccessExpression member:
                VisitExpression(member.Target, state, durableContext, activeFunctions);
                if (member.TargetProperty is { } property &&
                    member.PropertyGetter is { } getter)
                {
                    VisitPropertyGetter(
                        member, property, getter,
                        property.IsStatic ? null : member.Target,
                        member.InterfaceDispatchSlotIndex is not null,
                        state, durableContext, activeFunctions);
                    if (property.FullName.Parts[^1] == "Current" &&
                        IsCollectionEnumeratorType(member.Target.InferredType) &&
                        IsReferenceLikeOrReferenceBearing(property.Type, state))
                    {
                        var enumeratorRegion = GetRegion(
                            member.Target, state, durableContext);
                        if ((enumeratorRegion & Region.Arena) != 0 ||
                            enumeratorRegion == Region.None)
                        {
                            var result = state.GetResult(member);
                            state.SetResult(member, result with
                            {
                                Region = result.Region | Region.Arena,
                            });
                        }
                    }
                }
                break;
            case ArrayAccessExpression array:
                if (array.TargetProperty is not null &&
                    array.Target is MemberAccessExpression indexedPropertyTarget)
                {
                    VisitExpression(
                        indexedPropertyTarget.Target, state, durableContext, activeFunctions);
                }
                else
                {
                    VisitExpression(array.Target, state, durableContext, activeFunctions);
                }
                foreach (var index in array.Indices)
                    VisitExpression(index, state, durableContext, activeFunctions);
                if (array.TargetProperty is { } indexedProperty &&
                    array.PropertyGetter is { } indexedGetter)
                {
                    var indexedReceiver = array.Target is MemberAccessExpression memberTarget
                        ? memberTarget.Target
                        : null;
                    VisitPropertyGetter(
                        array,
                        indexedProperty,
                        indexedGetter,
                        indexedProperty.IsStatic ? null : indexedReceiver,
                        array.InterfaceDispatchSlotIndex is not null,
                        state,
                        durableContext,
                        activeFunctions,
                        array.Indices);
                }
                break;
            case ObjectCreationExpression creation:
                foreach (var argument in creation.Arguments)
                    VisitExpression(argument, state, durableContext, activeFunctions);
                VisitConstructor(creation, state, durableContext, activeFunctions);
                if (IsCoreCollectionType(creation.RequestedType) &&
                    state.GetResult(creation).Path is { } createdCollectionPath)
                {
                    state.SetCollectionContents(createdCollectionPath, Region.None);
                }
                break;
            case ArrayCreationExpression creation:
                VisitExpression(creation.Length, state, durableContext, activeFunctions);
                var arrayPath = new StoragePath(
                    new LocalVariableSymbol(
                        "__cx_array_creation", new ArrayType(creation.ElementType)),
                    "",
                    Captured: false);
                state.SetResult(creation, new CallResult(
                    durableContext ? Region.Durable : Region.Arena, arrayPath));
                state.MarkArrayContentsKnown(arrayPath);
                break;
            case NativeStackAllocationExpression allocation:
                VisitExpression(allocation.Count, state, durableContext, activeFunctions);
                break;
            case BinaryExpression binary:
                VisitExpression(binary.Left, state, durableContext, activeFunctions);
                if (binary.OperatorSymbol is null &&
                    (binary.Operator is "&&" or "||"))
                {
                    var skippedRightState = state.Clone();
                    var evaluatedRightState = state.Clone();
                    VisitExpression(
                        binary.Right, evaluatedRightState, durableContext, activeFunctions);
                    state.Merge(skippedRightState, evaluatedRightState);
                }
                else
                {
                    VisitExpression(binary.Right, state, durableContext, activeFunctions);
                }
                if (binary.OperatorSymbol?.Declaration is { } binaryOperator)
                {
                    var receiver = binaryOperator.IsStatic ? null : binary.Left;
                    var arguments = binaryOperator.IsStatic
                        ? new[] { binary.Left, binary.Right }
                        : new[] { binary.Right };
                    state.SetResult(binary, AnalyzeOperatorBody(
                        binaryOperator,
                        receiver,
                        arguments,
                        [binary.Left, binary.Right],
                        state,
                        durableContext,
                        activeFunctions,
                        binary.SourceSpan));
                }
                break;
            case UnaryExpression unary:
                if (unary.Operator == "await")
                    MarkJoinedTaskCalls(unary.Operand, state);
                VisitExpression(unary.Operand, state, durableContext, activeFunctions);
                if (unary.Operator == "await" &&
                    IsReferenceLikeOrReferenceBearing(
                        unary.InferredType ?? BuiltInSystemTypes.Object, state))
                {
                    state.SetResult(unary, new CallResult(
                        Region.Arena | Region.Durable, null));
                }
                if (unary.TargetProperty is { } mutationProperty &&
                    unary.PropertySetter is { } mutationSetter)
                {
                    VisitPropertyMutationSetter(
                        unary,
                        mutationProperty,
                        mutationSetter,
                        state,
                        durableContext,
                        activeFunctions);
                }
                else if (unary.OperatorSymbol?.Declaration is { } unaryOperator)
                {
                    var receiver = unaryOperator.IsStatic ? null : unary.Operand;
                    var arguments = unaryOperator.IsStatic
                        ? new[] { unary.Operand }
                        : [];
                    state.SetResult(unary, AnalyzeOperatorBody(
                        unaryOperator,
                        receiver,
                        arguments,
                        [unary.Operand],
                        state,
                        durableContext,
                        activeFunctions,
                        unary.SourceSpan));
                }
                break;
            case AwaitExpression awaited:
                MarkJoinedTaskCalls(awaited.Operand, state);
                VisitExpression(awaited.Operand, state, durableContext, activeFunctions);
                if (IsReferenceLikeOrReferenceBearing(
                    awaited.InferredType ?? BuiltInSystemTypes.Object, state))
                {
                    state.SetResult(awaited, new CallResult(
                        Region.Arena | Region.Durable, null));
                }
                break;
            case CastExpression cast:
                VisitExpression(cast.Operand, state, durableContext, activeFunctions);
                break;
            case ConditionalExpression conditional:
                VisitExpression(conditional.Condition, state, durableContext, activeFunctions);
                var conditionalInput = state.Clone();
                var trueState = conditionalInput.Clone();
                VisitExpression(
                    conditional.WhenTrue, trueState, durableContext, activeFunctions);
                var falseState = conditionalInput.Clone();
                VisitExpression(
                    conditional.WhenFalse, falseState, durableContext, activeFunctions);
                state.Merge(trueState, falseState);
                break;
            case NullCoalescingExpression coalescing:
                VisitExpression(coalescing.Left, state, durableContext, activeFunctions);
                var leftOnlyState = state.Clone();
                var rightEvaluatedState = state.Clone();
                VisitExpression(
                    coalescing.Right, rightEvaluatedState, durableContext, activeFunctions);
                state.Merge(leftOnlyState, rightEvaluatedState);
                break;
            case TypeTestExpression typeTest:
                VisitExpression(typeTest.Operand, state, durableContext, activeFunctions);
                break;
            case SwitchExpression selection:
                VisitExpression(selection.Selector, state, durableContext, activeFunctions);
                var switchInput = state.Clone();
                var switchStates = new List<AnalysisState>(selection.Arms.Count + 1);
                var unmatchedState = switchInput.Clone();
                foreach (var arm in selection.Arms)
                {
                    var armState = unmatchedState.Clone();
                    if (arm.Label is not null)
                        VisitExpression(arm.Label, armState, durableContext, activeFunctions);
                    if (arm.Filter is not null)
                        VisitExpression(arm.Filter, armState, durableContext, activeFunctions);
                    var guardState = armState.Clone();
                    VisitExpression(arm.Value, armState, durableContext, activeFunctions);
                    switchStates.Add(armState);

                    var nextUnmatchedState = unmatchedState.Clone();
                    nextUnmatchedState.Merge(unmatchedState, guardState);
                    unmatchedState = nextUnmatchedState;
                }
                if (!selection.Arms.Any(arm => arm.IsDefault))
                    switchStates.Add(unmatchedState);
                state.Merge(switchStates.ToArray());
                break;
        }
    }

    private static void VisitPropertyGetter(
        ExpressionBase expression,
        PropertySymbol property,
        PropertyAccessorSymbol getter,
        ExpressionBase? receiver,
        bool interfaceDispatch,
        AnalysisState state,
        bool durableContext,
        HashSet<FunctionDeclaration> activeFunctions,
        IReadOnlyList<ExpressionBase>? indexArguments = null)
    {
        var accessor = getter.Declaration;
        var getterFunction = accessor?.BodyFunction;
        var openDispatch = interfaceDispatch || IsOpenProperty(property, accessor);
        var arguments = indexArguments ?? [];
        var unsupportedIndexer = getter.ParameterTypes.Count != arguments.Count;
        if (openDispatch || unsupportedIndexer || getterFunction?.Body is null)
        {
            var scopedReceiver = receiver is not null
                ? MayContainArenaReference(receiver, state, durableContext)
                : !property.IsStatic &&
                    (((state.ThisRegion & Region.Arena) != 0) ||
                     (state.ThisPath is { } thisPath &&
                        (state.GetContainedRegion(thisPath) & Region.Arena) != 0));
            if (!property.IsStatic && !IsKnownNonRetainingCoreProperty(property, accessor) &&
                scopedReceiver)
            {
                throw new CompilationErrorException(
                    $"{OpaqueCallEscapeMessage} ({property.FullName})",
                    expression.SourceSpan);
            }
            foreach (var indexArgument in arguments)
            {
                if (MayContainArenaReference(
                    indexArgument, state, durableContext))
                {
                    throw new CompilationErrorException(
                        $"{OpaqueCallEscapeMessage} ({property.FullName})",
                        indexArgument.SourceSpan);
                }
            }
            if (IsReferenceBearingValueType(property.Type, state))
                state.SetResult(expression, new CallResult(Region.Arena | Region.Durable, null));
            return;
        }

        var result = VisitFunctionBody(
            getterFunction,
            property.IsStatic ? null : receiver,
            arguments,
            state,
            durableContext,
            activeFunctions);
        state.SetResult(expression, result);
        if (result.Region == Region.None && result.Path is null)
            return;

        var owner = arguments.Count == 0 ? expression switch
        {
            MemberAccessExpression member => GetOwnerPath(member.Target, state),
            IdentifierExpression when !property.IsStatic => state.ThisPath,
            _ => null,
        } : null;
        if (owner is { } propertyOwner)
            state.Set(new PropertySlot(propertyOwner, property), result.Region);
    }

    private static void VisitPropertyMutationSetter(
        UnaryExpression unary,
        PropertySymbol property,
        PropertyAccessorSymbol setter,
        AnalysisState state,
        bool durableContext,
        HashSet<FunctionDeclaration> activeFunctions)
    {
        var indexArguments = unary.Operand is ArrayAccessExpression indexed
            ? indexed.Indices
            : [];
        var propertyTarget = unary.Operand is ArrayAccessExpression arrayAccess
            ? arrayAccess.Target
            : unary.Operand;
        var receiver = (propertyTarget as MemberAccessExpression)?.Target;
        var setterFunction = setter.Declaration?.BodyFunction;
        var operatorResult = GetPropertyMutationResult(
            unary, state, durableContext, activeFunctions);
        var openDispatch = unary.PropertySetterInterfaceDispatchSlotIndex is not null ||
            IsOpenProperty(property, setter.Declaration);
        var unsupportedIndexer = setter.ParameterTypes.Count != indexArguments.Count;

        if (openDispatch || unsupportedIndexer || setterFunction?.Body is null)
        {
            if (receiver is not null &&
                MayContainArenaReference(receiver, state, durableContext))
            {
                throw new CompilationErrorException(
                    $"{OpaqueCallEscapeMessage} ({property.FullName})",
                    receiver.SourceSpan);
            }
            foreach (var indexArgument in indexArguments)
            {
                if (MayContainArenaReference(indexArgument, state, durableContext))
                {
                    throw new CompilationErrorException(
                        $"{OpaqueCallEscapeMessage} ({property.FullName})",
                        indexArgument.SourceSpan);
                }
            }
            if ((operatorResult.Region & Region.Arena) != 0)
            {
                throw new CompilationErrorException(
                    $"{OpaqueCallEscapeMessage} ({property.FullName})",
                    unary.SourceSpan);
            }
        }
        else
        {
            var arguments = indexArguments.Append(unary.Operand).ToArray();
            var argumentRegions = indexArguments
                .Select(index => GetValueRegion(index, state, durableContext))
                .Append(operatorResult.Region)
                .ToArray();
            var argumentPaths = indexArguments
                .Select(index => GetOwnerPath(index, state))
                .Append(operatorResult.Path)
                .ToArray();
            VisitFunctionBody(
                setterFunction,
                property.IsStatic ? null : receiver,
                arguments,
                state,
                durableContext,
                activeFunctions,
                argumentRegions,
                argumentPaths);
        }

        state.SetResult(
            unary,
            unary.Postfix ? state.GetResult(unary.Operand) : operatorResult);
    }

    private static CallResult GetPropertyMutationResult(
        UnaryExpression unary,
        AnalysisState state,
        bool durableContext,
        HashSet<FunctionDeclaration> activeFunctions)
    {
        if (unary.OperatorSymbol?.Declaration is { } operatorFunction)
        {
            if (operatorFunction.Body is not null)
            {
                var result = VisitFunctionBody(
                    operatorFunction,
                    operatorFunction.IsStatic ? null : unary.Operand,
                    operatorFunction.IsStatic ? [unary.Operand] : [],
                    state,
                    durableContext,
                    activeFunctions);
                return operatorFunction.ReturnType is VoidType
                    ? new CallResult(
                        GetValueRegion(unary.Operand, state, durableContext),
                        GetOwnerPath(unary.Operand, state))
                    : result;
            }

            if (!HasNoEscapeContract(operatorFunction) &&
                operatorFunction.NativeImport?.NoEscape != true &&
                MayContainArenaReference(unary.Operand, state, durableContext))
            {
                throw new CompilationErrorException(
                    $"{OpaqueCallEscapeMessage} ({operatorFunction.FullName})",
                    unary.Operand.SourceSpan);
            }

            var returnType = unary.OperatorSymbol.ReturnType;
            if (returnType is VoidType)
            {
                return new CallResult(
                    GetValueRegion(unary.Operand, state, durableContext),
                    GetOwnerPath(unary.Operand, state));
            }
            if (returnType is ObjectType or PtrType or FunctionType or NullableType ||
                returnType is NamedType { ClassType: ClassType.Class or ClassType.Interface } ||
                IsReferenceBearingValueType(returnType, state))
            {
                return new CallResult(Region.Arena | Region.Durable, null);
            }

            return default;
        }

        return default;
    }

    private static CallResult GetCompoundAssignmentResult(
        AssignmentExpression assignment,
        AnalysisState state,
        bool durableContext,
        HashSet<FunctionDeclaration> activeFunctions)
    {
        var targetResult = new CallResult(
            GetValueRegion(assignment.Target, state, durableContext),
            GetOwnerPath(assignment.Target, state));
        var rightRegion = GetValueRegion(assignment.Value, state, durableContext);
        if (assignment.OperatorSymbol?.Declaration is { } operatorFunction)
        {
            var receiver = operatorFunction.IsStatic ? null : assignment.Target;
            var arguments = operatorFunction.IsStatic
                ? new[] { assignment.Target, assignment.Value }
                : new[] { assignment.Value };
            if (operatorFunction.Body is not null)
            {
                var result = VisitFunctionBody(
                    operatorFunction,
                    receiver,
                    arguments,
                    state,
                    durableContext,
                    activeFunctions);
                return operatorFunction.ReturnType is VoidType
                    ? targetResult
                    : result;
            }

            if (!HasNoEscapeContract(operatorFunction) &&
                operatorFunction.NativeImport?.NoEscape != true &&
                (MayContainArenaReference(assignment.Target, state, durableContext) ||
                 MayContainArenaReference(assignment.Value, state, durableContext)))
            {
                throw new CompilationErrorException(
                    $"{OpaqueCallEscapeMessage} ({operatorFunction.FullName})",
                    assignment.SourceSpan);
            }

            if (operatorFunction.ReturnType is VoidType)
                return targetResult;
            return IsReferenceLikeOrReferenceBearing(
                operatorFunction.ReturnType, state)
                ? new CallResult(Region.Arena | Region.Durable, null)
                : default;
        }

        if (IsReferenceLikeOrReferenceBearing(
            assignment.Target.InferredType ?? BuiltInSystemTypes.Object, state))
        {
            return new CallResult(
                targetResult.Region | rightRegion | Region.Arena,
                targetResult.Path);
        }

        return default;
    }

    private static CallResult AnalyzeOperatorBody(
        FunctionDeclaration declaration,
        ExpressionBase? receiver,
        IReadOnlyList<ExpressionBase> arguments,
        IReadOnlyList<ExpressionBase> operands,
        AnalysisState state,
        bool durableContext,
        HashSet<FunctionDeclaration> activeFunctions,
        SourceSpan? sourceSpan)
    {
        if (declaration.Body is not null)
        {
            return VisitFunctionBody(
                declaration,
                receiver,
                arguments,
                state,
                durableContext,
                activeFunctions);
        }

        if (!HasNoEscapeContract(declaration) &&
            declaration.NativeImport?.NoEscape != true)
        {
            foreach (var operand in operands)
            {
                if (MayContainArenaReference(operand, state, durableContext))
                {
                    throw new CompilationErrorException(
                        $"{OpaqueCallEscapeMessage} ({declaration.FullName})",
                        sourceSpan);
                }
            }
        }

        if (declaration.ReturnType is VoidType)
        {
            return receiver is not null
                ? new CallResult(
                    GetValueRegion(receiver, state, durableContext),
                    GetOwnerPath(receiver, state))
                : default;
        }

        return IsReferenceLikeOrReferenceBearing(declaration.ReturnType, state)
            ? new CallResult(Region.Arena | Region.Durable, null)
            : default;
    }

    private static bool IsReferenceLikeOrReferenceBearing(
        TypeBase type,
        AnalysisState state) => type is ObjectType or PtrType or FunctionType or NullableType ||
        type is NamedType { IsDelegate: true } ||
        type is NamedType { ClassType: ClassType.Class or ClassType.Interface } ||
        IsReferenceBearingValueType(type, state);

    private static IReadOnlyList<FunctionDeclaration> GetCalleeDeclarations(
        InvocationExpression invocation,
        AnalysisState state,
        HashSet<FunctionDeclaration> activeFunctions)
    {
        var declaration = invocation.TargetSymbol?.Declaration;
        if (invocation.IsConceptMethodInvocation || declaration is null ||
            declaration.NativeImport is not null || declaration.IsAsync ||
            declaration.IsIteratorFactory)
        {
            return [];
        }

        var targets = new List<FunctionDeclaration>();
        if (invocation.DispatchSlotIndex is { } interfaceSlot)
        {
            foreach (var implementation in state.KnownTypes
                .SelectMany(type => type.InterfaceDispatchTables)
                .SelectMany(table => table.Slots)
                .Where(slot => slot.Index == interfaceSlot &&
                    ReferenceEquals(slot.ContractFunction, declaration))
                .Select(slot => slot.ImplementationFunction))
            {
                if (implementation is not null)
                    targets.Add(implementation);
            }
        }
        else if (declaration.VirtualSlotIndex is { } virtualSlot)
        {
            var contract = declaration.VirtualContract ?? declaration;
            foreach (var implementation in state.KnownTypes
                .SelectMany(type => type.VirtualMethodSlots)
                .Where(slot => slot.Index == virtualSlot &&
                    ReferenceEquals(slot.Contract, contract))
                .Select(slot => slot.Implementation))
            {
                if (implementation is not null)
                    targets.Add(implementation);
            }
            targets.Add(declaration);
        }
        else
        {
            targets.Add(declaration);
        }

        foreach (var target in invocation.GenericVirtualDispatchTargets)
        {
            if (target.Implementation.Declaration is { } implementation)
                targets.Add(implementation);
        }

        // Every known concrete dispatch target is analyzed. A recursive target
        // is skipped because its outer expansion still visits its body.
        return targets.Where(target => target.Body is not null &&
                target.NativeImport is null && !target.IsAsync &&
                !target.IsIteratorFactory && !activeFunctions.Contains(target))
            .Distinct()
            .ToArray();
    }

    private static void VisitCallees(
        InvocationExpression invocation,
        AnalysisState state,
        bool durableContext,
        HashSet<FunctionDeclaration> activeFunctions)
    {
        var declarations = GetCalleeDeclarations(invocation, state, activeFunctions);
        if (declarations.Count == 1)
        {
            VisitCallee(invocation, declarations[0], state, durableContext, activeFunctions);
            return;
        }

        if (declarations.Count == 0)
            return;

        var initialState = state.Clone();
        var dispatchStates = new List<AnalysisState>(declarations.Count);
        foreach (var declaration in declarations)
        {
            var dispatchState = initialState.Clone();
            VisitCallee(
                invocation, declaration, dispatchState, durableContext, activeFunctions);
            dispatchStates.Add(dispatchState);
        }
        state.Merge(dispatchStates.ToArray());
    }

    private static void VisitCallee(
        InvocationExpression invocation,
        FunctionDeclaration declaration,
        AnalysisState state,
        bool durableContext,
        HashSet<FunctionDeclaration> activeFunctions)
    {
        var result = VisitFunctionBody(
            declaration,
            invocation.Receiver,
            invocation.Arguments,
            state,
            durableContext,
            activeFunctions);
        state.SetResult(invocation, result);
    }

    private static CallResult VisitFunctionBody(
        FunctionDeclaration declaration,
        ExpressionBase? receiver,
        IReadOnlyList<ExpressionBase> arguments,
        AnalysisState state,
        bool durableContext,
        HashSet<FunctionDeclaration> activeFunctions,
        IReadOnlyList<Region>? argumentRegions = null,
        IReadOnlyList<StoragePath?>? argumentPaths = null)
    {
        if (declaration.Body is null || activeFunctions.Contains(declaration))
            return default;
        if (activeFunctions.Count >= MaxInlinedCallDepth)
        {
            throw new CompilationErrorException(
                $"{CallDepthEscapeMessage} ({declaration.FullName})");
        }
        activeFunctions.Add(declaration);
        state.Progress?.FunctionBodyVisited(declaration.FullName.ToString());

        var previousThisRegion = state.ThisRegion;
        var previousThisPath = state.ThisPath;
        var previousThisArenaDepth = state.ThisArenaDepth;
        var previousReturnAccumulator = state.CurrentReturnAccumulator;
        var returnAccumulator = new ReturnAccumulator();
        state.CurrentReturnAccumulator = returnAccumulator;
        if (!declaration.IsStatic && declaration.ParentClassDeclaration is not null &&
            receiver is not null)
        {
            state.ThisRegion = GetValueRegion(receiver, state, durableContext);
            state.ThisPath = GetOwnerPath(receiver, state);
            state.ThisArenaDepth = GetAllocationArenaDepth(receiver, state, durableContext);
        }

        for (var index = 0; index < declaration.Parameters.Count && index < arguments.Count; index++)
        {
            var parameter = declaration.Parameters[index];
            var argument = arguments[index];
            if (parameter.LocalSymbol is not { } local)
                continue;
            state.Set(local, argumentRegions is not null && index < argumentRegions.Count
                ? argumentRegions[index]
                : GetValueRegion(argument, state, durableContext));
            state.SetPath(local, argumentPaths is not null && index < argumentPaths.Count
                ? argumentPaths[index]
                : GetOwnerPath(argument, state));
            state.SetAllocationArenaDepth(
                local, GetAllocationArenaDepth(argument, state, durableContext));
        }

        VisitStatements(declaration.Body, state, durableContext, activeFunctions);
        var result = returnAccumulator.Result;
        state.ThisRegion = previousThisRegion;
        state.ThisPath = previousThisPath;
        state.ThisArenaDepth = previousThisArenaDepth;
        state.CurrentReturnAccumulator = previousReturnAccumulator;
        activeFunctions.Remove(declaration);
        return result;
    }

    private static void VisitConstructor(
        ObjectCreationExpression creation,
        AnalysisState state,
        bool durableContext,
        HashSet<FunctionDeclaration> activeFunctions)
    {
        var declaration = creation.Constructor?.Declaration;
        if (creation.ClassType is not (ClassType.Class or ClassType.Struct))
        {
            return;
        }

        if (declaration?.Body is null || declaration.NativeImport is not null)
        {
            var hasTrustedNoEscape = declaration?.NativeImport?.NoEscape == true;
            if (!hasTrustedNoEscape)
            {
                foreach (var argument in creation.Arguments)
                {
                    if (MayContainArenaReference(argument, state, durableContext))
                    {
                        throw new CompilationErrorException(
                            $"{OpaqueCallEscapeMessage} ({declaration?.FullName ?? "constructor"})",
                            argument.SourceSpan);
                    }
                }
            }
            return;
        }

        if (activeFunctions.Contains(declaration))
            return;
        if (activeFunctions.Count >= MaxInlinedCallDepth)
        {
            throw new CompilationErrorException(
                $"{CallDepthEscapeMessage} ({declaration.FullName})");
        }
        activeFunctions.Add(declaration);

        var previousThisRegion = state.ThisRegion;
        var previousThisPath = state.ThisPath;
        var previousThisArenaDepth = state.ThisArenaDepth;
        var previousReturnAccumulator = state.CurrentReturnAccumulator;
        state.CurrentReturnAccumulator = null;
        var followsLinkedListOwner = IsLinkedListNodeOwnerAllocation(
            creation, activeFunctions);
        state.ThisRegion = creation.ClassType == ClassType.Class
            ? followsLinkedListOwner
                ? previousThisRegion
                : durableContext ? Region.Durable : Region.Arena
            : Region.None;
        state.ThisArenaDepth = creation.ClassType == ClassType.Class
            ? followsLinkedListOwner
                ? previousThisArenaDepth
                : durableContext ? 0 : state.ArenaDepth
            : null;
        state.ThisPath = new StoragePath(
            new LocalVariableSymbol(
                creation.ClassType == ClassType.Struct
                    ? "__cx_constructed_value" : "__cx_constructed_object",
                creation.RequestedType),
            "",
            Captured: false);
        if (creation.ClassType == ClassType.Struct &&
            declaration.ParentClassDeclaration?.MemberDeclarations.Declarations
                .OfType<FieldDeclaration>()
                .Where(field => field.Initializer is not null)
                .All(field => !IsReferenceBearingValueType(field.Type, state)) == true)
        {
            state.MarkAggregateContentsKnown(state.ThisPath.Value);
        }

        foreach (var (parameter, argument) in declaration.Parameters.Zip(creation.Arguments))
        {
            if (parameter.LocalSymbol is not { } local)
                continue;
            state.Set(local, GetValueRegion(argument, state, durableContext));
            state.SetPath(local, GetOwnerPath(argument, state));
            state.SetAllocationArenaDepth(
                local, GetAllocationArenaDepth(argument, state, durableContext));
        }

        VisitStatements(declaration.Body, state, durableContext, activeFunctions);
        var resultRegion = state.ThisRegion;
        if (creation.ClassType == ClassType.Struct)
            resultRegion |= state.GetContainedRegion(state.ThisPath.Value);
        state.SetResult(creation, new CallResult(resultRegion, state.ThisPath));
        state.ThisRegion = previousThisRegion;
        state.ThisPath = previousThisPath;
        state.ThisArenaDepth = previousThisArenaDepth;
        state.CurrentReturnAccumulator = previousReturnAccumulator;
        activeFunctions.Remove(declaration);
    }

    private static bool MayContainArenaReference(
        ExpressionBase expression,
        AnalysisState state,
        bool durableContext)
    {
        if ((GetRegion(expression, state, durableContext) & Region.Arena) != 0)
            return true;
        if (expression is LambdaExpression lambda && lambda.Captures.Any(capture =>
            (state.Get(capture.Local) & Region.Arena) != 0))
        {
            return true;
        }

        if (GetReferenceBearingValueStructType(expression.InferredType, state) is not { } valueType)
        {
            return false;
        }

        if (GetOwnerPath(expression, state) is not { } owner)
            return true;
        if ((state.GetContainedRegion(owner) & Region.Arena) != 0)
            return true;
        return !HasCompleteAggregateContents(valueType, owner, state);
    }

    private static Region GetValueRegion(
        ExpressionBase expression,
        AnalysisState state,
        bool durableContext)
    {
        var region = GetRegion(expression, state, durableContext);
        if (GetReferenceBearingValueStructType(expression.InferredType, state) is not { } valueType)
        {
            return region;
        }

        if (GetOwnerPath(expression, state) is not { } owner)
            return region | Region.Arena | Region.Durable;

        var contained = state.GetContainedRegion(owner);
        if (!HasCompleteAggregateContents(valueType, owner, state))
            contained |= Region.Arena | Region.Durable;
        return region | contained;
    }

    private static bool HasCompleteAggregateContents(
        NamedType valueType,
        StoragePath owner,
        AnalysisState state,
        HashSet<string>? visiting = null)
    {
        if (state.IsAggregateContentsKnown(owner))
            return true;

        var identity = valueType.ResolvedTypeFullName.ToString() + "<" +
            string.Join(",", valueType.TypeArguments.Select(argument => argument.ToString())) + ">";
        visiting ??= new HashSet<string>(StringComparer.Ordinal);
        if (!visiting.Add(identity))
            return false;

        var declaration = GetValueTypeDeclaration(valueType, state);
        if (declaration is null)
            return false;

        var substitutions = declaration.GenericTypeNames
            .Zip(valueType.TypeArguments)
            .ToDictionary(pair => pair.First, pair => pair.Second,
                StringComparer.Ordinal);
        foreach (var field in declaration.MemberDeclarations.Declarations
            .OfType<FieldDeclaration>().Where(field => !field.IsStatic))
        {
            var fieldType = GenericTypeSubstitution.Substitute(field.Type, substitutions);
            if (!IsReferenceBearingValueType(fieldType, state))
                continue;

            var fieldPath = owner.Append(
                $"field:{declaration.ProjectName ?? ""}:{field.FullName}");
            if (UnwrapConst(fieldType) is NamedType { ClassType: ClassType.Struct } nested)
            {
                if (!HasCompleteAggregateContents(nested, fieldPath, state, visiting))
                    return false;
                continue;
            }

            if (fieldType is FixedArrayType)
                return false;
            if (!state.IsAggregateSlotKnown(fieldPath))
                return false;
            if (fieldType is ArrayType && !state.IsArrayContentsKnown(fieldPath))
                return false;
        }

        return true;
    }

    private static ClassDeclaration? GetValueTypeDeclaration(
        NamedType valueType,
        AnalysisState state)
    {
        var parts = valueType.ResolvedTypeFullName.Parts;
        return parts.Length < 2
            ? null
            : state.KnownTypes.FirstOrDefault(candidate =>
                candidate.FullName == new QualifiedIdentifier(parts.Skip(1).ToArray()) &&
                (candidate.ProjectName is null || candidate.ProjectName == parts[0]));
    }

    private static bool IsReferenceBearingValueType(
        TypeBase type,
        AnalysisState state,
        HashSet<string>? visiting = null)
    {
        switch (type)
        {
            case ConstType constant:
                return IsReferenceBearingValueType(constant.UnderlyingType, state, visiting);
            case NullableType nullable:
                return IsReferenceBearingValueType(nullable.UnderlyingType, state, visiting);
            case ArrayType:
            case ObjectType:
            case StringType:
            case FunctionType:
            case PtrType:
            case GenericType:
                return true;
            case NamedType named when named.IsDelegate ||
                named.ClassType is ClassType.Class or ClassType.Interface:
                return true;
            case NamedType { ClassType: ClassType.Struct } named:
            {
                var identity = named.ResolvedTypeFullName.ToString() + "<" +
                    string.Join(",", named.TypeArguments.Select(argument => argument.ToString())) + ">";
                visiting ??= new HashSet<string>(StringComparer.Ordinal);
                if (!visiting.Add(identity))
                    return true;

                var declaration = GetValueTypeDeclaration(named, state);
                if (declaration is null)
                    return true;

                var substitutions = declaration.GenericTypeNames
                    .Zip(named.TypeArguments)
                    .ToDictionary(pair => pair.First, pair => pair.Second,
                        StringComparer.Ordinal);
                return declaration.MemberDeclarations.Declarations
                    .OfType<FieldDeclaration>()
                    .Where(field => !field.IsStatic)
                    .Select(field => GenericTypeSubstitution.Substitute(
                        field.Type, substitutions))
                    .Any(fieldType => IsReferenceBearingValueType(
                        fieldType, state, visiting));
            }
            case FixedArrayType fixedArray:
                return IsReferenceBearingValueType(fixedArray.ElementType, state, visiting);
            default:
                return false;
        }
    }

    private static NamedType? GetReferenceBearingValueStructType(
        TypeBase? type,
        AnalysisState state)
    {
        while (type is ConstType constant)
            type = constant.UnderlyingType;
        while (type is NullableType nullable)
            type = nullable.UnderlyingType;
        return type is NamedType { ClassType: ClassType.Struct } valueType &&
            IsReferenceBearingValueType(valueType, state)
                ? valueType
                : null;
    }

    private static bool MayContainArenaArrayReference(
        ExpressionBase expression,
        AnalysisState state)
    {
        if (expression is LiteralExpression { SourceText: "null" })
            return false;

        if (GetOwnerPath(expression, state) is not { } owner ||
            !state.IsArrayContentsKnown(owner))
        {
            return true;
        }

        return (state.GetElements(owner, index: null) & Region.Arena) != 0;
    }

    private static int? GetReflectionArgumentArrayIndex(InvocationExpression invocation)
    {
        if (invocation.IsDelegateDynamicInvoke)
            return invocation.Arguments.Count == 0 ? null : 0;

        var target = invocation.TargetSymbol;
        if (target?.ModuleName != "cxcore" || target.FullName.Parts.Length < 4 ||
            target.FullName.Parts[^3] != "Reflection" ||
            target.FullName.Parts[^2] != "TypeInfo")
        {
            return null;
        }

        return target.FullName.Parts[^1] switch
        {
            "Invoke" or "InvokeFunction" => 2,
            "InvokeConstructor" => 1,
            "CreateCollectionFromSequence" => 0,
            "CreateInstance" when invocation.Arguments.Count == 1 => 0,
            _ => null,
        };
    }

    private static bool IsOpaqueCallBoundary(
        InvocationExpression invocation,
        AnalysisState state,
        HashSet<FunctionDeclaration> activeFunctions)
    {
        if (IsDeferredCall(invocation) &&
            !(IsAsyncCall(invocation) && state.AsyncArenaScopeDepth != 0))
            return true;

        if (invocation.TargetSymbol?.NativeImport is not null ||
            invocation.IsDelegateDynamicInvoke ||
            GetReflectionArgumentArrayIndex(invocation) is not null ||
            HasNoEscapeContract(invocation.TargetSymbol?.Declaration) ||
            IsKnownNonRetainingCoreCall(invocation) ||
            IsRetainedTaskContinuation(invocation, state) ||
            IsTaskHandleWhenAllContinuation(invocation, activeFunctions) ||
            IsGenericTaskContinuationRelay(invocation, state, activeFunctions) ||
            IsRetainedAsyncQueueCall(invocation, state) ||
            IsJoinedTaskAggregateCall(invocation, state) ||
            IsDurableArenaHandleCall(invocation) ||
            IsMemoryMethod(invocation, "WithArena") ||
            IsMemoryMethod(invocation, "WithArenaAsync") ||
            IsDurableAllocationContext(invocation) ||
            IsMemoryMethod(invocation, "Alloc") ||
            IsMemoryMethod(invocation, "Realloc") ||
            IsMemoryMethod(invocation, "Free") ||
            IsMemoryMethod(invocation, "Copy"))
        {
            return false;
        }

        // A locally known implementation is not enough to prove safety for a
        // virtual/interface call. Other modules may provide implementations
        // that are not present in this compilation, so callers need a contract
        // on the dispatch slot before arena-scoped values can cross it.
        if (IsOpenDispatch(invocation))
            return true;

        if (GetCalleeDeclarations(
            invocation, state, new HashSet<FunctionDeclaration>()) is { Count: > 0 })
            return false;

        return invocation.TargetSymbol?.Declaration?.Body is null;
    }

    private static bool IsOpenDispatch(InvocationExpression invocation) =>
        invocation.DispatchSlotIndex is not null ||
        invocation.TargetSymbol?.Declaration?.VirtualSlotIndex is not null;

    private static bool IsOpenProperty(
        PropertySymbol property,
        PropertyAccessorDeclaration? accessor)
    {
        if (accessor is null)
            return true;

        return accessor.ParentPropertyDeclaration.MemberModifiers.Any(modifier =>
            modifier is MemberModifier.Abstract or MemberModifier.Virtual or MemberModifier.Override);
    }

    private static bool IsKnownNonRetainingCoreProperty(
        PropertySymbol property,
        PropertyAccessorDeclaration? accessor)
    {
        if (property.ModuleName != "cxcore" || accessor?.Name == "set")
            return false;

        var parts = property.FullName.Parts;
        return parts.Length >= 3 &&
            (parts[^2] == "Array" && (parts[^1] is "Length" or "Count") &&
                parts[^3] == "Collections" ||
             parts[^2] == "NativeBuffer" && parts[^1] is "ByteLength" or "Data" &&
                parts[^3] == "Interop" ||
             parts[^2] == "String" && parts[^1] == "Length" &&
                parts[^3] == "System");
    }

    private static bool IsDeferredCall(InvocationExpression invocation) =>
        invocation.TargetSymbol?.Declaration is { } declaration &&
        (declaration.IsAsync || declaration.IsIteratorFactory);

    private static bool IsAsyncCall(InvocationExpression invocation) =>
        invocation.TargetSymbol?.Declaration?.IsAsync == true;

    private static bool HasNoEscapeContract(FunctionDeclaration? declaration)
    {
        var visited = new HashSet<FunctionDeclaration>();
        for (var contract = declaration;
            contract is not null && visited.Add(contract);
            contract = contract.VirtualContract)
        {
            if (contract.Annotations.Any(annotation =>
                annotation.ResolvedTypeName == "cxcore.System.Interop.NoEscape"))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsGenericTaskContinuationRelay(
        InvocationExpression invocation,
        AnalysisState state,
        HashSet<FunctionDeclaration> activeFunctions)
    {
        var target = invocation.TargetSymbol;
        return state.AsyncArenaScopeDepth > 0 &&
            state.JoinedAsyncBodyDepth > 0 &&
            target?.ModuleName == "cxcore" &&
            target.FullName.Parts.Length >= 4 &&
            target.FullName.Parts[^4] == "Threading" &&
            target.FullName.Parts[^3] == "Tasks" &&
            target.FullName.Parts[^2] == "TaskRuntime" &&
            target.FullName.Parts[^1] == "OnCompleted" &&
            activeFunctions.Any(function =>
                function.ParentClassDeclaration is
                {
                    ProjectName: "cxcore",
                    Name: "Task",
                });
    }

    private static bool IsKnownNonRetainingCoreCall(InvocationExpression invocation)
    {
        var target = invocation.TargetSymbol;
        if (target?.ModuleName != "cxcore" || target.FullName.Parts.Length < 2)
            return false;

        if (target.FullName.Parts[^2] == "Object" &&
            target.FullName.Parts[^1] is
                "ReferenceEquals" or "IdentityHashCode" or "GetHashCode" or "Equals")
        {
            return true;
        }

        return target.FullName.Parts[^2] == "StringBuilder" &&
            target.FullName.Parts[^1] is "Append" or "AppendLine";
    }

    private static bool IsTaskVoidResultObservation(InvocationExpression invocation)
    {
        var target = invocation.TargetSymbol;
        return target?.ModuleName == "cxcore" &&
            target.FullName.Parts.Length >= 4 &&
            target.FullName.Parts[^4] == "Threading" &&
            target.FullName.Parts[^3] == "Tasks" &&
            target.FullName.Parts[^2] is "TaskHandle" or "VoidTask" &&
            target.FullName.Parts[^1] == "GetResult" &&
            invocation.Arguments.Count == 0;
    }

    private static bool IsRetainedAsyncQueueCall(
        InvocationExpression invocation,
        AnalysisState state)
    {
        var target = invocation.TargetSymbol;
        if (state.AsyncArenaScopeDepth == 0 || target?.ModuleName != "cxcore" ||
            target.FullName.Parts.Length < 4 ||
            target.FullName.Parts[^4] != "Threading" ||
            target.FullName.Parts[^3] != "Tasks")
        {
            return false;
        }

        return target.FullName.Parts[^2] switch
        {
            "TaskScheduler" => state.JoinedTaskFactoryDepth > 0 &&
                target.FullName.Parts[^1] is "Post" or "PostAfter",
            "TaskRuntime" => target.FullName.Parts[^1] is
                "ScheduleContinuations" or "ScheduleReusableSourceContinuation" or
                "StoreCancellationCallback" ||
                state.JoinedAsyncBodyDepth > 0 &&
                target.FullName.Parts[^1] is "SetFault" or "SetCanceled" ||
                state.JoinedAsyncBodyDepth > 0 &&
                invocation.SourceSpan is null &&
                target.FullName.Parts[^1] is
                    "ForwardVoid" or "GetVoidTask",
            _ => false,
        };
    }

    private static bool IsJoinedTaskAggregateCall(
        InvocationExpression invocation,
        AnalysisState state)
        => state.AsyncArenaScopeDepth > 0 &&
            state.IsJoinedAsyncCall(invocation) &&
            IsTaskHandleWhenAllInvocation(invocation);

    private static bool IsTaskHandleWhenAllInvocation(InvocationExpression invocation) =>
        invocation.TargetSymbol is { ModuleName: "cxcore" } target &&
        target.FullName.Parts.Length >= 2 &&
        target.FullName.Parts[^2] is "Task" or "TaskHandle" &&
        target.FullName.Parts[^1] == "WhenAll" &&
        ((invocation.Arguments.Count == 2 && target.ParameterTypes.Count == 2) ||
         (invocation.Arguments.Count == 1 && target.ParameterTypes is
             [ArrayType
             {
                 ElementType: NamedType
                 {
                     Name: "TaskHandle",
                 },
             }]));

    private static IEnumerable<SourceSpan> EnumerateJoinedTaskAggregateChildSpans(
        FunctionDeclaration function)
    {
        if (function.Body is null)
            yield break;

        var provenAggregateSpans = CoroutineSyntaxAnalysis
            .EnumerateBodyExpressions(function.Body)
            .OfType<InvocationExpression>()
            .Where(IsTaskHandleWhenAllInvocation)
            .Select(invocation => invocation.SourceSpan)
            .OfType<SourceSpan>()
            .ToHashSet();
        foreach (var aggregate in function.JoinedTaskAggregateChildCallSpans)
        {
            if (provenAggregateSpans.Contains(aggregate.Key))
            {
                foreach (var childSpan in aggregate.Value)
                    yield return childSpan;
            }
        }
    }

    private static bool IsRetainedTaskContinuation(
        InvocationExpression invocation,
        AnalysisState state)
    {
        var target = invocation.TargetSymbol;
        return state.AsyncArenaScopeDepth > 0 &&
            state.JoinedAsyncBodyDepth > 0 &&
            invocation.SourceSpan is null &&
            target?.ModuleName == "cxcore" &&
            target.FullName.Parts.Length >= 4 &&
            target.FullName.Parts[^4] == "Threading" &&
            target.FullName.Parts[^3] == "Tasks" &&
            target.FullName.Parts[^2] is "TaskHandle" or "Task" or "VoidTask" &&
            target.FullName.Parts[^1] == "OnCompleted";
    }

    private static bool IsTaskHandleWhenAllContinuation(
        InvocationExpression invocation,
        HashSet<FunctionDeclaration> activeFunctions)
    {
        var target = invocation.TargetSymbol;
        return target?.ModuleName == "cxcore" &&
            target.FullName.Parts.Length >= 4 &&
            target.FullName.Parts[^4] == "Threading" &&
            target.FullName.Parts[^3] == "Tasks" &&
            target.FullName.Parts[^2] is "Task" or "TaskHandle" &&
            target.FullName.Parts[^1] == "OnCompleted" &&
            activeFunctions.Any(function =>
                function.Name == "Start" &&
                function.ParentClassDeclaration is
                {
                    ProjectName: "cxcore",
                } parent && parent.FullName.ToString() ==
                    "System.Threading.Tasks.TaskHandleWhenAllState");
    }

    private static bool IsJoinedTaskFactoryCall(
        InvocationExpression invocation,
        AnalysisState state)
    {
        var target = invocation.TargetSymbol;
        return state.AsyncArenaScopeDepth > 0 &&
            state.IsJoinedAsyncCall(invocation) &&
            target?.ModuleName == "cxcore" &&
            target.FullName.Parts.Length >= 4 &&
            target.FullName.Parts[^4] == "Threading" &&
            target.FullName.Parts[^3] == "Tasks" &&
            target.FullName.Parts[^2] == "TaskHandle" &&
            target.FullName.Parts[^1] == "Delay";
    }

    private static bool IsDurableArenaHandleCall(InvocationExpression invocation)
    {
        var target = invocation.TargetSymbol;
        return target?.ModuleName == "cxcore" &&
            target.FullName.Parts.Length >= 4 &&
            target.FullName.Parts[^4] == "Threading" &&
            target.FullName.Parts[^3] == "Tasks" &&
            target.FullName.Parts[^2] == "TaskRuntime" &&
            target.FullName.Parts[^1] is
                "CaptureCancellationCallbackArena" or "CaptureRetainedArena";
    }

    private static IReadOnlyList<int> GetCollectionStoredArgumentIndices(
        InvocationExpression invocation)
    {
        var target = invocation.TargetSymbol;
        if (target?.ModuleName != "cxcore" || target.FullName.Parts.Length < 2)
            return [];

        var owner = target.FullName.Parts[^2];
        var method = target.FullName.Parts[^1];
        return (owner, method) switch
        {
            ("Array" or "List", "Set") => [1],
            ("Array", "TrySet") => [1],
            ("Array", "Fill") => [0],
            ("List" or "ICollection", "Add") => [0],
            ("List", "Insert" or "TryInsert" or "TrySet") => [1],
            ("Dictionary", "Set" or "TryAdd") => [0, 1],
            ("Queue", "Enqueue") => [0],
            ("Stack", "Push") => [0],
            ("LinkedList", "AddFirst" or "AddLast") => [0],
            _ => [],
        };
    }

    private static int? TryGetCollectionCopyDestinationIndex(
        InvocationExpression invocation)
    {
        if (IsCollectionMethod(invocation, "List", "CopyTo", "TryCopyTo") ||
            IsCollectionMethod(invocation, "Array", "TryCopyTo"))
        {
            return invocation.Arguments.Count == 0 ? null : 0;
        }

        return null;
    }

    private static bool IsModeledCollectionCall(InvocationExpression invocation) =>
        GetCollectionStoredArgumentIndices(invocation).Count > 0 ||
        TryGetCollectionCopyDestinationIndex(invocation) is not null ||
        IsCollectionMethod(invocation, "List", "AddRange") ||
        IsCollectionMethod(invocation, "List", "ToArray");

    private static bool IsCollectionReadCall(InvocationExpression invocation) =>
        invocation.TargetSymbol?.ModuleName == "cxcore" &&
        invocation.TargetSymbol.FullName.Parts.Length >= 2 &&
        (invocation.TargetSymbol.FullName.Parts[^2],
            invocation.TargetSymbol.FullName.Parts[^1]) is
            ("Array", "Get") or
            ("List", "Get") or
            ("List", "GetOrDefault") or
            ("Dictionary", "GetValueOrDefault") or
            ("Queue", "Peek") or
            ("Queue", "Dequeue") or
            ("Stack", "Peek") or
            ("Stack", "Pop") or
            ("LinkedList", "RemoveFirst") or
            ("LinkedList", "RemoveLast");

    private static bool IsCollectionClearCall(InvocationExpression invocation) =>
        invocation.TargetSymbol?.ModuleName == "cxcore" &&
        invocation.TargetSymbol.FullName.Parts.Length >= 2 &&
        invocation.TargetSymbol.FullName.Parts[^2] is
            "Array" or "List" or "Dictionary" or "Queue" or "Stack" or "LinkedList" &&
        invocation.TargetSymbol.FullName.Parts[^1] == "Clear";

    private static bool IsCoreCollectionType(TypeBase type) =>
        UnwrapConst(type) is NamedType named &&
        named.ResolvedTypeFullName.Parts.Length >= 3 &&
        named.ResolvedTypeFullName.Parts[^2] == "Collections" &&
        named.ResolvedTypeFullName.Parts[^1] is
            "Array" or "List" or "Dictionary" or "Queue" or "Stack" or "LinkedList";

    private static bool IsCollectionEnumeratorType(TypeBase? type) =>
        type is not null && UnwrapConst(type) is NamedType named &&
        named.ResolvedTypeFullName.Parts.Length >= 3 &&
        named.ResolvedTypeFullName.Parts[^2] == "Collections" &&
        named.ResolvedTypeFullName.Parts[^1] is
            "ArrayEnumerator" or "LinkedListEnumerator" or "DictionaryEnumerator";

    private static bool IsCollectionMethod(
        InvocationExpression invocation,
        string owner,
        string method) =>
        invocation.TargetSymbol is { ModuleName: "cxcore" } target &&
        target.FullName.Parts.Length >= 2 &&
        target.FullName.Parts[^2] == owner &&
        target.FullName.Parts[^1] == method;

    private static bool IsCollectionMethod(
        InvocationExpression invocation,
        string owner,
        string method1,
        string method2) =>
        IsCollectionMethod(invocation, owner, method1) ||
        IsCollectionMethod(invocation, owner, method2);

    private static bool IsRuntimeCheckedCoreCollectionStore(
        InvocationExpression invocation,
        HashSet<FunctionDeclaration> activeFunctions) =>
        IsCollectionMethod(invocation, "Array", "Set") &&
        activeFunctions.Any(function =>
            function.ParentClassDeclaration is
            {
                ProjectName: "cxcore",
                Namespace.Parts: ["System", "Collections"],
                Name: "Array" or "List" or "Dictionary" or "Queue" or
                    "Stack" or "LinkedList",
            });

    private static bool MayContainArenaCollectionContents(
        ExpressionBase collection,
        AnalysisState state,
        bool durableContext)
    {
        if (GetOwnerPath(collection, state) is { } owner &&
            state.TryGetCollectionContents(owner, out var contents))
        {
            return (contents & Region.Arena) != 0;
        }

        var region = GetRegion(collection, state, durableContext);
        return (region & Region.Arena) != 0 || region == Region.None;
    }

    private static bool IsPotentiallyDurableCollectionOwner(
        ExpressionBase receiver,
        AnalysisState state,
        bool durableContext)
    {
        if (receiver is IdentifierExpression
            {
                TargetField: { Declaration.IsStatic: false } implicitField,
            } && state.ThisPath is { } thisPath)
        {
            var fieldRegion = state.Get(new FieldSlot(thisPath, implicitField));
            if ((fieldRegion & Region.Arena) != 0)
                return false;
            if ((fieldRegion & Region.Durable) != 0)
                return true;
        }

        var region = GetRegion(receiver, state, durableContext);
        if ((region & Region.Durable) != 0)
            return true;
        if ((region & Region.Arena) != 0)
            return false;

        return receiver switch
        {
            IdentifierExpression { TargetLocal: not null } => true,
            IdentifierExpression { TargetCapture: not null } => true,
            IdentifierExpression { TargetField.Declaration.IsStatic: true } => true,
            MemberAccessExpression { TargetField.Declaration.IsStatic: true } => true,
            MemberAccessExpression { TargetField: not null } field =>
                IsPotentiallyDurableCollectionOwner(field.Target, state, durableContext),
            MemberAccessExpression { TargetProperty: not null } property =>
                IsPotentiallyDurableCollectionOwner(property.Target, state, durableContext),
            // A collection loaded through an expression we cannot classify may
            // be durable. Reject scoped elements instead of assuming ownership
            // follows an unknown receiver path.
            _ => true,
        };
    }

    private static TypeBase UnwrapConst(TypeBase type) =>
        type is ConstType constant ? UnwrapConst(constant.UnderlyingType) : type;

    private static Region GetRegion(
        ExpressionBase expression,
        AnalysisState state,
        bool durableContext) => expression switch
    {
        ArrayCreationExpression => durableContext ? Region.Durable : Region.Arena,
        ObjectCreationExpression creation => state.GetResult(creation).Region,
        IdentifierExpression { TargetLocal: { } local } => state.Get(local),
        IdentifierExpression { TargetCapture: { } capture } => state.Get(capture.Local),
        IdentifierExpression
            { TargetField: { ModuleName: not "cxcore", Declaration.IsStatic: false } field }
            when state.ThisPath is { } thisPath =>
                state.Get(new FieldSlot(thisPath, field)),
        IdentifierExpression { TargetMethodValue: { } methodValue } =>
            GetRegion(methodValue, state, durableContext),
        IdentifierExpression { FunctionValueSymbol: { } functionValue } =>
            GetLocalFunctionValueRegion(functionValue, state, durableContext),
        AssignmentExpression assignment => state.GetResult(assignment).Region,
        BinaryExpression binary => state.GetResult(binary).Region,
        IdentifierExpression { TargetProperty: { } property } identifier =>
            state.GetResult(identifier).Region |
            (!property.IsStatic && state.ThisPath is { } thisPath
                ? state.Get(new PropertySlot(thisPath, property))
                : Region.None),
        ThisExpression { TargetCapture: { } capture } => state.Get(capture.Local),
        ThisExpression => state.ThisRegion,
        CastExpression cast => GetRegion(cast.Operand, state, durableContext),
        UnaryExpression unary => state.GetResult(unary).Region,
        ConditionalExpression conditional =>
            GetRegion(conditional.WhenTrue, state, durableContext) |
            GetRegion(conditional.WhenFalse, state, durableContext),
        NullCoalescingExpression coalescing =>
            GetRegion(coalescing.Left, state, durableContext) |
            GetRegion(coalescing.Right, state, durableContext),
        SwitchExpression selection => selection.Arms.Aggregate(Region.None,
            (region, arm) => region | GetRegion(arm.Value, state, durableContext)),
        LambdaExpression lambda => GetLambdaRegion(lambda, state, durableContext),
        MemberAccessExpression { FunctionValueSymbol: not null } member =>
            (member.FunctionValueEnvironmentName is null
                ? Region.None
                : (durableContext ? Region.Durable : Region.Arena) |
                    GetRegion(member.Target, state, durableContext)),
        InvocationExpression invocation when IsMemoryMethod(invocation, "Alloc") =>
            durableContext ? Region.Durable : Region.Arena,
        InvocationExpression invocation when IsDurableArenaHandleCall(invocation) =>
            Region.Durable,
        InvocationExpression invocation when IsOwnerAlignedArrayCreation(invocation) &&
            invocation.Receiver is { } receiver =>
                GetRegion(receiver, state, durableContext),
        InvocationExpression invocation when IsMemoryMethod(invocation, "Realloc") &&
            invocation.Arguments.Count > 0 =>
                GetRegion(invocation.Arguments[0], state, durableContext),
        InvocationExpression invocation when
            IsKnownNonRetainingCoreCall(invocation) && invocation.Receiver is { } receiver =>
                GetRegion(receiver, state, durableContext),
        InvocationExpression invocation => GetInvocationRegion(invocation, state),
        MemberAccessExpression { TargetField: { } field } member when
            GetOwnerPath(member.Target, state) is { } owner => state.Get(new FieldSlot(owner, field)),
        MemberAccessExpression { TargetProperty: { } property } member =>
            state.GetResult(member).Region |
            (GetOwnerPath(member.Target, state) is { } owner
                ? state.Get(new PropertySlot(owner, property))
                : Region.None),
        ArrayAccessExpression { TargetProperty: not null } array =>
            state.GetResult(array).Region,
        ArrayAccessExpression array when GetOwnerPath(array.Target, state) is { } owner =>
            state.GetElements(owner, GetArrayIndex(array)),
        _ => Region.None,
    };

    private static Region GetLocalFunctionValueRegion(
        FunctionSymbol function,
        AnalysisState state,
        bool durableContext)
    {
        var declaration = function.ImplementationDeclaration ?? function.Declaration;
        if (declaration?.LambdaOwner is not { Captures.Count: > 0 } closure)
            return Region.None;

        var region = durableContext ? Region.Durable : Region.Arena;
        if (closure.Captures.Any(capture =>
            (state.Get(capture.Local) & Region.Arena) != 0))
        {
            region |= Region.Arena;
        }
        return region;
    }

    private static Region GetInvocationRegion(
        InvocationExpression invocation,
        AnalysisState state)
    {
        var result = state.GetResult(invocation);
        return result.Region |
            (HasUnknownCallResult(invocation, state)
                ? Region.Arena | Region.Durable
                : Region.None);
    }

    private static bool HasUnknownCallResult(
        InvocationExpression invocation,
        AnalysisState state)
    {
        var returnType = invocation.TargetSymbol?.ReturnType ??
            invocation.FunctionType?.ReturnType;
        if (returnType is NamedType { ClassType: ClassType.Enum })
            return false;
        if (returnType is not (ObjectType or NamedType or PtrType or FunctionType or NullableType))
            return false;

        if (returnType is PtrType &&
            invocation.TargetSymbol?.Declaration?.Annotations.Any(annotation =>
                annotation.ResolvedTypeName ==
                    "cxcore.System.Interop.DurablePointerReturn") == true)
            return false;

        if (IsDeferredCall(invocation))
            return true;

        if (IsOpenDispatch(invocation))
            return true;

        return invocation.TargetSymbol?.Declaration?.Body is null &&
            !IsMemoryMethod(invocation, "Alloc") &&
            !IsMemoryMethod(invocation, "Realloc") &&
            !IsDurableArenaHandleCall(invocation) &&
            !IsKnownNonRetainingCoreCall(invocation) &&
            GetCalleeDeclarations(
                invocation, state, new HashSet<FunctionDeclaration>()).Count == 0;
    }

    private static Region GetLambdaRegion(
        LambdaExpression lambda,
        AnalysisState state,
        bool durableContext)
    {
        var region = lambda.Captures.Count == 0
            ? Region.None
            : durableContext ? Region.Durable : Region.Arena;
        if (lambda.Captures.Any(capture => (state.Get(capture.Local) & Region.Arena) != 0))
            region |= Region.Arena;
        return region;
    }

    private static void SetSlotRegion(
        ExpressionBase target,
        Region region,
        AnalysisState state)
    {
        switch (target)
        {
            case IdentifierExpression { TargetField: { Declaration.IsStatic: false } field }
                when state.ThisPath is { } thisPath:
                state.Set(new FieldSlot(thisPath, field), region);
                break;
            case MemberAccessExpression { TargetField: { } field } member
                when GetOwnerPath(member.Target, state) is { } owner:
                state.MarkAggregateSlotKnown(owner.Append(
                    $"field:{field.ModuleName}:{field.Declaration.FullName}"));
                state.Set(new FieldSlot(owner, field), region);
                break;
            case MemberAccessExpression { TargetProperty: { } property } member
                when GetOwnerPath(member.Target, state) is { } owner:
                state.Set(new PropertySlot(owner, property), region);
                break;
            case ArrayAccessExpression array
                when GetOwnerPath(array.Target, state) is { } owner:
                state.Set(new ArraySlot(owner, GetArrayIndex(array)), region);
                break;
        }
    }

    private static void SetAssignmentSlotRegion(
        AssignmentExpression assignment,
        Region region,
        AnalysisState state)
    {
        if (assignment.TargetProperty is { } property)
        {
            var propertyOwner = assignment.Target switch
            {
                MemberAccessExpression member => GetOwnerPath(member.Target, state),
                IdentifierExpression when !property.IsStatic => state.ThisPath,
                _ => null,
            };
            if (propertyOwner is { } owner)
                state.Set(new PropertySlot(owner, property), region);
            return;
        }
        SetSlotRegion(assignment.Target, region, state);
    }

    private static StoragePath? GetOwnerPath(ExpressionBase expression, AnalysisState state) => expression switch
    {
        IdentifierExpression { TargetLocal: { } local } => state.GetPath(local) ??
            (state.IsPathUnknown(local) ? null : new StoragePath(local, "", Captured: false)),
        IdentifierExpression { TargetCapture: { } capture } => state.GetPath(capture.Local) ??
            (state.IsPathUnknown(capture.Local) ? null : new StoragePath(capture.Local, "", Captured: true)),
        ThisExpression { TargetCapture: { } capture } => state.GetPath(capture.Local) ??
            (state.IsPathUnknown(capture.Local) ? null : new StoragePath(capture.Local, "", Captured: true)),
        ThisExpression => state.ThisPath,
        IdentifierExpression
            { TargetField: { ModuleName: not "cxcore", Declaration.IsStatic: false } field }
            when state.ThisPath is { } thisPath =>
                thisPath.Append($"field:{field.ModuleName}:{field.Declaration.FullName}"),
        InvocationExpression invocation => state.GetResult(invocation).Path,
        ObjectCreationExpression creation => state.GetResult(creation).Path,
        ArrayCreationExpression creation => state.GetResult(creation).Path,
        IdentifierExpression { TargetProperty: { } property } identifier =>
            state.GetResult(identifier).Path ??
            (!property.IsStatic && state.ThisPath is { } thisPath
                ? thisPath.Append($"property:{property.ModuleName}:{property.FullName}")
                : null),
        MemberAccessExpression { TargetField: { } field } member
            when GetOwnerPath(member.Target, state) is { } owner =>
                owner.Append($"field:{field.ModuleName}:{field.Declaration.FullName}"),
        MemberAccessExpression { TargetProperty: not null } member =>
            state.GetResult(member).Path ??
            (member.TargetProperty is { } property &&
                GetOwnerPath(member.Target, state) is { } owner
                    ? owner.Append($"property:{property.ModuleName}:{property.FullName}")
                    : null),
        ArrayAccessExpression { TargetProperty: not null } array =>
            state.GetResult(array).Path,
        ArrayAccessExpression array when GetOwnerPath(array.Target, state) is { } owner =>
            owner.Append($"array:{GetArrayIndex(array) ?? "*"}"),
        BinaryExpression binary => state.GetResult(binary).Path,
        AssignmentExpression assignment => state.GetResult(assignment).Path,
        UnaryExpression unary => state.GetResult(unary).Path,
        _ => null,
    };

    private static string? GetArrayIndex(ArrayAccessExpression array) =>
        array.Indices.Count == 1 && array.Indices[0] is LiteralExpression literal
            ? literal.SourceText
            : null;

    private static bool IsReferenceAllocation(ExpressionBase expression) => expression switch
    {
        ArrayCreationExpression => true,
        ObjectCreationExpression { ClassType: ClassType.Class } => true,
        _ => false,
    };

    private static LocalVariableSymbol? GetAssignedLocal(ExpressionBase target) => target switch
    {
        IdentifierExpression { TargetLocal: { } local } => local,
        IdentifierExpression { TargetCapture: { } capture } => capture.Local,
        _ => null,
    };

    private static int? GetAllocationArenaDepth(
        ExpressionBase expression,
        AnalysisState state,
        bool durableContext) => expression switch
    {
        ArrayCreationExpression or ObjectCreationExpression { ClassType: ClassType.Class } =>
            durableContext ? 0 : state.ArenaDepth,
        IdentifierExpression { TargetLocal: { } local } => state.GetAllocationArenaDepth(local),
        IdentifierExpression { TargetCapture: { } capture } =>
            state.GetAllocationArenaDepth(capture.Local),
        IdentifierExpression
            { TargetField: { ModuleName: not "cxcore", Declaration.IsStatic: false } field }
            when state.ThisPath is { } thisPath =>
                state.GetAllocationArenaDepth(new FieldSlot(thisPath, field)),
        IdentifierExpression { TargetProperty: not null } identifier =>
            state.GetResult(identifier).AllocationArenaDepth,
        ThisExpression { TargetCapture: { } capture } =>
            state.GetAllocationArenaDepth(capture.Local),
        ThisExpression => state.ThisArenaDepth,
        MemberAccessExpression { FunctionValueSymbol: not null } methodValue =>
            GetMethodValueAllocationArenaDepth(methodValue, state, durableContext),
        InvocationExpression invocation when IsOwnerAlignedArrayCreation(invocation) &&
            invocation.Receiver is { } receiver =>
                GetAllocationArenaDepth(receiver, state, durableContext),
        InvocationExpression invocation when !HasUnknownCallResult(invocation, state) =>
            state.GetResult(invocation).AllocationArenaDepth,
        MemberAccessExpression { TargetField: { } field } member
            when GetOwnerPath(member.Target, state) is { } owner =>
                state.GetAllocationArenaDepth(new FieldSlot(owner, field)),
        MemberAccessExpression { TargetProperty: { } property } member =>
            state.GetResult(member).AllocationArenaDepth ??
                (GetOwnerPath(member.Target, state) is { } owner
                    ? state.GetAllocationArenaDepth(new PropertySlot(owner, property))
                    : null),
        ArrayAccessExpression array
            when GetOwnerPath(array.Target, state) is { } owner =>
                state.GetResult(array).AllocationArenaDepth ??
                    state.GetAllocationArenaDepth(
                        new ArraySlot(owner, GetArrayIndex(array))),
        CastExpression cast =>
            GetAllocationArenaDepth(cast.Operand, state, durableContext),
        ConditionalExpression conditional => SameAllocationArenaDepth(
            GetAllocationArenaDepth(conditional.WhenTrue, state, durableContext),
            GetAllocationArenaDepth(conditional.WhenFalse, state, durableContext)),
        NullCoalescingExpression coalescing => SameAllocationArenaDepth(
            GetAllocationArenaDepth(coalescing.Left, state, durableContext),
            GetAllocationArenaDepth(coalescing.Right, state, durableContext)),
        AssignmentExpression assignment =>
            GetAllocationArenaDepth(assignment.Value, state, durableContext),
        BinaryExpression binary => state.GetResult(binary).AllocationArenaDepth,
        UnaryExpression unary => state.GetResult(unary).AllocationArenaDepth,
        _ => null,
    };

    private static int? GetMethodValueAllocationArenaDepth(
        MemberAccessExpression methodValue,
        AnalysisState state,
        bool durableContext)
    {
        if (methodValue.FunctionValueEnvironmentName is null)
            return 0;

        return UnwrapConst(methodValue.Target.InferredType ?? BuiltInSystemTypes.Object) is
            NamedType { ClassType: ClassType.Class or ClassType.Interface }
                ? GetAllocationArenaDepth(methodValue.Target, state, durableContext)
                : durableContext ? 0 : state.ArenaDepth;
    }

    private static int? SameAllocationArenaDepth(int? first, int? second) =>
        first is { } firstDepth && second == firstDepth ? firstDepth : null;

    private static bool IsOutlivedArenaOwnerStore(
        ExpressionBase target,
        ExpressionBase value,
        AnalysisState state,
        bool durableContext,
        PropertySymbol? targetProperty = null,
        ExpressionBase? propertyReceiver = null)
    {
        var destinationDepth = targetProperty is { IsStatic: true }
            ? 0
            : propertyReceiver is not null
                ? GetAllocationArenaDepth(propertyReceiver, state, durableContext)
                : target switch
        {
            IdentifierExpression { TargetField: { Declaration.IsStatic: false } } =>
                state.ThisArenaDepth,
            MemberAccessExpression { TargetField.Declaration.IsStatic: true } => 0,
            MemberAccessExpression { TargetField: not null } member =>
                GetAllocationArenaDepth(member.Target, state, durableContext),
            MemberAccessExpression { TargetProperty: not null } member =>
                GetAllocationArenaDepth(member.Target, state, durableContext),
            ArrayAccessExpression array =>
                GetAllocationArenaDepth(array.Target, state, durableContext),
            _ => null,
        };
        var valueDepth = GetAllocationArenaDepth(value, state, durableContext);
        if (destinationDepth is not { } ownerDepth)
            return false;

        var effectiveValueDepth = valueDepth ??
            (durableContext ? 0 : state.ArenaDepth);
        return effectiveValueDepth > ownerDepth;
    }

    private static void SetAssignmentSlotAllocationArenaDepth(
        ExpressionBase target,
        int? depth,
        AnalysisState state)
    {
        switch (target)
        {
            case IdentifierExpression { TargetField: { Declaration.IsStatic: false } field }
                when state.ThisPath is { } thisPath:
                state.SetAllocationArenaDepth(new FieldSlot(thisPath, field), depth);
                break;
            case MemberAccessExpression { TargetField: { } field } member
                when GetOwnerPath(member.Target, state) is { } owner:
                state.SetAllocationArenaDepth(new FieldSlot(owner, field), depth);
                break;
            case MemberAccessExpression { TargetProperty: { } property } member
                when GetOwnerPath(member.Target, state) is { } owner:
                state.SetAllocationArenaDepth(new PropertySlot(owner, property), depth);
                break;
            case ArrayAccessExpression array
                when GetOwnerPath(array.Target, state) is { } owner:
                state.SetAllocationArenaDepth(
                    new ArraySlot(owner, GetArrayIndex(array)), depth);
                break;
        }
    }

    private static bool IsPersistentDestination(ExpressionBase expression, AnalysisState state) => expression switch
    {
        IdentifierExpression { TargetCapture: not null } => true,
        IdentifierExpression { TargetField: { Declaration.IsStatic: true } } => true,
        IdentifierExpression { TargetField: { Declaration.IsStatic: false } } =>
            (state.ThisRegion & Region.Durable) != 0 ||
            (state.ThisPath is { Captured: true } path &&
                IsPersistentCaptured(path.Root, state)),
        IdentifierExpression { TargetProperty.IsStatic: true } => true,
        IdentifierExpression { TargetProperty: { IsStatic: false } } =>
            (state.ThisRegion & Region.Durable) != 0 ||
            (state.ThisPath is { Captured: true } path &&
                IsPersistentCaptured(path.Root, state)),
        MemberAccessExpression { TargetField: { Declaration.IsStatic: true } } => true,
        MemberAccessExpression { TargetProperty.IsStatic: true } => true,
        MemberAccessExpression { TargetField: not null } member =>
            IsCapturedReceiver(member.Target, state) || IsKnownDurableLocal(member.Target, state),
        MemberAccessExpression { TargetProperty: not null } member =>
            IsCapturedReceiver(member.Target, state) || IsKnownDurableLocal(member.Target, state),
        ArrayAccessExpression array =>
            IsCapturedReceiver(array.Target, state) || IsKnownDurableLocal(array.Target, state),
        _ => false,
    };

    private static bool IsOwnerAlignedArrayCreation(InvocationExpression invocation) =>
        invocation.TargetSymbol is { ModuleName: "cxcore" } target &&
        target.FullName.Parts.Length >= 2 &&
        target.FullName.Parts[^2] == "Array" &&
        target.FullName.Parts[^1] == "CreateForOwner";

    private static bool IsLinkedListNodeOwnerAllocation(
        ObjectCreationExpression creation,
        HashSet<FunctionDeclaration> activeFunctions)
    {
        return creation.RequestedType is NamedType nodeType &&
            nodeType.ResolvedTypeFullName.Parts.Length >= 3 &&
            nodeType.ResolvedTypeFullName.Parts[^1] == "LinkedListNode" &&
            nodeType.ResolvedTypeFullName.Parts[^2] == "Collections" &&
            activeFunctions.Any(function =>
                function.Name is "AddFirst" or "AddLast" &&
                function.Parameters.Count == 1 &&
                function.ParentClassDeclaration is
                    { Name: "LinkedList", GenericTypeNames.Length: 1,
                      Namespace.Parts: ["System", "Collections"] });
    }

    private static bool IsLinkedListNodeLinkStore(
        AssignmentExpression assignment,
        HashSet<FunctionDeclaration> activeFunctions)
    {
        var field = assignment.Target switch
        {
            IdentifierExpression { TargetField: { } identifierField } => identifierField,
            MemberAccessExpression { TargetField: { } memberField } => memberField,
            _ => null,
        };
        return field is { } targetField &&
            targetField.Declaration.Name is "Previous" or "Next" or "_first" or "_last" &&
            activeFunctions.Any(function =>
                function.Name is "AddFirst" or "AddLast" &&
                function.ParentClassDeclaration is
                    { Name: "LinkedList", GenericTypeNames.Length: 1,
                      Namespace.Parts: ["System", "Collections"] });
    }

    private static bool IsKnownDurableLocal(ExpressionBase expression, AnalysisState state) =>
        GetRegion(expression, state, durableContext: false) is var region &&
        (region & Region.Durable) != 0;

    private static bool IsCapturedReceiver(ExpressionBase expression, AnalysisState state)
    {
        if (GetOwnerPath(expression, state) is { Captured: true } path)
            return IsPersistentCaptured(path.Root, state);

        return expression switch
        {
            IdentifierExpression { TargetField: { Declaration.IsStatic: true } } => true,
            MemberAccessExpression { TargetField: { Declaration.IsStatic: true } } => true,
            _ => false,
        };
    }

    private static bool IsPersistentCaptured(LocalVariableSymbol local, AnalysisState state)
    {
        var region = state.Get(local);
        return (region & Region.Durable) != 0 || (region & Region.Arena) == 0;
    }

    private static bool IsMemoryMethod(InvocationExpression invocation, string name) =>
        invocation.TargetSymbol is { } target &&
        target.ModuleName == "cxcore" &&
        target.FullName.Parts.Length >= 2 &&
        target.FullName.Parts[^2] == "Memory" &&
        target.FullName.Parts[^1] == name;

    private static void MarkJoinedTaskCalls(
        ExpressionBase expression,
        AnalysisState state)
    {
        switch (expression)
        {
            case InvocationExpression invocation:
                state.JoinedAsyncCalls.Add(invocation);
                if (invocation.SourceSpan is { } sourceSpan)
                    state.JoinedAsyncCallSpans.Add(sourceSpan);
                break;
            case UnaryExpression { Operator: "await" } awaited:
                MarkJoinedTaskCalls(awaited.Operand, state);
                break;
            case AwaitExpression awaited:
                MarkJoinedTaskCalls(awaited.Operand, state);
                break;
            case CastExpression cast:
                MarkJoinedTaskCalls(cast.Operand, state);
                break;
            case ConditionalExpression conditional:
                MarkJoinedTaskCalls(conditional.WhenTrue, state);
                MarkJoinedTaskCalls(conditional.WhenFalse, state);
                break;
        }
    }

    private static bool IsDurableAllocationContext(InvocationExpression invocation)
    {
        if (IsMemoryMethod(invocation, "WithDurable")) return true;
        var target = invocation.TargetSymbol;
        return target?.ModuleName == "cxcore" &&
            target.FullName.Parts.Length >= 4 &&
            target.FullName.Parts[^4] == "Threading" &&
            target.FullName.Parts[^3] == "Tasks" &&
            target.FullName.Parts[^2] == "TaskRuntime" &&
            target.FullName.Parts[^1] == "WithDurable";
    }

    private static void VisitAsyncCallee(
        InvocationExpression invocation,
        AnalysisState state,
        bool durableContext,
        HashSet<FunctionDeclaration> activeFunctions)
    {
        var declaration = invocation.TargetSymbol?.Declaration;
        if (declaration is null || declaration.Body is null ||
            declaration.NativeImport is not null || !declaration.IsAsync)
        {
            if (invocation.Arguments.Any(argument =>
                MayContainArenaReference(argument, state, durableContext)))
            {
                throw new CompilationErrorException(
                    $"{OpaqueCallEscapeMessage} ({declaration?.FullName ?? "async target"})",
                    invocation.SourceSpan);
            }
            return;
        }

        state.JoinedAsyncBodyDepth += 1;
        try
        {
            state.JoinedAsyncCallSpans.UnionWith(
                declaration.JoinedAwaitOperationSpans);
            state.JoinedAsyncCallSpans.UnionWith(
                EnumerateJoinedTaskAggregateChildSpans(declaration));
            VisitFunctionBody(
                declaration,
                invocation.Receiver,
                invocation.Arguments,
                state,
                durableContext,
                activeFunctions,
                invocation.Arguments.Select(argument =>
                    GetValueRegion(argument, state, durableContext)).ToArray(),
                invocation.Arguments.Select(argument =>
                    GetOwnerPath(argument, state)).ToArray());
        }
        finally
        {
            state.JoinedAsyncBodyDepth -= 1;
        }
        if (IsReferenceLikeOrReferenceBearing(declaration.ReturnType, state))
            state.SetResult(invocation, new CallResult(Region.Arena, null));
    }

    private sealed class AnalysisState
    {
        public AnalysisProgress? Progress { get; set; }
        private readonly Dictionary<LocalVariableSymbol, Region> _locals = [];
        private readonly Dictionary<LocalVariableSymbol, int> _allocationArenaDepths = [];
        private readonly Dictionary<LocalVariableSymbol, StoragePath> _paths = [];
        private readonly HashSet<LocalVariableSymbol> _pathUnknown = [];
        private readonly Dictionary<FieldSlot, Region> _fields = [];
        private readonly Dictionary<PropertySlot, Region> _properties = [];
        private readonly Dictionary<ArraySlot, Region> _elements = [];
        private readonly Dictionary<FieldSlot, int> _fieldArenaDepths = [];
        private readonly Dictionary<PropertySlot, int> _propertyArenaDepths = [];
        private readonly Dictionary<ArraySlot, int> _arrayArenaDepths = [];
        private readonly Dictionary<ExpressionBase, CallResult> _callResults = [];
        private readonly HashSet<StoragePath> _knownArrayContents = [];
        private readonly Dictionary<StoragePath, Region> _collectionContents = [];
        private readonly HashSet<StoragePath> _knownCollectionContents = [];
        private readonly HashSet<StoragePath> _knownAggregateSlots = [];

        public Region ThisRegion { get; set; }
        public int ArenaDepth { get; set; }
        public int? ThisArenaDepth { get; set; }
        public int AsyncArenaScopeDepth { get; set; }
        public int JoinedAsyncBodyDepth { get; set; }
        public int JoinedTaskFactoryDepth { get; set; }
        public HashSet<InvocationExpression> JoinedAsyncCalls { get; } = [];
        public HashSet<SourceSpan> JoinedAsyncCallSpans { get; } = [];
        public StoragePath? ThisPath { get; set; }
        public ReturnAccumulator? CurrentReturnAccumulator { get; set; }
        public IReadOnlyList<ClassDeclaration> KnownTypes { get; set; } = [];

        public CallResult GetResult(ExpressionBase expression) =>
            _callResults.GetValueOrDefault(expression);

        public void SetResult(ExpressionBase expression, CallResult result) =>
            _callResults[expression] = result;

        public bool IsJoinedAsyncCall(InvocationExpression invocation) =>
            JoinedAsyncCalls.Contains(invocation) ||
            invocation.SourceSpan is { } sourceSpan &&
            JoinedAsyncCallSpans.Contains(sourceSpan);

        public void MarkArrayContentsKnown(StoragePath path) =>
            _knownArrayContents.Add(path);

        private readonly HashSet<StoragePath> _knownAggregateContents = [];

        public void MarkAggregateContentsKnown(StoragePath path) =>
            _knownAggregateContents.Add(path);

        public bool IsAggregateContentsKnown(StoragePath path) =>
            _knownAggregateContents.Contains(path);

        public void MarkAggregateSlotKnown(StoragePath path) =>
            _knownAggregateSlots.Add(path);

        public bool IsAggregateSlotKnown(StoragePath path) =>
            _knownAggregateSlots.Contains(path);

        public Region GetContainedRegion(StoragePath owner)
        {
            var region = Region.None;
            foreach (var (slot, value) in _fields)
            {
                if (IsDescendantOrSame(
                    slot.Owner.Append($"field:{slot.Field.ModuleName}:{slot.Field.Declaration.FullName}"),
                    owner))
                {
                    region |= value;
                }
            }
            foreach (var (slot, value) in _properties)
            {
                if (IsDescendantOrSame(
                    slot.Owner.Append($"property:{slot.Property.ModuleName}:{slot.Property.FullName}"),
                    owner))
                {
                    region |= value;
                }
            }
            foreach (var (slot, value) in _elements)
            {
                if (IsDescendantOrSame(
                    slot.Owner.Append($"array:{slot.Index ?? "*"}"), owner))
                {
                    region |= value;
                }
            }
            return region;
        }

        private static bool IsDescendantOrSame(StoragePath candidate, StoragePath owner)
        {
            if (candidate.Root != owner.Root || candidate.Captured != owner.Captured)
                return false;
            return candidate.Suffix == owner.Suffix ||
                candidate.Suffix.StartsWith(owner.Suffix + "/", StringComparison.Ordinal);
        }

        public bool IsArrayContentsKnown(StoragePath path) =>
            _knownArrayContents.Contains(path);

        public bool TryGetCollectionContents(StoragePath path, out Region region)
        {
            if (_knownCollectionContents.Contains(path))
            {
                region = _collectionContents.GetValueOrDefault(path);
                return true;
            }

            region = Region.None;
            return false;
        }

        public void SetCollectionContents(StoragePath path, Region region)
        {
            _knownCollectionContents.Add(path);
            Set(_collectionContents, path, region);
        }

        public void AddCollectionContents(StoragePath path, Region region)
        {
            if (!TryGetCollectionContents(path, out var current))
            {
                ForgetCollectionContents(path);
                return;
            }

            SetCollectionContents(path, current | region);
        }

        public void ForgetCollectionContents(StoragePath path)
        {
            _knownCollectionContents.Remove(path);
            _collectionContents.Remove(path);
        }

        public Region Get(LocalVariableSymbol local) =>
            _locals.GetValueOrDefault(local, Region.None);

        public void Set(LocalVariableSymbol local, Region region)
        {
            if (region == Region.None) _locals.Remove(local);
            else _locals[local] = region;
        }

        public int? GetAllocationArenaDepth(LocalVariableSymbol local) =>
            _allocationArenaDepths.TryGetValue(local, out var depth) ? depth : null;

        public void SetAllocationArenaDepth(LocalVariableSymbol local, int? depth)
        {
            _allocationArenaDepths.Remove(local);
            if (depth is { } knownDepth)
                _allocationArenaDepths[local] = knownDepth;
        }

        public int? GetAllocationArenaDepth(FieldSlot slot) =>
            _fieldArenaDepths.TryGetValue(slot, out var depth) ? depth : null;

        public int? GetAllocationArenaDepth(PropertySlot slot) =>
            _propertyArenaDepths.TryGetValue(slot, out var depth) ? depth : null;

        public int? GetAllocationArenaDepth(ArraySlot slot)
        {
            var depths = new List<int>(2);
            if (_arrayArenaDepths.TryGetValue(slot, out var exactDepth))
                depths.Add(exactDepth);
            if (slot.Index is not null &&
                _arrayArenaDepths.TryGetValue(new ArraySlot(slot.Owner, null), out var anyIndexDepth))
            {
                depths.Add(anyIndexDepth);
            }
            return depths.Count > 0 && depths.All(depth => depth == depths[0])
                ? depths[0]
                : null;
        }

        public void SetAllocationArenaDepth(FieldSlot slot, int? depth) =>
            Set(_fieldArenaDepths, slot, depth);

        public void SetAllocationArenaDepth(PropertySlot slot, int? depth) =>
            Set(_propertyArenaDepths, slot, depth);

        public void SetAllocationArenaDepth(ArraySlot slot, int? depth) =>
            Set(_arrayArenaDepths, slot, depth);

        private static void Set<TKey>(Dictionary<TKey, int> depths, TKey slot, int? depth)
            where TKey : notnull
        {
            depths.Remove(slot);
            if (depth is { } knownDepth)
                depths[slot] = knownDepth;
        }

        public StoragePath? GetPath(LocalVariableSymbol local) =>
            _paths.TryGetValue(local, out var path) ? path : null;

        public bool IsPathUnknown(LocalVariableSymbol local) => _pathUnknown.Contains(local);

        public void SetPath(LocalVariableSymbol local, StoragePath? path)
        {
            _paths.Remove(local);
            _pathUnknown.Remove(local);
            if (path is { } value) _paths[local] = value;
            else _pathUnknown.Add(local);
        }

        public Region Get(FieldSlot slot) => _fields.GetValueOrDefault(slot, Region.None);
        public Region Get(PropertySlot slot) => _properties.GetValueOrDefault(slot, Region.None);

        public Region GetElements(StoragePath owner, string? index)
        {
            if (index is null)
                return _elements.Where(pair => pair.Key.Owner == owner)
                    .Aggregate(Region.None, (region, pair) => region | pair.Value);
            return _elements.GetValueOrDefault(new ArraySlot(owner, index), Region.None) |
                _elements.GetValueOrDefault(new ArraySlot(owner, null), Region.None);
        }

        public void Set(FieldSlot slot, Region region) => Set(_fields, slot, region);
        public void Set(PropertySlot slot, Region region) => Set(_properties, slot, region);
        public void Set(ArraySlot slot, Region region) => Set(_elements, slot, region);

        public AnalysisState Clone()
        {
            var clone = new AnalysisState();
            clone.Progress = Progress;
            foreach (var (local, region) in _locals) clone._locals.Add(local, region);
            foreach (var (local, depth) in _allocationArenaDepths)
                clone._allocationArenaDepths.Add(local, depth);
            foreach (var (local, path) in _paths) clone._paths.Add(local, path);
            foreach (var local in _pathUnknown) clone._pathUnknown.Add(local);
            foreach (var (slot, region) in _fields) clone._fields.Add(slot, region);
            foreach (var (slot, region) in _properties) clone._properties.Add(slot, region);
            foreach (var (slot, region) in _elements) clone._elements.Add(slot, region);
            foreach (var (slot, depth) in _fieldArenaDepths)
                clone._fieldArenaDepths.Add(slot, depth);
            foreach (var (slot, depth) in _propertyArenaDepths)
                clone._propertyArenaDepths.Add(slot, depth);
            foreach (var (slot, depth) in _arrayArenaDepths)
                clone._arrayArenaDepths.Add(slot, depth);
            foreach (var (invocation, result) in _callResults)
                clone._callResults.Add(invocation, result);
            foreach (var path in _knownArrayContents)
                clone._knownArrayContents.Add(path);
            foreach (var (path, region) in _collectionContents)
                clone._collectionContents.Add(path, region);
            foreach (var path in _knownCollectionContents)
                clone._knownCollectionContents.Add(path);
            foreach (var path in _knownAggregateContents)
                clone._knownAggregateContents.Add(path);
            foreach (var path in _knownAggregateSlots)
                clone._knownAggregateSlots.Add(path);
            clone.ThisRegion = ThisRegion;
            clone.ArenaDepth = ArenaDepth;
            clone.ThisArenaDepth = ThisArenaDepth;
            clone.AsyncArenaScopeDepth = AsyncArenaScopeDepth;
            clone.JoinedAsyncBodyDepth = JoinedAsyncBodyDepth;
            clone.JoinedTaskFactoryDepth = JoinedTaskFactoryDepth;
            clone.JoinedAsyncCalls.UnionWith(JoinedAsyncCalls);
            clone.JoinedAsyncCallSpans.UnionWith(JoinedAsyncCallSpans);
            clone.ThisPath = ThisPath;
            clone.CurrentReturnAccumulator = CurrentReturnAccumulator;
            clone.KnownTypes = KnownTypes;
            return clone;
        }

        public void WidenArenaFlows()
        {
            // Arena is the lifetime being proven here. Preserve known durable
            // ownership, paths, and allocation depths so a conservative
            // widening does not mistake an arena-owned destination for a
            // persistent one.
            foreach (var local in _locals.Keys.ToArray())
                _locals[local] |= Region.Arena;
            foreach (var field in _fields.Keys.ToArray())
                _fields[field] |= Region.Arena;
            foreach (var property in _properties.Keys.ToArray())
                _properties[property] |= Region.Arena;
            foreach (var element in _elements.Keys.ToArray())
                _elements[element] |= Region.Arena;
            foreach (var path in _collectionContents.Keys.ToArray())
                _collectionContents[path] |= Region.Arena;

            ThisRegion |= Region.Arena;
            foreach (var expression in _callResults.Keys.ToArray())
            {
                var result = _callResults[expression];
                if (result.Region != Region.None)
                    _callResults[expression] = result with { Region = result.Region | Region.Arena };
            }
        }

        public void Merge(params AnalysisState[] states)
        {
            JoinedAsyncCalls.Clear();
            JoinedAsyncCallSpans.Clear();
            foreach (var state in states)
            {
                JoinedAsyncCalls.UnionWith(state.JoinedAsyncCalls);
                JoinedAsyncCallSpans.UnionWith(state.JoinedAsyncCallSpans);
            }
            JoinedTaskFactoryDepth = states[0].JoinedTaskFactoryDepth;
            JoinedAsyncBodyDepth = states[0].JoinedAsyncBodyDepth;
            _locals.Clear();
            _allocationArenaDepths.Clear();
            _paths.Clear();
            _pathUnknown.Clear();
            _fields.Clear();
            _properties.Clear();
            _elements.Clear();
            _fieldArenaDepths.Clear();
            _propertyArenaDepths.Clear();
            _arrayArenaDepths.Clear();
            _callResults.Clear();
            _knownArrayContents.Clear();
            _collectionContents.Clear();
            _knownCollectionContents.Clear();
            _knownAggregateContents.Clear();
            _knownAggregateSlots.Clear();
            ThisRegion = states.Aggregate(Region.None, (region, state) => region | state.ThisRegion);
            var firstThisPath = states[0].ThisPath;
            ThisPath = states.Skip(1).All(state => state.ThisPath == firstThisPath)
                ? firstThisPath
                : null;
            CurrentReturnAccumulator = states[0].CurrentReturnAccumulator;
            var resultExpressions = states.SelectMany(state => state._callResults.Keys).Distinct();
            foreach (var expression in resultExpressions)
            {
                var results = states.Select(state => state.GetResult(expression)).ToArray();
                var region = results.Aggregate(Region.None,
                    (current, result) => current | result.Region);
                var firstPath = results[0].Path;
                var path = results.Skip(1).All(result => result.Path == firstPath)
                    ? firstPath
                    : null;
                var firstDepth = results[0].AllocationArenaDepth;
                var depth = results.Skip(1).All(result =>
                    result.AllocationArenaDepth == firstDepth)
                    ? firstDepth
                    : null;
                _callResults[expression] = new CallResult(region, path, depth);
            }
            var knownArrayPaths = states[0]._knownArrayContents
                .Where(path => states.Skip(1).All(state =>
                    state._knownArrayContents.Contains(path)));
            foreach (var path in knownArrayPaths)
                _knownArrayContents.Add(path);
            var knownCollectionPaths = states[0]._knownCollectionContents
                .Where(path => states.Skip(1).All(state =>
                    state._knownCollectionContents.Contains(path)));
            foreach (var path in knownCollectionPaths)
            {
                _knownCollectionContents.Add(path);
                _collectionContents[path] = states.Aggregate(
                    Region.None,
                    (region, state) => region |
                        state._collectionContents.GetValueOrDefault(path));
            }
            var knownAggregatePaths = states[0]._knownAggregateContents
                .Where(path => states.Skip(1).All(state =>
                    state._knownAggregateContents.Contains(path)));
            foreach (var path in knownAggregatePaths)
                _knownAggregateContents.Add(path);
            var knownAggregateSlots = states[0]._knownAggregateSlots
                .Where(path => states.Skip(1).All(state =>
                    state._knownAggregateSlots.Contains(path)));
            foreach (var path in knownAggregateSlots)
                _knownAggregateSlots.Add(path);
            foreach (var state in states)
                foreach (var (local, region) in state._locals)
                    _locals[local] = _locals.GetValueOrDefault(local) | region;
            var depthLocals = states.SelectMany(state => state._allocationArenaDepths.Keys).Distinct();
            foreach (var local in depthLocals)
            {
                var firstDepth = states[0].GetAllocationArenaDepth(local);
                if (firstDepth is { } depth && states.Skip(1).All(state =>
                    state.GetAllocationArenaDepth(local) == depth))
                {
                    _allocationArenaDepths[local] = depth;
                }
            }
            var pathLocals = states.SelectMany(state => state._paths.Keys.Concat(state._pathUnknown)).Distinct();
            foreach (var local in pathLocals)
            {
                var firstPath = states[0].GetPath(local);
                var firstUnknown = states[0].IsPathUnknown(local);
                if (states.Skip(1).All(state => state.GetPath(local) == firstPath &&
                        state.IsPathUnknown(local) == firstUnknown))
                {
                    if (firstPath is { } path) _paths[local] = path;
                    else if (firstUnknown) _pathUnknown.Add(local);
                }
                else
                {
                    _pathUnknown.Add(local);
                }
            }
            foreach (var state in states)
            {
                foreach (var (slot, region) in state._fields)
                    _fields[slot] = _fields.GetValueOrDefault(slot) | region;
                foreach (var (slot, region) in state._properties)
                    _properties[slot] = _properties.GetValueOrDefault(slot) | region;
                foreach (var (slot, region) in state._elements)
                    _elements[slot] = _elements.GetValueOrDefault(slot) | region;
            }
            MergeKnownDepths(states, state => state._fieldArenaDepths, _fieldArenaDepths);
            MergeKnownDepths(states, state => state._propertyArenaDepths, _propertyArenaDepths);
            MergeKnownDepths(states, state => state._arrayArenaDepths, _arrayArenaDepths);
        }

        private static void MergeKnownDepths<TKey>(
            IReadOnlyList<AnalysisState> states,
            Func<AnalysisState, Dictionary<TKey, int>> getDepths,
            Dictionary<TKey, int> destination)
            where TKey : notnull
        {
            var slots = states.SelectMany(state => getDepths(state).Keys).Distinct();
            foreach (var slot in slots)
            {
                var firstDepths = getDepths(states[0]);
                if (firstDepths.TryGetValue(slot, out var depth) &&
                    states.Skip(1).All(state =>
                        getDepths(state).TryGetValue(slot, out var branchDepth) &&
                        branchDepth == depth))
                {
                    destination[slot] = depth;
                }
            }
        }

        public bool HasSameFlowState(AnalysisState other) =>
            ThisRegion == other.ThisRegion && ThisPath == other.ThisPath &&
            ArenaDepth == other.ArenaDepth && ThisArenaDepth == other.ThisArenaDepth &&
            SameDictionary(_locals, other._locals) &&
            SameDictionary(_allocationArenaDepths, other._allocationArenaDepths) &&
            SameDictionary(_paths, other._paths) &&
            _pathUnknown.SetEquals(other._pathUnknown) &&
            SameDictionary(_fields, other._fields) &&
            SameDictionary(_properties, other._properties) &&
            SameDictionary(_elements, other._elements) &&
            SameDictionary(_fieldArenaDepths, other._fieldArenaDepths) &&
            SameDictionary(_propertyArenaDepths, other._propertyArenaDepths) &&
            SameDictionary(_arrayArenaDepths, other._arrayArenaDepths) &&
            SameDictionary(_callResults, other._callResults) &&
            _knownArrayContents.SetEquals(other._knownArrayContents) &&
            SameDictionary(_collectionContents, other._collectionContents) &&
            _knownCollectionContents.SetEquals(other._knownCollectionContents) &&
            _knownAggregateContents.SetEquals(other._knownAggregateContents) &&
            _knownAggregateSlots.SetEquals(other._knownAggregateSlots);

        private static bool SameDictionary<TKey, TValue>(
            Dictionary<TKey, TValue> left,
            Dictionary<TKey, TValue> right)
            where TKey : notnull
        {
            if (left.Count != right.Count)
                return false;
            foreach (var (key, value) in left)
            {
                if (!right.TryGetValue(key, out var otherValue) ||
                    !EqualityComparer<TValue>.Default.Equals(value, otherValue))
                {
                    return false;
                }
            }
            return true;
        }

        private static void Set<TKey>(Dictionary<TKey, Region> values, TKey key, Region region)
            where TKey : notnull
        {
            if (region == Region.None) values.Remove(key);
            else values[key] = region;
        }
    }

    private sealed class AnalysisProgress(Action<string>? report)
    {
        private int _functionBodies;
        private int _loopIterations;
        private int _widenedLoops;

        public void FunctionBodyVisited(string fullName)
        {
            var count = ++_functionBodies;
            if (count % 100 == 0)
                report?.Invoke($"visited {count} function bodies; latest is {fullName}");
        }

        public void LoopIteration()
        {
            var count = ++_loopIterations;
            if (count % 100 == 0)
                report?.Invoke($"analyzed {count} loop iterations");
        }

        public void LoopWidened()
        {
            var count = ++_widenedLoops;
            report?.Invoke($"conservatively widened {count} loops");
        }
    }

    private readonly record struct CallResult(
        Region Region,
        StoragePath? Path,
        int? AllocationArenaDepth = null);

    private sealed class ReturnAccumulator
    {
        private bool _hasReturn;
        private bool _hasConsistentPath = true;
        private bool _hasConsistentAllocationArenaDepth = true;
        private StoragePath? _path;
        private int? _allocationArenaDepth;

        public Region Region { get; private set; }

        public CallResult Result => new(
            Region,
            _hasConsistentPath ? _path : null,
            _hasConsistentAllocationArenaDepth ? _allocationArenaDepth : null);

        public void Add(Region region, StoragePath? path, int? allocationArenaDepth)
        {
            Region |= region;
            if (!_hasReturn)
            {
                _path = path;
                _allocationArenaDepth = allocationArenaDepth;
                _hasReturn = true;
            }
            else if (_path != path)
            {
                _path = null;
                _hasConsistentPath = false;
            }
            if (_allocationArenaDepth != allocationArenaDepth)
            {
                _allocationArenaDepth = null;
                _hasConsistentAllocationArenaDepth = false;
            }
        }
    }
}

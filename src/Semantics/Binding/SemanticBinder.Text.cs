using Antlr4.Runtime;
using CxCompiler.Grammar;
using CxCompiler.Model;
using CxCompiler.Model.Common;
using CxCompiler.Model.Types;
using CxCompiler.Model.Types.BuiltInTypes;
using CxCompiler.ParserVisitors;

namespace CxCompiler.Semantics;

public sealed partial class SemanticBinder
{
    private void AddCoreTextDeclarations()
    {
        // The core project owns these declarations. Adding the synthetic cxcore
        // reference while compiling cxcore makes its own private members look
        // like referenced-module internals and hides them from the binder.
        if (_project?.Name == "cxcore") return;

        // A consuming source project can provide its own sequence contracts.
        // Prefer those declarations instead of adding a second set to the
        // synthetic core context.
        var projectTypeNames = _project!.CompilationContexts
            .SelectMany(context => EnumerateClasses(
                context.DeclarationScope.Declarations))
            .Select(declaration => declaration.FullName.ToString())
            .ToHashSet(StringComparer.Ordinal);

        // Standalone consumers need the same core declarations as source-project
        // references. Parse them through the ordinary binder rather than inventing
        // a separate representation for constructors, virtual slots or parameters.
        AddCoreStandardLibraryDeclarations(Add);
        Add("""
            namespace System;
            public interface IDisposable { void Dispose(); }
            public class Exception {
                private string _message;
                private Exception? _innerException;
                public string Message { extern const get; }
                public extern constructor(string message);
                public extern constructor(string message, Exception innerException);
                public extern constructor();
                public static extern void Throw(Exception exception);
            }
            public final class WideString : Object {
                private uint _length; private ptr _data;
                public static WideString Empty { extern const get; }
                public uint Length { extern const get; }
                public wchar Item { extern const get(uint index); }
                public uint ScalarCount { extern const get; }
                public extern char ScalarAt(uint index) const;
                public extern constructor();
                public unsafe extern constructor(ptr<const wchar> data, uint length);
                public override extern string ToString() const;
                public override extern bool Equals(object other) const;
                public override extern int GetHashCode() const;
                public extern int CompareOrdinal(WideString other) const;
                public extern bool operator ==(WideString other) const;
                public extern bool operator !=(WideString other) const;
            }
            public class EncodingException : Exception {
                private uint _position;
                public uint Position { extern const get; }
                public extern constructor(uint position);
            }
            """);
        Add("""
            namespace System.Interop;
            public final class NativeBuffer {
                private ptr _data; private ulong _byteLength; private ptr _allocation;
                public ptr Data { extern const get; }
                public ulong ByteLength { extern const get; }
                public static extern NativeBuffer Allocate(ulong byteLength);
                public static extern NativeBuffer AllocateAligned(ulong byteLength, uint alignment);
                public static extern NativeBuffer FromUtf8(string value, bool terminated = true);
                public static extern NativeBuffer FromUtf16(System.WideString value, bool terminated = true);
                public extern string ToUtf8String(uint byteLength) const;
                public extern System.WideString ToWideString(uint codeUnitLength) const;
                public extern void Dispose();
                public static unsafe extern void Copy(ptr destination, ptr source, ulong byteLength);
            }
            public static class NativeCallback {
                public static extern System.Exception GetLastException();
                public static extern void ClearLastException();
                public static unsafe extern NativeCallbackRegistration Register<T>(ptr callback);
            }
            public final class NativeCallbackRegistration {
                private ptr _state;
                private System.Exception _lastException;
                private extern constructor();
                public ptr Context { extern const get; }
                public System.Exception LastException { extern const get; }
                public unsafe extern T Function<T>() const;
                public extern void Unregister(delegate() => void unregister);
            }
            public final class NativeLibrary {
                private ptr _handle;
                private extern constructor();
                public static extern NativeLibrary Load(string path);
                public unsafe extern T Resolve<T>(string name) const;
            }
            """);
        Add("""
            namespace System.Threading.Tasks;
            public delegate void CancellationCallback();
            public struct CancellationToken {
                private CancellationTokenSource _source;
                public static CancellationToken None { const get { return new CancellationToken(null); } }
                public bool IsCancellationRequested { const get => _source != null && _source.IsCancellationRequested; }
                public void ThrowIfCancellationRequested() {
                    OperationCanceledException.ThrowIfCanceled(IsCancellationRequested, new CancellationToken(_source));
                }
                public CancellationTokenRegistration Register(delegate() => void callback) { if (_source != null) return _source.Register(callback); return CancellationTokenRegistration.None; }
                internal constructor(CancellationTokenSource source) { _source = source; }
            }
            public struct CancellationTokenRegistration {
                private CancellationTokenSource _source;
                private uint _registrationId;
                public static CancellationTokenRegistration None { const get => new CancellationTokenRegistration(null, 0u); }
                public void Dispose() { if (_source != null) { _source.Unregister(_registrationId); _source = null; _registrationId = 0u; } }
                internal constructor(CancellationTokenSource source, uint registrationId) { _source = source; _registrationId = registrationId; }
            }
            public final class CancellationTokenSource {
                private bool _isCancellationRequested;
                private ptr _callbacks;
                private ptr _callbackIds;
                private uint _callbackCount;
                private uint _nextCallbackId;
                public bool IsCancellationRequested { const get => _isCancellationRequested; }
                public CancellationToken Token { const get => new CancellationToken(this); }
                public CancellationTokenRegistration Register(delegate() => void callback) { return CancellationTokenRegistration.None; }
                internal void Unregister(uint registrationId) { }
                public void Cancel() { _isCancellationRequested = true; }
                public constructor() { _isCancellationRequested = false; }
            }
            public class OperationCanceledException : System.Exception {
                private CancellationToken _cancellationToken;
                public CancellationToken CancellationToken { const get => _cancellationToken; }
                public constructor() : this(CancellationToken.None) { }
                public constructor(CancellationToken cancellationToken) : base() { _cancellationToken = cancellationToken; }
                public static void ThrowIfCanceled(bool isCanceled, CancellationToken cancellationToken) {
                    if (isCanceled) throw new OperationCanceledException(cancellationToken);
                }
            }
            public struct VoidTask {
                private ptr _value;
                private bool _isCompleted;
                private bool _isFaulted;
                private System.Exception _exception;
                public bool IsCompleted { const get => _isCompleted; }
                public static extern void ThrowIfFaulted(bool isFaulted, System.Exception exception);
                public void GetResult() const { ThrowIfFaulted(_isFaulted, _exception); }
                public static VoidTask Faulted(System.Exception exception) {
                    var task = new VoidTask();
                    task._isFaulted = true;
                    task._exception = exception;
                    return task;
                }
                public constructor() {
                    _value = null; _isCompleted = true; _isFaulted = false; _exception = null;
                }
            }
            internal final class TaskState {
                private object _value;
                private bool _isCompleted;
                private bool _isFaulted;
                private bool _isCanceled;
                private System.Exception _exception;
                private ptr _continuations;
                public bool IsCompleted { const get => _isCompleted; }
                public object Value { const get => _value; }
                public bool IsFaulted { const get => _isFaulted; }
                public bool IsCanceled { const get => _isCanceled; }
                public System.Exception Exception { const get => _exception; }
                public static extern void RegisterUnobservedFault(TaskState state);
                public extern void MarkFaultObserved();
                public static extern void ThrowIfNotCompleted(bool isCompleted);
                public static extern void ThrowIfAlreadyCompleted(bool isCompleted);
                public extern void OnCompleted(delegate() => void continuation);
                private extern void ScheduleContinuations();
                public void SetResult(object value) {
                    ThrowIfAlreadyCompleted(_isCompleted);
                    _value = value; _isFaulted = false; _isCompleted = true;
                    ScheduleContinuations();
                }
                public extern void SetFault(System.Exception exception);
                public void SetCanceled() {
                    ThrowIfAlreadyCompleted(_isCompleted);
                    _isFaulted = false; _isCanceled = true; _isCompleted = true;
                    ScheduleContinuations();
                }
                public constructor() {
                    _value = null; _isCompleted = false; _isFaulted = false; _isCanceled = false; _exception = null;
                    _continuations = null;
                }
            }
            public final class TaskHandle {
                private TaskState _state;
                public bool IsCompleted { const get => _state.IsCompleted; }
                public bool IsCanceled { const get => _state.IsCanceled; }
                public void GetResult() const {
                    TaskState.ThrowIfNotCompleted(_state.IsCompleted);
                    _state.MarkFaultObserved();
                    OperationCanceledException.ThrowIfCanceled(_state.IsCanceled, CancellationToken.None);
                    VoidTask.ThrowIfFaulted(_state.IsFaulted, _state.Exception);
                }
                public static TaskHandle Faulted(System.Exception exception) {
                    var task = new TaskHandle(false);
                    task.SetFault(exception);
                    return task;
                }
                public static TaskHandle Delay(uint milliseconds) {
                    var completion = new TaskCompletionSourceVoid();
                    TaskScheduler.PostAfter(milliseconds, () => completion.SetResult());
                    return completion.Task;
                }
                public static TaskHandle Delay(uint milliseconds, CancellationToken cancellationToken) {
                    var completion = new TaskCompletionSourceVoid();
                    if (cancellationToken.IsCancellationRequested) { completion.SetCanceled(); return completion.Task; }
                    return completion.Task;
                }
                public void SetResult() { _state.SetResult(null); }
                public void SetFault(System.Exception exception) { _state.SetFault(exception); }
                public void SetCanceled() { _state.SetCanceled(); }
                public extern void OnCompleted(delegate() => void continuation);
                public constructor() { _state = new TaskState(); _state.SetResult(null); }
                internal constructor(bool completed) {
                    _state = new TaskState();
                    if (completed) _state.SetResult(null);
                }
            }
            alias Task = TaskHandle;
            public class Task<T> {
                private bool _isCompleted;
                private bool _isFaulted;
                private bool _isCanceled;
                private System.Exception _exception;
                private ptr _continuations;
                private T _result;
                public bool IsCompleted { const get => _isCompleted; }
                public bool IsCanceled { const get => _isCanceled; }
                public T GetResult() const {
                    TaskState.ThrowIfNotCompleted(_isCompleted);
                    TaskRuntime.MarkFaultObserved(this);
                    OperationCanceledException.ThrowIfCanceled(_isCanceled, CancellationToken.None);
                    VoidTask.ThrowIfFaulted(_isFaulted, _exception);
                    return _result;
                }
                public constructor(T result) {
                    _result = result; _isCompleted = true;
                }
                public constructor() { }
                public void SetResult(T result) {
                    TaskRuntime.ThrowIfAlreadyCompleted(this);
                    _result = result; _isFaulted = false; _isCanceled = false;
                    _exception = null; _isCompleted = true;
                    TaskRuntime.ScheduleContinuations(this);
                }
                public void SetFault(System.Exception exception) { TaskRuntime.SetFault(this, exception); }
                public void SetCanceled() { TaskRuntime.SetCanceled(this); }
                public void OnCompleted(delegate() => void continuation) {
                    TaskRuntime.OnCompleted(this, continuation);
                }
            }
            public struct ValueTask<T> {
                private Task<T> _task;
                private ReusableValueTaskSource<T> _source;
                private uint _version;
                private bool _hasSource;
                private bool _consumed;
                private T _result;
                public bool IsCompleted { const get => _hasSource ? TaskRuntime.ReusableSourceIsCompleted(_source, _version) : _task == null || _task.IsCompleted; }
                public T GetResult() {
                    TaskState.ThrowIfAlreadyCompleted(_consumed);
                    _consumed = true;
                    if (_hasSource) return _source.GetResult(_version);
                    if (_task != null) return _task.GetResult();
                    return _result;
                }
                public Task<T> AsTask() {
                    TaskState.ThrowIfAlreadyCompleted(_consumed);
                    _consumed = true;
                    if (_hasSource) {
                        var completion = new TaskCompletionSource<T>();
                        var source = _source;
                        var version = _version;
                        source.OnCompleted(version, () => {
                            try {
                                completion.SetResult(source.GetResult(version));
                            } catch (OperationCanceledException) {
                                completion.SetCanceled();
                            } catch (Exception exception) {
                                completion.SetException(exception);
                            }
                        });
                        return completion.GetTask();
                    }
                    if (_task != null) return _task;
                    return new Task<T>(_result);
                }
                public void OnCompleted(delegate() => void continuation) {
                    if (_hasSource) _source.OnCompleted(_version, continuation);
                    else if (_task != null) _task.OnCompleted(continuation);
                }
                public void SetTask(Task<T> task) { _task = task; _source = null; _version = 0u; _hasSource = false; _consumed = false; }
                public void SetSource(ReusableValueTaskSource<T> source, uint version) { _task = null; _source = source; _version = version; _hasSource = true; _consumed = false; }
                public constructor(T result) { _result = result; _consumed = false; }
                public constructor() { }
            }
            public struct ReusableValueTask<T> {
                private ReusableValueTaskSource<T> _source;
                private uint _version;
                public bool IsCompleted { const get => TaskRuntime.ReusableSourceIsCompleted(_source, _version); }
                public T GetResult() { return _source.GetResult(_version); }
                public Task<T> AsTask() {
                    var completion = new TaskCompletionSource<T>();
                    var source = _source;
                    var version = _version;
                    source.OnCompleted(version, () => {
                        try {
                            completion.SetResult(source.GetResult(version));
                        } catch (OperationCanceledException) {
                            completion.SetCanceled();
                        } catch (Exception exception) {
                            completion.SetException(exception);
                        }
                    });
                    return completion.GetTask();
                }
                public void OnCompleted(delegate() => void continuation) { _source.OnCompleted(_version, continuation); }
                public void SetSource(ReusableValueTaskSource<T> source, uint version) { _source = source; _version = version; }
                public constructor() { _source = null; _version = 0u; }
            }
            internal final class ReusableValueTaskContinuationState {
                private delegate() => void _continuation;
                public extern void Clear();
                public extern constructor();
            }
            public final class ReusableValueTaskSource<T> {
                private uint _version;
                private bool _isCompleted;
                private bool _isConsumed;
                private bool _isRetired;
                private uint _pendingCallbacks;
                private bool _continuationRegistered;
                private ReusableValueTaskContinuationState _continuationState;
                private object _socketWaitItem;
                private T _result;
                private System.Exception _exception;
                private bool _isFaulted;
                private bool _isCanceled;
                private void ThrowIfStale(uint version) { TaskState.ThrowIfAlreadyCompleted(_isRetired || version != _version); }
                public T GetResult(uint version) { ThrowIfStale(version); TaskState.ThrowIfNotCompleted(_isCompleted); TaskState.ThrowIfAlreadyCompleted(_isConsumed); _isConsumed = true; OperationCanceledException.ThrowIfCanceled(_isCanceled, CancellationToken.None); VoidTask.ThrowIfFaulted(_isFaulted, _exception); return _result; }
                public bool IsCompleted { const get => _isCompleted; }
                public void SetResult(T result) { SetResult(_version, result); }
                public void SetResult(uint version, T result) { ThrowIfStale(version); TaskState.ThrowIfAlreadyCompleted(_isRetired || _isCompleted); _result = result; _exception = null; _isFaulted = false; _isCanceled = false; _isCompleted = true; TaskRuntime.ScheduleReusableSourceContinuation(this, _version); }
                public void SetException(System.Exception exception) { SetException(_version, exception); }
                public void SetException(uint version, System.Exception exception) { ThrowIfStale(version); TaskState.ThrowIfAlreadyCompleted(_isRetired || _isCompleted); _result = default; _exception = exception; _isFaulted = true; _isCanceled = false; _isCompleted = true; TaskRuntime.ScheduleReusableSourceContinuation(this, _version); }
                public void SetCanceled() { SetCanceled(_version); }
                public void SetCanceled(uint version) { ThrowIfStale(version); TaskState.ThrowIfAlreadyCompleted(_isRetired || _isCompleted); _result = default; _exception = null; _isFaulted = false; _isCanceled = true; _isCompleted = true; TaskRuntime.ScheduleReusableSourceContinuation(this, _version); }
                public void OnCompleted(uint version, delegate() => void continuation) { ThrowIfStale(version); TaskState.ThrowIfAlreadyCompleted(_isConsumed || _continuationRegistered); if (_continuationState == null) _continuationState = new ReusableValueTaskContinuationState(); TaskRuntime.RegisterReusableSourceContinuation(this, version, continuation); }
                public void Reset() { TaskState.ThrowIfAlreadyCompleted(_isRetired || !_isCompleted || !_isConsumed || _pendingCallbacks != 0u); _result = default; _exception = null; _isFaulted = false; _isCanceled = false; _isCompleted = false; _isConsumed = false; _continuationRegistered = false; if (_continuationState != null) _continuationState.Clear(); if (_version == 4294967295u) { _isRetired = true; return; } _version += 1u; }
                public uint GetVersion() { return _version; }
                public constructor() { }
            }
            public struct ValueTaskHandle {
                private TaskHandle _task;
                private bool _hasTask;
                private bool _consumed;
                public bool IsCompleted { const get => !_hasTask || _task.IsCompleted; }
                public void GetResult() {
                    TaskState.ThrowIfAlreadyCompleted(_consumed);
                    _consumed = true;
                    if (_hasTask) _task.GetResult();
                }
                public TaskHandle AsTask() {
                    TaskState.ThrowIfAlreadyCompleted(_consumed);
                    _consumed = true;
                    if (_hasTask) return _task;
                    return new TaskHandle();
                }
                public void OnCompleted(delegate() => void continuation) {
                    if (_hasTask) _task.OnCompleted(continuation);
                }
                public constructor() { _task = null; _hasTask = false; _consumed = false; }
                public constructor(TaskHandle task) {
                    _task = task; _hasTask = true; _consumed = false;
                }
            }
            alias ValueTask = ValueTaskHandle;
            public struct TaskCompletionSource<T> {
                private Task<T> _task;
                public Task<T> Task { const get => _task; }
                public Task<T> GetTask() { return _task; }
                public void SetResult(T result) { _task.SetResult(result); }
                public void SetException(System.Exception exception) { _task.SetFault(exception); }
                public void SetCanceled() { _task.SetCanceled(); }
                public constructor() { _task = new Task<T>(); }
                }
            public final class TaskCompletionSourceVoid {
                private TaskHandle _task;
                public TaskHandle Task { const get => _task; }
                public void SetResult() { _task.SetResult(); }
                public void SetException(System.Exception exception) { _task.SetFault(exception); }
                public void SetCanceled() { _task.SetCanceled(); }
                public constructor() { _task = new TaskHandle(false); }
            }
            alias TaskCompletionSource = TaskCompletionSourceVoid;
            public static class TaskScheduler {
                public static extern void Post(delegate() => void continuation);
                public static extern void PostAfter(uint milliseconds, delegate() => void continuation);
                public static extern bool RunOne();
                public static extern uint Run();
                public static extern void Shutdown();
                public static extern void RunUntil(object task);
            }
            public static class TaskRuntime {
                public static extern void OnCompleted(object task, delegate() => void continuation);
                public static extern void ThrowIfAlreadyCompleted(object task);
                public static extern void ScheduleContinuations(object task);
                public static extern void MarkFaultObserved(object task);
                public static extern void SetFault(object task, System.Exception exception);
                public static extern void SetCanceled(object task);
                public static extern void ForwardVoid(object source, object destination);
                public static extern bool ReusableSourceIsCompleted(object source, uint version);
                public static extern void RegisterReusableSourceContinuation(object source, uint version, delegate() => void continuation);
                public static extern void ScheduleReusableSourceContinuation(object source, uint version);
                internal static extern object GetReusableSourceSocketWaitItem(object source);
                internal static extern void SetReusableSourceSocketWaitItem(object source, object item);
                public static extern object GetVoidTask(object source);
            }
            """);
        if (_project!.CompilationContexts
            .SelectMany(context => EnumerateFunctions(
                context.DeclarationScope.Declarations))
            .Any(function => function.IsAsync &&
                AsyncFunctionLowerer.IsAsyncEnumerableType(function.ReturnType)))
        {
            Add("""
                namespace System;
                public interface IAsyncDisposable {
                    System.Threading.Tasks.ValueTaskHandle DisposeAsync();
                }
                """);
            Add("""
                namespace System.Collections;
                public interface IAsyncEnumerable<T> {
                    IAsyncEnumerator<T> GetAsyncEnumerator(
                        System.Threading.Tasks.CancellationToken cancellationToken);
                }
                public interface IAsyncEnumerator<T> : System.IAsyncDisposable {
                    T Current { const get; }
                    System.Threading.Tasks.ValueTask<bool> MoveNextAsync();
                }
                """);
        }
        void Add(string source)
        {
            var parser = new CxParser(new CommonTokenStream(new CxLexer(new AntlrInputStream(source))));
            var parsed = new CompilationUnitParserVisitor().Visit(parser.compilationUnit());
            var context = new CompilationContext();
            context.SetNamespace(parsed.Namespace);
            foreach (var declaration in parsed.DeclarationScope.Declarations)
            {
                if (declaration is ClassDeclaration classDeclaration &&
                    !projectTypeNames.Contains(classDeclaration.FullName.ToString()) &&
                    FindCoreClass(classDeclaration.FullName.ToString(),
                        classDeclaration.GenericTypeNames.Length) is null)
                {
                    context.DeclarationScope.AddDeclaration(classDeclaration);
                }
                else if (declaration is AliasDeclaration aliasDeclaration &&
                    !_project!.CompilationContexts.SelectMany(candidate =>
                            candidate.DeclarationScope.Declarations)
                        .OfType<AliasDeclaration>()
                        .Any(existing => existing.FullName == aliasDeclaration.FullName &&
                            existing.GenericTypeNames.Length ==
                                aliasDeclaration.GenericTypeNames.Length))
                {
                    context.DeclarationScope.AddDeclaration(aliasDeclaration);
                }
            }
            if (context.DeclarationScope.Declarations.Count != 0)
                _project!.AddReferencedCompilationContext(context, "cxcore", syntheticCore: true);
        }
    }

    private void AddCoreStringTranscoding()
    {
        if (FindCoreClass("System.String") is not null) return;
        var owner = _types.Single(type => type.Declaration.FullName.ToString() == "System.String").Declaration;
        var result = new NamedType("System.WideString", []);
        result.SetResolvedType(new QualifiedIdentifier("System", "WideString"), "cxcore", ClassType.Class);
        var function = new FunctionDeclaration("ToWideString", owner.FullName, result,
            [MemberModifier.Public, MemberModifier.Extern], owner, @const: true);
        owner.MemberDeclarations.AddDeclaration(function);
        _symbols.Add(new FunctionSymbol("cxcore", function.FullName, [], result, declaration: function));
    }
}

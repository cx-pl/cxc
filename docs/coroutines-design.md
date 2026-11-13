# Coroutines and iterators

This document describes the source, runtime, and native I/O contracts for
iterators and cooperative async execution in the C backend. Supported lowering
shapes are bounded; unsupported suspension, capture, and cleanup forms receive
compiler diagnostics.

## Synchronous iterator contract

An iterator method is an ordinary method whose body contains `yield return` or
`yield break` and whose declared return type is `System.Collections.IEnumerable<T>`.
The method call constructs a sequence object and captures its receiver,
parameters, and closure environment. It does not execute the body. Each call to
`GetEnumerator()` creates an independent iterator frame. The first call to
`MoveNext()` starts the body; subsequent calls resume immediately after the
last yield. Re-enumerating a sequence creates a fresh frame and reruns the body.
Sequence objects do not cache elements or promise replayability of external
resources.

`IEnumerator<T>` exposes `Current`, `MoveNext()`, and `Dispose()` through
`System.IDisposable`. `Current` is
valid only after a successful `MoveNext()` and before the next move or disposal;
invalid access raises `System.Exception`. `MoveNext()` returns false
after normal completion. Repeated moves after completion return false.
`Dispose()` is idempotent, runs active `finally` blocks once, and makes later
moves return false. Exceptions from the body are raised by `MoveNext()` after
cleanup has run; disposal does not rethrow an already-observed iterator fault.
Reentrant or concurrent advancement of one enumerator is invalid and diagnosed
by the runtime. Generated synchronous iterators reject reentrant `MoveNext()`
with `System.Exception` and reset the advancement guard even when the move throws.
Async iterators reject a second request while a move is pending and leave the
original request intact. Independent enumerators may be advanced independently.

`foreach` owns the enumerator returned by `GetEnumerator()` and disposes it on
normal completion, `break`, `return`, and exceptions. Cleanup exceptions follow
the existing `finally` transfer rules. The existing direct array and structural
enumerator `foreach` paths remain valid; disposal is emitted when the selected
enumerator contract has `Dispose()`.

Iterator methods may return normally (equivalent to `yield break`) but may not
return a value. `yield return` values use the ordinary implicit conversion to
`T`. `yield break` is legal only inside an iterator method. A method containing
yield cannot be `async`, an accessor, a constructor, or a local function in the
supported iterator contract. Yield is allowed in `try` only when it has `finally` and no `catch`;
yield in `catch` or `finally`, and yield in a `try` with `catch`, is rejected
with a source diagnostic. Yield inside nested local functions or lambdas does
not make the containing method an iterator.

## Shared lowering boundary

Parsing and binding retain `async` and explicit yield/await nodes in the shared
compiler model. Binding identifies iterator methods and validates their return
type and legal yield regions before backend generation. A backend-independent
coroutine plan records ordered suspension points, continuation targets, live
locals/parameters/receiver/captures, and active cleanup regions. C emission
consumes this plan to emit typed frame fields and create/resume/dispose entry
points; the plan must not depend on C names or C layout. Suspension state numbers
and frame representation are implementation details, not source or ABI
promises.

Semantic body/expression traversal now lives in `CoroutineSyntaxAnalysis`, not
the C emitter. Async-iterator await-form validation and generic specialization
discovery use this shared source-level traversal. `CoroutineSuspensionPlan` now
models source-level states, next/branch/yield/await/completion terminators, live
locals, captured aliases, and cleanup regions/edges without generated names or
frame layout. The plan is unit-tested without C generation and the synchronous
iterator lowerer consumes it for linear bodies and `if`/`else` branches. Its
initial builder conservatively computes values and captures used by reachable
continuations after each yield.

The existing iterator builder remains the fallback for loops, switch,
enumerators, and protected cleanup, and continues to handle async iterator state
construction. Those paths do not yet produce the shared plan's cleanup edges or
await states. Those paths retain their existing lowering; fields must not be
trimmed solely from the shared plan's current conservative liveness result.

For an iterator, state zero means not started, positive states resume after a
yield, and terminal/faulted/disposed states prevent further execution. Values
that are live across a yield are copied to typed frame fields. Captured locals
continue to use their shared closure storage so a suspension cannot split an
alias. Reference-bearing frame fields are traced from the rooted iterator
handle, and references whose live range ends are cleared when doing so does not
break captured aliases. No native `setjmp` handler frame survives a resume
return: each resume installs fresh native handlers for the logical protected
regions saved in the iterator state.

The lowering supports ordinary control flow and locals across yields,
including `if`, loops, `switch`, and nested blocks. It
builds explicit continuation blocks for top-level bodies containing expressions,
explicit local declarations, nested blocks, `if`/`else` branches, `while`,
`do`/`while`, and `for` loops, `break`/`continue`, yields, and void returns.
Switch sections can contain yields; the switch expression runs once at its
dispatch state, and each selected section continues at its own state. Yields
nested in array, string, and structural-enumerator `foreach` loops are lowered
with collection/enumerator state in the frame. Enumerator cleanup runs on
completion, early exit, faults, and disposal. A `try` with only a `finally` can
contain yields; active cleanup regions are persisted in the frame and unwind in
reverse order on completion, `yield break`, faults, and disposal. Protected
bodies may transfer through `break` and `continue`; the corresponding `finally`
runs before the target loop state. Catches and yields in `finally` remain
unsupported; the lowerer reports an iterator-shape diagnostic for these cases.
`Current` is guarded between successful moves, and a producer exception clears
the frame, marks it terminal, and is rethrown from `MoveNext()`.
Control-flow lowering must preserve left-to-right expression evaluation and
short-circuit behavior.
Unsupported liveness or capture forms receive diagnostics; they must not fall
back to stack locals that expire when `MoveNext()` returns.

### Current support boundary and diagnostics

The iterator contract uses `System.Exception` for an invalid `Current` read.
Reads before the first successful move, after a false move, and after disposal
throw; a read after a successful move returns the typed element.

The current executable entry point remains synchronous: top-level `Main` must
return `void` or `int` and take no parameters or one `string[]`. An `async Main`
returning `Task`/`Task<T>` is rejected by the existing entry-point signature
check. Use a synchronous `Main` to initialize/drain the scheduler explicitly;
Async entry points are not supported. Preserve
all current synchronous `Main` forms and their return codes.

Suspension inside a Task `try`/`catch`, catch body, or exception filter is not
part of the supported Task contract. The compiler reports a specific diagnostic
for these shapes. A fault from a supported resumed await completes the returned
Task with the original exception; callers can catch it when they retrieve the
result. An enclosing catch does not catch a fault across suspension. Filters remain
synchronous. Async iterators may use the
currently implemented switch-label filter behavior, including fault cleanup;
that does not imply Task catch/filter suspension support. Task cleanup supports
two bounded no-result forms: a top-level try/finally with a synchronous body and
one or two sequential awaited cleanup tasks, and a nested pair of try/finally
regions with one awaited cleanup in each. The nested form unwinds inner then
outer on success, fault, or cancellation; a later outer cleanup fault or
cancellation overrides a pending inner cleanup failure. Other awaited Task
control-flow shapes receive source diagnostics. Minimal boundary examples:

No-result `Task` also supports one narrow protected-body form: a top-level
`try` with one direct await of a parameter and a synchronous `finally` body.
The continuation executes the protected `GetResult()` and finally as one
exception region, then completes, faults, or cancels the outer task after
cleanup. Success, faults, and cancellation complete only after cleanup. Awaited cleanup
in the same try, body statements around the await, and catches remain deferred.

```cx
// Supported: synchronous entry point drives the single-thread executor.
public int Main() { TaskScheduler.Run(); return 0; }

// Supported: bounded async Task consumer; disposal is awaited before completion.
public async Task Consume(IAsyncEnumerable<int> source) {
    await foreach (int value in source) { }
}

// Supported: a synchronous prefix and final result after the loop.
public async Task<int> Last(IAsyncEnumerable<int> source) {
    int last = -1;
    await foreach (int value in source) { last = value; }
    return last;
}

// Rejected: suspension across a Task catch region is not supported.
public async Task Recover(Task<int> source) {
    try { int value = await source; }
    catch (Exception) { await Task.Delay(1); } // rejected with a catch-region diagnostic
}
```

## Task and async-sequence runtime contract

Tasks are stable, multi-await handles. An async method starts synchronously at
invocation and runs until it completes or reaches an await whose task is still
pending. Completed awaits continue inline. A single-thread executor owns ready
continuations and timers; it does not create worker threads. Faults propagate
from `await`; cancellation is cooperative through `CancellationToken`.
Protected bodies have bounded awaited-cleanup support; other async cleanup and
control-flow forms remain unsupported. `CancellationTokenSource` callbacks run
once on cancellation, and callbacks registered after cancellation
run immediately. `CancellationTokenRegistration.Dispose()` unregisters a callback
before cancellation. Canceled tasks remain distinct from faulted tasks and throw
`OperationCanceledException` from `GetResult()`. `Task.Delay(milliseconds,
token)` is cancellation-aware; canceling its token completes the task as
canceled, while the queued timer callback safely observes that completion.
An `OperationCanceledException` escaping synchronous execution of an async
`Task`/`Task<T>` method, including an inline completed await, produces a canceled
outer task. The generated cancellation handler precedes the ordinary fault
handler, matching cancellation after suspension. Other exceptions retain the
existing fault and original-exception behavior.
Socket I/O accepts a cancellation token and resolves cancellation/readiness on
the owner executor. Unobserved faults are reported at executor shutdown.
`TaskScheduler.RunUntil` is the explicit owner-thread join; a pending task with
no callback, timer, or readiness source is rejected rather than blocking
forever.

### Connected-socket I/O contract

The native I/O contract covers connected stream sockets only. Regular
files, DNS, accept/connect orchestration, pipes, TLS, and worker-backed blocking
operations stay out of scope until their platform and threading contracts are
specified. Expose an owned `System.Net.Sockets.Socket` with `ReadAsync` and
`WriteAsync` methods returning `Task<uint>`; each call performs at most one
nonblocking transfer and reports the transferred byte count. A read returning
zero means peer EOF. A write may complete with fewer bytes than requested, so
callers that need a full write loop until all bytes are sent. Empty buffers
complete inline with zero bytes. The API must not imply that an entire message
or buffer is transferred atomically.

Sockets use nonblocking mode. Linux waits for readiness with `poll`/`epoll`; the
initial Windows implementation uses Winsock `WSAPoll`. The scheduler's wait
path combines the nearest timer deadline with pending socket readiness, then
performs the actual `recv`/`send` attempt and runs completion callbacks on the
CX owner thread. This keeps managed state, task completion, and GC access on
the registered thread without a worker pool. `RunOne()` may block while waiting
when no ready callback exists, but `Post()` remains immediately runnable.
Platform code owns readiness registration and error translation; it does not
retain CX callbacks or managed pointers.

Each pending operation owns a rooted native operation record containing the
socket handle, operation kind, buffer reference, offset/length, task completion
source, cancellation registration, and terminal state. The record and buffer
remain live until the native readiness registration is removed and the task is
completed. `ReadAsync`/`WriteAsync` borrow the caller's buffer until completion;
the caller must not mutate or free it while pending. Disposing the socket
cancels its pending operations and closes the native handle after readiness
registrations are detached.

Cancellation is cooperative and races with readiness on the CX owner thread.
Whichever terminal event is processed first wins: cancellation completes the
task as canceled and removes readiness interest; a transfer already completed
reports its byte count. A late readiness notification for a canceled or closed
operation is discarded after checking its terminal state and generation. A
socket error faults the task with a translated exception; EOF is not a fault.
No native callback may dereference a managed pointer or complete a task from an
OS completion thread. These rules define the implemented connected-socket
slice. Accept/connect, DNS, TLS, and worker-backed blocking operations remain
out of scope.

`IAsyncEnumerable<T>` is lazy and pull-driven. Each `MoveNextAsync()` requests
at most one next value; `await foreach` awaits advancement and disposal on all
exits. Cancellation during advancement and exceptions from the producer are
observed by the consumer. It is distinct from `Task<T[]>`, which represents one
materialized result.
A producer performs no work before its first move and advances only on demand,
without prefetching between requests. Independent enumerators keep independent
state. A pending continuation retains its suspended frame, and escaped closures
retain their captured state after iterator disposal.

## Task storage and scheduler lifecycle

`System.Threading.Tasks.Task<T>` is a stable reference class backed by
GC-visible typed result storage; generic results are not boxed into a shared
object slot. `TaskCompletionSource<T>` and the no-result `TaskCompletionSource` expose
pending task handles and complete them once with a value/void result or
exception. `Task` is an alias for a reference-type `TaskHandle` backed by the
same non-generic GC-visible completion state as `Task<T>`. Async methods still
run inline until the supported await form; completed awaits call `GetResult()`
inline. `TaskScheduler` provides a rooted, single-thread FIFO ready queue, and
`PostAfter` stores one-shot callbacks by monotonic deadline on Windows and
Linux. `RunOne` waits for the next timer when the ready queue is empty. The
scheduler claims its owner on first access; all later `Post`, timer, readiness,
cancellation, and run-loop operations must come from that OS thread. Callback
code may post more work, but recursive `Run`/`RunOne` is rejected with
`System.Exception`. Callback exceptions leave dispatch state before being
rethrown, so a caller may catch the failure and continue using the scheduler.
Pending
tasks support multiple completion continuations, with late registrations posted
immediately. `TaskScheduler.RunUntil(object)` explicitly retains one supplied
`TaskHandle` or `Task<T>` while pumping the owner executor until that task
completes. The caller then observes the result, fault, or cancellation through
`GetResult()`. A pending task with no queued callback, timer, or readiness source
raises a no-completion-path exception; an empty queue is not treated as task
completion. Tasks remain detached unless the caller explicitly joins them.
Generated native entry points call `TaskScheduler.Shutdown()` after synchronous
`Main` returns. Shutdown cancels pending socket readiness operations through
their registered cleanup callbacks, drops future timers and their scheduler
roots, then drains ready work to quiescence. `Post` remains available while that
drain runs so completion callbacks can enqueue follow-up work; `PostAfter` runs
as ready work during shutdown, and socket registrations receive their
cancellation callback immediately. New work is rejected after shutdown
completes. Faulted tasks remain rooted by the unobserved-fault registry until
`GetResult()` observes them; shutdown throws the original exception for the
first fault that remains unobserved. Cancellation is not reported as a fault.
No finalizer is involved.

## Supported await shapes

Pending `Task<T>` forms include direct `return await` and supported top-level
awaited-local chains with synchronous continuation statements and a final
return. Selected conditional branches and top-level loop or switch forms can
suspend, subject to the lowerer's operand, prefix, and cleanup restrictions.

For supported top-level loop forms, the awaited operation begins the loop
body and the continuation is synchronous. A trampoline processes already
completed sources without recursive stack growth. Awaits in loop conditions,
unsupported nested control flow, and Task catch/filter regions receive source
diagnostics.

Selected `ValueTask<T>` return-await and awaited-local forms also work with
public imported producers. Completed paths continue inline; pending paths
retain their live results, prefixes, and captures. Generic support is bounded
by the operand and body shape and does not imply arbitrary generic async bodies.

See the [language reference](language-reference.md#statements-and-control-flow)
for source syntax and restrictions and the
[task representation guide](task-performance-design.md) for consumption and
storage contracts.

## Async enumerator ownership

`IAsyncEnumerator<T>.MoveNextAsync()` returns `ValueTask<bool>`, and
`IAsyncDisposable.DisposeAsync()` returns `ValueTask`. A generated async
enumerator owns reusable move state. Consume each move result before requesting
the next one; overlapping moves are invalid.

The consumer must await disposal after completion or early termination.
Disposal, fault handling, and cancellation retain the frame and any borrowed
buffer until cleanup has finished. Reusable iterator state does not own an
independent socket readiness registration; each operation keeps its own native
lifetime obligations.

## Implementation boundaries

Logical suspension state is backend-independent where the shared plan supports
the source shape. Frame layouts, state numbers, specialized fast paths, and
native stack usage are implementation details rather than stable source promises.

Generated modules and runtime libraries must be rebuilt together when their
experimental ABI changes. Public cross-module reflection is limited to the
runtime's supported metadata and compiled invocation adapters; it does not
authorize arbitrary dynamic generic construction.

Keep internal delivery tracking, audit histories, acceptance reports, and
dated performance captures outside the source repository. Reproduction
instructions are in the
[coroutine benchmark guide](coroutine-performance-baselines.md).

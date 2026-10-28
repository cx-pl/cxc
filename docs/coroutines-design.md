# Coroutines and iterators design

This document records Milestone 18's source, runtime, and native I/O contracts
for the C backend. Synchronous iterators, cooperative Tasks, the connected
socket API, and async iterators have implemented slices; the acceptance notes
below distinguish verified shapes from remaining restrictions.

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
invalid access currently raises `System.Exception`; the specific
`InvalidOperationException` type is not yet implemented. `MoveNext()` returns false
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
first slice. Yield is allowed in `try` only when it has `finally` and no `catch`;
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
await states. Expand plan construction and migrate those paths in later B/C/D
slices; do not trim fields based on the current conservative liveness result.

For an iterator, state zero means not started, positive states resume after a
yield, and terminal/faulted/disposed states prevent further execution. Values
that are live across a yield are copied to typed frame fields. Captured locals
continue to use their shared closure storage so a suspension cannot split an
alias. Reference-bearing frame fields are traced from the rooted iterator
handle, and references whose live range ends are cleared when doing so does not
break captured aliases. No native `setjmp` handler frame survives a resume
return: each resume installs fresh native handlers for the logical protected
regions saved in the iterator state.

The first implementation target is ordinary control flow and locals across
yields, including `if`, loops, `switch`, and nested blocks. The current lowering
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

The current iterator contract intentionally uses `System.Exception` for an
invalid `Current` read because the core library does not yet provide the
specific `InvalidOperationException` type. Keep this behavior consistent in
tests and documentation until that type is added as a separate library/API
change. Acceptance cases are: read before the first successful move, after a
false move, and after disposal; each must throw, while a read after a successful
move returns the typed element.

The current executable entry point remains synchronous: top-level `Main` must
return `void` or `int` and take no parameters or one `string[]`. An `async Main`
returning `Task`/`Task<T>` is rejected by the existing entry-point signature
check. Use a synchronous `Main` to initialize/drain the scheduler explicitly;
async entry-point integration is deferred to executor lifecycle work. Preserve
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
cleanup. Pending success, fault before/after suspension, cancellation, forced
GC, and exactly-once cleanup have Windows x64 native coverage. Awaited cleanup
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

The native I/O slice covers connected stream sockets only. Regular
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
The native async iterator suite verifies that a counting producer performs no
work before its first move, performs one read for each request, and does not
prefetch between requests. Independent enumerators keep independent state; a
forced-GC test also verifies a pending continuation retains its suspended frame
after the caller drops the enumerator reference. Escaped closures retain captured
state after iterator disposal.

## Iterator acceptance gate

### Current implementation status

The compiler currently lowers top-level iterator methods with straight-line
execution, conditional branches, and `while`/`do`/`while`/`for` loops that may
contain yields to a generated class implementing `IEnumerable<T>` and
`IEnumerator<T>`. Its
frame stores typed parameters and explicit local values; each `GetEnumerator()`
makes a fresh frame, and `MoveNext()` dispatches through explicit states. Native
coverage checks lazy execution, local retention across yields, conditional
execution before a yield, yields in both sides of a conditional, continuation
after those yields, while/for loop advancement and continue behavior, and two
independent enumerations of the same sequence. A native fixture also covers
`do`/`while` continuation after `continue`.
`foreach` emits `Dispose()` in a
`finally` block for compatible enumerators, and the core collection enumerators
clear retained references when disposed. Generated iterators clear the current
value and live locals on completion or disposal while retaining captured
parameters for a fresh enumeration. Static and instance iterator methods on
non-generic classes are lowered; instance frames retain their receiver and pass
it to fresh enumerators. Iterators on generic classes and instance iterators on
structs receive an explicit unsupported-shape diagnostic. Local functions and
captured locals remain unsupported.
### Completed-task slice status

The [task representation and allocation plan](task-performance-design.md) defines
the subsequent typed-result and value-task optimization work. The reference
task representation described here is the current implementation, not the
final allocation target.

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
No finalizer is involved. Pending
`Task<T>` suspension supports `return await task;` and one
or more top-level inferred locals initialized by `await task`, surrounded by
synchronous statements and one final return. Conditional return paths after an
await are supported when they do not cross enumerator-disposal or `finally`
cleanup. A top-level `if/else` whose two arms each await their selected `Task<T>`
and return its result, or await their selected no-result `Task`, selects exactly
one operand before using the existing pending/completed relay path. For non-generic
`Task<T>` methods, each arm may also begin with one or more contiguous inferred
awaited-local declarations, then run synchronous continuation statements before
returning; the saved condition selects the matching continuation after resumption.
Later selected-task operands must not refer to locals declared in the branch.
Both branch choices have direct and awaited-local compiler coverage; Windows x64
native coverage checks two pending awaits in one arm, completion in sequence,
completion of the unselected arm's sources, forced GC while suspended, and a
condition side effect that runs once. Selected completed-source paths complete
inline for the single-await case. Awaited-local branches with intervening
statements between awaits remain unsupported. A non-generic `Task<T>` method
also supports one top-level `while` immediately before a final result return.
The loop body begins with one inferred awaited local followed by synchronous
statements. `while` checks its condition before each body; `do/while` awaits its
first body before checking the condition; `for` runs initialization once,
checks the condition before each body, and runs iterators after each resumed
body including `continue`. A trampoline processes completed sources inline
without recursive stack growth. `break`, `continue`, and early result return
are supported in blocks and `if` branches. Native coverage checks zero, one,
and two `while`/`for` iterations, pending and completed sources, forced GC,
each transfer, body-first `do/while` iterations, and 4,096 inline-completed
iterations. No-result `Task` methods support the same top-level loop forms when
the body starts with a standalone await and continues synchronously, and the
method may end at the loop or use an explicit `return;`. Native coverage
exercises pending/completed sources, forced GC and transfers for each loop form;
the while trampoline stress uses 4,096 inline-completed awaits. Awaits in the condition or task
operand, generic methods, and continuations containing any nested loop/foreach,
switch, try, or local function statement remain unsupported and receive a source diagnostic.
A separate top-level switch slice supports `Task<T>` and no-result `Task`
methods with integer selectors, unfiltered integer literal labels, one default,
and one awaited local or standalone await at the start of each section.
Synchronous statements may follow the await, including assignment and explicit
`break`; a result method may return after the switch. The selector is cached
once and reused after resumption, and only the selected case task is awaited.
Filters, string or enum labels, fallthrough, multiple awaits per section, and
other awaited switch shapes remain unsupported and are diagnosed. Other
Non-generic `Task<T>` methods also support a limited set of returned expressions
with one direct await: a direct identifier call argument (including one awaited
argument among several), one binary operand, one arm of a conditional, the right
side of `&&`/`||`, or the fallback of `??`. The lowering preserves source order,
evaluates conditional and short-circuit operands lazily, and keeps the `??` left
operand to a local or parameter as required by the current language contract.
Pending and completed paths have Windows x64 native coverage, including forced
GC. These are bounded forms; member-call targets, multiple awaits in one
expression, and other nested-await shapes receive a source diagnostic. Direct
top-level awaited locals continue through their existing continuation path.
Other awaited Task forms outside these lowerings remain limited and may not be
used as general nested expressions. Locals used after each await are
retained by chained continuations that complete or fault the result task;
pending sources resume through the queue and completed sources run inline. The short
form `var result = await task; return result;` uses a direct relay. Generic
async functions support pending direct relays after specialization. A bounded
generic `Task<T>` shape also supports two sequential top-level locals awaited
from `Task<T>` parameters, for generic methods and generic class owners. Its
closed specializations retain callback-only aliases and typed locals/results;
captured types are restored in reverse nested-lambda order during emission.
Native coverage includes int, string, and reference-containing struct results,
an imported producer/forwarder, forced GC, and pending fault identity and
cancellation across Windows x64/x86 and Linux x64 static/shared builds. This
does not imply arbitrary generic suspension or longer chains. No-result async methods
may suspend at one or more top-level `await task;` statements around synchronous
statements, forwarding completion or faults to the returned task. `Task.Delay`
composes this handle with `PostAfter`. Native tests
cover success/fault forwarding, multiple and late subscribers, delay completion,
static and DLL project references, and forced collection while queued callbacks
retain captured objects, including both outcomes of conditional returns after a
pending await. Cancellation tokens, callback registration, canceled task states,
cancellation propagation through awaits, and token-aware delays are covered
natively. Async `Task` supports a top-level `try` with no catches and a
synchronous protected body followed by one or two standalone awaits in `finally`.
The returned task stays pending until cleanup finishes; pending GC, completed
cleanup, and faults or cancellation in either cleanup await are covered natively. A nested
synchronous protected region in the body runs its `finally` before the outer
cleanup begins. A protected
body fault is forwarded after successful cleanup, while a cleanup fault or
cancellation takes precedence. The Windows x64 native entry-point fixture covers
successful cleanup, delayed protected-body fault propagation, cleanup-fault
precedence, and cleanup cancellation. A nested awaited `try/finally` form now
awaits the inner cleanup before evaluating/awaiting the outer cleanup. It keeps
an inner cleanup fault or cancellation pending while the outer cleanup runs;
outer cleanup fault or cancellation takes precedence. Windows x64 coverage tests
pending and completed nested cleanups, forced collection while each cleanup is
pending, inner fault/cancellation, outer fault/cancellation, and pending-exception
retention. A focused Linux x64 fixture also verifies
early disposal runs a protected synchronous iterator's `finally` exactly once,
forces GC while awaited cleanup is pending, and passes the same success/fault/
cancellation cases in static and shared linking; the shared executable passes
`ldd -r`. No-result `Task` also supports one direct parameter await inside a
top-level protected `try` with a synchronous `finally`; pending success/fault/
cancellation and exactly-once cleanup have Windows x64 native coverage. A
terminal `return;` in the exact top-level protected body shape is also supported:
its awaited cleanup completes before the no-result task completes. Pending cleanup
across forced GC and an already-completed cleanup pass the Windows x64 harness.
Bounded `Task<T>` forms also support a sole returned expression in a protected
body with one awaited cleanup, including a nested pair of awaited cleanup regions. It evaluates and saves the expression
once before suspension. The Windows x64 native fixture covers pending cleanup
across forced GC, completed cleanup, body-fault retention after successful cleanup,
and cleanup fault/cancellation precedence over a body fault. Nested typed return
coverage also verifies pending/completed inner and outer cleanup, forced collection
while suspended, retained inner fault/cancellation, outer fault/cancellation
precedence, and exactly-once return evaluation. No-result Task and Task<T> top-level loop-local break/continue also pass through
one awaited cleanup. Native cases cover pending and completed cleanup, forced GC,
and fault/cancellation propagation. C3 is complete for these bounded shapes;
arbitrary protected-body/control-flow forms remain unsupported with diagnostics.
Other awaited-`finally` and protected-body suspension shapes receive an unsupported-
form diagnostic. Awaits in nested expressions remain unsupported.
Asynchronous I/O
is available through the separate `cxnet` socket library. Async sequences
support lazy completed-task advancement, cancellation, asynchronous disposal,
and producer fault propagation. Async iterators also support direct
`await task;` statements and one local initialized by `await task`; its type may
be explicit or inferred with `var`.
Completed tasks continue inline; pending tasks resume through their task
continuation, with awaited result, fault, and cancellation propagation covered
by native tests. Async iterators may also use `yield return await task`; the
await completes into the yield state without repeating prefix statements.
Awaited expressions in other expression positions remain unsupported.
Cancellation tokens cancel pending advancement immediately,
and `DisposeAsync()` cancels an in-flight advancement before cleanup; late source
completion is ignored. Native tests cover both paths. Nested await expressions
and multiple declarators in an await-initialized declaration remain
unsupported. Async iterator bodies now lower nested `await foreach` over
`IAsyncEnumerable<T>` into continuation states. Each `MoveNextAsync()` is awaited
without blocking, the consumer cancellation token is passed to
`GetAsyncEnumerator`, and normal completion, `break`, and `return` await the
inner `DisposeAsync()` before the outer iterator resumes or completes. A native
test verifies that `break` awaits disposal and runs an inner iterator's `finally`
exactly once. A Linux x64 native fixture now verifies the same break/disposal
ordering after forced GC in both static and shared linking. It also verifies a
consumer fault after a yield disposes the inner enumerator before the original
exception is rethrown. This first slice is
limited to async iterator methods; async `Task` and `Task<T>` consumers now
support one top-level `await foreach` over an `IAsyncEnumerable<T>` parameter.
The loop may follow synchronous prefix statements; `Task<T>` consumers may
return a synchronous expression immediately after the loop. Loop bodies support
synchronous statements and top-level break, continue, return, or throw. Awaited
expressions in the body, nested loops, and more general control flow remain
unsupported and receive diagnostics. The consumer calls `GetAsyncEnumerator`
with `CancellationToken.None`; a canceled or faulted `MoveNextAsync()` still
awaits exactly-once `DisposeAsync()` before the returned task is canceled or
faulted. Native tests cover pending advancement, returned values, break cleanup,
body-fault cleanup, forced GC, and disposal precedence. An awaited finally block
that protects a yield remains unsupported and receives a specific diagnostic;
synchronous nested finally blocks remain available. Exceptions from loop
operations, branch and yield expressions, switch-selector evaluation, and
`MoveNextAsync().GetResult()` now suspend through inner `DisposeAsync()` before
rethrowing. Native tests verify producer and consumer faults both run an inner
iterator's `finally` before the caller observes the fault. Switch-label filter
exceptions also await inner disposal before rethrow; the native fixture covers
that path. Fault cleanup ignores cancellation until pending inner disposal finishes,
then rethrows the original producer fault; a native test covers that ordering.
Pending-disposal tests verify the outer move remains pending until inner
`DisposeAsync()` completes and that disposal runs exactly once. Pending awaits now
resume in a separate state so their operation prefix is not executed again. A
second move request while an advancement is pending fails, leaving the first move
intact. A forced-GC test drops the caller's enumerator reference while an async
iterator is suspended, collects, then completes its awaited task and verifies the
iterator frame resumes with its value intact. The frame-rooting case passes on
Windows x64 and Linux x64; Linux generated C passes with both static and shared
runtime linking.
Linux x64 `cxcore` (29 tests) and `cxnet` (1 test) also pass in shared and static
runtime builds. Native tests pin down cancellation ordering: cancellation wins if
it runs after the awaited source completes but before its queued continuation
resumes; cancellation after a successful resume leaves the move result and current
value intact. A native test forces collection after an awaited source
faults but before its continuation resumes, then verifies the original exception
message survives. Another interleaves a faulting and successful iterator and checks
that the success result remains intact. Forced-GC tests drop the enumerator while a
closure captures a local across suspension, and after disposal verify an escaped
closure still reads its captured value. These exception, interleaving, and closure
tests pass with static and shared runtime linking on Windows x64 and Linux x64.
Iterator capture cells alias frame fields so closure aliases remain valid across
resumes and disposal; captured fields are retained while escaped closures can still
reference them. An additional Windows x64 native fixture creates a class instance
inside an async iterator, suspends on a pending task, forces collection, then reads
the instance field after resumption. The reference is retained through the iterator
frame and the yielded field value is correct. A second fixture mutates a captured
local after suspension and verifies the closure alias observes the mutation; an
escaped closure also retains its captured value after iterator disposal and forced
collection. The full `run-entry-point-e2e.ps1` harness passes with both cases.
A cancellation test triggers `Cancel()` synchronously while an async
iterator evaluates its await operand; the cancellation registration reenters the
resume path, cancels the move, and ignores later source completion. It passes with
static and shared runtime linking on Windows x64 and Linux x64. The full Windows
x86 entry-point harness also passes, including pending async iterator and
`await foreach` fixtures. That run exposed a generic `T[]` `Get` specialization
that used pointer-sized element access; the C emitter now uses the closed element
layout and a compiler regression test covers the `long` case. A Windows x64
full-harness case pre-registers a source callback that cancels during source
completion, before the iterator's queued await continuation runs. It verifies
both callbacks run once, cancellation wins, no value is published, and disposal
still succeeds. Other reentrant source/task callbacks and broader event-
interleaving stress remain open. This does not satisfy the async-sequence
acceptance gate.

Async iterator cleanup now has Windows x64 coverage for a faulted or canceled
`DisposeAsync()`, nested async enumerators, repeated disposal, disposal before
the first move, and disposal while advancement is pending. Nested cleanup counts
are exact: disposing an outer iterator that consumed two inner iterators runs
three finalizers total. If cleanup succeeds, an original `Current` or producer
fault is preserved; a cleanup fault overrides an original `Current` fault, and
cleanup cancellation makes the outer move canceled. Cancellation arriving while
producer-fault cleanup is pending does not replace the producer fault. These are
verified shapes, not a claim that every possible nested fault/cancellation
interleaving has been covered.

The native scheduler now also has an internal socket-readiness queue. Its
Windows wait uses Winsock `WSAPoll`; Linux uses `poll`. `RunOne()` waits on all
queued socket interests with the next timer deadline as its timeout, then moves
ready callbacks to the ordinary FIFO queue so they execute on the CX owner
thread. The callback queue is GC-rooted. Windows x86/x64 loopback tests cover
readable and writable interests, simultaneous interests, timer deadlines while
a socket is pending, and removal by registration id before readiness. Removing
a socket wait is the scheduler primitive needed for later cancellation and
close handling. A forced-GC case confirms that a callback's captured object
stays alive until readiness dispatch. Linux platform, scheduler, and test
sources pass strict GCC syntax checking; the scheduler readiness test and
`cxnet` integration test also pass under CMake 3.31/GCC 11.4. The public
`System.Net.Sockets`
API and native socket operation implementation now live in the separate
`cxnet` library, which references `cxcore`. Core retains the scheduler readiness
queue and exports its registration/cancellation bridge; rooted operation
records, socket ownership, cancellation/close handling, and platform transfer
functions are in `cxnet`. The `Socket` implements `IDisposable`. CX emission and
the Windows native runtime build succeed. Windows x86/x64 and Linux loopback tests pass
for pending reads and task byte counts, immediate writes, EOF, cancellation,
invalid buffer ranges, and closing a pending operation. Linux also observes a
partial write from one transfer; Winsock may accept the full test buffer, so its
test checks that the returned count is within the request. A forced-GC test
drops the caller-local pending-read buffer reference, scrubs the stack,
collects, and verifies that the operation retained and wrote the buffer.
This passes on Windows x86/x64 and Linux x64. The complete `cxcore` CTest suite
passes on Windows x64 (28/28) and Linux x64 (29/29); `cxnet` passes in shared
and static runtime builds on Windows x64 and Linux x64, and in the shared build
on Windows x86. The generated streaming-read consumer also builds on Linux x64
with static and shared project dependencies using GCC 11.4/CMake 3.31.6; shared
modules pass `ldd -r`. Static linking initially failed because the shared
consumer received non-PIC static archives, so generated CMake now enables PIC
for Linux static-link projects. The Linux static and shared consumers now pass
GCC 11.4 with `-Wall -Wextra -Werror -Wno-unused-parameter`; generated reflection
invoker declarations that are intentionally not referenced by a runtime invoker
table carry the portable `CX_MAYBE_UNUSED` annotation. The gate needs no
unused-function suppression, and `ldd -r` resolves the shared dependencies.
Strict-warning generated-C compilation passes on Windows x64 and x86 for
`cxcore`, `cxnet`, and the streaming consumer in static and shared module modes.
MSVC uses `/std:c11 /W4 /WX`, with `/wd4100` for unused generated thunk
parameters, `/wd4130` for intentional constant comparisons, and `/wd4702` for
unreachable coroutine continuation paths; all other warnings remain errors.
A socket late-event test queues
three readable operations in one poll, executes the first, cancels the second
after its callback has moved to the ready queue, and closes the socket while the
third callback is queued. After forced collection and scheduler drain, it verifies
both queued readiness callbacks were removed, both canceled tasks and buffers
remain unchanged, and all three task continuations run once. The interleave runs
for 32 rounds per test invocation. The repeated version passes on Windows x64,
Win32, and Linux x64, each with static and shared linking; the Linux shared
executable also passes `ldd -r`. Other socket close/cancel races remain to be
verified.

- Add parser/model and semantic coverage for iterator signatures, value
  conversions, invalid contexts, return rules, and yield/finally restrictions.
- Verify laziness, repeated and independent enumeration, normal completion,
  early exit, explicit disposal, exceptions, and exactly-once cleanup.
- Exercise primitive, reference, and struct values without mandatory boxing,
  captured state, evaluation order, loops, and GC while suspended.
- Exercise strict-warning native C on Windows x86/x64 and Linux x64, and iterator
  handles across project and DLL boundaries.
- Confirm active native exception handlers never survive a return from a
  suspended resume function.

Task, I/O, and asynchronous-sequence gates remain those in
[`../../CX_ROADMAP.md`](../../CX_ROADMAP.md); they do not become complete when the
iterator gate passes.

## Cross-module reflection boundary (Milestone 18 F2)

The current targeted cross-module probe confirms that `TypeInfo.Name` and
`TypeInfo.Invoke` can be used for a public library type in the tested static and
DLL configurations. `TypeInfo.Fields` and `TypeInfo.Functions` are not populated
as usable public reflection collections by the current runtime/compiler output;
do not use their enumeration as an ABI acceptance check until that metadata
surface is implemented. This is a recorded limitation, not a claim that the
full cross-module iterator/task matrix has passed. The reproducible F2 fixture
is `tests/native/run-cross-module-abi.ps1`, with static/shared coverage recorded
in the Milestone 18 checklist.

The bounded one-await local-result form can consume a public imported
`ValueTask<T>` producer once, check completion, and defer conversion and relay
state until it suspends. Cross-project native coverage includes integer and
reference-containing struct results, inline allocation counts, pending forced
GC, original fault identity and cancellation on Windows x64/x86 and Linux x64,
static/shared. Export discovery retains the generic templates needed by closed
support instances even after synthetic core contexts are removed. Compiler
regressions check unique emitted generic method/type ownership for int, string
and struct results. See
`tests/native/results/milestone18-g4-imported-valuetask-ownership-20261021.txt`
for exact results and the remaining native regression/intermittent diagnostics.

The imported ValueTask first-source optimization also covers a bounded
non-generic `Task<int>` consumer with two adjacent inferred awaited locals and
a terminal result: the first source is a uniquely resolved ValueTask<int>
producer invocation; the second is a Task<int> parameter. This slice has no
synchronous prefix. The producer runs once before readiness; completed values
are consumed inline and pending first sources use `AsTask()` on that same
materialized value. Relay and callback state is allocated only in a pending
branch. Native static/shared checks on Windows x64/x86 and Linux x64 cover
either/both awaits pending, GC at both suspensions, faults and cancellation
before/after method invocation, and Task-backed/reusable-source-backed values.
Inline success creates only the returned persistent Task. Prefixes beyond one
explicitly typed local and other multi-await imported ValueTask forms remain
outside this optimization.
See `tests/native/results/milestone18-g4-twoawait-inline-cancellation-20261021.txt`.

One explicit or inferred prefix local is now supported for that two-await
imported ValueTask form. Compiler and native checks cover integer locals and a
reference-containing `TaskPayload`; pending continuations retain a typed
callback alias and survive forced collection. The inline branch uses the
original local and allocates only the returned Task. Explicit prefixes have
inline, pending-first, pending-second and both-pending native coverage; inferred
prefixes have inline and pending-first coverage. Multi-statement prefixes
remain unsupported. See
`tests/native/results/milestone18-g4-twoawait-prefix-20261021.txt`.

## Current async-enumerator return contract (Milestone 18 G6; partial)

`IAsyncEnumerator<T>.MoveNextAsync()` returns `ValueTask<bool>` and
`IAsyncDisposable.DisposeAsync()` returns `ValueTask`. A generated async
enumerator owns one reusable move source; the consumer consumes each result
before requesting the next move. Completed move/disposal paths have a Windows
x86/x64 static/shared fixture that measures zero CX allocations after the
enumerator is created. Pending socket moves, cancellation and EOF pass in the
same matrix. The socket's pending transfer still uses `Task<uint>` and the
reusable iterator source does not own that readiness registration. Linux,
source-backed `AsTask()` conversion, and pending callback/reentrancy allocation
budgets remain open, so this is not the full G6 gate.

### G7 async iterator callback environment sharing (2026-10-21)

For an async iterator await that registers the same resume body with both cancellation and completion, lowering creates one immutable state-local delegate and passes it to both registrations. The environment retains the generated iterator pointer, preserving the GC root for the suspended state machine. Fault-cleanup awaits have only one registration and retain their standalone callback. On the captured branch/pending-await fixture, this removes one environment object per pending await: lifecycle totals move from 11 to 10 objects, saving 24 requested bytes on x64 and 12 on x86. Frames remain 120 bytes x64/Linux and 68 bytes x86. The native GC, branch-value and exactly-once cleanup assertions pass on Windows x64/x86 and Linux x64 static. This is a bounded single-shape result and does not set a general frame/allocation budget. Exact commands, roots and source hashes: `tests/native/results/milestone18-g7-async-callback-environment-sharing-20261021.txt`.

**G7 socket readiness-item audit (2026-10-21; bounded slice complete):** the earlier fresh Windows socket-test crashes came from a stale six-argument `BeginTransfer` fixture ABI; the native declaration and two calls now match the seven-argument CX/C entry point. Scheduler item completion, timer and socket payloads share an exclusive union, with inactive pointer-bearing state cleared at timer, readiness, shutdown and dispatch transitions. Test-only layout assertions confirm 112 bytes on x64 (from 136) and 64 bytes on x86 (from 80). Continuation hook, scheduler lifecycle and socket integration tests pass 3/3 in Windows x64/x86 static/shared, and full Linux runtime/socket CTest passes 36/36. ASAN conflicts with the conservative stack scanner's stack-redzone reads and is not counted as a pass. Broader G7 frame/code-size/copy/allocation budgets remain open. Full commands/results/hashes: `cxc/tests/native/results/milestone18-g7-readiness-item-audit-20261021.txt`.

### G4 generic direct ValueTask await (2026-10-21; bounded check)

Generic `async Task<T>` methods can directly await a `ValueTask<T>` parameter
with the inline readiness check before AsTask conversion and relay creation.
Generic method closure assigns constructed identities to nested generic named
types, and emitted local/lambda functions plus captured local types are closed
with the enclosing method arguments before callback environments are written.
The native fixture verifies one output Task allocation for inline int, pending
int and reference-containing `TaskPayload` results after forced collection,
pending fault identity, and cancellation propagation. Compiler build and all
963 compiler tests pass; native cross-module CTest passes in Windows x64/x86 and
Linux x64 static/shared. This validates the tested direct-await shape only;
general G4 escaped-state/copy budgets and B5 chained-generic suspension remain
open. Exact roots and commands are in
`tests/native/results/milestone18-g4-generic-direct-valuetask-20261021.txt`.

### G4 generic async ValueTask<T> return-await (2026-10-21; bounded)

The direct `return await source` ValueTask lowering now accepts generic methods.
Generic specializations emit the structured readiness and fault-conversion body,
using `TaskCompletionSource<T>.GetTask()` for typed fault backing. Native tests
verify zero allocations for inline int and large reference-containing results
(104 bytes x64, 52 bytes x86), pending int/large completion after forced GC,
fault identity, and cancellation. Compiler build passes with 0 warnings/errors,
the full suite passes 964/964, and native CTest passes all six Windows x64/x86
and Linux x64 static/shared cells; Linux shared `ldd -r` resolves both modules.
The bounded shape has one direct await, no prefix and no chain. General G4
escaped-state/frame/copy budgets remain open. Exact roots and commands:
`tests/native/results/milestone18-g4-generic-valuetask-relay-20261021.txt`.

The same generic direct `ValueTask<T>` return-await also accepts one explicit
source local initialized in the synchronous prefix. The source local remains
before the readiness test; inline large-value completion allocates nothing, and
pending large-value completion survives forced GC in all six platform/link
cells. The compiler suite passes 965/965 and the build has 0 warnings/errors.
Multiple prefix statements and chained awaits remain outside this bounded form.
See `tests/native/results/milestone18-g4-generic-valuetask-prefix-20261021.txt`.

### G4 nested generic async ValueTask<T> relay (2026-10-21; bounded)

Closed async generic callers now bind nested generic method targets after
substituting the caller's type arguments. Invocations whose substituted types
remain open are skipped, and registration is limited to async generic callers
so existing generic forwarding and concept method resolution keep their current
paths. The one-local `Inner<T>` to `Outer<T>` chain returns an inline 104-byte
x64 / 52-byte x86 reference-containing struct with zero CX allocations; its
pending result survives forced GC. Build passes with 0 warnings/errors, the
compiler suite passes 966/966, and CTest passes all six Windows x64/x86 and
Linux x64 static/shared cells. Linux shared `ldd -r` resolves both modules.
Wider chains and general G4/G7 frame, copy and allocation budgets remain open.
Evidence and superseded probes:
`tests/native/results/milestone18-g4-generic-valuetask-nested-20261021.txt`.

### G4 two initialized locals before generic ValueTask<T> await (2026-10-21; bounded)

Generic body validation and the direct ValueTask<T> lowerer now preserve an
initialized-local-only synchronous prefix ending in the awaited source local.
The structural test checks both locals precede IsCompleted. Native inline large
reference-containing completion allocates zero CX objects; pending completion
survives forced GC in all six Windows x64/x86 and Linux x64 static/shared cells.
Build passes with 0 warnings/errors, the suite passes 967/967 and Linux shared
`ldd -r` resolves both modules. Prefix initializers containing awaits and
arbitrary synchronous statements remain outside this slice; broader G4/G7
budgets remain open. The first x86 attempt returned unrelated process result
635, then the retained-root retry and a fresh matrix passed. Full evidence:
`tests/native/results/milestone18-g4-generic-valuetask-two-prefix-20261021.txt`.

### G4 generic two-await ValueTask<T> relay (2026-10-21; bounded)

Generic `async Task<T>` now accepts two sequential awaited locals sourced from
`ValueTask<T>`. The all-inline path checks both sources before relay state is
created and allocates only the returned Task. Pending-first, pending-second and
both-pending cases preserve the returned first result across forced collection
and scheduler resumption. The generic source template's open relay callbacks are
not emitted; closed specializations emit their concrete typed helpers. Build
passes with 0 warnings/errors, the compiler suite passes 968/968, and CTest
passes in all six Windows x64/x86 and Linux x64 static/shared cells. Linux
shared `ldd -r` resolves both modules. Existing generic-pointer/linkage warnings
remain. This bounded chain does not establish arbitrary generic Task chains or
general G4/G7 state, frame and copy budgets. Exact runs, roots and the corrected
C emission diagnostic are in
`tests/native/results/milestone18-g4-generic-valuetask-two-await-20261021.txt`.

### Generic Task<T> sequential local awaits (B5, bounded complete; 2026-10-21)

A generic `async Task<T>` method can currently await two `Task<T>` parameters into
locals in sequence and return the first value. The lowerer creates a callback-
only alias for the later generic Task parameter before constructing the relay;
without it, generated C referenced an undeclared capture cell. `int`, `string`,
and a reference-containing struct pass pending-second forced-GC execution in
Windows x64/x86 and Linux x64 static/shared builds. This is a bounded generic
method and class-owner case. An imported producer case also passes: the first Task is
created in the math project and the pending second Task is forwarded by that
project, then the generic consumer resumes after forced GC in all six platform
and link-mode cells. Generic class owners pass pending success plus fault
identity and cancellation. Closed-specialization relay helpers and inner-to-outer
capture type restoration keep captures aligned with closed continuation cells. See
`tests/native/results/milestone18-b5-generic-task-chain-probe-20261021.txt`.

### Generic Task<T> all-completed chain (G4 bounded slice; 2026-10-22)

The two-local generic Task<T> chain previously allocated nine CX objects on
success, including eight relay/support objects that were unnecessary when both
sources were complete. Generic method and owner paths now check source readiness
and build the result before pending relay state. Inline int, string and
reference-containing TaskPayload method calls and a generic-owner int call each
allocate one returned Task<T>. Pending behavior continues through the B5 relay
path and passes forced-GC success, imported producer, fault and cancellation
checks. A generic owner specialized with the 104-byte x64/52-byte x86
LargeTaskPayload also returns the first value with one allocation and preserves
its first and last string references. Generic pointer/linkage warnings remain;
broad G4 frame/copy budgets remain open. Full tests, roots and diagnostic details:
`tests/native/results/milestone18-g4-generic-task-chain-inline-20261022.txt`.
The large-owner fixture evidence is recorded in
`tests/native/results/milestone18-g4-generic-task-chain-large-owner-20261022.txt`.

The prefix-free non-generic method with two direct `Task<T>` await parameters
also checks both sources before constructing relay state. Its callback-only
parameter aliases live in the pending branch, leaving one returned Task
allocation when both inputs are complete. The pending-second path survives
forced GC. Longer chains, prefixes and conditional forms remain outside this
bounded matcher. Evidence, roots and the first failed capture-cell probe:
`tests/native/results/milestone18-g4-nongeneric-task-two-await-inline-20261022.txt`.

The prefix-free one-await direct Task<T> parameter form also checks readiness
before pending relay state: inline completion allocates only its result Task,
while pending completion resumes after forced GC. Conditional returns, prefixes
and other continuations are not covered by this matcher. Evidence and roots:
`tests/native/results/milestone18-g4-nongeneric-task-one-await-inline-20261022.txt`.

### Imported ValueTask<T> with two prefix locals (G4 bounded; 2026-10-22)

The two-await imported ValueTask<T> call-source path accepts exactly two
initialized local declarations before its first await. Inline completion
allocates one output Task. Pending-first and pending-second cases preserve both
prefix locals across forced GC, and the imported producer executes once. The
focused structural tests pass 3/3, the solution build has 0 warnings/errors,
and the compiler suite passes 971/971. CTest passes in all six Windows x64/x86
and Linux x64 static/shared cells; Linux shared `ldd -r` resolves both modules.
This does not cover other prefix statements or close general G4/G7 budgets.
Exact roots and commands:
`tests/native/results/milestone18-g4-imported-valuetask-two-prefix-20261022.txt`.

### Non-generic Task<T> with two prefix locals (G4 bounded; 2026-10-22)

A non-generic `Task<int>` method with two initialized local declarations before
one direct parameter await now checks readiness before relay state. Its terminal
expression can combine those locals with the awaited result. Inline completion
allocates one returned Task; pending completion preserves both prefix locals
across forced GC through callback-only aliases. Focused structural tests pass
3/3, the build has 0 warnings/errors, and the compiler suite passes 972/972.
The current native fixture passes CTest in all six Windows x64/x86 and Linux
x64 static/shared cells; Linux shared `ldd -r` resolves both modules. Other
prefix forms and G4/G7 budgets remain open. Details:
`tests/native/results/milestone18-g4-nongeneric-task-two-prefix-20261022.txt`.

### Non-generic Task<T> with one prefix local (G4 bounded; 2026-10-22)

The same direct-parameter matcher also accepts one initialized prefix local.
Inline completion allocates one returned Task; a pending source survives forced
GC and resumes with the prefix value intact through a callback-only alias.
Focused tests pass 4/4, build has 0 warnings/errors, suite passes 973/973, and
CTest passes all six Windows x64/x86 and Linux x64 static/shared cells. Linux
shared `ldd -r` resolves both modules. Details:
`tests/native/results/milestone18-g4-nongeneric-task-one-prefix-20261022.txt`.

### Non-generic Task<T> with a local-only prefix chain (G4 bounded; 2026-10-22)

The direct-parameter matcher now accepts any non-empty prefix of initialized,
single-declarator local statements, provided their initializers contain no
await. A three-local integer/string/integer fixture verifies prefix order,
one-allocation inline completion, pending-only callback aliases and forced-GC
resume. Focused tests pass 4/4, the build has 0 warnings/errors, the suite
passes 974/974, and CTest passes all six Windows x64/x86 and Linux x64
static/shared cells; Linux shared `ldd -r` resolves both modules. Other prefix
statement forms and broad G4/G7 budgets remain open. Exact roots:
`tests/native/results/milestone18-g4-nongeneric-task-multi-local-prefix-20261022.txt`.

### Non-generic Task<T> with a multi-declarator local prefix (G4 bounded; 2026-10-22)

The direct-parameter one-await matcher accepts an initialized local declaration
with multiple declarators and creates pending-only aliases for each live local.
The native probe confirms one output Task inline and preserves both values over
pending forced GC. Focused structural tests pass 3/3, the build has 0
warnings/errors, and the suite passes 976/976. All six Windows x64/x86 and
Linux x64 static/shared cells pass; the initial x86 static run hit unrelated
existing process result 635, and the retained-root full x86 retry passed both
cells. Linux shared `ldd -r` resolves both project libraries. Broad G4/G7
budgets remain open. Evidence:
`tests/native/results/milestone18-g4-nongeneric-task-multi-declarator-prefix-20261022.txt`.

### Non-generic Task<T> with an assigned local prefix (G4 bounded; 2026-10-22)

The direct-parameter one-await matcher accepts an explicitly typed local that
is assigned before the await. Its inline path allocates one output Task; its
pending callback preserves the value through forced GC. Focused structural
tests pass 4/4, build has 0 warnings/errors, and the full suite passes 977/977.
All six Windows x64/x86 and Linux x64 static/shared cells pass; Linux shared
`ldd -r` resolves both project libraries. Inferred locals without initializers
remain excluded, and broad G4/G7 budgets remain open. Evidence:
`tests/native/results/milestone18-g4-nongeneric-task-assigned-local-prefix-20261022.txt`.

### Non-generic Task<T> with an await-free conditional prefix (G4 bounded; 2026-10-22)

The direct-parameter one-await matcher accepts `if` statements and blocks made
from supported await-free prefix statements. A conditional counter probe proves
exactly-once behavior inline and pending; inline completion allocates one output
Task and pending completion survives forced GC. Focused tests pass 4/4, build
has 0 warnings/errors, suite passes 978/978, and all six Windows x64/x86 and
Linux x64 static/shared native cells pass. The first x86 static attempt hit
unrelated existing process result 636; the retained-root full x86 retry passed.
Linux shared `ldd -r` resolves both project libraries. Other control transfers
and broad G4/G7 budgets remain open. Evidence:
`tests/native/results/milestone18-g4-nongeneric-task-conditional-prefix-20261022.txt`.

### Imported ValueTask<T> with three local prefixes (G4 bounded; 2026-10-22)

The two-await imported ValueTask call-source path removes its two-statement
prefix cap. A three-local integer/string/integer probe verifies one output Task
inline, exactly-once imported producer execution, and pending-first/pending-
second completion through forced GC with pending-only aliases. Focused tests
pass 3/3, build has 0 warnings/errors, suite passes 979/979, and all six Windows
x64/x86 and Linux x64 static/shared cells pass; Linux shared `ldd -r` resolves
both project libraries. This is bounded evidence for the three-local shape;
broader G4/G7 budgets remain open. Evidence:
`tests/native/results/milestone18-g4-imported-valuetask-three-prefix-20261022.txt`.

### Generic Task<T> chain with a synchronous prefix (G4 bounded; 2026-10-22)

The two-await generic Task<T> method now has structural coverage for an
await-free local prefix that remains live after the awaits. Its all-completed
path checks both sources before relay state, allocates only the returned Task,
and executes the post-await use of the prefix. Pending-first and pending-second
cases preserve the prefix through forced GC and scheduler resumption. The
focused structural pair passes 2/2, the solution build has 0 warnings/errors,
the full compiler suite passes 980/980, and all six Windows x64/x86 and Linux
x64 static/shared cells pass; Linux shared `ldd -r` resolves both libraries.
This checks one generic int-local prefix with a post-await side effect, not
arbitrary generic prefixes or general G4/G7 state/frame/copy budgets. Evidence:
`tests/native/results/milestone18-g4-generic-task-chain-prefix-20261022.txt`.

The prefixed generic Task<T> chain also has pending failure coverage at both
await positions. Faults preserve exact exception identity, cancellations remain
canceled, and the post-await prefix effect does not run after failure. All six
Windows x64/x86 and Linux x64 static/shared fixture cells pass with forced GC;
Linux shared `ldd -r` resolves both project libraries. See
`tests/native/results/milestone18-g4-generic-task-chain-prefix-20261022.txt`.

### Imported ValueTask<T> chain with a conditional prefix (G4 bounded; 2026-10-22)

The two-await imported ValueTask call-source path accepts an await-free if/block
prefix before readiness. A conditional counter runs exactly once inline and on
pending-first/second paths; the imported producer also runs once. Inline
completion allocates one returned Task, and pending completion survives forced
GC. Focused compiler tests pass 3/3, build has 0 warnings/errors, suite passes
981/981, and all six Windows x64/x86 and Linux x64 static/shared cells pass.
Linux shared `ldd -r` resolves both libraries. This does not establish arbitrary
control flow or G4/G7 caller-frame/copy budgets. Evidence:
`tests/native/results/milestone18-g4-imported-valuetask-conditional-prefix-20261022.txt`.

### Generic Task<T> prefix with a large result (G4 bounded; 2026-10-22)

The generic two-await Task<T> prefix method is specialized with the 104-byte
x64/52-byte x86 reference-containing `Math.LargeTaskPayload`. Inline completion
allocates one returned Task; pending-first and pending-second results preserve
Number, Label0 and Label11 after forced GC while suspended and after scheduler
drain. The post-await prefix value remains intact. CTest passes all six Windows
x64/x86 and Linux x64 static/shared cells; Linux shared `ldd -r` resolves both
libraries. This is payload correctness, not a frame/copy budget. Evidence:
`tests/native/results/milestone18-g4-generic-task-chain-large-prefix-20261022.txt`.

### Generic owner Task<T> prefix with a large result (G4 bounded; 2026-10-22)

`GenericTaskAwaitOwner<T>.AwaitTwoWithPrefix` executes an int prefix before two
Task<T> awaits. Its structural test verifies readiness ordering and callback-only
aliases. A closed `LargeTaskPayload` owner specialization allocates one result
Task inline; pending-first and pending-second results preserve Number, Label0 and
Label11 after forced GC, and the prefix value remains 29. All six Windows
x64/x86 and Linux x64 static/shared cells pass after a retained-root retry from
an unrelated existing x86 process result 635. Linux shared `ldd -r` resolves
both libraries. This does not establish owner fault/cancel or frame/copy
budgets. Evidence:
`tests/native/results/milestone18-g4-generic-task-owner-large-prefix-20261022.txt`.

The generic-owner prefix path also has pending-first/second fault and
cancellation coverage for the large result. Faults preserve exact exception
identity, cancellations remain canceled, and the prefix side effect does not
run. The fault objects are explicitly rooted across forced collection. The
corrected final matrix passes all six Windows x64/x86 and Linux x64 static/shared
cells; Linux shared `ldd -r` resolves both libraries. An earlier x86 attempt
hit existing unrelated imported-call result 635, which did not recur in the
final run. See
`tests/native/results/milestone18-g4-generic-task-owner-large-prefix-20261022.txt`.

### Generic Task<T> large-result prefix fault and cancellation (G4 bounded; 2026-10-22)

`AwaitGenericTwoTasksWithPrefix<T>` now has pending-first/second fault and
cancellation coverage for `Math.LargeTaskPayload`. Faults preserve exact
exception identity and cancellations remain canceled. Every path forces GC while
suspended, roots source/result and fault objects, and confirms the post-await
prefix effect does not run. The six Windows x64/x86 and Linux x64 static/shared
cells pass; Linux shared `ldd -r` resolves both project libraries. Compiler
source is unchanged from build 0 warnings/errors and suite 982/982. This is
functional payload coverage, not G4 escaped-state/frame/copy budget evidence.
See `tests/native/results/milestone18-g4-generic-task-chain-large-fault-cancel-20261022.txt`.
### Generic-owner synchronous iterator measurement (G7 bounded; 2026-10-22)

The native benchmark closes `GenericIteratorProvider<T>.GenericValues` over
`int` and compares one and sixteen yields. The closed frame stores `cx_int` as
its current value, and the generated closed `IEnumerator.Current` thunk returns
`cx_int`. All three static targets pass expected sums and report 3 allocations
at both lengths: 128 bytes / 56-byte frame on Windows x64 and Linux x64, and
88 bytes / 40-byte frame on Windows x86. This verifies one closed owner/type
shape only; generic boxing, allocation and frame budgets remain broader G7
work. Exact commands, roots, baseline refreshes and retained exploratory runs:
`tests/native/results/milestone18-g7-generic-owner-iterator-20261022.txt`.


### G7 generic-owner IteratorPayload Current thunk (2026-10-22; bounded)

Closed generic owner iterators whose element is a struct containing a string
reference now emit `IEnumerator<T>.Current` thunks that return the specialized
frame field directly. The first native attempt caught a real MSVC C2440: the old
thunk called the open generic getter and cast its erased `void*` through
`size_t` to an aggregate. The corrected thunk reads the typed closed field and
preserves the invalid-Current exception by delegating to the original getter
only when `__cx_hasCurrent` is false. The structural regression passes, the
solution builds with 0 warnings/errors, and the full suite passes 983/983. All
three reduced static benchmark targets pass value/reference checks and equal
one-vs-16 allocation counts: 3 objects / 176 bytes and 80-byte frames on x64
and Linux, 3 / 104 bytes and 48-byte frame on x86. Exact failure, commands,
roots and reports: `tests/native/results/milestone18-g7-generic-owner-payload-20261022.txt`.
This covers one reference-containing struct specialization, not universal
boxing or copy budgets.

### G7 generic-owner string Current thunk (2026-10-22; bounded)

The same generic owner iterator was closed over `string`. Since reference-type
generic arguments share the pointer-sized open iterator frame, the interface
thunk returns the concrete `String*` type while reading that frame's current
slot. The native check verifies each value's expected string content for one
and sixteen yields and confirms equal allocations: Windows x64 3 objects / 144
bytes with a 64-byte frame; Windows x86 3 / 88 bytes with a 40-byte frame;
Linux x64 3 / 144 bytes with a 64-byte frame. All three reduced static runs
pass. This complements the int and struct specializations without establishing
general generic-owner or G7 budgets. Commands, roots and per-target reports:
`tests/native/results/milestone18-g7-generic-owner-string-20261022.txt`.

### Four-layer mixed-reference await (G8 bounded; 2026-10-22)

Completed and pending `IteratorPayload` results (integer plus string reference)
now flow through four nested `Task<IteratorPayload>` awaits. The pending result
is checked after GC while suspended and again after completion before reading;
both fields match. Completed/pending totals are 5/280 and 27/1,368 bytes on
x64/Linux, and 5/140 and 27/780 on x86. The final static benchmark runs pass
Windows x64/x86 and Linux x64. The accompanying reusable-source probes retain
flat post-drain heap totals through 192 cycles after 64 warmups. This verifies
one bounded nesting depth; broad G8 budgets and independent physical-machine
timing remain open. Full evidence:
`tests/native/results/milestone18-g8-four-layer-reference-20261022.txt`.

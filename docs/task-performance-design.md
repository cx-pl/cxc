# Task representation and allocation performance

CX provides persistent task handles and single-consumption value tasks. Their
ownership contracts determine which completion storage can be shared or reused.
Representation and allocation choices must preserve those contracts.

## Completion contracts

| Type | Intended use | Result storage | Consumption |
| --- | --- | --- | --- |
| `Task<T>` | Shared results, joining, caching, and fan-out | Typed result in persistent completion storage | Repeated awaits and multiple subscribers |
| `Task` | Shared completion without a result | Persistent completion state | Repeated awaits and multiple subscribers |
| `ValueTask<T>` | Immediate results or single-consumer pending operations | Inline `T`, a persistent task, or a versioned reusable source | One consumption |
| `ValueTask` | Immediate success or single-consumer completion without a result | Inline success or persistent completion state | One consumption |

Async methods start synchronously. A completed await continues inline; a
pending operation resumes through the owner-thread executor. Fault identity,
cooperative cancellation, and cleanup ordering apply to both task families.
See [coroutines and iterators](coroutines-design.md) for supported source forms.

`Task<T>` stores its result with its concrete type rather than boxing every
result into a common object slot. A persistent task can outlive its producer
and keeps its result available for later observers.

## Value-task consumption and sharing

Consume a value task once, even when it completed synchronously. Use
`AsTask()` once to obtain a persistent handle when the operation must be
shared, cached, or observed repeatedly. Conversion may allocate completion
storage; retain the returned task instead of converting the same value again.

A struct representation does not enforce exclusive ownership. Copies of a
value task can have independent per-value consumption flags, so copying one
does not authorize a second consumption. Source-backed values also carry a
generation checked by their reusable source.

Large results increase the size of inline value tasks and of caller frames
that retain them. A value type can also be stored in a heap object or boxed;
its declaration does not promise stack allocation or lower end-to-end cost.

## Suspension and live state

The synchronous prefix of supported async forms can run without a separate
escaped frame. When an await suspends, the compiler retains state needed by
the continuation and roots references through managed storage.

Only values whose lifetimes cross suspension belong in escaped state.
Captured aliases must continue to share storage with any escaped closure.
Pending work must never retain an expired native stack frame, and native
exception frames cannot survive a return from a resume function.

Selected return-await and completed-value paths avoid separate relay frames.
This is specific to the lowered shape; it does not promise zero allocation for
arbitrary async code. Unsupported await, capture, or protected-region shapes
produce diagnostics rather than silently using expired local storage.

Measure compiler-generated temporaries separately from result storage, callback
environments, continuation records, and scheduler items. A reported generated
frame size of zero does not imply that the operation creates no runtime state.

## Reusable completion sources

`ReusableValueTaskSource<T>` couples one operation generation to one consumer.
Status checks, continuation registration, result consumption, and completion
must use the expected generation. Stale tokens and duplicate consumption are
rejected.

Reset is allowed only after completion, result consumption, and acknowledgment
of outstanding callbacks. A queued continuation remains outstanding even after
it has been removed from the source's own callback list. Reusing its storage
before dispatch finishes can corrupt a later operation.

Sources clear retained results, exceptions, and continuation state when resetting.
Generation wrap retires the source instead of making an old token valid again.
A native operation also needs its own registration identity so a late readiness
event cannot complete a newer operation using the same source.

Native buffers, captures, exceptions, and source state remain live through
their last permitted use. Cancellation requested by the caller is not proof
that native readiness registrations or callbacks have stopped.

Async enumerators own reusable move state. Consume each `MoveNextAsync()`
result before requesting the next move and await disposal before releasing
resources. An overlapping move is invalid.

## Scheduler and native I/O

Task execution is cooperative and owned by one OS thread. A completion callback
can enqueue work, but it cannot recursively enter the executor loop.
Continuation and cancellation publication must follow that executor's rules.

Socket providers own readiness registrations and buffer lifetimes. They report
readiness to the owner executor, which performs managed completion. A task or
value-task wrapper does not itself shorten the native operation's borrow.

An arena and a reusable completion source solve different lifetime problems.
Arena storage expires with its scope; reusable state remains owned until its
consumer and callbacks finish. Only supported joined operations may cross an
asynchronous arena boundary. See the runtime's
[arena contract](../../cxcore/docs/scoped-allocation-arenas-design.md).

## Pooling constraints

Completed persistent tasks cannot be returned to a pool while callers can
still observe their original result. Reuse internal single-consumer state only
after generation, consumption, and callback-quiescence checks permit it.

A pool retains idle items and their references. Bound retained capacity, clear
references on return or discard, and release native resources explicitly.
Returning an item does not revoke aliases held by its borrower.

Arena-owned objects cannot enter a longer-lived pool. Shared pools need their
own synchronization; thread-safe pool bookkeeping does not make rented objects
thread-safe. These constraints are not a general public pooling API.

## Measuring performance

Use the [coroutine benchmark guide](coroutine-performance-baselines.md) to run
the native harness. Compare like-for-like workloads with matching source,
architecture, toolchain, configuration, and linking mode.

Record:

- Managed allocation count and requested bytes, including cold and warmed paths.
- Generated frame size, native caller stack usage, and result-copy traffic.
- Continuation, scheduler, and native-registration work.
- GC collections, retained references, and memory after a burst of operations.
- Completed and pending operations, sharing, conversion, cancellation, and faults.
- Small scalar, reference-containing, and large struct results.

Use repeated Release samples and report ranges along with medians. Instrumented
allocation counts describe the isolated workload; they exclude unrelated user
work unless explicitly measured. Native stack reports are compiler-specific
estimates, rather than runtime high-water measurements.

A handwritten C state machine can expose infrastructure costs but usually has
different ownership and scheduling responsibilities. Lower allocations do not
by themselves establish lower latency. Keep internal performance targets,
acceptance reports, and rollout measurements outside the source repository.

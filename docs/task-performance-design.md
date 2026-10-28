# Task representation and allocation performance

Keep `Task<T>` as the stable, shareable completion handle and add `ValueTask<T>`
and `ValueTask` for operations normally awaited once. A value task can carry a
synchronous result directly and refer to shared state when an operation suspends.
Changing `Task<T>` from `class` to `struct` alone would preserve its existing
state allocation and boxing while breaking native object-based task helpers.

This is the allocation optimization plan for Milestone 18's delivery step 5.
G2 typed `Task<T>` layout work is complete: pending cross-module completion
passes for value, reference, and reference-containing struct results. The native
allocator probe measures one CX allocation for direct task construction and
equal tracked allocations for two-subscriber pending completion across those
result types. This is structural evidence against a per-value result box, not a
claim that completion is allocation free. G3 `ValueTask<T>`/`ValueTask` and
bounded G4 pending-only continuation paths are implemented; broader escaped
state and frame/copy budgets remain open.

## Baseline costs before the G2 typed-layout experiment

The measured pre-G2 implementation stored a `TaskState` reference in each
`Task<T>`. `TaskState.Value` was an `object`; `SetResult` boxed value-type
results and `GetResult` unboxed them. G2 moved completion fields and `_result`
onto `Task<T>` and emits closed typed layouts. The open generic placeholder is
`cx_ptr`, while closed layouts carry concrete field types. A generic completion
source is a value wrapper around its persistent task, so construction allocates
the task only. Pending awaits may still allocate forwarding or closure state,
continuation entries, and scheduler items. Reference results do not need a value
box.

The recorded Release Windows x64 static
[baseline](coroutine-performance-baselines.md) reports eight allocations for a
completed-await operation, twelve for a pending operation, and 4,008 for a
1,000-element completed-yield async enumeration. These are whole-fixture counts,
including generated temporaries; they must not be equated with one task's cost.
The handwritten C references omit task ownership and scheduling and provide a
lower bound rather than an equivalent implementation.

The first localized optimization embeds the first continuation entry in its
native queue allocation. Registering the first pending subscriber now needs
one managed allocation instead of two; further subscribers need one each.
The opaque `_continuations` field and the public task ABI are unchanged.
FIFO delivery still posts callbacks through the executor. The native
continuation test asserts the allocation counts, collects while registrations
are pending, and exercises captured references, reentrant completion, and late
registration. Its static task reference is explicitly registered as a GC root.
This optimization does not remove task or result allocations. The recorded
whole-fixture baselines above precede this change.

### G2 implementation note (2026-10-18)

Typed `_result` storage follows the shared completion prefix on `Task<T>`, and
closed-layout casts are emitted at generic-owner field boundaries. Supported
return-await relays create and complete the destination `Task<T>` directly.
Compiler regressions cover direct return-await capture binding, and native
fixtures cover pending fan-out, typed results and forced collection. The native
allocation counter proves one CX allocation for direct `Task<T>` construction
with `int`, `string`, and a reference-containing struct, and equal allocation
counts for two-subscriber pending completion across those result types. Final
Windows x64/x86 and Linux x64 static/shared fixture cells pass. Strict generated-C
passed for the four fixture sources before the final public header base-field
adjustment; the six-cell runtime matrix was rerun after that adjustment. Generic
inheritance was rejected after compiler binding regressions, so
`TaskCompletionSource<T>` is a value wrapper around its separately allocated
task. Shared Windows generic-specialization C4273 warnings remain. G2 is complete
for the typed result, ABI, fan-out and structural allocation acceptance checks.

### G3 value-task slice (2026-10-18)

`Task.cx` now declares `ValueTask<T>` with an inline typed result or a persistent
`Task<T>` reference, and `ValueTask` with default inline success or a persistent
`TaskHandle`. Both expose completion status, result access, and `AsTask()`. The
compiler accepts these awaitables in the supported direct-await forms. Direct
parameter awaits check `IsCompleted` before converting pending values through
`AsTask()`. The immediate `Task<T>`-returning form still allocates its returned
Task; the same struct instance rejects a second `GetResult`/`AsTask()`;
copies are ordinary value copies, so the API requires the caller to consume the
logical value task only once even though copied flags cannot coordinate that
rule. Convert once with `AsTask()` when the completion must be shared. The native
fixture covers default results, same-instance duplicate rejection, copied value
behavior, conversion allocation, inline and pending awaits, forced GC, and
pending success/fault/cancel propagation. Windows x64/x86 and Linux x64 static
and shared fixture cells pass. This does not claim ValueTask-specific pooled
sources or zero total allocation for methods that return a persistent Task.

### G4 initial allocation slice (2026-10-18)

The open generic `ValueTask<T>` placeholder in `cxthreading.h` now includes the
object base present in closed generic layouts. Direct parameter awaits branch on
completion before `AsTask()`, so inline success avoids the intermediate task
conversion. Native allocation probes report one CX allocation for an inline
`ValueTask<int>` awaited by a function returning `Task<int>` (the persistent
returned Task), and two allocations for an inline non-generic `ValueTask`
returned as `Task` (the TaskHandle plus its TaskState).

A cross-module fixture also checks inline and pending `ValueTask<TaskPayload>`,
where `TaskPayload` contains an integer and a string reference. It verifies
`GetResult`, inline await allocation count, pending `IsCompleted`, and pending
completion after forced GC. This exposed a stale embedded fallback declaration:
`Task.cx` and the runtime placeholder used `_task` followed by `_result`, while
the compiler fallback used the reverse order. The fallback now matches the
runtime/source layout. Windows x64/x86 and Linux x64 static/shared cells pass
after the correction. This exercises a reference-containing payload, but does
not establish the large-value copying/frame-size budget.

The local-await continuation now has a bounded fast path for one inferred local
initialized from a `ValueTask<T>` parameter followed by a terminal return
expression that uses the local. The completed branch calls `GetResult()` and
constructs only the returned `Task<T>`. It creates no TaskCompletionSource,
callback delegate, or forwarding Task. The pending branch calls `AsTask()` once,
then creates the completion source and callback. Separate callback-only aliases
keep captured source/completion locals out of the enclosing return path; the
callback preserves result, fault, and cancellation propagation. An earlier
implementation shared captured locals with the outer branch and failed with
“A captured local is referenced outside a lambda helper”; the alias form is the
verified implementation.

Native probes assert exactly one CX allocation for inline `ValueTask<int>` and
inline `ValueTask<TaskPayload>` returning persistent Tasks. Pending int and
reference-containing payload cases force GC before completion; pending fault
and cancellation are also checked. The cross-module fixture passes all six
Windows x64/x86 and Linux x64 static/shared cells. A compiler regression checks
that the completed branch precedes AsTask, completion-source construction, and
delegate construction. This path is limited to one ValueTask parameter, one
awaited local, and a terminal return expression using that local. Multiple
awaits, branch continuations, the optimized non-generic path, and large-value
copy/frame-size budgets remain open. Bounded nested all-completed ValueTask
call chains are verified separately below; G4 is not complete.

A bounded no-result form now handles one standalone `await ValueTaskParameter;`
followed by an explicit `return;`. Inline success checks completion first,
consumes the ValueTask, and returns the existing two-allocation TaskHandle/
TaskState representation. Only the pending branch converts to TaskHandle and
creates its completion source and callback. Native checks cover pending success
after forced GC, fault identity, and cancellation. Compiler IR and the native
cross-module fixture pass in Windows x64/x86 and Linux x64 static/shared cells.
The void optimization is limited to this terminal shape; broader void
continuations and multiple awaits still allocate continuation state early.

### G4 nested ValueTask relay follow-up (2026-10-18)

The direct `async ValueTask<T>` and `async ValueTask` relay also accepts one
explicitly typed local initialized by a nested call returning the same shape,
followed by the matching `return await local;` or `await local;`. The initializer
stays in the synchronous prefix and the completion branch checks that local.
All-inline generic and no-result nested calls allocate zero CX objects; nested
reference-containing and 104-byte x64 / 52-byte x86 payloads return the expected
fields. Separate wrappers compose the same one-local lowering into a three-layer
call chain; its all-inline generic and no-result paths still allocate zero CX
objects. Nested generic pending success survives forced GC, and nested generic
fault identity and cancellation are retained. Pending no-result completion also
survives forced GC. The native fixture passes CTest 1/1 in all six Windows
x64/x86 and Linux x64 static/shared cells; Linux shared `ldd -r` resolves. The
compiler suite passes 879/879 and the compiler build has 0 warnings/errors.
Each function is still limited to one typed ValueTask local and one await;
arbitrary nested expressions and multiple awaited locals in async ValueTask
functions remain unsupported. G4's general escaped-state and exact frame/copy
budgets remain open.

**2026-10-21 current-source refresh:** the complete cross-module ABI fixture was
rebuilt in Windows x64, Windows x86, and Linux x64, static and shared. Each
CTest passes 1/1. Native counters assert zero CX allocations for nested and
three-layer inline `ValueTask<int>`/void relays, a `TaskPayload` with a string
reference, and a `LargeTaskPayload` with twelve references (104 bytes x64, 52
bytes x86). Pending paths force collection before completion and verify resumed
values; integer cases preserve fault identity and cancellation. Linux shared
`ldd -r` resolves both project libraries. Retained roots, commands, and logs:
`cxc/tests/native/results/milestone18-g4-nested-valuetask-inline-refresh-20261021.txt`.
This closes the bounded nested all-completed call-chain check; arbitrary nested
expressions and general escaped-state/copy budgets remain open.

**Generic direct `ValueTask<T>` audit (2026-10-21):**
`GenericAsyncTaskDirectValueTaskAwaitChecksInlineBeforeRelayState` passes 1/1,
confirming the inline readiness branch precedes AsTask/continuation creation
and inference closes the generic parameter to `ValueTask<int>`. Generic method
closure now assigns identities to nested generic named types, and generic
function emission closes captured local types before writing lambda
environments and specializes generated local/lambda functions with the enclosing
generic method arguments. The generic relay fixture checks one output Task on
inline success, then retains pending `ValueTask<int>` and
`ValueTask<TaskPayload>` sources across forced collection; the payload string
reference survives. Pending int fault and cancellation also survive forced GC,
with original exception identity and cancellation observed at the returned
Task. CTest passes 1/1 in Windows x64/x86 and Linux x64, static/shared; Linux
shared `ldd -r` resolves both project libraries. The solution build has 0
warnings/errors and the full compiler suite passes 963/963. Generated
Windows/Linux C still reports generic pointer/linkage warnings unrelated to
this relay check. The earlier unresolved non-generic callback symbols are fixed
by closing generated local-function types before lambda environment emission.
Exact commands and preserved roots are in
`tests/native/results/milestone18-g4-generic-direct-valuetask-20261021.txt`.
This verifies one generic `ValueTask<T>` direct-await shape, not general escaped
state, frame/copy budgets, or B5 chained-generic suspension; G4 remains open.

### Generic async ValueTask<T> direct return-await (2026-10-21)

The lowerer now admits the same bounded direct return-await when the enclosing
method is generic. Generic specializations emit the resulting structured
readiness/try body through the pending-await relay path; typed fault completion
obtains the generic completion task through `GetTask()`. The native fixture
verifies zero CX allocations for inline int and 104-byte x64 / 52-byte x86
large reference-containing results. Pending int and large results survive forced
GC; completed fault and cancellation preserve their outcomes. Focused test 1/1,
full suite 964/964, build 0 warnings/errors, and cross-module CTest 1/1 in all
six Windows/Linux platform-link cells pass. Linux shared `ldd -r` resolves both
modules. Generic pointer/linkage warnings remain in generated native C. This is
one direct await with no prefix or chain; broader G4 allocation, frame and copy
budgets remain open. See
`tests/native/results/milestone18-g4-generic-valuetask-relay-20261021.txt`.

**Generic ValueTask explicit prefix (2026-10-21):** generic async body
validation now admits one explicit local initialized before a direct ValueTask
await. `GenericAsyncValueTaskReturnAwaitPreservesExplicitPrefixLocal` verifies
the local stays before the readiness branch. Native coverage checks a
zero-allocation inline large result and pending completion after forced GC in
Windows x64/x86 and Linux x64 static/shared. The build has 0 warnings/errors
and the suite passes 965/965. See
`tests/native/results/milestone18-g4-generic-valuetask-prefix-20261021.txt`.
This does not cover multiple prefixes or await chains.

### G4 single ValueTask await after a synchronous prefix (2026-10-18)

`async Task<T>` also has an all-inline branch for one inferred local initialized
from a `ValueTask<T>` parameter after a synchronous prefix, followed by a
terminal result return. It checks `IsCompleted` before creating the completion
source or callback. The inline integer fixture allocates only the returned
Task. A pending fixture retains a reference-containing prefix local through
forced GC and completes with the expected result. The compiler regression
passes 1/1, the full suite passes 880/880, and the build has 0 warnings/errors.
The cross-module CTest passes 1/1 in all six Windows x64/x86 and Linux x64
static/shared cells; Linux shared `ldd -r` resolves. This remains a bounded
single-await/terminal-return shape; other G4 escaped-state paths require their
own allocation probes.

The same all-inline path now handles a terminal conditional whose branches each
return a result Task. Each branch is lowered to construct its Task only after
the condition selects it. Both inline branch outcomes allocate one returned
Task; the pending callback preserves the reference prefix and selects the same
result after forced GC. Compiler regression passes 1/1; the full suite passes
881/881; build has 0 warnings/errors. CTest passes 1/1 in all six Windows
x64/x86 and Linux x64 static/shared cells, with Linux shared `ldd -r` resolving.
This remains limited to terminal conditionals whose branches return results;
arbitrary control-flow after a single await is not implied.

The single-await prefix path accepts an explicitly typed result local as well
as an inferred one. The lowered inline and callback locals retain that declared
type; the binder checks it against `GetResult()`. The inline probe allocates
only its returned Task, and pending completion survives forced GC. The focused
compiler regression, full 881-test suite, warning-free compiler build, and six
native platform/link cells pass. See the explicit-local G4 evidence row in
`CX_MILESTONE18_CHECKLIST.md`.

Explicit result locals also work in the bounded two-await ValueTask path. The
all-inline path returns one Task; pending-first and pending-second each survive
forced GC and preserve the typed results through the mutation and synchronous
prefix call between awaits. Focused compiler regression passes 1/1, the full
suite passes 882/882, build has 0 warnings/errors, and CTest passes 1/1 in all
six Windows x64/x86 and Linux x64 static/shared cells. See the explicit
multi-await evidence row in `CX_MILESTONE18_CHECKLIST.md`.

## Completion contracts

| Type | Intended use | Result storage | Consumption |
| --- | --- | --- | --- |
| `Task<T>` | Shared results, caching, joining and fan-out | Typed result in persistent completion storage | Repeated awaits and multiple subscribers |
| `ValueTask<T>` | Buffered reads, writes, async iterator moves and immediate results | Inline `T`, persistent task, or versioned reusable source | One consumption; explicit conversion for sharing |
| `ValueTask` | Async disposal and operations without a result | Inline success, persistent task, or versioned reusable source | One consumption; explicit conversion for sharing |

Both task families start async methods eagerly and preserve fault identity,
cancellation, awaited cleanup, and owner-thread executor rules. A completed
await continues inline. A pending completion resumes through the executor.
The value-task consumption restriction applies to all representations so code
does not change validity depending on whether it happened to suspend.

Provide an explicit `AsTask()` conversion that consumes a value task once and
returns a persistent multi-await handle. Materialize completion storage when
necessary; callers retain and share that returned task. Repeatedly converting
the same source-backed value task is invalid. Diagnose provable double
consumption and use runtime checks for reusable sources; a struct declaration
does not itself enforce ownership.

## Representation and compiler work

1. Replace `object` result storage with typed completion storage for `Task<T>`.
   Keep completion flags, exception and continuation plumbing in a common header
   or explicit state accessor. Generate typed result access and forwarding per
   specialization. Native helpers currently cast `object` to the task wrapper;
   migrate those helpers together with compiler lowering and metadata rather
   than assuming a new generic layout matches the old one. Target one persistent
   task allocation, including its typed result, without a separate result box.
2. Represent `ValueTask<T>` as a discriminated value holding either inline `T`
   or a completion-source reference plus generation. A zero-initialized value
   represents successful completion with `default(T)`; the no-result form
   represents successful completion. Faults and cancellation can use shared
   completion storage so the success representation carries no separate exception
   field. Start with persistent task backing; add reusable sources only after
   their lifetime checks pass.
3. Teach the shared async lowering and await binding about both task families.
   Pass value tasks as values with typed awaiters; do not route them through
   `object`, reflective invocation, or boxed interface dispatch. Execute the
   synchronous prefix without allocating a completion source, closure or frame.
   Allocate and root escaped live state only when the first await actually
   suspends. Preserve captures shared with escaped closures, exception handling,
   and cleanup; never let pending work reference an expired native stack frame.
4. Use value tasks for async iterator `MoveNextAsync` and `DisposeAsync` and for
   selected socket operations. The enumerator can own reusable completion state
   because overlapping moves are already forbidden. Keep the existing task API
   working during migration. Async iterator interface signatures, generated
   implementations, consumers, reflection and DLL exports must change together.
5. Store the common single continuation in completion state where measured
   layout costs justify it; allocate overflow entries for fan-out. Let frames or
   operation records own scheduler links when ownership permits, avoiding a new
   queue node on every resume. Preserve FIFO and reentrancy behavior. Pool only
   after eliminating unnecessary objects; reset pooled references and bound
   retention so lower allocation rates do not hide retained buffers or frames.

Large `T` values enlarge value tasks and any caller frames retaining them. Measure
copying and return conventions on x86 and x64 before selecting layouts. A compact
reference task may outperform a value task for large results or heavily shared,
usually pending operations. Structs can be fields in heap objects and can be
boxed; they do not imply stack allocation or universal speed improvements.

## Alternative value type Task

A struct `Task<T>` containing either inline `T` or a persistent completion
reference can also support repeated awaits: copying a pending handle copies the
reference, and copying a completed handle copies its immutable result. The
single-consumption restriction comes from reusing completion sources, not from
being a struct. This is a viable alternative for a new language and must be
benchmarked against a compact reference task plus a separate value-task family
before freezing the public representation.

Compare end-to-end workloads with small and large results, completion ratios,
deep async call chains and task sharing. Include caller frame bytes, copying,
GC roots and generic code size. Adopting a struct `Task<T>` also requires an
explicit migration of null/default behavior, identity, inheritance, public
producer mutation methods and object-based native helpers. Retain persistent
state for the multi-await contract even if a value representation wins; pooled
single-consumer sources still require a distinct consumption contract. The
immediate decision to preserve the working class does not freeze CX to .NET's
public type split.

## Reusable source lifetime

A reusable source must check generation and single-consumer ownership when
querying status, registering a continuation, consuming a result, and converting
to a persistent task. Reject stale tokens and duplicate consumption. Prevent
generation wrap from making a stale handle valid again, for example by retiring
a source before its token wraps. Only reuse the source after its consumer has
finished and native readiness registrations and queued callbacks are detached.
Each native operation also needs its own registration identity so a late event
cannot complete a later operation on the same source or socket.

Retain buffers, exceptions, captures and source state until their last permitted
use. Clear references before pool return. A value task containing a reference is
a GC root wherever it is live, including typed locals, nested structs, coroutine
frames, arrays and cross-module returns. Test these locations with forced GC;
do not assume native stack scanning is sufficient for future LLVM output.
Cross-thread completion remains outside the initial cooperative executor; later
threading integration needs explicit completion/registration synchronization.

### G5 source-lifetime audit (2026-10-19)

The current `ValueTask<T>` and `ValueTask` carry a per-struct `_consumed` bit;
copies therefore have independent bits. `TaskCompletionSource<T>` and
`TaskCompletionSourceVoid` each create a fresh persistent Task, and there is no
generation-bearing reusable source yet. Native `TaskState.ScheduleContinuations`
clears its continuation list and posts each action to the scheduler, so an empty
source-side list does not prove those callbacks have finished. A reusable source
must acknowledge callback execution (including exception exits) and native
readiness-registration detachment before advancing its generation or clearing
roots. Do not implement reset by reusing public `Task<T>`/`TaskHandle` state.
This is a source/runtime constraint discovered while starting G5, not an
acceptance result; stale-token, callback-quiescence, cancellation/readiness,
rooting and retention tests remain required.

### G5 compiler feasibility probe (2026-10-19)

Consuming projects bind against the synthetic core declarations in
`SemanticBinder.Text.cs`; adding members to `Task.cx` alone does not export them.
Synchronizing that fallback exposed current compiler limits: generic property
accessors cannot forward through generic state, the supported closed value
layout constructor shape excludes the extra lease fields/constructors, and a
generic reusable state referenced by the consumer lacked emitted type-info.
The fallback-generated callback closure also failed with undefined capture-cell
and environment symbols. The source prototype was reverted to preserve the
established `ValueTask<T>` and `TaskCompletionSource<T>` ABI. Attempt paths,
diagnostics and the restored-state build/test result are recorded in
`cxc/tests/native/results/milestone18-g5-synthetic-api-probes-20261019.txt`.
G5 needs a representation within exported generic layouts or public generic
source state, plus compiler support for closed internal generic type-info if
the source state stays internal. The tested public generic callback closure
shape now compiles in consumer modules; generation/lifetime tests still need a
source implementation and a callback-quiescence/detachment mechanism.

The compiler support slice allocates and initializes captured parameter cells
in both closed generic-owner bodies and replacement implementation bodies,
matching ordinary function emission. It also emits closure environments, lazy
lambda reflection descriptors, helper prototypes and helper bodies beside the
consumer's generic method specialization. The cross-project regression
`ImportedGenericMethodAllocatesCellsForCapturedParameters` checks these emitted
parts. A native cross-module test executes a wrapper lambda that captures its
generic owner and delegate parameter, updates the owner and invokes the callback.
Windows x64/x86 static/shared cells pass. The final Windows x64 static rerun
also instantiates both `GenericCallbackSource<int>` and
`GenericCallbackSource<string>`; it exits successfully with CTest 1/1. Linux
x64 is unverified because the WSL CMake is below the runner's 3.31 minimum. This
verifies the tested generic-independent callback shape only; generic-dependent
closure layout specialization still needs audit. It does not change the G5
lifetime contract or qualify as source-reuse evidence.

### G5 public generic source probe (2026-10-19)

Keeping `ValueTask<T>` unchanged and adding `ReusableValueTask<T>` plus a public
`ReusableValueTaskSource<T>` passed emit, but the consumer C build referenced an
undeclared closed source `__typeinfo`. Regenerating `cxthreading.metadata.c`
from the declarations did not emit the closed generic source/handle metadata;
the generated core metadata C then failed on missing generic type-info and
struct declarations. The prototype was removed, metadata regenerated from the
restored Task source, and Windows x64 static cross-module CTest passed 1/1. The
full compiler suite passed 893/893 and build had 0 warnings/errors. This
identifies the next compiler requirement and implements no reusable source.
Exact roots and commands are recorded in
`tests/native/results/milestone18-g5-public-generic-source-probe-20261019.txt`.
Keep typed result storage when resolving the type-info boundary; an object
owner would box value-type results. Callback completion acknowledgement and
native readiness-registration detachment are still required before reset.

### G5 synchronous reusable-source prototype (2026-10-19; partial)

The closed type-info emitter now forward-declares open generic metadata and
retains constructed generic identity after reflection substitution. Two focused
compiler regressions pass. With those fixes and generic callback-helper emission,
the public typed `ReusableValueTaskSource<T>` / `ReusableValueTask<T>` prototype
now compiles and executes cross-module on Windows x64 static. The source stores
the result in a typed `T` field, checks generation and duplicate consumption,
clears the result on reset, and retires instead of wrapping its `uint` token.
`AsTask()` consumes once and copies of the handle share the source's consumed
state. A native prefix bridge checks handle status without exposing generic
result layout. The runtime C placeholder now follows the CX field order and the
status getter is exported for shared builds. The fixture rejects reset before
consumption, duplicate result reads, stale status/result reads, and verifies
`AsTask()` result propagation. The extra `ReusableValueTaskSource(T initialResult)`
constructor initially exceeded the closed reference-generic metadata validator's
one-constructor limit; removing it and relying on the zero-initialized
parameterless constructor lets `ReusableValueTaskSource<string>` metadata emit.
The parameterless constructor is exported for shared consumers as well.

This remains a synchronous-only prototype. It has no continuation registration,
callback execution acknowledgement, native readiness detachment, cancellation,
or late-readiness path. The final fixture passes Windows x64/x86 static/shared
CTest 1/1. A helper creates a string result, stores it in the source and returns
only the source; after the helper frame exits, forced GC runs before the result
is read. This verifies the typed source field retains the result across that
collection. After consuming and resetting the source, a fixture-only C helper
checks that the pointer-sized `_result` slot in the runtime's open-generic
prefix is null. This verifies field clearing for the string specialization;
the fixture also performs 32 warmed completed generations with zero CX
allocations, sets a source to `uint.MaxValue`, and proves reset retires it by
rejecting the old handle status and a later completion attempt. This is bounded
count/retirement evidence for the tested synchronous cases, not a long-running
mixed-reference retention measurement. See
`tests/native/results/milestone18-g5-sync-source-windows-20261019.txt`. Do not
advance a generation for pending work or treat this slice as G5 acceptance.
Linux has not been run in this update.

### G5 scheduler callback completion hook (2026-10-19; prerequisite only)

The internal scheduler now supports a non-throwing native completion hook on a
ready callback. It runs once after normal callback return or after a CX
exception is captured, before that exception is rethrown; the callback remains
inside the scheduler dispatch guard until the hook returns. Focused runtime
tests verify callback-before-hook ordering on success and fault, and preserve
the original exception, across Windows x64/x86 static/shared cells. This is
only the scheduler primitive: `TaskState` continuation entries do not yet carry
source-generation acknowledgements, socket readiness IDs are not associated
with reusable source generations, and the reusable source remains
synchronous-only. Do not reset a source with pending callbacks based on this
hook alone. Test details are in
`tests/native/results/milestone18-g5-scheduler-ack-windows-x64-static-20261019.txt`.

### G5 source-owned callback acknowledgment (2026-10-19; partial)

`TaskState.OnCompleted` now has an internal acknowledged registration path.
Continuation entries preserve the completion function/environment through
queueing, and the TaskState scheduling loop posts acknowledged callbacks with
the native scheduler completion hook. `ReusableValueTaskSource<T>` stores its
delegate in a lazily created, reusable non-generic callback-state object because
the closed generic layout validator rejects delegate fields directly. It allows
one registration per generation and keeps `_pendingCallbacks` nonzero from
registration until the callback returns or throws. Reset checks that count, so
Reset reentered from the callback fails; the scheduler acknowledgment then
allows Reset after dispatch. If scheduling fails before queue acceptance, the
source rolls back its callback and pending count and rethrows the original
exception.

Cross-module Windows x64/x86 static/shared tests cover pending and completed
registration, duplicate rejection, result consumption in the callback,
reentrant Reset rejection, and acknowledgment after a callback exception.
Forced collection between scheduler enqueue and callback dispatch verifies the
queued closure remains rooted in both registration paths. A callback closure
captures a public cross-module struct returned by a Math-module factory; the
struct contains a string reference. A separate callback captures a string array
with a string element. Both values survive helper return and collection before
dispatch. A 256-generation
`ReusableValueTaskSource<string>` loop reuses one callback delegate and its
callback-state object, forces collection before each dispatch, consumes each
distinct-length result, resets, and checks that the native result slot is null.
The pending non-generic async Task frame probe keeps a cross-module
`TaskPayload` containing a string alive across source callback dispatch and a
second collection before its async continuation runs. All four Windows x64/x86
static/shared cells pass. This demonstrates bounded repeated reuse and a
selected suspended-frame root; it does not measure heap high-water or cover
reusable-source cancellation/readiness detachment. The initial generic Task
frame variant failed to link due to an unresolved generic `Task.GetResult`
symbol; the final non-generic frame probe passes.
The compiler suite
passes 895/895. The focused core socket scheduler test passes 1/1, including
registration, cancellation and readiness ordering. The full runtime/socket
matrix is blocked by stale `cxnet/tests/socket_tests.c` uses of the old Task
`_state` ABI; exact diagnostics and result are recorded in the checklist. See
`tests/native/results/milestone18-g5-source-callback-ack-windows-20261019.txt`.
This does not yet attach socket readiness registrations to source generations,
nor prove cancellation/late-readiness detachment, nested GC roots or long-run
retention. G5 remains open.

### G6 socket ABI audit (2026-10-19)

The F4 runtime CTest failure is a typed-result ABI mismatch, not only stale
field spelling. `SocketRuntime.ReadAsync`/`WriteAsync` return `Task<uint>`, while
`Socket.c` still allocates the old TaskState-backed object and boxes the count;
the G2 `Task<T>` stores `T` in its typed field. `tests/socket_tests.c` repeats
the old `_state` reads and registers continuations through `TaskState`. The
contracts also remain `IAsyncEnumerator<T>.MoveNextAsync(): Task<bool>` and
`IAsyncDisposable.DisposeAsync(): TaskHandle`. Update task construction and
completion through a stable typed native bridge, then migrate those contracts,
generated iterator paths, adapters, metadata and native tests together after G5
has a supported reusable source. Reproduced failure log:
`cxc/tests/native/results/milestone18-f4-runtime-windows-x64-static-c759b29e.log`.

### G6 typed socket bridge (2026-10-19; partial)

`SocketRuntime.ReadAsync`/`WriteAsync` now create `Task<uint>` and complete it
through three captured Cx actions passed to a native transfer record. The native
record owns readiness/cancellation registrations and the buffer/token roots, so
`Socket.c` no longer constructs a base Task, writes the obsolete `_state`, or
boxes the byte count. The native socket tests use a TaskHandle-backed test
wrapper over the same BeginTransfer/StartOperation bridge and pass 35/35 CTests
in Windows x64 and x86, static and shared. A focused socket regression throws
from a native completion action and verifies the same exception object is
re-thrown. A generated cxnet + streaming-read native fixture also passes 1/1
after exercising the public typed `Task<uint>` socket path through pending
reads, EOF, early disposal and cancellation.

The bridge catches completion-action exceptions through exported exception
APIs, retains the exception object identity, clears native operation roots, then
rethrows. Shared linking exposed that the private exception-state accessors were
not exported; that attempt failed and is preserved in
`milestone18-f4-runtime-windows-x64-shared-86659b80.log`. The corrected x64
static log ends in `23a2fe7b`; x64 shared uses the Visual Studio generator after
a Ninja run hit a missing test-output directory (`23a2fe7b` failed log; final
pass `c2e8ad71`). The final x86 static/shared logs end in `0439cda2`.
This is only the typed socket ABI slice: `MoveNextAsync` and `DisposeAsync`
remain Task-based; reusable source generations are not yet tied to readiness,
cancellation, or late-event detachment; completed move/disposal allocation
budgets and Linux coverage remain open. G6 remains partial.

### G6 generic ValueTask interface return probe (2026-10-19; blocked)

A contract prototype changed `IAsyncEnumerator<T>.MoveNextAsync` to return
`ValueTask<bool>` and `IAsyncDisposable.DisposeAsync` to return `ValueTask`.
Cx generation exposed a missing generic-struct interface return convention:
the generated closed-interface thunk used a `void` C return type but emitted a
value return, and interface invocation supplied no hidden result buffer. MSVC
then crashed on the generated cxnet unit (`base type 2 bad size 32`). The
contracts and generated iterator methods were restored to `Task<bool>` and
`TaskHandle`; no allocation claim follows from this failed prototype. The probe
and final verification are recorded in
`cxc/tests/native/results/milestone18-g6-valuetask-interface-abi-20261019.txt`.

Before retrying the versioned interface migration, the compiler needs an
explicit ABI for open and closed generic struct results through interface
dispatch: matching thunk signatures, caller result storage, and the same rules
in reflection/export metadata. The focused cxcore Object-before-closed-value
layout fix is independent and covered by
`EmitsCxCoreObjectBeforeClosedGenericValueLayouts`; it resolved the initial
C2079 incomplete Object base but not the interface-return ABI gap. G6 remains
unchecked.

## Object and buffer pools

Add a bounded `ObjectPool<T>` library facility for reference objects whose
construction or reusable backing storage is expensive. It complements inline
value-task results and typed storage: eliminate unnecessary allocations first,
then reuse the remaining objects where measurements justify the retention cost.
Builders, parser/serializer scratch state and reusable operation records are
initial candidates. A separate `ArrayPool<T>` with size buckets is appropriate
for variable-sized buffers; a generic object pool cannot by itself select an
array large enough for a requested minimum length. Public pooling is planned,
not implemented by the continuation-queue optimization above.

The initial contract should include these rules:

- A creation policy constructs an item on a miss. A return/reset policy clears
  operation state and decides whether an item is reusable. A discard policy
  releases owned native resources when an item is rejected, retention is full,
  or the pool is drained. Reset failure or an exception never publishes a
  partially reset item to the pool. Define cleanup-exception precedence with
  the existing `finally` rules.
- `MaxRetained` bounds idle items. A miss normally creates a new item rather
  than blocking. This does not bound the number of rented objects or the total
  memory used by borrowers. A fixed-capacity resource pool instead needs
  distinct `TryRent`/waiting, cancellation and exhaustion contracts.
- Bound retained capacity as well as object count. Policies reject or shrink
  unusually large builders/buffers, and trimming releases idle capacity. Clear
  reference-bearing fields and used array slots before retention so a small
  pooled object cannot keep an entire previous request graph alive. Explicit
  clearing of sensitive byte data is a separate requirement from clearing GC
  references. Renting a buffer must not imply zero-initialized contents.
- A renter exclusively uses an item until return; return ends that borrow.
  Reject provable wrong-pool or duplicate returns in checked usage. Optional
  generation-bearing lease handles can detect duplicate return even when a
  lease value is copied, but returning a raw `T` reference does not revoke its
  aliases. Document invalid use after return and do not claim that an ordinary
  struct lease enforces affine ownership. Compiler-enforced non-escaping borrows
  can be integrated with Milestone 22's later lifetime work.
- State the threading contract explicitly. An owner-executor pool can use
  compact preallocated storage without synchronization. A shared pool requires
  synchronization and the Milestone 16 GC/thread contracts; its thread safety
  does not make rented objects thread-safe. Do not silently use a thread-local
  pool across async continuation migration. Run user reset/discard callbacks
  outside internal locks and define reentrancy and shutdown behavior.
- A pool keeps idle items GC-reachable while the pool is live. Clear a slot when
  renting or trimming it; register native pool storage as explicit roots. Pool
  shutdown drains idle resources and handles later returns without reissuing
  them. It must not dispose items still in use by borrowers. Unreturned items
  are not tracked by a simple idle cache, so deterministic resource cleanup
  remains the borrower's responsibility unless a stronger lease contract owns it.

Do not return public multi-await `Task<T>` objects to a reusable pool merely
because they completed: callers may retain the handle and await its original
result later. Pool internal single-consumer completion sources only under the
generation, consumption and native-quiescence rules above. Native I/O buffers
remain rented until the last native access and queued callback are finished;
cancellation requested by a caller is not sufficient for return. Prefer keeping
reusable completion state inside an existing socket or enumerator when it has
one natural owner, avoiding an extra general-pool lookup for every operation.

Pools initially retain ordinary GC-heap objects. An arena allocation must never
enter a longer-lived pool; future arena integration requires enforced lifetime
checks on items and their transitive references. Scoped allocation arenas from
Milestone 22 reclaim groups of temporary objects, while a pool retains selected
objects for individual reuse; neither facility replaces the other. General
object/buffer pools are a library follow-on, not a prerequisite for Milestone
18's existing correctness slices or Milestone 22's arena implementation.

Before adopting a pool at a call site, compare against ordinary allocation and
owner-embedded reuse. Measure cold misses, warmed hits, burst concurrency,
reset/clearing cost, oversized returns, trimming and post-burst retained heap,
as well as allocations, GC cost and latency. Require no allocations for a
warmed rent/return of an existing item within retained capacity, excluding
explicit user-policy work; the bookkeeping or lease itself must not add an
allocation per operation. Validate return on exceptions, stale leases, borrowed
buffers across suspension, forced GC with only the pool retaining an item,
reference clearing, resource disposal and shutdown before claiming a supported
public pooling API.

## Acceptance and measurement

Treat allocation counts as deterministic structural budgets for the isolated
operations below. They exclude user work, user captures, external I/O setup,
and test pressure allocations. Timing budgets follow repeated Release samples
on supported toolchains; the existing nanosecond figures are diagnostic.

| Workload | Allocation target |
| --- | --- |
| Immediate `ValueTask<int>`, reference result, or void success and direct await | Zero task, result-box, frame, continuation, or scheduler allocations |
| Nested value-task calls whose awaits all complete synchronously | Zero async infrastructure allocations |
| Persistent `Task<T>` completion with primitive or struct result | No result boxing; one combined task/completion allocation as the initial target |
| Completed async iterator moves after enumerator creation | Zero per move; no allocation for completed disposal |
| Reusable iterator or socket source after warm-up | Zero recurring completion, continuation and queue allocations within warmed capacity; account separately for buffer and native registration work |
| Cold pending value-task suspension | Allocate only required escaped state; record frame, source, continuation and queue counts separately |

### G8 measured await allocation budgets (2026-10-20)

The following exact per-operation totals are now asserted by the native
benchmark for the listed bounded workloads. These are requested CX managed
allocation payload bytes, not native allocator metadata. The x64 Windows and
Linux x64 columns match; Linux is WSL on the same physical machine. Shared
pending counts one operation with two generated consumers. The 50% ratio uses
eight operations and is therefore an exact weighted mean (4.5 objects/op).
These values are workload-specific regression budgets, not universal async
cost targets.

| Generated workload | Windows x64 (objects / bytes) | Windows x86 (objects / bytes) | Linux x64 WSL (objects / bytes) |
| --- | ---: | ---: | ---: |
| Completed direct await | 2 / 96 | 2 / 48 | 2 / 96 |
| Pending direct await | 7 / 328 | 7 / 172 | 7 / 328 |
| Task awaits, 0% pending | 2 / 96 | 2 / 48 | 2 / 96 |
| Task awaits, 50% pending | 4.5 / 212 | 4.5 / 110 | 4.5 / 212 |
| Task awaits, 100% pending | 7 / 328 | 7 / 172 | 7 / 328 |
| Three-layer nested await, completed | 4 / 192 | 4 / 96 | 4 / 192 |
| Three-layer nested await, pending | 19 / 888 | 19 / 468 | 19 / 888 |
| Four-layer mixed-reference await, completed | 5 / 280 | 5 / 140 | 5 / 280 |
| Four-layer mixed-reference await, pending | 27 / 1,368 | 27 / 780 | 27 / 1,368 |
| Shared pending Task, two consumers | 13 / 600 | 13 / 316 | 13 / 600 |
| Inline `ValueTask<int>.AsTask()` | 1 / 48 | 1 / 24 | 1 / 48 |
| Task-backed `ValueTask<int>.AsTask()` | 1 / 48 | 1 / 24 | 1 / 48 |
| Cold reusable-source pending await | 14 / 644 | 14 / 348 | 14 / 644 |
| Warm reusable-source pending await | 11 / 404 | 11 / 212 | 11 / 404 |

All three samples in each final 8-operation CSV agree on each structural
allocation total; the runner checks exact totals at runtime. The 2026-10-22
full reduced benchmark refreshes the overlapping scenario totals and adds the
four-layer mixed-reference case. Raw platform data is in
`tests/native/results/milestone18-g8-four-layer-reference-20261022-<target>-static.csv`;
the earlier retained-heap run remains historical evidence for its own fixture.
Timing remains diagnostic: three samples on one host are not enough to set a
portable latency threshold. The benchmark still lacks a socket partial-transfer
performance case, nesting beyond four layers, broader result/capture shapes,
independent physical-machine evidence, and a general reference-retention budget;
G8 therefore remains partial.

Extend the existing coroutine benchmark with direct construction/await cases,
void and reference results, large structs containing references, completion
ratios from always synchronous to always pending, nested calls, fan-out,
`AsTask`, cold versus warmed sources, socket partial transfers, and GC pressure.
Report allocated bytes and object counts, frame sizes, throughput, latency
distributions and retained heap after drain. Keep equivalent ownership and
dispatch costs in any performance comparison.

Semantic gates cover repeated task awaits, single consumption of value tasks,
source reuse and stale generations, cancellation/readiness order, late events,
fault identity, exactly-once cleanup, reentrant callbacks, and forced GC while
suspended. Run compiler and native tests with strict warnings on Windows
x86/x64 and Linux x64, static and shared, with cross-project generic results.
Any task or iterator ABI change must update generated-code/runtime compatibility
checks and package consumers together. Milestone 18's correctness evidence
does not establish that these optimization budgets have been met.

## .NET references

[Microsoft's object-pool documentation](https://learn.microsoft.com/en-us/aspnet/core/performance/objectpool?view=aspnetcore-10.0)
describes policy-based reuse and the distinction between retained capacity and
total allocations. Its
[default pool implementation](https://github.com/dotnet/aspnetcore/blob/main/src/ObjectPool/src/DefaultObjectPool.cs)
is a useful reference for reuse; CX should choose storage and synchronization
for its own runtime rather than copy the implementation mechanically.

[Microsoft's ValueTask documentation](https://learn.microsoft.com/en-us/dotnet/api/system.threading.tasks.valuetask-1)
describes the single-consumption contract and copying costs.
[The .NET team's design explanation](https://devblogs.microsoft.com/dotnet/understanding-the-whys-whats-and-whens-of-valuetask/)
explains inline results and reusable asynchronous sources. These are useful
precedents; CX's GC layout, native ABI and coroutine lowering need their own
measurements and conformance tests.

### G2 typed-layout continuation (2026-10-18)

The current experiment places a common completion prefix and a typed `_result`
field directly on `Task<T>`. The open generic C metadata uses `cx_ptr` as the
placeholder for `_result`; closed value layouts emit `cx_int` for `Task<int>`
and the concrete struct type for `Task<TaskPayload>`. `Task<T>.GetResult` and
completion methods read/write those typed fields, while native TaskRuntime
helpers operate on the common prefix. `Task<T>.OnCompleted` is emitted as a
closed specialization that forwards to TaskRuntime; the colliding legacy
native generic wrapper was removed. The compiler fallback was updated to match.

The updated cross-module fixture passes CTest 1/1 in all six supported platform,
architecture, and link cells for repeated int reads, string results, and a
reference-containing struct result. This does not establish pending completion
or remove result boxes. The open-generic Task<T> source still produces two
`cx_ptr`/`bool` C type warnings, and consumer C reports pointer-signature
mismatches where generic Task<T> methods resolve through the legacy Task
identity. Windows shared generic-specialization builds also retain C4273
warnings. Strict C acceptance has not been run on this experiment.

Pending relay now constructs the destination `Task<T>` directly and completes it
through `SetResult`, `SetFault`, or `SetCanceled`. This avoids an extra
`TaskCompletionSource<T>` wrapper and its cross-module metadata collision. The
cross-module fixture now exercises two subscribers for int, string, and a
reference-containing struct result; forced GC runs while each source remains
pending. Windows x64 and x86 pass in both static and shared modes. Linux x64 has
not been rerun because the configured WSL image provides CMake 3.22.1 while the
fixture requires CMake 3.31. `Task<T>.IsCompleted` calls explicitly cast the
closed receiver to the shared open `Task*` ABI. Generated TaskRuntime completion
paths still report typed pointer warnings.

Next work: add a deterministic allocator-count assertion around pending typed
completion to prove no result box is allocated, resolve the remaining
TaskRuntime C pointer signatures, run strict generated-C checks, and refresh the
Linux x64 cells before claiming G2.

Post-metadata verification on 2026-10-18 passed `dotnet build cxc/src/CxCompiler.slnx --no-restore` (0 warnings/errors), the full compiler test suite (863/863), threading metadata regeneration, and cxcore emit. The first freshly generated native header exposed a declaration-order defect because closed Task<bool> embedded Object before Object's full definition. The C emitter now writes closed generic class layouts after ordinary class layouts. The typed-layout probe passed the earlier six-cell native matrix; the current pending typed fan-out fixture passes four Windows cells for int, string, and a reference-containing struct, with two subscribers and forced GC while pending. Linux x64 was not rerun because WSL CMake is below the required version. Strict C and no-box allocation evidence remain open. See the G2 evidence logs and checklist checkpoint.

Direct `return await taskExpression` lowering now uses callback-only source and
completion aliases. This prevents the source Task from being promoted into a
heap capture cell before the completion check. A nested all-completed caller
that awaits the optimized local-ValueTask method allocates exactly two CX
objects: the inner and outer persistent result Tasks. A nested pending call
survives forced GC and completes through both awaits. The combined nested and
void fixture passes all six platform/link cells. A bounded two-local ValueTask
path also checks both sources before allocating relay state; its all-inline
result creates one returned Task, and either pending source order survives
forced GC. Fault identity and cancellation propagate through the chain. The
bounded multi-await form supports simple synchronous prefix local declarations
and a direct result expression. Prefix statements run before the completion
checks. A prefix local referenced by the continuation is copied to a
callback-only alias inside the pending branch, while the all-inline result
reads the original stack local; the allocation probe still reports only the
returned Task. Both pending orders retain the alias through forced GC. Branches
and general intervening statements remain outside this path except for the
supported conditional/block alias rewrite described below.

The two-await prefix case also carries a reference-containing local. The
fixture copies a user-defined heap object into a prefix local, clears the
caller's reference before forcing collection, and reads the object's string
length after the second await. The all-inline path still allocates only the
returned `Task<int>`; pending-first and pending-second pass on Windows
x64/x86 and Linux x64, static/shared. This verifies the bounded callback alias
roots the object without materializing it on the inline path.

Pending callback rewriting now recursively clones `if` and block statements
when they reference callback-only aliases. The native case uses a conditional
between awaits whose condition reads both the mutated first result and the
reference-containing prefix local. Both references are rewritten to callback
aliases on the pending path; the inline path retains its stack locals. A
compiler regression checks both substitutions, while a loop mutation remains
rejected with a focused diagnostic. The final compiler suite passes 872/872
and all six native platform/link cells pass.

For the bounded two-await path, a mutation to the first awaited local before a
later await now uses a callback-only alias in the pending chain. The all-inline
branch retains its stack local, and the callback chain mutates/captures the alias
across the later suspension. A fixture applies `firstResult++`,
forces collection between awaits, and checks inline, pending-first, and
pending-second results. Compiler suite 872/872 and all six Windows x64/x86 and
Linux x64 static/shared cells pass. The alias transformation supports expression
statements, returns, and conditional/block statements; loop mutations that
require rewriting produce a focused source diagnostic. See the G4 evidence
ledger for the retained initial failure and final matrix logs.

The same bounded path carries a mutated first result and reference-prefix alias
through three sequential `ValueTask<int>` awaits. The fixture forces collection
in the first continuation, clears the caller's prefix reference, and completes
each await position independently. Pending-first, pending-second, and
pending-third preserve the expected result; all-inline creates only the
returned Task. A compiler regression confirms three nested callbacks and the
first-result alias in the chain. Full suite: 873/873. CTest passes 1/1 in all
six platform/link cells; see the three-local evidence row in the checklist.

The expanded G4 fixture also probes `LargeTaskPayload`, a 104-byte x64 / 52-byte
x86 value containing twelve string references. Inline and pending local-await
and nested-local-await paths return the expected fields; pending nested values
survive forced GC. These checks establish typed payload transfer and reference
retention, not caller-frame size or copy cost. The 2026-10-18 generated-code
audit now records static stack reservations for local/nested large-payload
awaits: GCC 11.4 Linux x64 reports 752/736 bytes (`-O2 -fstack-usage`), while
MSVC 19.51 Windows x64 reserves 800/880 bytes and Win32 reserves 208/252 bytes
(plus saved nonvolatile registers). The generated local C path materializes
the result and passes it by value to the typed Task constructor; pending
captures retain source/completion references, not the large payload. Optimized
machine code contains compiler register/stack moves and ABI calls, but no exact
physical copy-byte count has been established. See
`cxc/tests/native/results/milestone18-g4-large-payload-frame-copy-audit-20261018.txt`.
These stack figures include compiler-generated temporaries and are toolchain
reports, not allocation or runtime high-water measurements. G4/G7 frame/copy
budgets remain open. The terminal void tail fast path
now handles a direct ValueTask parameter and a Task-producing invocation. Its
nested inline case allocates four objects for the inner and outer TaskHandle /
TaskState pairs, versus eight under eager continuation lowering. Expanded CTest
passes 1/1 in all six Windows x64/x86 and Linux x64 static/shared cells. The full
compiler suite passes 870/870. See `CX_MILESTONE18_CHECKLIST.md` for exact logs,
known warnings and the remaining G4 work, including broader shapes, escaped-state
allocation and exact physical value-copy budgets.

The explicit-result form now also covers a `LargeTaskPayload` local (104 bytes
on x64 / 52 bytes on x86, with twelve string references) followed by a
synchronous `ForceCollection()` before return. Inline execution allocates only
the returned Task, and pending completion forces collection inside the callback
before publishing the result; all twelve references remain valid. This passes
Windows x64/x86 and Linux x64 static/shared CTest cells, with Linux shared
`ldd -r` resolving. The original explicit-large probe exposed the inferred-only
fast-path restriction and is retained as an initial failure. This proves typed
transfer and GC safety for this bounded shape, not an exact large-value copy
budget.

The explicit large local also survives a second pending-capable ValueTask
await. The fixture reads the payload's number and last string field only after
the second await, forces collection between the awaits and again at the end of
the callback, and exercises pending-first and pending-second schedules. The
all-inline path allocates only the returned Task. CTest passes in all six
Windows x64/x86 and Linux x64 static/shared cells; Linux shared `ldd -r`
resolves. This verifies the bounded two-await struct transfer and callback
aliases, not a general large-value frame or copy budget. Exact logs are in the
G4 explicit-large multi-await evidence row in `CX_MILESTONE18_CHECKLIST.md`.

### G4 explicit large payload through second-await failure (2026-10-19)

The second-await fixture now faults or cancels while the first awaited
`LargeTaskPayload` remains captured. It clears the caller's label reference,
forces collection while the second source is pending, and then completes that
source with the exact exception instance or cancellation. The test checks
exception identity, `Task<int>.IsCanceled`, and `GetResult()`'s
`OperationCanceledException`. Generated C inspection confirms that the first
continuation's typed payload cell is passed into the second callback and that
the callback failure handler completes the outer task. The compiler regression
passes 1/1 and CTest passes 1/1 in all six Windows x64/x86 and Linux x64
static/shared cells; Linux shared `ldd -r` resolves. See the fault/cancel
evidence row in `CX_MILESTONE18_CHECKLIST.md`. This covers the bounded
two-await chain and does not close general escaped-state allocation or G7's
frame/copy budgets.

### G4 explicit large local in conditional inter-await code (2026-10-19)

The explicit large-local chain now has an `if` between its awaits that reads
`payload.Number`, increments that struct member, and forces collection in the
taken branch. The pending callback rewrite maps both the branch condition and
compound assignment target to the callback-only payload alias. The later await
still reads the updated number and retained string fields; inline still
allocates only the result Task. A focused compiler test and the full 883-test
suite pass, and CTest passes in all six Windows x64/x86 and Linux x64
static/shared cells. The fixture keeps the second-await fault-identity and
canceled-state cases. See the conditional explicit-large evidence row in
`CX_MILESTONE18_CHECKLIST.md`. This validates one bounded if/block rewrite and
does not establish arbitrary expression rewriting or close G4/G7 budgets.

### G4 explicit large local across three ValueTask awaits (2026-10-19)

The explicit large-local continuation now covers three sequential ValueTask
awaits. A conditional member mutation and collection follow the first await;
another collection follows the second; the final result reads the updated
number and last string reference. Native cases suspend at each await position,
clear the caller's dynamic label reference, and force GC. Third-await fault
identity and canceled task state also pass after collection while pending. The
all-inline path allocates only the returned Task. The focused compiler test
passes 1/1, full suite 884/884, and CTest passes in all six Windows x64/x86 and
Linux x64 static/shared cells; Linux shared `ldd -r` resolves. See the three-
await explicit-large evidence row in `CX_MILESTONE18_CHECKLIST.md`. This
validates the tested callback-alias chain and does not close arbitrary escaped
state, frame, or copy budgets.

### G4 awaited-loop first-suspension result (2026-10-19)

`TryLowerAwaitLoopContinuation` now runs completed awaits inline within the
original loop before creating any escaped state. The first incomplete source
enters a pending branch that copies only callback-live values into branch-local
aliases, then creates the completion source, trampoline step and first callback.
After resumption, each iteration checks `IsCompleted` before constructing its
continuation; a completed source executes `GetResult`, the synchronous body and
the iterator inline, while a pending source creates the callback and registers
it. This keeps loop condition, body and iterator evaluations single-shot.

The compiler regression checks the branch-local aliases and the location of the
resumed callback in the pending branch. Native tests verify one CX allocation
(the returned Task) for an all-inline ValueTask loop, pending success after
forced GC, and a pending Task iteration followed by an already-completed
iteration. The latter returns the expected accumulated result and records one
allocation during completion and scheduler drain (the scheduler queue item).
Focused compiler tests pass 7/7; the full suite passes 885/885; CTest passes
1/1 in all Windows x64/x86 and Linux x64 static/shared cells. Linux shared
`ldd -r` resolves `libmath.so` and `libcxcore.so`; the runner used temporary
WSL CMake 3.31.8 because the installed default is 3.22.1. See the awaited-loop
evidence row and logs in `CX_MILESTONE18_CHECKLIST.md`. This closes the tested
awaited-loop allocation slice only; G4's remaining supported shapes and G7's
caller-frame/copy budgets remain open.

### G4 third-await failure propagation (2026-10-18)

The cross-module three-local fixture now covers a pending fault and cancellation
at the third await. It clears the caller's reference-prefix, forces collection
while that await is pending, and verifies exact exception identity or
`OperationCanceledException` through all three nested callbacks. The all-inline
case still allocates only the returned Task. Focused compiler tests pass 5/5,
the full suite passes 873/873, and CTest passes 1/1 in all six Windows x64/x86
and Linux x64 static/shared cells. This validates the bounded three-local path;
it does not close general escaped-state allocation or G7 copy/frame budgets.
See the corresponding evidence ledger entry in `CX_MILESTONE18_CHECKLIST.md`.

### G4 loop alias continuation (2026-10-18)

Pending callback rewriting now clones `while`, `do/while`, and `for` loops that
reference a mutated awaited result. It maps conditions and bodies, as well as
for-loop initializers and iterators, to the callback-only alias. The native
fixture increments the first result to 5 between two awaits in each form.
All-inline allocates only the returned Task; pending-first survives forced GC
and returns the expected result. The while path exercises `continue`; the for
path exercises `break`. Switch statements that read a mutated await result are
also cloned into the pending callback, including label filters. The native
fixture uses a constant selector so its `when` filter is the only reference to
the awaited-result alias; collection runs in the selected section. Inline and
pending-first results pass. The focused compiler filter passes 7/7, the full
suite passes 876/876, the compiler build has 0 warnings and errors, and CTest
passes 1/1 in all six Windows x64/x86 and Linux x64 static/shared cells. Broader
guarded labels and nested control flow remain open. See the loop and switch
evidence rows in
`CX_MILESTONE18_CHECKLIST.md`.

### G4 synchronous try alias continuation (2026-10-18)

Pending callback rewriting now clones a synchronous `try/catch/finally` region
between local ValueTask awaits when it references a mutated awaited-result
alias. The clone maps aliases in the protected body, catch filters and bodies,
and `finally`, preserves the try statement source span, and retains each bound
catch variable symbol. A separate pending throw case rewrites an exception
prefix local to its callback-only alias and verifies exact exception identity
after forced collection. The try fixture triggers a catch filter that reads
only the resumed alias, forces collection inside catch and finally, and checks
the pending result; all-inline still allocates only the returned Task. The
focused compiler regression passes 1/1, the full suite passes 878/878, and the
compiler build has 0 warnings and errors. CTest passes 1/1 in all six Windows
x64/x86 and Linux x64 static/shared cells; Linux shared `ldd -r` resolves. See
the throw/try evidence row and `milestone18-g4-try-alias-v1-*` logs in
`CX_MILESTONE18_CHECKLIST.md`. This does not support an await inside these
regions or close general escaped-state allocation and G4/G7 frame/copy budgets.

### G4 direct async ValueTask relay (2026-10-18)

The compiler now supports one direct await of a same-shaped ValueTask parameter
in an async function returning `ValueTask<T>` or `ValueTask`. Inline success
returns an inline value/default and has a measured zero CX allocation count.
Pending input is consumed once and forwarded through its existing persistent
Task backing, with no continuation or completion-source allocation. An already
completed fault or cancellation is caught and represented by a task-backed
ValueTask, preserving exception identity and cancellation. Native coverage
passes CTest 1/1 in all six Windows x64/x86 and Linux x64 static/shared cells;
Linux shared `ldd -r` resolves. Compiler suite 879/879 and build 0 warnings and
errors pass. Other async ValueTask body shapes receive an explicit unsupported
form diagnostic. See the `Async ValueTask direct-await relay` evidence row in
`CX_MILESTONE18_CHECKLIST.md` and the final
`milestone18-g4-async-valuetask-inline-v4-*` matrix logs. This direct-relay
result was extended by the bounded nested ValueTask relay fixture below. Its
nested and three-layer all-completed calls now pass zero-allocation checks for
void, int, reference-containing and large reference-containing results on the
fresh six-cell matrix. Arbitrary nested expressions, multiple awaited locals
in async ValueTask functions, and broader G4 escaped-state lowering remain
open.

### G4 awaited-finally inline cleanup result (2026-10-19)

The bounded no-result `Task` awaited-finally lowering now checks the cleanup
source before building any escaped continuation state. Completed cleanup runs
`GetResult` inline; the successful path returns the completed Task while
preserving any exception saved from the protected body. Inline cancellation is
translated into a canceled Task. Only an incomplete cleanup enters the branch
that snapshots source/exception aliases, creates the completion source and
callback, and registers the continuation. Compiler structure coverage verifies
that split and branch-local capture. Native checks measure only the returned
TaskHandle+TaskState for inline cleanup, and pending cleanup survives forced GC
through completion and scheduler drain. Focused compiler tests pass 3/3, the
full suite passes 885/885, and the cross-module CTest passes in all six Windows
x64/x86 and Linux x64 static/shared cells; Linux shared `ldd -r` resolves both
project/runtime libraries. See the awaited-finally evidence row and
`milestone18-g4-awaited-finally-v1-*` logs in `CX_MILESTONE18_CHECKLIST.md`.
This covers the simple void awaited-finally path, not nested/multiple cleanup
awaits or the remaining G4 escaped-state and G7 frame/copy acceptance.

### G4 no-result multi-await inline result (2026-10-19)

`TryLowerVoidAwaitContinuation` now has a bounded all-completed path for
sequences of direct `Task` or `Task<T>` parameter awaits with no explicit return
or nested await in the continuation. It checks all source completion states,
then calls each `GetResult` and runs the synchronous statements inline before
returning the output Task. Callback-only Task parameter aliases and all
completion/callback infrastructure are declared only in the pending branch;
this avoids a capture cell for a later Task parameter being hoisted ahead of
the inline check. Compiler structure coverage checks the branch and callback
placement. Native checks measure the two output TaskHandle+TaskState
allocations, plus pending-first and pending-second completion after forced GC.
The full compiler suite passes 885/885, and CTest passes in all six Windows
x64/x86 and Linux x64 static/shared cells; Linux shared `ldd -r` resolves both
libraries. See the no-result multi-await evidence row and
`milestone18-g4-void-multi-await-inline-v1-*` logs in
`CX_MILESTONE18_CHECKLIST.md`. Other await operand shapes, broader escaped-state
lowering and G7 frame/copy budgets remain open.

### G4 Task<T> binary-await inline and callback-registration correction (2026-10-19)

The all-inline local-await branch now accepts `Task<T>` only when the awaited
local was synthesized by the bounded binary-await normalizer. This keeps the
optimization from changing ordinary direct Task continuation shapes. The
synchronous left operand is still evaluated once before the completion check;
the completed path calls `GetResult` and constructs only the returned Task, and
the pending path uses the established callback continuation.

Native repetition exposed that async lowering's generic
`TaskRuntime.OnCompleted(object, ...)` call is unsafe for non-generic
`TaskHandle`: the C helper reads it as a `Task<T>` generic state prefix, while
TaskHandle stores a pointer to its separate TaskState there. Lowered async paths
now call `OnCompleted` on the typed source itself. Compiler build is clean,
focused regressions pass 3/3, the full compiler suite passes 885/885, and the
cross-module CTest passes in all six Windows x64/x86 and Linux x64 static/shared
cells, including forced-GC pending completion. See the callback-registration
and binary-await evidence row in `CX_MILESTONE18_CHECKLIST.md`. G4 remains
partial; this bounded path does not close G7 frame/copy budgets.

### G4 Task<T> normalized call-argument await (2026-10-19)

The same completed-source branch now covers a single direct Task<T> await
normalized out of one argument to a returned invocation. Prefix arguments still
run once, in source order, before the `IsCompleted` check. On success, the await
result and returned call execute inline and allocate only the output Task; the
pending branch retains the callback continuation. The matcher requires the
generated awaited local to be consumed by the terminal invocation, leaving
ordinary Task local-continuation shapes unchanged. The focused compiler tests
pass 2/2, the full suite passes 885/885, the build has 0 warnings/errors, and
the cross-module fixture passes all six platform/link cells with forced-GC
pending completion. See `milestone18-g4-task-call-argument-await-final-20261019.log`
and the corresponding evidence row in `CX_MILESTONE18_CHECKLIST.md`. Other
call argument positions and general nested suspension remain unsupported.

### G4 short-circuit and null-coalescing Task<T> await guards (2026-10-19)

The normalized short-circuit and null-coalescing forms select one Task source
before entering the direct return-await lowering. Compiler regressions now verify
the `IsCompleted` check precedes the output completion Task and the inline
`OnCompleted` callback registration. Native cases verify that short-circuit and
non-null paths leave the pending fallback untouched, already-completed selected
sources return the right value, and pending sources complete after forced GC in
the six-cell platform/link matrix.

The completed short-circuit false and non-null coalesce cases currently allocate
two Tasks: the normalizer's completed Task value plus the returned async Task.
Awaiting an already-completed source Task allocates only the output Task. These
results show that continuation state is deferred; they do not prove the
temporary completed Task has been eliminated. See the conditional/coalesce
evidence row and `milestone18-g4-conditional-coalesce-await-final-20261019.log`
in `CX_MILESTONE18_CHECKLIST.md`. G4 and G7 remain open.

### G4 single-await conditional branch continuation (2026-10-19)

The normalized `if/else` result shape with one direct `Task<T>` await in each
branch now has an all-inline path. It evaluates the condition and selected Task
once, checks that selected Task's completion before entering the inline result
branch, and constructs only the returned Task when complete. The pending branch
copies the selector and selected-source locals into callback-only aliases so the
compiler does not lift stack locals into heap cells before the completion
check. The existing callback continuation remains responsible for pending
completion and survives forced collection.

Compiler structure coverage checks that the selected-source `IsCompleted`
guard owns the inline and pending branches. Native tests measure one allocation
for each selected completed branch and verify selected pending completion after
forced GC. The full suite passes 885/885, the solution build has 0 warnings and
errors, and cross-module CTest passes all six Windows x64/x86 and Linux x64
static/shared cells; Linux shared `ldd -r` resolves both libraries. This is
limited to one awaited local per branch with Task<T> parameter sources; branch
continuations with multiple awaits and G7 frame/copy budgets remain open. See
the conditional-branch evidence row and
`milestone18-g4-conditional-branch-await-final-*` logs in
`CX_MILESTONE18_CHECKLIST.md`.

### G4 multi-await conditional branch continuation (2026-10-19)

The same all-inline path now covers two sequential direct `Task<T>` awaits per
normalized branch when every selected source is a Task<T> parameter and both
branch task locals use the same generated selector. The readiness guard checks
each selected parameter directly; it does not hoist a later branch-local
initializer ahead of the first await. That initializer remains in its original
sequence in both the inline continuation and the pending callback. Callback-only
parameter aliases avoid early capture cells, and an awaited result used after a
later await gets its own callback-only alias so the inline local remains a
stack local.

Compiler structure tests verify the combined readiness check, two inline
`GetResult` operations, the original later-local position and pending callback.
Native tests assert one output Task allocation for both completed branch choices
and successful pending-first and pending-second completion after forced GC. The
full suite passes 885/885, build has 0 warnings/errors, and cross-module CTest
passes all six Windows x64/x86 and Linux x64 static/shared cells; Linux shared
`ldd -r` resolves. Compiler-generation probes before the callback aliases and
awaited-result alias are preserved as failed logs. Forced-GC native cases also
verify exact fault identity at either await and cancellation at the second
await. This two-await slice does not cover call-source arms, more than three
awaits, or G7 frame/copy budgets. The one-await free-function subset below uses
resolved declaration signatures before expression binding; multi-await call
sources stay on the established lowering because checking a later call before
an earlier await would change source order. See the evidence row and
`milestone18-g4-conditional-multi-await-fault-cancel-final-*` logs in
`CX_MILESTONE18_CHECKLIST.md`.

### G4 one-await conditional branch calls (2026-10-19)

The one-await branch fast path also accepts a conditional whose two arms call
uniquely named, non-generic, same-namespace top-level functions with matching
`Task<T>` return types. Declaration signatures have resolved before async
lowering, so this bounded proof does not require moving lowering after
expression binding. Member calls, local-function shadowing, overloads, generic
functions, imports/project references, and call sources in multi-await branches
remain on the existing lowering. The selected call still runs once in its
original conditional-local initializer; the completion guard reads that local,
so it neither repeats the call nor evaluates the unselected arm. Native probes
count both branch calls and assert the all-inline path allocates only its output
Task. Pending selected success, exact fault identity and cancellation after
forced GC preserve the same one-time/lazy call counts. A negative compiler test
keeps ambiguous overloads on the established relay path. Focused call-source
tests pass 2/2, the compiler suite passes 888/888, and CTest passes all six
platform/link cells; Linux shared
`ldd -r` resolves both libraries. See the call-source branch evidence row and
`milestone18-g4-conditional-call-branch-v2-*` logs in
`CX_MILESTONE18_CHECKLIST.md`. G4 and G7 remain open.

### G4 three-await conditional branch continuation (2026-10-19)

The existing all-inline branch construction also handles three sequential
direct `Task<T>` parameter awaits. Its readiness condition checks all three
selected sources, while the second and third selected-task locals stay in their
original positions after the previous `GetResult`, preserving source order.
Compiler coverage verifies the three checks, three inline results and pending
callback. Native tests assert one output Task for both completed selectors,
pending-first, pending-second and pending-third completion after forced GC, plus
third-await fault identity and cancellation propagation. Focused conditional
branch tests pass 5/5, the compiler suite passes 888/888, and CTest passes all
six platform/link cells; Linux shared `ldd -r` resolves both libraries. See the
three-await evidence row and `milestone18-g4-conditional-three-await-v2-*` logs
in `CX_MILESTONE18_CHECKLIST.md`. This covers three direct parameter sources;
higher await counts, multi-await call sources, G4 escaped-state coverage and G7
frame/copy budgets remain open.

### G4 four-await conditional branch continuation (2026-10-19)

The same all-inline branch path handles four sequential direct `Task<T>`
parameter awaits. The combined readiness check covers each selected source, and
the fourth selected-task local stays after the third `GetResult`, preserving
source order. Compiler structure coverage verifies all four readiness checks
and inline results plus that local placement. Native tests assert one output
Task allocation for either completed selector; pending-first, second, third and
fourth completion after forced GC; and fourth-await fault identity and
cancellation propagation. Focused conditional-branch tests pass 5/5, and
cross-module CTest passes all six platform/link cells; Linux shared `ldd -r`
resolves both libraries. See the four-await evidence row and
`milestone18-g4-conditional-four-await-v1-*` logs in
`CX_MILESTONE18_CHECKLIST.md`. Higher await counts, multi-await call sources,
broader G4 escaped-state coverage and G7 frame/copy budgets remain open.

### G4 explicit Task<T> local continuation (2026-10-19)

A single explicit `System.Threading.Tasks.Task<T>` local can now use the
all-inline continuation path when its declared result type matches the async
function, whether written as imported `Task<T>` or fully qualified
`System.Threading.Tasks.Task<T>`. Its initializer stays in the synchronous
prefix, and the readiness guard reads the materialized local. Compiler regressions check the guard and
pending branch placement and ensure a Task-returning invocation initializer is
not repeated. Reference-containing prefix locals used after the await become
callback-only aliases inside the pending branch; the inline branch keeps its
stack locals. Native counters verify one source call in both completed and
pending paths; completed input allocates one output Task, and pending completion
survives forced GC after the caller clears its reference. The reference-prefix
case combines the Task-returning initializer and captured value and verifies the
source call remains exactly once. The terminal-conditional regression verifies
the callback-only alias in the condition and both return arms. Native checks
cover both branches inline and after pending forced GC, plus pending fault
identity and cancellation after GC with the caller reference cleared. Focused explicit-local
tests pass 3/3, the full compiler suite passes 891/891, and the build has 0
warnings/errors. CTest passes all six platform/link cells and Linux shared
`ldd -r` resolves both libraries. See the explicit-local evidence row and
`milestone18-g4-explicit-task-local-reference-prefix-v4-*` logs in
`CX_MILESTONE18_CHECKLIST.md`. Broader G4 escaped-state coverage remains open.

For a normalized conditional branch with multiple awaits, a later selected
Task-returning call remains in continuation lowering after earlier awaits.
Including that later local in the outer all-ready guard would both violate
source order and reference a local before its declaration. The compiler
structure regression verifies the fallback keeps the local after the first
`GetResult`. Native `PickConditionalTaskBranchCallAfterAwait` verifies both
inline selector choices execute one selected call and no unselected call. Its
pending-first path verifies zero calls before the first source completes, one
selected call after resumption, and the same call count and expected result
after forced GC before the second source completes. Cross-module CTest passes
all six Windows x64/x86 and Linux x64 static/shared cells; Linux shared
`ldd -r` resolves both dependencies. This is a source-order-safe fallback, not
a multi-await call-source all-inline optimization.

### G4 later call source after a completed await (2026-10-20)

A bounded two-await `Task<T>` shape takes the all-inline path when the second
awaited local is initialized by a uniquely resolved, same-namespace,
non-generic Task-returning call, either directly or through a conditional with
matching call/Task parameter arms. The first source is checked before its
result is consumed; then the call runs once in its original position, and the
second readiness check reads the materialized Task local. Pending-only aliases
retain Task parameters, and a first awaited result used by the terminal return
is copied to a callback-only alias inside the second-pending branch. This keeps
the all-completed path from allocating capture cells as well as relay state.
Native coverage asserts exactly one output Task allocation when both sources
are complete, first- and second-pending completion after forced GC, call
order/count, and second-await fault identity/cancellation. Focused compiler
coverage passes 3/3; the full compiler suite passes 902/902; the build has 0
warnings/errors. CTest passes all six Windows x64/x86 and Linux x64
static/shared cells, with Linux shared `ldd -r` resolving both dependencies.
This is a single two-await call-source form; longer call-source chains, other
escaped-state allocation and G7 frame/copy budgets remain open. Exact evidence
and final roots are listed in `CX_MILESTONE18_CHECKLIST.md` and
`cxc/tests/native/results/milestone18-g4-later-call-source-20261020.txt`.

### G4 sequential multi-await later call sources (2026-10-20)

The bounded sequential Task<T> local path supports three or more awaits when
the first source is a Task<T> parameter and every later awaited local is
initialized immediately after the preceding await by a uniquely resolved
direct or supported conditional Task<T>-returning call. The all-completed path
checks each materialized source in order and evaluates each call once; pending
relay state and callback-only Task-parameter aliases are created only inside the
branch whose readiness check fails. If a later call expression cannot be
cloned, lowering falls back to the existing continuation path. Callback names
include pending-branch identity and nested callback position to prevent
duplicate generated C helpers across continuation paths. Compiler structure
regressions and native exact-allocation/GC/fault/cancellation coverage pass for
three, four and five awaits. The five-await native fixture checks one output-Task
allocation inline; success, fault identity and cancellation at each pending
position after forced GC; and ordered call counts. A separate three-await form
selects a later call source conditionally, exercises both completed branches,
and checks every pending position plus final fault/cancellation. Focused
five-await and conditional-source tests pass 1/1 each; the full suite passes
906/906, the build has 0 warnings/errors, and CTest passes all six Windows
x64/x86 and Linux x64 static/shared cells; Linux shared `ldd -r` resolves both
libraries. Other source shapes, broader escaped-state shapes and G4/G7
frame/copy budgets remain open. A three-await
mutated-result call-source form now also supports await-free synchronous
expression statements before each later Task local: `left += 1` and
`middle += 2` preserve source order and callback-only result aliases. Its
all-completed path allocates exactly the output Task; each pending position
resumes after forced GC with the expected result and call counts. The focused
compiler test passes 1/1, full suite 907/907, build 0 warnings/errors, and the
final six native matrix cells pass; Linux shared `ldd -r` resolves both
dependencies. Other declarations, blocks, loops and other control flow
remain open. Evidence: `cxc/tests/native/results/milestone18-g4-multi-await-call-sources-20261020.txt`,
`cxc/tests/native/results/milestone18-g4-five-await-conditional-call-sources-20261020.txt`,
`cxc/tests/native/results/milestone18-g4-multi-await-mutated-call-source-20261020.txt`,
and the evidence ledger in `CX_MILESTONE18_CHECKLIST.md`.

The next narrow slice admits await-free typed local declarations between the
awaited result and the following Task call local. Explicit `int` declarations
are preserved in all-inline lowering and pending callback cloning; the native
fixture checks exactly one output-Task allocation inline and all three pending
positions after forced GC. Baseline exit 506 showed correct results and call
values but eager relay allocation. The focused compiler test passes 1/1, the
full suite passes 908/908, and the compiler build has 0 warnings/errors. Final
CTest passes all six Windows x64/x86 and Linux x64 static/shared cells; Linux
shared `ldd -r` resolves both libraries. Other declaration forms, blocks,
loops and nested control flow remain open. Evidence:
`cxc/tests/native/results/milestone18-g4-multi-await-local-call-source-values-20261020.txt`.

The gap matcher also accepts await-free `if` statements when the recursive
callback clone succeeds. Conditional adjustments to `left` and `middle` run
before their later calls both inline and after resumption. Native checks assert
one output Task inline and success at all three pending positions after forced
GC. Baseline exit 516 had correct results/calls but exceeded that budget. The
focused compiler test passes 1/1, the full suite passes 909/909, the build has
0 warnings/errors, and CTest passes all six native cells; Linux shared
`ldd -r` resolves both libraries. Blocks and other control flow remain open.
Evidence: `cxc/tests/native/results/milestone18-g4-multi-await-conditional-gap-20261020.txt`.

An await-free block gap is also supported when callback cloning succeeds. The
verified blocks declare a typed local from the awaited value and assign it
back; compiler structure checks confirm the explicit type and bound local
symbol survive cloning. The native fixture checks exact inline output-Task
allocation and all three pending positions after forced GC. Baseline exit 526
had the correct result/call values but exceeded the allocation budget. Focused
test 1/1, full suite 910/910, build 0 warnings/errors, and CTest 1/1 in all six
cells pass; Linux shared `ldd -r` resolves both dependencies. Other block and
control-flow contents remain open. Evidence:
`cxc/tests/native/results/milestone18-g4-multi-await-block-gap-20261020.txt`.

An await-free `while` statement is also supported in the gap. The native case
adjusts each awaited result with a loop before creating the next Task source;
compiler structure checks verify condition/body/call order. The inline path
allocates exactly one output Task and each pending position succeeds after
forced GC. Baseline exit 536 had correct result/call values but exceeded the
allocation budget. Focused test 1/1, full suite 911/911, build 0 warnings/errors
and all six CTest cells pass; Linux shared `ldd -r` resolves both dependencies.
For and other loop shapes remain open. Evidence:
`cxc/tests/native/results/milestone18-g4-multi-await-loop-gap-20261020.txt`.

A do/while gap also passes with its body running before its condition. Its body
increments each awaited result before the later call; exact inline allocation
and all three pending positions after forced GC pass. Baseline exit 546 had
correct results/calls but exceeded the one-output-Task budget. Focused test
1/1, full suite 912/912, build 0 warnings/errors, and all six native cells
pass; Linux shared `ldd -r` resolves both dependencies. For loops and other
loop contents remain open. Evidence:
`cxc/tests/native/results/milestone18-g4-multi-await-do-while-gap-20261020.txt`.

### G6 reusable async-iterator move value (2026-10-19; partial)

`IAsyncEnumerator<T>.MoveNextAsync()` now returns `ValueTask<bool>` and
`IAsyncDisposable.DisposeAsync()` returns `ValueTask`. Each generated async
iterator allocates one `ReusableValueTaskSource<bool>` with its enumerator and
returns a versioned value task for each move. A consumer must consume the move
result before requesting another move. The generic value-task source/version
fields precede the inline `T` result field so the open generic status helper and
closed value layouts agree on those offsets. Generic struct placeholders carry
the `Object` prefix used by their closed layouts; the fixed-layout
`Nullable<T>` placeholder is deliberately excluded.

The streaming native fixture verifies pending byte delivery before EOF, EOF,
early disposal, cancellation of a pending read, and zero tracked CX allocations
for 64 warmed inline moves plus terminal completion and completed disposal after
enumerator creation. Windows x64 and x86 static/shared CTest cells pass. A later
socket-source prototype keeps the public `Socket.ReadAsync` Task API but routes
the streaming iterator through a reusable `ValueTask<uint>` source. In each
Windows cell, the first pending read plus move counted 25 CX allocations and a
repeated pending read plus move counted 18; the earlier Task-backed baseline
counted 20 for one pending read plus move. The first source-backed count includes
iterator/source initialization. Pending I/O therefore still allocates, and this
count does not measure retained heap. Readiness/cancellation ownership,
reentrancy and late-event safety, and Linux ABI coverage remain open. The native
fixture now queues read readiness before cancellation, verifies a canceled move
and exactly-once iterator cleanup, and exercises public `Socket.ReadAsync` as a
pending `Task<uint>` across forced GC. The Task smoke runs before cancellation;
placing it after prior cancellation intermittently yielded a canceled or
mismatched result in earlier shared runs. G6 remains partial; see its evidence
row in `CX_MILESTONE18_CHECKLIST.md`.

The first source-backed `AsTask()` prototype created a typed
`TaskCompletionSource<T>` and completed it from the source's acknowledged
callback. Its Windows x64 cross-module link failed on open-generic completion
symbols. Generic local-lambda helpers now emit per closed caller specialization,
and nested lambda bodies participate in closed-target registration. The source
and synthetic declaration both use this path without boxing `T`. The compiler
suite passes 898/898, and the cross-module fixture passes completed and pending
success, fault identity, cancellation, forced GC, duplicate conversion, and
reset-after-ack checks in Windows x64/x86 static/shared cells. The fixture uses
`TaskScheduler.RunOne()` to dispatch the callback; `RunUntil(object)` expects the
non-generic TaskHandle layout and must not receive `Task<T>`. Linux and the other
G6 lifecycle/reflection gates remain open. Exact evidence is recorded in
`cxc/tests/native/results/milestone18-g6-as-task-runtime-probe-20261019.txt`.

### G7 native frame and iterator-allocation audit (2026-10-20; partial)

The existing coroutine benchmark fixture was refreshed to use the current
`ValueTask<bool>`/`ValueTask` async-enumerator contract. On Windows 11 x64,
MSVC 19.51 / CMake 4.4.4, its Release/static runs at 64 and 128 yielded
elements reported the same 40-byte synchronous generated iterator frame, two
managed allocations (80 requested bytes) per enumeration, and zero GC
collections. Doubling yielded elements did not increase the allocation count;
the generic `IEnumerator<int>.Current` path did not box once per value. The
handwritten C control used the same 40-byte frame size and allocated zero CX
objects. The generated async iterator benchmark reported an 80-byte frame and
four allocations (288 requested bytes) per 64/128-element enumeration; these
per-enumeration costs include iterator/task state and are not a per-yield
measure. The benchmark now asserts allocation count and bytes are identical
for one element and the configured full enumeration; its 128-element native
run passes at 2 objects/80 bytes for both checks on Windows x64 and Linux x64,
and 2 objects/64 bytes on Windows x86. Timing from these reduced three-sample
runs is diagnostic only.

A reference-containing synchronous iterator payload was added as a separate
Windows x64 probe. `PayloadRange` yields a typed struct containing an integer
and a string reference; the consumer reads both fields. A one-element run and
the configured 16-element run each used two managed allocations and 112
requested bytes. Generated C returns the payload from `IEnumerator<T>.Current`
as `struct IteratorPayload`; boxing helpers found by inspection are in
reflection support, outside this iteration path. Together with the cross-arch
`int` probes, this is bounded evidence for typed `Current` and no per-yield
allocation/boxing in these shapes. It is not a universal element-type guarantee
or an allocation budget. The generated report and CSV are
`cxc/tests/native/results/milestone18-g7-reference-struct-windows-x64-20261020.txt/.csv`;
the exact command and generated-source audit are in the matching `-audit.txt`.

The follow-up now covers reference-only `string`, the reference-containing
small struct, and a 104-byte x64 / 52-byte x86 struct with twelve string fields
on Windows x64, Windows x86, and Linux x64 (WSL). Each shape validates its
fields and uses the same two managed allocations at one and 16 elements. The
fixed enumerator frames are 40/48/56/144 bytes on 64-bit and 32/32/36/80 bytes
on x86 for `int`, `string`, small struct, and large struct respectively. The
large payload C `Current` getter and interface slot return the concrete struct;
reflection boxing helpers do not appear on the iteration path. These checks
bound no-per-yield allocation/boxing evidence to these shapes; they do not
measure optimized copy traffic for yielded structs or define a general budget.
Full matrix and limits: `cxc/tests/native/results/milestone18-g7-iterator-element-shapes-20261020.txt`.

The 104-byte x64 / 52-byte x86 yielded struct also received an optimized
copy-path audit. In MSVC x64 and GCC Linux x64, `MoveNext` writes the 104-byte
payload from stack scratch into the enumerator frame; the standalone generated
`Current` getter copies the same 104 bytes to its by-value result buffer. In
MSVC x86, `MoveNext` stores the 52-byte payload fields directly into the frame,
and the standalone getter copies 52 bytes to its result buffer. The optimized
`LargePayloadSum` foreach loops on all three targets read the frame fields
directly and contain no call to `Current` or separate result temporary. This is
one compiler/configuration fixture: a different consumer may materialize the
by-value Current result. The measurements support retaining the direct known
factory path, but do not establish a universal copy budget or speedup. Exact
disassembly observations and roots are in
`cxc/tests/native/results/milestone18-g7-yielded-large-struct-copy-20261020.txt`.

The next G7 shape adds a synchronous `BranchingRange` with an if/else yield in
the loop. Windows x64/x86 and Linux x64 validate its sequence and keep two
allocations at both one and 16 elements. Its frame fields match `Range` and
measure 40 bytes x64/Linux and 32 bytes x86. In MSVC x64 `/O2`, the five
branching iterator methods total 1,330 named text bytes vs 1,232 for Range;
the 98-byte growth is in `MoveNext`. With the corrected `IEnumerator<T> :
IDisposable` contract, both `BranchingSum` and `Sum` are 578 bytes in this
generated direct-foreach fixture; the earlier 325-byte consumer result omitted
foreach disposal. This is one control-flow shape
and compiler configuration, not a budget. The scheduler-link embedding option
was inspected and not pursued: the reusable source already owns an acknowledged
cached node, while ready, timer and I/O queues control node lifetime separately.
See `cxc/tests/native/results/milestone18-g7-branching-iterator-20261020.txt`.

A captured if/else variant now exercises the provider receiver and cleanup
state too. `BranchingValues` yields a provider-relative value from each branch
and increments its disposal counter in `finally`. The native benchmark invokes
the lifecycle checks from its final `main`; Windows x64/x86 and Linux x64 pass
lazy start, early-break disposal exactly once, repeated enumeration and a
one-vs-16 allocation comparison. Its frame is 56 bytes x64/Linux and 40 bytes
x86; allocation remains two objects, 112 bytes x64/Linux and 80 bytes x86.
The emitted frame retains the provider receiver and `finally` active flag. The
benchmark's minimal `IEnumerator<T>` now inherits `IDisposable` so that this
check matches the runtime contract. This is one captured sync shape, not a
general frame budget. Full details and preserved attempts are in
`cxc/tests/native/results/milestone18-g7-captured-branch-iterator-20261020.txt`.

A captured async branch fixture now combines a receiver/provider capture,
branch-dependent yields, one pending source await, forced GC before and after
resume, and `finally` cleanup. Normal completion and early `DisposeAsync` after
the first resumed value each run cleanup once. Windows x64 and Linux x64 report
a 120-byte frame and 11 CX allocations / 720 requested bytes for both one and
16 yielded values; Windows x86 reports a 68-byte frame and 11 / 404 bytes.
Those allocation counts include source/task/frame/move setup and completion.
Generated frame inspection confirms the receiver, awaited source, count, loop
and continuation locals, and finally-active flag are retained. This is one
bounded static fixture, not a general async-iterator budget. Commands, roots,
and limits are in
`cxc/tests/native/results/milestone18-g7-async-captured-branch-20261020.txt`.

The G8 benchmark includes deterministic generated Task await workloads with
0%, 50%, and 100% pending sources. It alternates completed and pending sources
for the mixed case and checks managed allocation totals against the weighted
completed/pending baseline before recording timing. Windows x64 and Linux x64
report 2/96, 4.5/224, and 7/352 allocations/bytes per operation; Windows x86
reports 2/48, 4.5/118, and 7/188. A focused 8-operation, 3-sample run passes on
Windows x64/x86 and Linux x64 static.

A shared pending-source scenario has two generated `Task<int>` awaiters subscribe
to one `TaskCompletionSource<int>`. It forces GC while both awaiters are pending,
completes once, drains, forces GC before reading either result, and checks both
results. The asserted lifecycle cost is 13 allocations/648 requested bytes on
x64 and 13/348 on x86 per two-consumer operation.

Inline-result and Task-backed `ValueTask<int>.AsTask()` paths each assert one
allocation/48 bytes on Windows x64 and Linux x64, or one/24 on Windows x86. The
Task-backed path's measured allocation is the original Task; conversion adds no
second Task in this benchmark. These samples do not cover reusable-source
conversion. All timing samples share one Windows host; WSL is not an independent
machine, and the handwritten controls are not equivalent ownership/dispatch
comparisons. The refreshed three-layer integer await chain asserts completed
4/192 and pending 19/888 allocations/bytes on x64, or 4/96 and 19/468 on x86.
The four-layer mixed-reference chain asserts completed 5/280 and pending
27/1,368 on x64/Linux, or 5/140 and 27/780 on x86. Windows x64/x86 and Linux
x64 full reduced runs pass these exact checks.

The G8 cold/warm probe compares a new `ReusableValueTaskSource<int>` per
operation with one source reused and reset only after its generated await result
is consumed and the callback is acknowledged. The refreshed cold path uses 14
allocations and 644 requested bytes x64/Linux, or 14/348 x86. Warm reuse uses
11/404 x64/Linux or 11/212 x86. It saves three allocations per operation (240
bytes x64, 136 x86), while the returned Task and continuation/frame work still
allocate.

After warming, a retained-heap check forced GC after batches of 64 drained
pending/complete/consume/reset cycles, through 192 cycles. The managed heap
stayed at 544 bytes x64, 416 bytes x86, and 592 bytes Linux x64. These values
include the intentionally retained reusable source and scheduler state. This is
one bounded integer case, not a universal retention budget. A 2026-10-22
extension repeats the drain check with `ReusablePayload` (`int` plus a fresh
string) for 192 cycles. After eight initialization warmups, the mixed-reference
heap is flat at 768 bytes x64, 604 bytes x86, and 928 bytes Linux x64. The int
case is also flat at 520, 292, and 568 bytes respectively with the same final
warm-up logic. These measurements include the retained source and continuation
state and establish no general reference-retention budget. Exact runs and the
retained first x86 attempt are documented in
`cxc/tests/native/results/milestone18-g8-reference-retention-20261022.txt`.
The timing samples share one Windows host; WSL is not independent hardware, and
the handwritten controls are not equivalent ownership/dispatch comparisons.
G8 remains partial: nesting beyond four layers, broader result/capture budgets,
and independent physical-machine evidence remain open. MSVC and GCC were both
used, but the Linux runs share the Windows host under WSL. The four-layer
follow-up's exact roots, commands and refreshed full-run CSVs are in
`cxc/tests/native/results/milestone18-g8-four-layer-reference-20261022.txt`.
The completion-ratio evidence is in
`cxc/tests/native/results/milestone18-g8-completion-ratios-20261020.txt`; shared
consumer and AsTask details are in
`cxc/tests/native/results/milestone18-g8-sharing-as-task-20261020.txt`; deep
nested-await data and commands are in
`cxc/tests/native/results/milestone18-g8-deep-nesting-20261020.txt`; cold/warm
source and post-drain retention results are in
`cxc/tests/native/results/milestone18-g8-cold-warm-20261020.txt` and
`cxc/tests/native/results/milestone18-g8-retained-heap-20261020.txt`.

The 2026-10-22 mixed-reference extension sends an `IteratorPayload` (int plus
string reference) through three nested Task<T> awaits. Completed inline uses
4 allocations / 224 requested bytes on x64/Linux and 4 / 112 on x86. Pending
completion uses 21 / 1,080 bytes on x64/Linux and 21 / 628 on x86; a separate
session forces GC while suspended and again before result consumption, then
checks both fields. A fresh-string reusable-source loop also stays flat after
GC through 192 drained/reset cycles: mixed-reference heap is 1,040 bytes x64,
804 x86 and 1,200 Linux x64; the matching int heap is 792, 600 and 840 bytes.
These figures follow a 64-cycle initialization warm-up and include the retained
source/continuation state. They are one bounded payload/chain, not general
allocation or retention budgets. Exact commands, roots and transient assertion
failures: `cxc/tests/native/results/milestone18-g8-deep-reference-20261022.txt`.

A generated `Socket.WriteAsync` case uses a 4 MiB request and constrained
4 KiB socket buffers. It measures `StartWrite`, the synchronous completion
check, and `GetResult`; socket setup, buffer allocation, warmup and peer drain
are outside the timer. Exact assertions pass at 7 allocations/384 requested
bytes per operation on x64/Linux and 7/212 on x86. Linux x64 returns an 8,064
byte partial transfer in all 32 operations per sample. Windows x64/x86 returns
the full 4 MiB request in this loopback TCP setup, even with both socket buffers
constrained; the report records this without treating it as partial-transfer
evidence. Pending readiness and scheduler dispatch are not measured. Windows
uses loopback TCP while Linux uses AF_UNIX, so timing values are diagnostic and
not directly comparable. Static exact-budget tests pass Windows x64/x86 and
Linux x64; shared-link CTest passes on all three targets. See
`cxc/tests/native/results/milestone18-g8-socket-write-20261020.txt`.

The generated-source runner directs emitted project and native build outputs to
unique temporary roots. The compiler also refreshed dependency-generated
`cxcore/src/.obj` and `cxnet/.obj` sources in the checked-out trees; those
generated directories were preserved. The first WSL attempt used CMake 3.22.1
and stopped at the documented 3.31 minimum; the retry used the existing
`/tmp/cmake-3.31.8/bin/cmake` installation and passed.

Emitted streaming code inspection found the iterator's receiver, parameters,
five persisted locals, move-source state, and cancellation registration are
read across resume/yield/dispose paths. The two 24-byte x64 callback
environments serve separate cancellation and completion registrations. No
field or capture was removed from the generated frame because this inspection
did not establish one as dead. A first optimized code-size sample is now
recorded in `cxc/tests/native/results/milestone18-g7-code-size-20261020.txt`:
MSVC 19.51 x64 Release reports 1,185 bytes across the five named synchronous
Range methods and 2,417 bytes across the eight named AsyncRange methods. These
totals exclude thunks, reflection, metadata and linker effects, and establish
neither a general budget nor a normalized comparison. A bounded optimized copy
audit is now recorded in
`cxc/tests/native/results/milestone18-g7-large-copy-assembly-20261020.txt`:
MSVC x64 `/O2` moves the 104-byte large payload twice (208 bytes written) for
the completed inline ValueTask path, and three times (312 bytes written) for a
completed task-backed ValueTask. These are measured load/store sequences in one
retained fixture, not a compiler-independent budget; x86/Linux physical copy
traffic remains open. The combined counts include the `ValueTask.GetResult`
return boundary and the output Task result store, which the earlier G4 audit
had only identified at source level. G7 remains open.
The bounded native reusable socket operation change and six-cell verification
are recorded in `CX_MILESTONE18_CHECKLIST.md` and the G6 socket-source evidence
file.

For a direct call to a non-generic generated iterator factory in the same
module, binding now associates the factory with its generated state-machine
class. The `foreach` emitter uses direct generated calls for `GetEnumerator`,
`MoveNext`, `Current`, and `Dispose`, while storing and returning the factory's
declared interface value through the ordinary ABI. Calls through an interface
variable, across a module boundary, or to a generic generated iterator keep
the vtable path. Native checks verify that assigning the sequence does not run
the factory, an early `break` runs the iterator's `finally` exactly once, and
the same lazy sequence can be enumerated twice to produce independent
enumerators. The direct factory path also passes early disposal and normal
completion checks.

The reduced Release benchmark harness passes these runtime assertions on
Windows x64 and x86 and Linux x64 static. Its existing generated `Range`
benchmark still reports two managed allocations per 128-element enumeration:
80 bytes with a 40-byte frame on x64 and 64 bytes with a 32-byte frame on x86.
The optimization removes interface-slot dispatch from eligible loops but does
not change the enumeration allocation or frame. On the retained MSVC x64
pre-disposal minimal-interface fixture the `Sum` text section grew from 189 to
325 bytes (+136) after direct calls were emitted; that fixture omitted
`IDisposable`, so it did not account for foreach disposal. The corrected
fixture measures a 578-byte `Sum` with disposal. This single method/configuration tradeoff needs broader
code-size and timing evidence, so no general speedup or size claim is made.
Compiler-output coverage is in
`cxc/tests/CxCompiler.Tests/IteratorSemanticTests.cs`; native reports are
`cxc/tests/native/results/milestone18-g7-direct-foreach-windows-x64-fresh.txt`,
`milestone18-g7-direct-foreach-windows-x86-fresh.txt`, and
`milestone18-g7-direct-foreach-linux-x64-fresh.txt`. A same-module non-generic
instance factory is also covered by compiler and native probes on Windows
x64/x86 and Linux x64 static; its reports are
`cxc/tests/native/results/milestone18-g7-direct-foreach-instance-windows-x64.txt`,
`milestone18-g7-direct-foreach-instance-windows-x86.txt`, and
`milestone18-g7-direct-foreach-instance-linux-x64.txt`. Generic and
cross-module factories remain on the interface path.

The retained Windows x64 generated C was also audited for dead frame storage.
The `Range` frame fields (`state`, `hasCurrent`, `current`, `advancing`, the
factory parameter, and loop index) all have emitted method references. The
`AsyncRange` state/current/parameter/index fields and async move source,
started/suspended/fault-cleanup flags, cancellation token, and cancellation
registration also appear in emitted method bodies. No field was removed based
on this fixture; the measurement does not rule out dead fields in other iterator
shapes.

The retained G4 large-payload fixture was also inspected on Windows x86 and
Linux x64. Both preserve the x64 transfer shape: completed inline ValueTask
results make two full copies and task-backed results make three. The payload is
52 bytes on Windows x86 and 104 bytes on Linux x64. These optimized objects
predate current HEAD; toolchain details, instruction forms, and exclusions are
in `cxc/tests/native/results/milestone18-g7-large-copy-cross-arch-20261020.txt`.

A counted `for` gap now passes as well. The fixture uses declaration
initializers, conditions, compound iterators, and bodies that mutate each
awaited result before the later call. Baseline return 556 had the correct result
and call values but exceeded the one-output-Task budget. A first shrinking-bound
probe returned 554 because its expected value was invalid; that root remains
preserved and is excluded from evidence. The accepted matcher also requires an
await-free statement and successful recursive callback cloning. Focused test
1/1, full suite 913/913, clean build, and all six native cells pass; Linux
shared `ldd -r` resolves both dependencies. Nested loops and other loop contents
remain open. See
`cxc/tests/native/results/milestone18-g4-multi-await-for-gap-20261020.txt`.

Nested counted-loop gaps were audited as well. The existing recursive callback
clone correctly rewrites awaited-result aliases through two nested `for`
statements with independent loop-local symbols, so this slice added compiler and
native regression coverage without changing the lowerer. The fixture verifies
one output Task inline, result 88, later call values 23 and 31, and pending-first,
second, and third success after forced GC. Focused test 1/1, full suite 914/914,
clean build, and all six native matrix cells pass; Linux shared `ldd -r` resolves
both dependencies. See
`cxc/tests/native/results/milestone18-g4-multi-await-nested-for-gap-20261020.txt`.

A `switch` gap now passes as well. The selector and case/default statements are
cloned into the pending callback with awaited-result alias rewriting. Baseline
return 576 had the correct result and call values but exceeded the one-output-
Task allocation budget. Inline case arms and pending default arms are covered;
each pending position survives forced GC. Focused test 1/1, full suite 915/915,
clean build, and all six native cells pass; Linux shared `ldd -r` resolves both
dependencies. Try/catch/finally gaps remain open. See
`cxc/tests/native/results/milestone18-g4-multi-await-switch-gap-20261020.txt`.

A no-throw `try/finally` gap now passes. The protected body and cleanup each
mutate the awaited result; callback rewriting carries the alias through both
blocks before the later Task call. Baseline return 586 had correct results and
call values but exceeded the one-output-Task budget. Focused test 1/1, full suite
916/916, clean build, and all six native cells pass; Linux shared `ldd -r`
resolves both dependencies. Catch/filter behavior remains open. See
`cxc/tests/native/results/milestone18-g4-multi-await-try-finally-gap-20261020.txt`.

Catch filters in a try gap now pass with awaited-result aliases and exception
variable scope preserved in all three continuation callbacks. The native fixture
throws a preallocated exception, takes each filtered catch, mutates the result,
and then runs its finally block. A pending-first false-filter case forces GC,
checks that the exact exception instance escapes through the output Task, checks
that finally runs once, and verifies the later Task source is not called.
Baseline fallback was established for the try/finally shape at return 586; no
separate catch-only baseline was run. Focused test 1/1, full suite 917/917,
clean compiler build, and all six native cells pass; Linux shared `ldd -r`
resolves both dependencies. See
`cxc/tests/native/results/milestone18-g4-multi-await-try-catch-filter-gap-20261020.txt`.

Synchronous `foreach` gaps are now cloned before later Task
call sources as well. The source collection and loop declaration are preserved,
the body is recursively alias-rewritten, and binding after async lowering
recreates the loop-local symbols. Compiler coverage verifies the two callbacks;
the native array fixture checks one output Task inline and all three pending
positions after forced GC. Focused test 1/1, full suite 918/918, clean solution
build, and all six native cells pass; Linux shared `ldd -r` resolves both
dependencies. Async foreach remains rejected. See
`cxc/tests/native/results/milestone18-g4-multi-await-foreach-gap-20261020.txt`.

The foreach gap was extended to a custom sequence with a struct enumerator.
Compiler checks verify distinct callback-bound loop/enumerator symbols plus
GetEnumerator, MoveNext, Current and Dispose bindings. The native Dispose
counter confirms normal cleanup at each of three pending positions after forced
GC and a helper return, while the all-inline path still allocates only the output
Task. Focused test 1/1, full suite 919/919, clean solution build and all six
native cells pass; Linux shared `ldd -r` resolves both dependencies. See
`cxc/tests/native/results/milestone18-g4-multi-await-enumerator-foreach-gap-20261020.txt`.
Fault and cancellation now also terminate each pending position after forced
GC. Tests check exact fault identity or cancellation state, ensure no later
Task-source call occurs, and confirm the active disposal count remains 0, 1, or
2 for the first, second, or third await. All six native cells, the 919-test
compiler suite, and a zero-warning/zero-error solution build pass. This remains
a bounded synchronous foreach gap; async foreach and broader G4/G7 budgets are
open.

Await-free declarations before later Task call sources cover both explicit and
inferred locals. For `var`, callback rebinding retains the source `AutoType`
while the bound declarator carries the inferred type and symbol. The native
fixture verifies one inline output Task plus pending-first/second/third
completion after forced GC. Focused regression, 920/920 compiler tests,
zero-warning/zero-error build, and six native platform/link cells pass. Broader
G4 escaped-state and G7 frame/copy/allocation budgets remain open.

Multi-declarator gap cloning is covered with two explicit `int` declarations.
Each statement's second initializer uses its first local, and the compiler test
checks that callback rebinding preserves this symbol identity. Native evidence
checks one inline output Task and all three pending positions after forced GC.
The focused regression, 921/921 compiler tests, clean solution build, and six
platform/link cells pass. Broader declaration forms and G4/G7 budgets remain
open.

An explicit uninitialized local followed by assignment is now also covered in a
three-await call-source gap. Callback cloning rebinds the declaration and
assignment independently for each continuation, and compiler assertions verify
the assignment target refers to that cloned declaration's symbol. The native
fixture checks one output Task inline and each pending position after forced GC.
The focused regression passes 1/1, the compiler suite passes 922/922, the
solution build has 0 warnings/errors, and all six Windows x64/x86 and Linux x64
static/shared cells pass. This bounded declaration shape does not close G4's
other call-source forms or G4/G7 frame, copy, and allocation budgets. Exact
commands, results and matrix roots are in
`cxc/tests/native/results/milestone18-g4-multi-await-assigned-local-call-source-values-20261020.txt`.

The later Task call-source matcher now recursively accepts nested conditional
trees. Every leaf must be a Task<T> parameter or a uniquely resolved
same-namespace non-generic Task<T>-returning call, and at least one leaf must
be a call; unsupported invocation forms keep the existing fallback. Compiler
and native probes cover each of three leaves, exact one-output-Task inline
allocation, and pending-first/second/third completion after forced GC. The
focused test passes 1/1, the full suite passes 923/923, the solution build has
0 warnings/errors, and all six Windows x64/x86 and Linux x64 static/shared cells
pass. The exact matrix roots and preserved failed probe roots are in
`cxc/tests/native/results/milestone18-g4-multi-await-nested-conditional-call-source-20261020.txt`.

Nested conditional Task call sources include a bounded qualified static-class
form. A same-namespace class may be written with its exact namespace qualifier,
provided its non-generic class and static, non-generic Task<T> method are unique.
`App.TaskSources.FirstSource` is covered in both compiler and native fixtures;
imported/project calls, instance calls, generic forms, ambiguity, and arbitrary
invocation targets keep the fallback. The focused regression passes 1/1, the
compiler suite passes 923/923, the solution build has 0 warnings/errors, and all
six Windows x64/x86 and Linux x64 static/shared CTest cells pass 1/1; Linux
shared `ldd -r` resolves both libraries. Details and roots:
`tests/native/results/milestone18-g4-multi-await-qualified-nested-conditional-call-source-20261020.txt`.

The exact-namespace-qualified top-level form is also covered: a unique
same-namespace function call such as `App.LaterSource(...)` is recognized as a
Task<T> source with matching result/arity. The native six-cell refresh, compiler
suite, and solution build were rerun after adding that form; see
`tests/native/results/milestone18-g4-multi-await-qualified-nested-conditional-call-source-20261020.txt`.

Imported/project Task<T> sources now have a bounded identity check as well.
The binder resolves declaration types for all contexts before async lowering,
then supplies the lowerer with the current import list and all available
functions, filtering external contexts to public API declarations. The matcher
compares candidate qualified names built from the source spelling, current
namespace, and imports. It accepts exactly one non-generic declaration with
matching arity and Task<T> result type, either a top-level function or a static
method on a non-generic class. Compiler tests cover an imported qualified static
method, an unqualified imported top-level function, and a nested conditional
tree with both an imported producer leaf and a Task<T> parameter leaf. The cross-module native
fixture exercises the public static method after the second await, verifies the
prior result and exactly-once invocation, one output-Task allocation inline,
and pending-first/second/third plus third-await fault/cancellation after forced
GC. The focused compiler regressions pass 1/1 for the imported conditional and
1/1 for the imported top-level function, 2/2 for the direct imported/static
and existing nested-source filter. Overloaded imported declarations are
conservatively rejected by the source matcher while ordinary overload binding
selects the matching arity; imported generic declarations likewise retain the
fallback for both inferred and explicit type arguments while normal generic
binding closes `T` to `int`. The overload, inferred-generic, and explicit-generic
focused tests pass 1/1 each. An imported top-level producer shadowed by a local
function also stays on fallback, and normal binding selects the local
declaration; its focused test passes 1/1. Imported generic-owner methods also
retain fallback while binding through the closed owner; that focused test passes
1/1. Imported instance producers also retain fallback while ordinary binding
preserves the instance receiver and `Task<int>` result; its focused test passes
1/1. A delegate/function-value invocation also stays on fallback and normal
binding records a `FunctionType` call returning `Task<int>`; its focused test
passes 1/1. A delegate-array element call has the same fallback boundary and
normal function-type binding; its focused test passes 1/1. The full suite passes
944/944, the solution build has 0 warnings/errors,
and all six Windows x64/x86 and Linux x64
static/shared CTest cells pass. Linux shared `ldd -r` resolves `libmath.so`
and `libcxcore.so`. Generic, instance, ambiguous and arbitrary invocation
forms retain the fallback where the language binds the call. Delegate-valued
instance fields and properties are not currently invocable as members; the binder reports
`Cannot resolve function 'Producer' with 1 argument(s).` and the negative
compiler tests record this boundary. See
`tests/native/results/milestone18-g4-multi-await-imported-call-source-20261021.txt`.

G4 also accepts a switch expression as a later Task<T> call source when every
arm is a validated Task<T> parameter or uniquely resolved Task<T>-returning
call and at least one arm contains a call. Cloning carries the selector, labels,
filters, arm values and bound switch temporary into the callback, preserving
lazy arm selection. Compiler test
`AsyncTaskImportedSwitchCallSourceKeepsLazyArmAndUsesInlinePath` and native
`AwaitTwoTasksWithSwitchCallSource` cover the parameter and imported-call arms,
exactly-once producer invocation, inline one-output-Task allocation, pending
first/fallback and preferred Task paths, and forced-GC resumption. The native
fixture's explicit object-root table was increased from 32 to 512 entries so
the long-running harness does not silently ignore later roots. Full suite:
945/945; solution build: 0 warnings/errors; CTest: 1/1 across Windows x64,
Windows x86 and Linux x64, static/shared. Linux shared `ldd -r` resolves
`libmath.so` and `libcxcore.so`. Exact commands, roots and initial diagnostic
runs are in `tests/native/results/milestone18-g4-multi-await-imported-call-source-20261021.txt`.

An additional project-reference visibility regression puts a public
`Forward(Task<int>)` beside a private same-name `Forward(Task<string>)` whose
parameter type would otherwise match the source call. The lowerer sees only the
public API function and chooses the inline-ready structure; normal binding then
reports `No overload of 'Forward'` for the inaccessible invocation. This keeps
the matcher from treating private declarations as candidate API and preserves
ordinary access filtering. The focused test passes 1/1; the full suite passes
944/944; and the solution build has 0 warnings/errors.

Imported public static Task-valued properties and fields used as later await
sources currently retain relay fallback. The callback AST keeps the external
getter/field identity and completion-source initialization precedes the
readiness check. The focused property/field pair passes 2/2. This records the
supported binding and fallback contract; the matcher does not inline these
member-read source forms yet.

An imported public extension method returning `Task<int>` is another accepted
binding form that currently stays on relay fallback. The test verifies the
external extension declaration and receiver remain bound inside the callback,
with relay state initialized before readiness. A direct `Task<int>` extension
target is not supported by existing extension-target rules. The focused test
passes 1/1; the full suite passes 944/944; the solution build has 0
warnings/errors.

The G4 call-source matcher now admits null-coalescing expressions when both
operands are validated Task<T> parameters or uniquely resolved Task<T> calls
and at least one side is a call. Callback cloning preserves the lazy
`NullCoalescingExpression`. Compiler structure tests confirm the imported call
stays on the right branch and completion state remains pending-only. Native
tests verify a non-null preferred Task skips the imported call, a null preferred
Task invokes it once, and both choices survive pending-first/pending-source
forced-GC resumes. The six Windows x64/x86 and Linux x64 static/shared CTest
cells pass; Linux shared `ldd -r` resolves both libraries. The focused compiler
test passes 1/1; full suite 944/944; solution build 0 warnings/errors. Details
and matrix roots are in `tests/native/results/milestone18-g4-multi-await-imported-call-source-20261021.txt`.

G4 also supports a later imported Task<T> call source wrapped in an explicit
cast when the cast target can be validated before expression binding. Async
lowering runs before body expression binding, so it accepts only an exact
`System.Threading.Tasks.Task<T>` spelling or an unqualified `Task<T>` in a
context importing or declared in `System.Threading.Tasks`; final type checking
remains with ordinary binding. The cast is cloned into the callback and the
producer remains ordered after earlier awaits. Compiler structure and native
tests cover one inline output Task, exactly-once imported invocation, pending
first/source paths and forced-GC resumption. Full suite 946/946, solution build
0 warnings/errors, and all six Windows x64/x86 and Linux x64 static/shared CTest
cells pass. The native object-root fixture uses a 4096-entry table and aborts on
overflow instead of silently ignoring roots. Exact commands and matrix roots are
in `cxc/tests/native/results/milestone18-g4-multi-await-imported-call-source-20261021.txt`.
This remains a bounded source-shape result; G4/G7 acceptance remains open.

The first awaited source can also be a validated imported `Task<T>` call. For
the bounded one-await, two-await (later Task parameter), and three-or-more
await (later validated call-source) shapes, lowering evaluates the first call
once into a local before readiness. Inline success creates only the returned
Task; a pending branch creates its completion source and callback and reuses
the hoisted source rather than invoking the producer again. Task parameters
used only after suspension are copied into callback-only aliases. Compiler
structure tests and native allocation/forced-GC checks pass in all six
Windows x64/x86 and Linux x64 static/shared cells. Current full suite: 949/949;
solution build: 0 warnings/errors. A failed broad one-await eligibility probe
and its cross-module duplicate-metadata linker diagnostic are preserved in the
report. These shapes do not close G4/G7; see the report for exact roots and
limits.

The one-await imported-call path also passes a `Task<TaskPayload>` test, where
the result is a struct containing a string reference. Inline execution allocates
only the returned Task. Pending execution collects while suspended, completes
the source and drains the scheduler, collects again, then checks the struct's
number and label. The focused compiler regression and all six native
Windows x64/x86 and Linux x64 static/shared cells pass; current full suite is
950/950 and the solution build has 0 warnings/errors. This verifies typed
reference-containing result correctness for this shape but does not measure
large-struct copy/frame costs. See the evidence report for roots and limits.

The same one-await imported-call path passes the cross-module
`LargeTaskPayload` result, with one integer and twelve string references.
Inline execution creates only the result Task. Pending completion forces GC
before resume and after scheduler drain, then checks the number and first/last
string fields. Compiler structure and all six native static/shared cells pass;
the full compiler suite is 951/951 and the solution build has 0
warnings/errors. This is payload correctness evidence, not an optimized copy or
frame-size budget. See the report for roots and the corrected fixture probe.

### G4 imported ValueTask<T> first-call source (2026-10-21)

The bounded single-await local-result path also recognizes a uniquely resolved
imported `ValueTask<T>` call. It stores the call result once in a typed local
before `IsCompleted`, uses `GetResult()` and constructs the output Task on the
inline branch, and converts that same local with `AsTask()` only in the pending
branch. The compiler regression verifies the imported declaration, ordering,
single invocation and pending-only completion state. The initial native import
attempt exposed duplicate closed generic definitions (MSVC LNK2005/LNK1169).
The export scan now includes templates retained by closed instances after the
binder removes synthetic core contexts. This restores dependency discovery and
ownership for Task/completion-source/reusable-source support metadata.

Actual project-reference `ValueTask<int>` and `ValueTask<TaskPayload>` producers
now pass Windows x64/x86 and Linux x64 static/shared execution. Inline success
allocates one output Task, producer calls execute once, and pending typed results
survive forced GC before completion and after scheduler drain. Imported integer
sources also preserve fault identity and cancellation. A compiler regression
checks that producer and consumer do not emit duplicate closed generic methods
or metadata for int, string and a reference-containing struct (3/3).
The full suite passes 955/955 and the build has 0 warnings/errors. The broad
entry-point harness still fails at the previously recorded MSVC C2440 generic
struct casts. An earlier output-free x86 CTest failure remains recorded alongside
successful direct/repeated runs and the fresh six-cell matrix; the fixture now
reports process failure codes and supports retained builds for investigation.
Exact commands, logs and limits:
`cxc/tests/native/results/milestone18-g4-imported-valuetask-ownership-20261021.txt`.
G4 and G7 remain partial; this result does not establish general frame/copy or
escaped-state budgets.

### G4 imported ValueTask first source across two awaits (2026-10-21)

A non-generic three-statement Task<int> body can await a uniquely resolved
ValueTask<int>-returning producer call, await a Task<int> parameter, then return
their sum. The first call is materialized once before readiness. The inline
branch consumes its value and checks the second source without creating relay
state; the first pending branch converts the stored value once with AsTask.
Both pending positions use the established callback-only aliases and preserve
earlier results. The initial slice did not support synchronous prefixes; one
explicitly typed prefix local is now supported as documented below. Generic
methods and wider imported ValueTask chains remain outside this matcher.

The native fixture checks one returned Task allocation for inline success with
both inline-result and completed reusable-source-backed ValueTasks. Pending
first, pending second and both-pending cases survive forced GC and evaluate the
producer once. Reusable-source pending conversion completes and permits Reset
after scheduler acknowledgment. Fault identity and cancellation status are
checked at both await positions, with sources completed before invocation and
while suspended. An inline canceled-source probe exposed an existing shared
wrapper gap: it returned a faulted Task despite rethrowing OperationCanceledException.
The wrapper now handles cancellation before ordinary faults for Task and Task<T>;
the void completed-source cancellation case is covered natively too.

Final compiler suite: 958/958; build: 0 warnings/errors. Final CTest: 1/1 in
all six Windows x64/x86 and Linux x64 static/shared cells; Linux shared ldd -r
resolves both dependencies. The broad harness remains blocked by the recorded
generic struct-cast C2440. Exact red/green results, retained roots, commands and
limits: `cxc/tests/native/results/milestone18-g4-twoawait-inline-cancellation-20261021.txt`.
This does not close G4/G7 or establish general frame/copy budgets.

### G4 imported ValueTask two-await explicit prefix (2026-10-21)

The same non-generic `Task<int>` form accepts exactly one explicitly typed
local declaration with an initializer before its first await. The inline branch
keeps using the original stack local. Pending branches copy it to a typed alias
that survives callback execution. Compiler tests cover an `int` and a
reference-containing `TaskPayload`; native cases check one inline output Task
and pending-first, pending-second and both-pending completions with forced GC.
The `TaskPayload` case reads both its integer and string fields after suspension.

The matcher also accepts one initialized inferred local. Compiler tests verify
the later binder infers the original and callback alias types for both integer
and reference-containing prefixes. Native checks cover inferred int and
TaskPayload inline execution and pending-first completion after forced GC in
all six platform/link cells; explicit prefixes retain their pending-first,
pending-second and both-pending coverage. The matcher excludes multiple prefix
statements, generic methods and wider imported ValueTask chains. This bounded
extension does not establish G4/G7 frame, copy or allocation budgets. Exact
commands, retained roots and the allocation-probe correction are in
`tests/native/results/milestone18-g4-twoawait-prefix-20261021.txt`.

### G7 async iterator callback environment sharing (2026-10-21)

For an async iterator await that registers the same resume body with both cancellation and completion, lowering creates one immutable state-local delegate and passes it to both registrations. The environment retains the generated iterator pointer, preserving the GC root for the suspended state machine. Fault-cleanup awaits have only one registration and retain their standalone callback. On the captured branch/pending-await fixture, this removes one environment object per pending await: lifecycle totals move from 11 to 10 objects, saving 24 requested bytes on x64 and 12 on x86. Frames remain 120 bytes x64/Linux and 68 bytes x86. The native GC, branch-value and exactly-once cleanup assertions pass on Windows x64/x86 and Linux x64 static. This is a bounded single-shape result and does not set a general frame/allocation budget. Exact commands, roots and source hashes: `tests/native/results/milestone18-g7-async-callback-environment-sharing-20261021.txt`.

**G7 socket readiness-item audit (2026-10-21; bounded slice complete):** the earlier fresh Windows socket-test crashes came from a stale six-argument `BeginTransfer` fixture ABI; the native declaration and two calls now match the seven-argument CX/C entry point. Scheduler item completion, timer and socket payloads share an exclusive union, with inactive pointer-bearing state cleared at timer, readiness, shutdown and dispatch transitions. Test-only layout assertions confirm 112 bytes on x64 (from 136) and 64 bytes on x86 (from 80). Continuation hook, scheduler lifecycle and socket integration tests pass 3/3 in Windows x64/x86 static/shared, and full Linux runtime/socket CTest passes 36/36. ASAN conflicts with the conservative stack scanner's stack-redzone reads and is not counted as a pass. Broader G7 frame/code-size/copy/allocation budgets remain open. Full commands/results/hashes: `cxc/tests/native/results/milestone18-g7-readiness-item-audit-20261021.txt`.

### G4 nested generic async ValueTask<T> relay (2026-10-21; bounded)

A closed async generic caller now closes generic method targets using its
substituted type arguments, while skipping calls with remaining open types.
The bounded one-local `Inner<T>`/`Outer<T>` chain returns an inline 104-byte
x64 / 52-byte x86 reference-containing struct with zero CX allocations; pending
large results survive forced GC. Final compiler build passes with 0 warnings/
errors, the suite passes 966/966 and CTest passes all six platform/link cells.
Linux shared `ldd -r` resolves both dependencies. Generic forwarding and concept
binding remain on the established non-async paths. Wider chains and general
G4/G7 frame/copy/allocation budgets remain open. See
`tests/native/results/milestone18-g4-generic-valuetask-nested-20261021.txt`.

**G4 two-local generic ValueTask<T> prefix (2026-10-21; bounded):** generic
body validation and direct ValueTask<T> lowering now preserve two initialized
local declarations before one awaited source. Prefix initializers cannot await;
the final local is the same-shaped ValueTask<T> source. `int prefix = 17` then
`ValueTask<T> source = input` remains before the readiness check. Native inline
LargeTaskPayload completion allocates zero CX objects and pending large results
survive forced GC in all six Windows x64/x86 and Linux x64 static/shared cells.
Build passes with 0 warnings/errors, the compiler suite passes 967/967, and
Linux shared `ldd -r` resolves both modules. This does not establish arbitrary
prefix statements or general G4/G7 frame, copy and allocation budgets. One
unrelated x86 process-result 635 was followed by a passing retry and a fresh
passing matrix. Full evidence: `tests/native/results/milestone18-g4-generic-valuetask-two-prefix-20261021.txt`.

### G4 generic two-await ValueTask<T> relay (2026-10-21; bounded)

A generic `async Task<T>` with two sequential `ValueTask<T>` local awaits now
checks all sources for inline completion before creating relay state. Native
large-payload assertions show one CX allocation on the all-inline Task-return
path and preserve the first result when the second await suspends; pending-first,
pending-second and both-pending cases survive forced collection and scheduler
resumption. C emission skips the unused open generic relay lambda templates and
emits the closed typed helpers per specialization. Build: 0 warnings/errors;
compiler suite: 968/968; CTest: 1/1 in all six Windows x64/x86 and Linux x64
static/shared cells, with Linux shared dependencies resolved by `ldd -r`.
Existing generic-pointer/linkage warnings remain. Arbitrary generic Task chains
and broad G4/G7 frame/copy/allocation budgets remain open. Details and retained
roots: `tests/native/results/milestone18-g4-generic-valuetask-two-await-20261021.txt`.

### G4 direct Task<T> two-prefix local await (2026-10-22)

A bounded non-generic `Task<int>` method can now execute two initialized local
declarations before one direct parameter await, then return an expression that
uses the awaited result. The all-completed path checks `IsCompleted` before
relay construction and allocates only its result Task. Pending execution
materializes callback-only aliases for the int and string prefixes after the
pending split; forced GC before source completion preserves the result. Focused
compiler tests pass 3/3, the solution build has 0 warnings/errors, the full
suite passes 972/972, and CTest passes in all six Windows x64/x86 and Linux x64
static/shared cells. Linux shared `ldd -r` resolves both libraries. Other
prefix lengths/statements, continuation forms, and G4/G7 budgets remain open.
The corrected measurement and first setup-contaminated probe are recorded in
`tests/native/results/milestone18-g4-nongeneric-task-two-prefix-20261022.txt`.

The matcher also accepts the same form with one initialized local. Its inline
path allocates one result Task; pending completion preserves the local across
forced GC through a callback-only alias. The full compiler suite passes 973/973,
the solution build has 0 warnings/errors, and all six Windows x64/x86 and Linux
x64 static/shared CTest cells pass. Linux shared `ldd -r` resolves both
libraries. Exact roots:
`tests/native/results/milestone18-g4-nongeneric-task-one-prefix-20261022.txt`.

The direct-Task matcher now accepts a non-empty local-only prefix of any length,
provided each statement is one initialized local declaration and no initializer
contains an await. A three-local int/string/int case passes inline-allocation
and pending forced-GC checks. Build has 0 warnings/errors, suite passes 974/974,
and all six native cells pass; Linux shared `ldd -r` resolves both libraries.
Other prefix statement kinds and broad G4/G7 budgets remain open. Results:
`tests/native/results/milestone18-g4-nongeneric-task-multi-local-prefix-20261022.txt`.

The bounded direct-Task<T> prefix matcher accepts a declaration containing
multiple initialized declarators and aliases each live local only on the pending
path. The inline result allocates one Task; pending completion survives forced
GC. Build passes with 0 warnings/errors and suite passes 976/976. All six native
cells pass after retrying an unrelated transient x86 process-result 635; Linux
shared `ldd -r` resolves both project libraries. Evidence:
`tests/native/results/milestone18-g4-nongeneric-task-multi-declarator-prefix-20261022.txt`.

An explicitly typed local assigned before a direct Task<T> await now follows the
bounded readiness-first path. Inline completion allocates one returned Task;
pending completion preserves the value through forced GC. Build passes with 0
warnings/errors and the suite passes 977/977. All six Windows x64/x86 and Linux
x64 static/shared cells pass; Linux shared `ldd -r` resolves both project
libraries. Evidence:
`tests/native/results/milestone18-g4-nongeneric-task-assigned-local-prefix-20261022.txt`.

Await-free `if` and block prefixes are supported for the bounded direct
Task<T> readiness-first path. A conditional side-effect probe passes exactly
once inline and pending; inline completion allocates one result Task and
pending completion survives forced GC. Build passes with 0 warnings/errors,
suite passes 978/978, and all six Windows x64/x86 and Linux x64 static/shared
cells pass after a retained-root x86 retry from unrelated process result 636.
Linux shared `ldd -r` resolves both project libraries. Evidence:
`tests/native/results/milestone18-g4-nongeneric-task-conditional-prefix-20261022.txt`.

The imported ValueTask<T> two-await path now accepts three initialized prefix
locals. Inline completion allocates one returned Task; the imported producer runs
once; pending-first and pending-second results survive forced GC. Build passes
with 0 warnings/errors, suite passes 979/979, and all six Windows x64/x86 and
Linux x64 static/shared cells pass. Linux shared `ldd -r` resolves both project
libraries. Evidence:
`tests/native/results/milestone18-g4-imported-valuetask-three-prefix-20261022.txt`.

### Generic Task<T> synchronous prefix in a two-await chain (G4 bounded; 2026-10-22)

A generic Task<T> method with one initialized int prefix local now has structural and native evidence across two direct Task<T> awaits. Inline completion checks both sources before relay state and allocates one returned Task; pending-first and pending-second completion preserve the prefix through forced GC and scheduler drain. Focused tests pass 2/2, build has 0 warnings/errors, suite passes 980/980, and all six Windows x64/x86 and Linux x64 static/shared cells pass. Linux shared `ldd -r` resolves both libraries. This does not close arbitrary generic prefix forms or G4/G7 state/frame/copy budgets. Evidence: `tests/native/results/milestone18-g4-generic-task-chain-prefix-20261022.txt`.

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
### G7 generic-owner iterator probe (2026-10-22; bounded)

`GenericIteratorProvider<T>.GenericValues` is measured after closing `T` to
`int`. The generated closed frame stores `cx_int`, and its `IEnumerator.Current`
thunk returns `cx_int`. One and sixteen values produce the expected sums and
both allocate 3 managed objects: 128 bytes with a 56-byte frame on Windows
x64/Linux x64, and 88 bytes with a 40-byte frame on Windows x86. All three
reduced static benchmark runs pass. The full harness also required refreshing
stale current-source expected byte counts; those changes are benchmark baselines,
not runtime optimization claims. This one specialization does not set general
G7 budgets. Reports, exact commands, roots and retained failed probes:
`tests/native/results/milestone18-g7-generic-owner-iterator-20261022.txt`.

### G7 generic-owner struct Current thunk (2026-10-22; bounded)

A closed generic iterator over `IteratorPayload` (an `int` plus `string`
reference) previously emitted a `Current` thunk that called the open generic
getter and cast its erased `void*` return through `size_t` to a C struct. MSVC
reported C2440. Closed generic iterator `Current` thunks now return the typed
`__cx_current` field and call the original getter only when `__cx_hasCurrent` is
false, retaining the existing exception contract. The new compiler regression
passes; build is 0 warnings/errors and suite is 983/983. Windows x64 and Linux
x64 report 3 allocations / 176 bytes and an 80-byte frame at both 1 and 16
yields; Windows x86 reports 3 / 104 bytes and a 48-byte frame. Value and string
reference checks pass on all three. Evidence and retained failure:
`tests/native/results/milestone18-g7-generic-owner-payload-20261022.txt`.

### G7 generic-owner string Current thunk (2026-10-22; bounded)

Closing `GenericIteratorProvider<T>.GenericValues` over `string` uses the
pointer-sized open iterator frame and emits a concrete `String*`
`IEnumerator.Current` thunk. Exact string content is checked for one and
sixteen yields, with equal allocations on all three reduced static targets:
3 objects / 144 bytes and a 64-byte frame on Windows x64/Linux x64; 3 / 88
bytes and a 40-byte frame on Windows x86. This is one reference-type
specialization and does not establish general frame or allocation budgets.
Reports, roots and commands:
`tests/native/results/milestone18-g7-generic-owner-string-20261022.txt`.

### G8 four-layer mixed-reference await (2026-10-22; bounded)

Completed and pending `IteratorPayload` results (int plus string reference)
now pass through `AwaitPayloadOne -> AwaitPayloadNested -> AwaitPayloadDeep ->
AwaitPayloadVeryDeep`. Pending values survive forced collection while suspended
and before consumption; both fields validate. Exact costs are 5 allocations /
280 bytes completed and 27 / 1,368 pending on x64; x86 is 5 / 140 and 27 / 780.
Reduced static runners pass Windows x64/x86 and Linux x64. Int and
mixed-reference reusable-source heaps remain flat through 192 drained/reset
cycles after 64 warmups: 792/1,040 bytes x64, 600/804 x86, and 840/1,200 Linux.
This one bounded depth does not establish general G8 budgets; WSL timing shares
the Windows host. Commands, roots and platform reports:
`tests/native/results/milestone18-g8-four-layer-reference-20261022.txt`.

### G4 generic-owner large local across two awaits (2026-10-22; bounded)

`GenericTaskAwaitOwner<T>.AwaitTwoWithLargePrefix` copies a 104-byte x64 /
52-byte x86 `LargeTaskPayload` (twelve string references) into a method local,
then reads and records it after two awaits. All-completed execution allocates
one returned Task. Pending-first and pending-second cases clear the caller's
reference fields and force GC while suspended and after scheduler drain; all
twelve local fields remain readable. The full cross-module CTest passes in all
six Windows x64/x86 and Linux x64 static/shared cells. Linux shared `ldd -r`
resolves both libraries. This verifies lifetime for the tested owner shapes, not
caller-frame size or physical copy counts. Exact roots, commands and the
allocation-probe correction:
`tests/native/results/milestone18-g4-generic-owner-large-local-prefix-20261022.txt`.

The same generic-owner large-local path also passes a pending-first fault and
a pending-second cancellation after forced collection. It preserves exact
fault identity, canceled status, and suppresses the post-await prefix record.
This checks exceptional unwinding at two suspension positions; it does not
claim payload access after failure or add a frame/copy budget. Full roots and
commands: `tests/native/results/milestone18-g4-generic-owner-large-prefix-unwind-20261022.txt`.

The awaited `for` loop path also carries a 104-byte x64 / 52-byte x86
`LargeTaskPayload` result through its callback. Inline completion allocates one
returned Task; a pending first await survives forced collection before
completion and after scheduler drain, with all twelve string fields checked.
This complements the existing integer loop allocation/GC probe and does not
establish a general loop frame/copy budget. Evidence:
`tests/native/results/milestone18-g4-loop-large-reference-20261022.txt`.

### G4 async Task<T> await-foreach consumer (2026-10-22; bounded)

For the simple synchronous-body consumer shape, completed `MoveNextAsync` and
`DisposeAsync` operations now run inline and return a directly constructed
`Task<T>`. The preallocated reusable sequence allocates exactly one returned
Task: 48 bytes on x64 and 24 bytes on x86. The first-pending-move case forces
collection while suspended, completes and drains the scheduler, checks the
result, and verifies disposal exactly once. Windows x64/x86 and Linux x64
static/shared benchmark cells pass. This slice does not cover bodies with
control transfers or establish general frame/copy budgets. Detailed commands,
roots and reports: `tests/native/results/milestone18-g4-await-foreach-20261022.txt`.

The compiler also resolves the unbound `MoveNextAsync` return type from the
available `IAsyncEnumerator<T>` declaration, because async lowering precedes
statement binding. A focused regression covers a `Task<bool>` enumerator; the
six-cell allocation/forced-GC matrix above remains the `ValueTask<bool>` case.

The simple synchronous-body consumer also passes the same six-cell native
allocation/forced-GC matrix with a `Task<bool>`/`TaskHandle` enumerator contract.
The reusable inline source returns preallocated completed Task objects, so the
consumer itself allocates exactly one returned Task: 48 bytes x64 and 24 bytes
x86. The first pending `MoveNextAsync` survives forced GC and completes with
result 43 and exactly-once disposal. Windows x64/x86 and Linux x64 static/shared
pass; Linux shared relocation validation passes. This verifies the simple-body
consumer for both return contracts, not general body control flow or broad
frame/copy costs. Exact commands, per-cell reports and roots:
`tests/native/results/milestone18-g4-await-foreach-task-20261023.txt`.

The bounded inline path now also supports await-free `break`, `continue`,
`return;`, and `throw` statements in the body. These transfers stay inside the
completed-move loop; a pending move creates escaped TaskCompletionSource and
callback state only after the inline loop exits. The `Task<bool>`/`TaskHandle`
native probe returns 2 after four moves and exactly one disposal, with one
returned Task allocated inline (48 bytes x64, 24 bytes x86). Its pending-first
case forces GC before completion and verifies the same result and disposal
count. The focused compiler regression, full 1009-test compiler suite, all six
native cells, and the entry-point E2E pass. This remains a bounded control-flow
slice and does not establish aggregate G4 frame/copy budgets. Evidence:
`tests/native/results/milestone18-g4-await-foreach-control-20261026.txt`.

An isolated MSVC code-generation comparison changed the two cancellation
dispatch conditions in a copied `AwaitLargePayloadValueTaskLocal` generated-C
procedure to `if (0)`. `/O2` reduced its x64 stack reservation from 896 to 800
bytes (96 bytes); Win32 remained at 208 bytes (0 bytes). The probe did not alter
the preserved fixture and was not a runtime-semantic test. It attributes the
current x64 frame increase to cancellation handling for this one procedure and
configuration. Exact physical large-payload copy traffic and a numeric G4
budget remain unverified. Full method, commands, listings and limitations:
`tests/native/results/milestone18-g4-cancellation-frame-probe-20261026.txt`.

The project-reference E2E also uses a Task-based async-enumerator contract.
Iterator lowering follows the declared Task<bool>/TaskHandle signatures, and
awaited continuations register through each awaitable's instance `OnCompleted`.
`TaskScheduler.RunUntil` recognizes generic Task<T> completion through runtime
field metadata and defers fault reporting for the selected task until its
result is observed. The resumed loop-local continue through an awaited finally
now advances directly within the generated step loop when the next cleanup
await is already complete. The generated-C regression passes; the full
AwaitExpressionTests suite is 172/172 and the full compiler suite is 988/988.
Windows x64 static runtime/socket CTest passes 35/35, and a fresh entry-point
E2E passes the Task-based iterator, fault cleanup, and awaited-loop checks. This
bounded semantic case is not part of the ValueTask six-cell allocation matrix;
the broad G4 state, frame and copy acceptance remains open. See the latest
Milestone 18 checkpoint and `tests/native/results/milestone18-g4-awaited-loop-continue-20261023.txt`.

### Reusable source completion generations

`ReusableValueTaskSource<T>` supports versioned `SetResult`, `SetException`, and
`SetCanceled` calls. A producer that saves the token returned by `GetVersion()`
when it arms asynchronous work can pass the same token on completion. The source
rejects the callback if a reset has advanced or retired that generation. The
unversioned convenience methods target the current generation for synchronous
producers. This guards late completion after reuse when the producer carries the
token; source reuse still requires native readiness registrations and queued
callbacks to be detached or acknowledged.

### Version reusable socket completions by operation

The reusable socket read path captures its source generation before arming I/O
and stores it in the native operation record. Success, cancellation, and fault
callbacks pass that token to the versioned source completion methods. The
operation record overlays the generation with callback fields unused by its
single-completion path, so ordinary Task-based socket operations keep their
existing size. `cx_socket_finish_operation` removes readiness and cancellation
registrations before completing the source. The streaming-read matrix passes
all six Windows/Linux ABI cells, including late and already-queued readiness
cancellation after forced GC; its Task WriteAsync allocation budgets remain
stable. This provides operation-level late-callback protection, while G5's
broader source lifecycle requirements remain open.

The expanded direct-source fixture passes Windows x64 static/shared, Windows
x86 shared, and Linux x64 static/shared. Since the full fixture stops at an
unrelated earlier pending-state assertion on Windows x86 static, an isolated
app-to-cxcore fixture now runs the stale status/result/`AsTask()`/registration
and stale-completion checks in that cell; CTest passes 1/1. Together with the
six-cell socket lifecycle matrix, callback-acknowledgment and retention probes,
this closes G5. The larger fixture's unrelated result-635 case remains recorded
as a separate issue. See
`tests/native/results/milestone18-g5-x86-static-isolated-20261023.txt`.

**G6 socket-source reset ordering (2026-10-23; bounded):** the streaming-read
fixture can inject its reusable source into a socket read. Reset is rejected
while that read owns active readiness. A queued cancellation/readiness race is
forced through GC with cancellation dispatched first; after native detachment
and callback drain, the canceled inner result must be consumed before Reset can
succeed. The next iterator generation then receives the byte left unread by
the canceled operation, with exactly-once cancellation and cleanup. The new
`ReadBytesAsyncWithSource` entry point has a distinct name to preserve the
existing `ReadBytesAsync` reflection/header ABI. CTest passes Windows x64/x86
and Linux x64 static/shared; Linux shared relocation checks resolve all three
libraries. This closes the explicit live-readiness reset leaf, while recurring
readiness-registration setup allocations and broader G6 reflection/ABI
acceptance remain open. Commands, roots and unchanged allocation budgets:
`tests/native/results/milestone18-g6-socket-source-reset-order-20261023.txt`.

**G6 source-owned readiness-node reuse (2026-10-23; bounded):** a
`ReusableValueTaskSource<T>` now retains one GC-traced socket readiness item
across sequential source generations. The scheduler returns it to idle only
after dispatch clears its callback roots; cancellation can detach it by identity
from the I/O list or ready queue. A spurious-ready retry uses an ordinary node
until the active callback returns, avoiding reuse while dispatching. The six
Windows x64/x86 and Linux x64 static/shared streaming cells pass. Warmed pending
read setup drops from 7 to 6 CX objects (the 112-byte x64 / 64-byte x86 wait
item); completion remains allocation-free and completed moves/disposal remain
allocation-free. Existing WriteAsync budgets and Linux shared relocation checks
pass. This bounds sequential reuse to the source owner and does not establish
concurrent reads on one source. Details and retained roots:
`tests/native/results/milestone18-g6-socket-readiness-node-reuse-20261023.txt`.

**G6 integrated async-iteration/socket slice (2026-10-23; complete):** the
ValueTask<bool> and Task<bool>/TaskHandle iterator contracts have matching
generated signatures, consumers, reflection and allocation/forced-GC coverage.
The source-injected socket API is intentionally public for external owners and
is checked in the generated header and reflection. Native cache getters are
internal and absent from the generated core header. Across Windows x64/x86 and
Linux x64 static/shared, the warmed pending read uses 6 setup objects and 0
completion/queue posts; 64 completed moves plus disposal allocate 0. The
ordinary Task WriteAsync budget stays at 7 objects / 384 bytes x64/Linux and 7
/ 212 x86. Windows x64 runtime CTest passes 35/35 and the compiler suite passes
988/988. Together with the Task-based iterator E2E and G4 await-foreach matrices,
this satisfies the G6 selected ABI slice. G4's broad state/frame/copy budget
and other milestone gates remain open.

**G4 conditional Task await inside while (2026-10-23; bounded):** the two-iteration all-completed `AwaitWhileCount` path allocates exactly one returned `Task<int>`. A zero-iteration call with both sources pending also completes with zero and allocates one Task without reading either source. Pending-first and pending-second cases each survive forced collection and scheduler drain; the outer Task stays pending while the second source is incomplete. The focused compiler test passes 1/1 and the isolated native fixture passes 1/1 in all six Windows x64/x86 and Linux x64 static/shared cells; Linux shared `ldd -r` resolves `libcxcore.so`. During integration, the full ABI fixture exposed a stale core header missing the new private `_socketWaitItem` layout field. Adding its `cx_ptr` layout declaration restored the Windows x64 static cross-module fixture, which passes CTest 1/1. This evidence closes one conditional awaited-while allocation/retention slice and does not close G4's broad state/frame/copy gate. Details and roots: `tests/native/results/conditional-while-allocation-20261023.txt`.
**G4 direct Task await inside `for` (2026-10-23; bounded):** the focused native fixture now covers `AwaitTaskInsideLoop` with a completed Task source across two iterations and asserts exactly one returned Task allocation. A pending-first source survives forced collection; after it completes, the loop consumes the same source inline on its second iteration and returns the expected sum. CTest passes 1/1 in all six Windows x64/x86 and Linux x64 static/shared cells, with Linux shared `ldd -r` resolving `libcxcore.so`. See `tests/native/results/conditional-while-allocation-20261023.txt`. The G4 general state/frame/copy gate remains open.
**G4 standalone-await void loop allocation (2026-10-23; bounded):** a no-result `Task` while, body-first do/while and for each allocate exactly two CX objects for the returned Task/TaskState pair when both loop sources are complete. Pending first and second awaits survive separate forced collections and scheduler drains; the output stays pending until the last selected source completes. Existing compiler loop regressions pass 4/4 and the combined native fixture passes 1/1 in all six Windows x64/x86 and Linux x64 static/shared cells. Linux shared `ldd -r` resolves `libcxcore.so`. This covers the three standalone-await void loop forms, not general state/frame/copy budgets. Details and roots: `tests/native/results/conditional-while-allocation-20261023.txt`.

**G4 conditional Task await inside `for` (2026-10-23; bounded):** `AwaitConditionalTaskFor` selects a `Task<int>` by iteration, stores its awaited value in a local, then continues the loop. Zero iterations with pending sources and two all-completed iterations each allocate only the returned Task. Pending first and second selections survive forced collection; the outer Task stays pending between selections. The compiler structural regression passes 1/1, and the native CTest passes in all six Windows x64/x86 and Linux x64 static/shared cells; Linux shared `ldd -r` resolves `libcxcore.so`. The assignment-expression form `result = await selected;` is rejected by the current bounded loop-await matcher; the await-local form is covered. This does not close G4's broad escaped-state/frame/copy gate. Full roots and the CMake version correction are in `tests/native/results/conditional-while-allocation-20261023.txt`.

**G4 awaited `for` control-transfer allocation (2026-10-23; bounded):** the completed `break` case allocates exactly one returned Task; a pending source selecting `return` survives forced GC and completes with 42 after scheduler dispatch. Focused compiler loop-control checks pass 2/2 and the native CTest passes in all six Windows x64/x86 and Linux x64 static/shared cells; Linux shared relocation validation resolves `libcxcore.so`. Existing entry-point E2E covers break, continue and return behavior; this adds the allocation and GC bound for inline break and resumed return. General G4 state/frame/copy acceptance remains open. Roots: `tests/native/results/conditional-while-allocation-20261023.txt`.

**G4 awaited `while`/`do-while` control-transfer allocation (2026-10-23; bounded):** each completed `break` path allocates one returned Task; each pending source selecting `return` survives forced collection and completes with 42 after scheduler dispatch. The do/while case executes its body first. Combined with the `for` control case, each result-returning loop form now has an inline-break allocation and pending-return retention probe. The combined CTest passes all six Windows x64/x86 and Linux x64 static/shared cells, and Linux shared `ldd -r` resolves `libcxcore.so`. Broad G4 state/frame/copy acceptance remains open. Roots: `tests/native/results/conditional-while-allocation-20261023.txt`.
**G4 void Task reference prefix across two awaits (2026-10-23; bounded):** `AwaitVoidTwoWithReferencePrefix` reads a string local after two direct Task awaits. The inline path allocates exactly the returned Task and TaskState; pending-first and pending-second cases survive forced collection after the caller clears its reference and preserve the expected prefix length. The lowerer clones the inline and callback statement trees and creates typed live-local aliases only in the pending branch, fixing both eager cell allocation and cross-branch AST capture. The focused compiler regression passes 1/1, the full suite passes 989/989, and the focused CTest passes 1/1 across Windows x64/x86 and Linux x64 static/shared. Linux shared `ldd -r` resolves `libcxcore.so`. Full roots and warnings are recorded in `tests/native/results/conditional-while-allocation-20261023.txt`. Broad G4 escaped-state/frame/copy acceptance remains open.
**G4 void Task inferred reference prefix across two awaits (2026-10-23; bounded):** `AwaitVoidTwoWithInferredReferencePrefix` uses `var prefix = marker` and reads the string after two Task awaits. Since async lowering precedes ordinary local binding, it resolves an inferred local type from a directly referenced parameter or earlier prefix local, then declares the typed callback alias only in the pending branch. The generated-C regression checks explicit and inferred prefixes both remain allocation-free before readiness. Inline calls each allocate two CX objects for Task/TaskState; pending-first and pending-second paths survive forced GC and preserve the reference after caller clearing. Focused compiler regression passes 1/1, full suite 989/989, and CTest passes all six Windows x64/x86 and Linux x64 static/shared cells; Linux shared `ldd -r` resolves `libcxcore.so`. An initial process result 67 was caused by constructing the probe string inside the allocation window; after moving setup before measurement, the final matrix passes. Exact roots and diagnostics are in `tests/native/results/conditional-while-allocation-20261023.txt`. Broad G4 state/frame/copy acceptance remains open.
**G4 direct Task<T> return-await allocation and retention (2026-10-23; bounded):** `AwaitTaskDirectReturn` returns a direct `await source`. Completed result 41 allocates one returned Task; pending result 43 survives forced GC and completes after scheduler dispatch. The existing compiler structural regression passes 2/2, and the focused native CTest passes all six Windows x64/x86 and Linux x64 static/shared cells; Linux shared `ldd -r` resolves `libcxcore.so`. Full retained roots and warnings are in `cxc/tests/native/results/conditional-while-allocation-20261023.txt`. Broad G4 escaped-state/frame/copy acceptance remains open.
**G4 explicit Task-local continuation with reference prefix (2026-10-23; bounded):** `AwaitTaskLocalWithReferencePrefix` initializes a string prefix, materializes `Task<int> selected = source`, awaits the local, then reads the prefix. Inline result 44 allocates one returned Task; pending result 47 survives forced collection after caller clearing. Existing structural tests verify inline readiness ordering and pending-only callback alias placement; focused compiler checks pass 4/4. Native CTest passes all six Windows x64/x86 and Linux x64 static/shared cells, with Linux shared `ldd -r` resolving `libcxcore.so`. Exact roots and the initial unavailable generator selection are recorded in `cxc/tests/native/results/conditional-while-allocation-20261023.txt`. Broad G4 escaped-state/frame/copy acceptance remains open.
**G4 inferred Task-local continuation with reference prefix (2026-10-23; bounded):** `AwaitTaskLocalWithInferredReferencePrefix` uses `var selected = source`, awaits the materialized local, then reads a live string prefix. The first probe exposed eager completion/capture allocation. The lowerer now resolves inferred Task locals from a parameter or earlier prefix local in both the continuation classifier and all-inline readiness builder. Inline result 44 allocates one returned Task; pending result 47 survives forced collection after caller clearing. The new structural test passes 1/1, focused Task-local compiler tests pass 3/3, full suite passes 990/990, and native CTest passes all six Windows x64/x86 and Linux x64 static/shared cells; Linux shared `ldd -r` resolves `libcxcore.so`. Exact roots and the original failure are recorded in `cxc/tests/native/results/conditional-while-allocation-20261023.txt`. Broad G4 escaped-state/frame/copy acceptance remains open.
**G4 inferred Task-local continuation from a Task<T>-returning call (2026-10-23; bounded):** `AwaitTaskLocalCallWithInferredReferencePrefix` materializes `var selected = TaskLocalSourceProbe.Pass(source)`, awaits that local and reads a live string prefix. Type resolution for uniquely proven Task-returning calls now participates in both continuation matching and all-inline readiness. Inline result 44 allocates one returned Task and invokes the source once; pending result 47 survives forced collection after caller clearing, with one source invocation. Structural test passes 1/1, full compiler suite passes 991/991, and native CTest passes all six Windows x64/x86 and Linux x64 static/shared cells; Linux shared `ldd -r` resolves `libcxcore.so`. Exact roots and initial probe correction are in `cxc/tests/native/results/conditional-while-allocation-20261023.txt`. Broad G4 escaped-state/frame/copy acceptance remains open.
**G4 inferred Task-local call source (2026-10-23; bounded):** `AwaitTaskLocalCallWithInferredReferencePrefix` materializes a uniquely resolved Task<T>-returning call into `var selected`, awaits that local and reads a live string prefix. Inline result 44 allocates one output Task and invokes the source once; pending result 47 survives forced collection after caller-side clearing, with one call after dispatch. The structural regression passes 1/1 and the compiler suite passes 991/991; the focused native fixture passes all six Windows x64/x86 and Linux x64 static/shared cells. The broader cross-module fixture passes five cells but Windows x86 static still fails at result 635 in its pending-preferred null-coalescing scenario; this is documented as unresolved. Full roots and the diagnostic split are in `cxc/tests/native/results/conditional-while-allocation-20261023.txt`. G4 remains open.
**G4 x86 static cross-module sequence diagnostic (2026-10-23):** the full cross-module fixture reaches result 635 in the pending-preferred null-coalescing case only after earlier test operations. Running that method as the first and only operation on Windows x86 static passes CTest 1/1, confirming the isolated lowering path returns a pending task as expected. The temporary early-return probe was removed; the permanent diagnostic split remains. The contaminating prior operation is unknown and the broad fixture is not yet clean in this cell. Evidence/root: `cxc/tests/native/results/conditional-while-allocation-20261023.txt`.

**G4 x86 static fixture rooting correction (2026-10-23):** the earlier result 635 came from rooting the pending preferred Task after, instead of before, the async call's allocations. The fixture now stores and roots `pendingCoalescingPreferred.Task` before invocation. The full cross-module fixture then passes CTest 1/1 in all six Windows x64/x86 and Linux x64 static/shared cells. Windows x86 static root: `C:\Users\k.klein\AppData\Local\Temp\cx-cross-module-abi-904a5603a4184510ad81544af8b7ba6a`; x86 shared: `C:\Users\k.klein\AppData\Local\Temp\cx-cross-module-abi-1a512749b87a423f8cfb8aea5699ede5`; x64 static/shared: `C:\Users\k.klein\AppData\Local\Temp\cx-cross-module-abi-b0b4817422ec496bbdcfcd7dabb4650f`; Linux static/shared: `C:\Users\k.klein\AppData\Local\Temp\cx-cross-module-abi-042fe1a0365a48a7beab108381e1364e` (shared relocation check passed). G4's broad escaped-state/frame/copy gate remains open.

**G4 later direct Task<T> local after first await (2026-10-24; bounded):** a two-await chain may initialize a direct Task<T> local from a parameter after the first await. The lowerer now includes this source shape in its inline readiness branch and delays the captured source alias until a pending branch. `AwaitTwoTasksWithLaterDirectLocal` allocates one output Task inline (result 523); pending-first and pending-second cases survive forced GC and return 527/531. Its structural compiler regression passes 1/1, the full compiler suite passes 992/992, and the broad cross-module CTest passes in all six Windows x64/x86 and Linux x64 static/shared cells. Broad G4 escaped-state/frame/copy acceptance remains open. Exact roots and the diagnostic correction are in `cxc/tests/native/results/conditional-while-allocation-20261023.txt`.

**G4 Task-returning first await with later direct Task<T> local (2026-10-24; bounded evidence):** `AwaitCallThenLaterDirectLocal` awaits a Task<T> returned by `FirstTaskSource`, initializes a direct Task parameter local after that result, then awaits the local. The source counter proves exactly-once invocation. Inline returns 535 with one output Task; pending-first and pending-second survive forced GC and return 539/543 while keeping the call count at one. The structural regression passes 1/1, the full compiler suite passes 993/993, and the native fixture passes all six Windows x64/x86 and Linux x64 static/shared cells; Linux shared `ldd -r` resolves its dependencies. The lowerer already supported this combination; this adds evidence only. Broad G4 frame/copy acceptance remains open. Exact roots and runner history: `cxc/tests/native/results/conditional-while-allocation-20261023.txt`.

**G4 three-await chain with later direct Task<T> locals (2026-10-24; bounded):** a multi-await chain can materialize a Task<T> parameter into a local after each prior await. The matcher now recognizes this direct-source pattern when inter-await statements are cloneable and await-free; callback aliases defer to pending branches. `AwaitThreeTasksWithLaterDirectLocals` returns 822 inline with one output Task. Pending-first/middle/final each survive forced GC and return 831/840/849. Structural regression 1/1, full compiler suite 994/994, and the cross-module CTest passes in all six Windows x64/x86 and Linux x64 static/shared cells; Linux shared `ldd -r` resolves dependencies. Broad G4 frame/copy acceptance remains open. Exact roots and Linux runner history: `cxc/tests/native/results/conditional-while-allocation-20261023.txt`.

**G4 three-await chain mixing a direct Task local and later call source (2026-10-24; bounded):** the multi-await matcher now handles a chain with a direct Task<T> parameter local followed by a Task-returning call local, enabling pending-only aliases when any later initializer is direct. `AwaitThreeTasksWithMixedLaterSources` returns 858 inline with one output Task and one call; pending-first/middle/final survive forced GC and return 867/876/885, with the call delayed until needed and invoked once. Structural checks pass 2/2; the full compiler suite passes 995/995; native CTest passes all six Windows x64/x86 and Linux x64 static/shared cells; Linux shared `ldd -r` resolves dependencies. Broad G4 frame/copy acceptance remains open. Exact roots and Linux runner details: `cxc/tests/native/results/conditional-while-allocation-20261023.txt`.

**G4 three-await inferred direct Task<T> local (2026-10-24; bounded evidence):** the first later source in `AwaitThreeTasksWithLaterDirectLocals` now uses `var selectedSecond = second`, with an explicit third Task local retained. The structural test confirms inference and ordering. Inline returns 822 with one output Task; pending-first/middle/final survive forced GC and return 831/840/849. Related structural tests pass 2/2; the full compiler suite passes 995/995; cross-module CTest passes in all six Windows x64/x86 and Linux x64 static/shared cells, and Linux shared `ldd -r` resolves dependencies. The focused build initially overlapped the full suite and hit a test DLL lock; the serial rerun passed. Exact roots and runner history: `cxc/tests/native/results/conditional-while-allocation-20261023.txt`. Broad G4 frame/copy acceptance remains open.
**G4 three-await mixed chain with conditional later call source (2026-10-24; bounded evidence):** `AwaitThreeTasksWithConditionalLaterSource` materializes the second Task directly into an inferred local, then chooses between a counted Task-returning call and the direct third Task after the second await. The structural test confirms materialization and readiness order. Inline returns 894 with one output Task; the selected branch calls once and the unselected branch calls zero times. Pending-first/second delay the call until resumption; pending-third calls once before suspension. All pending positions survive forced GC and return 903/912/921. Focused test 1/1; full suite 996/996; cross-module CTest passes all six Windows x64/x86 and Linux x64 static/shared cells; Linux shared `ldd -r` passes. Existing lowerer support sufficed. Roots and full evidence: `cxc/tests/native/results/conditional-while-allocation-20261023.txt`. Broad G4 frame/copy acceptance remains open.

**G4 three-await mixed chain with a later null-coalescing call source (2026-10-24; bounded evidence):** AwaitThreeTasksWithNullCoalescingLaterSource materializes a direct Task local after the first await, then selects preferred ?? LaterTaskSource(middle, fallback) after the second. Existing lowerer support preserves the expression and delayed fallback timing. Inline preferred/fallback results 963/975 each allocate one output Task, with zero/one calls. Pending-first/second defer the fallback call until resumption (984/993); pending fallback calls once before third suspension (1002); pending preferred survives forced GC with no fallback call (1012). Structural test 1/1; full compiler suite 997/997; CTest 1/1 in all six Windows x64/x86 and Linux x64 static/shared cells; Linux shared ldd -r resolves project and system dependencies. Windows x64 root C:\Users\k.klein\AppData\Local\Temp\cx-cross-module-abi-3df613b578f0471d8838f545f6bc7ee2; x86 root C:\Users\k.klein\AppData\Local\Temp\cx-cross-module-abi-494cb7d1c501447db57a36e5be1ff3e8; Linux root C:\Users\k.klein\AppData\Local\Temp\cx-cross-module-abi-c721c65a18904ec7951e08785b94e077. Initial probe counted test setup Tasks and was corrected; x86 Run stack pressure was fixed by isolating the scenarios in a helper. Broad G4 frame/copy acceptance remains open. Full evidence: cxc/tests/native/results/conditional-while-allocation-20261023.txt.
**G4 three-await call-first chain with later direct Task locals (2026-10-24; bounded evidence):** AwaitThreeTasksWithCallFirstAndLaterDirectLocals invokes FirstTaskSource as the first await source, then materializes the second and third Task parameters after their preceding awaits. Existing lowerer source matching and all-inline building supported this shape; the compiler regression adds direct readiness-order evidence. Inline returns 76 with one output Task and one first-source call; pending-first/second/third survive forced GC and return 76 without reevaluation. Focused test 1/1; full compiler suite 998/998; CTest 1/1 in all six Windows x64/x86 and Linux x64 static/shared cells; Linux shared ldd -r resolves project/system dependencies. Roots: Windows x64 C:\Users\k.klein\AppData\Local\Temp\cx-cross-module-abi-3fcbc777cda14dc8babbfcb9c09d6248; x86 C:\Users\k.klein\AppData\Local\Temp\cx-cross-module-abi-b532105c699046bb9899154865129dc3; Linux C:\Users\k.klein\AppData\Local\Temp\cx-cross-module-abi-5fc3bb3e5da2435985fec8b7d9ce71a6. Full evidence: cxc/tests/native/results/conditional-while-allocation-20261023.txt. Broad G4 frame/copy acceptance remains open.

**G4 three-await inferred first Task local with later direct sources (2026-10-24; bounded evidence):** AwaitThreeTasksWithInferredFirstLocalAndLaterDirectLocals materializes var selectedFirst = first before await one, then materializes second and third after preceding awaits. Existing local typing and source matcher support the shape. Inline returns 76 with one output Task; pending-first/middle/final survive forced GC and return 76. Structural test 1/1; full compiler suite 999/999; CTest 1/1 all six Windows x64/x86 and Linux x64 static/shared cells; Linux shared ldd -r resolves project/system dependencies. Roots: Windows x64 C:\Users\k.klein\AppData\Local\Temp\cx-cross-module-abi-e82c0b7c41674d1bb89767152df9c368; x86 C:\Users\k.klein\AppData\Local\Temp\cx-cross-module-abi-fbf131f61a6440119df90d022d0f1c0d; Linux C:\Users\k.klein\AppData\Local\Temp\cx-cross-module-abi-8ce39860dfaa41478771115c77292b38. Evidence: cxc/tests/native/results/conditional-while-allocation-20261023.txt. Broad G4 frame/copy acceptance remains open.

**G4 three-await inferred call-first Task local with later direct locals (2026-10-25; bounded evidence):** AwaitThreeTasksWithInferredCallFirstLocalAndLaterDirectLocals materializes FirstTaskSource(first) into var selectedFirst before await one, followed by direct Task locals after the first and second results. Existing inference and multi-await support handled it. Inline returns 76 with one output Task and one first-source call; pending-first/middle/final survive forced GC and return 76 without reevaluating the call. Focused test 1/1; full compiler suite 1000/1000; CTest 1/1 in all six Windows x64/x86 and Linux x64 static/shared cells; Linux shared ldd -r resolves project/system dependencies. Roots: Windows x64 C:\Users\k.klein\AppData\Local\Temp\cx-cross-module-abi-1e85f98c14394e21930a946f859b5d2b; Windows x86 C:\Users\k.klein\AppData\Local\Temp\cx-cross-module-abi-b6b851ec42df497b99ec5ebedd69de5b; Linux C:\Users\k.klein\AppData\Local\Temp\cx-cross-module-abi-9af5c09722534850a3aa740c6de607a3. Evidence: cxc/tests/native/results/conditional-while-allocation-20261023.txt. Broad G4 acceptance remains open.

**G4 three-await inferred call-first local with conditional later call source (2026-10-26; bounded evidence):** `AwaitThreeTasksWithInferredCallFirstLocalAndConditionalLaterSource` materializes a counted call in `var selectedFirst`, awaits a direct later local, then materializes a conditional call/direct Task source. Structural checks verify inferred locals and ordered readiness. Inline returns 76 with one output Task and exactly one call to each producer; pending-first and pending-second defer the later producer until resumption, while pending-third invokes it once before suspending. All three pending positions survive forced GC and return 76. Focused test 1/1; full suite 1001/1001; CTest passes all six Windows x64/x86 and Linux x64 static/shared cells; Linux shared `ldd -r` resolves project and system dependencies. Existing lowerer support sufficed. Exact roots and the resolved WSL CMake issue are documented in `cxc/tests/native/results/conditional-while-allocation-20261023.txt`. Broad G4 frame/copy acceptance remains open.


**G4 inferred call-first chain with a later null-coalescing Task source (2026-10-26; bounded evidence):** `AwaitThreeTasksWithInferredCallFirstLocalAndNullCoalescingLaterSource` materializes a counted first call into an inferred local, awaits a later direct Task local, then stores `preferred ?? LaterTaskSource(middle, fallback)` in a third inferred local. Inline preferred and fallback paths each allocate one output Task and return 76 with fallback calls 0/1. Pending-first, pending-second and pending-fallback force GC, retain fallback timing and complete with result 76. The compiler regression verifies inference and source readiness order; focused test 1/1, full suite 1002/1002, and native CTest passes all six Windows x64/x86 and Linux x64 static/shared cells. Linux shared `ldd -r` resolves dependencies. Existing lowerer support sufficed. Exact roots and runner details: `cxc/tests/native/results/conditional-while-allocation-20261023.txt`. Broad G4 frame/copy acceptance remains open.


**G4 inferred call-first chain with a later switch-selected Task source (2026-10-26; bounded evidence):** `AwaitThreeTasksWithInferredCallFirstLocalAndSwitchLaterSource` materializes a counted first call in an inferred local, awaits a direct second Task local, then stores a switch choosing between a later counted call and direct third Task. Both inline arms allocate one output Task and return 76, with producer counts 1/1 or 0/1. Pending-first/second preserve call laziness until resume; pending-third calls once before suspension. All pending cases survive forced GC and return 76. Focused compiler test 1/1, full suite 1003/1003, native CTest all six Windows x64/x86 and Linux x64 static/shared cells, and Linux shared `ldd -r` pass. Existing lowerer support sufficed. Exact roots: `cxc/tests/native/results/conditional-while-allocation-20261023.txt`. Broad G4 frame/copy acceptance remains open.

**G4 inferred call-first chain with a cast-wrapped later Task source (2026-10-26; bounded evidence):** `AwaitThreeTasksWithInferredCallFirstLocalAndCastLaterSource` materializes a counted first call in an inferred local, awaits a direct second Task local, then stores an explicit Task cast around a counted later producer. Inline returns 76 with one output Task and exactly-once producer counts. Pending-first/second defer the call until resumption; pending-third invokes it once before suspension. All pending positions survive forced GC and return 76. Focused compiler test 1/1; full suite 1004/1004; native CTest passes the six Windows x64/x86 and Linux x64 static/shared cells; Linux shared `ldd -r` resolves project/system dependencies. Existing lowerer support sufficed. Exact roots: `cxc/tests/native/results/conditional-while-allocation-20261023.txt`. Broad G4 frame/copy acceptance remains open.

**G4 inferred first-local chain with a conditional later Task source (2026-10-26; bounded evidence):** `AwaitThreeTasksWithInferredFirstLocalAndConditionalLaterSource` materializes the first parameter and later direct Task in inferred locals, then stores a conditional call/direct source in a third inferred local. Inline call/direct arms return 76 with one output Task and call counts 1/0; pending-first/second defer the call, while pending-third calls once before suspension. All pending positions survive forced GC and return 76. Focused compiler test 1/1; full suite 1005/1005; native CTest passes the six Windows x64/x86 and Linux x64 static/shared cells; Linux shared `ldd -r` resolves dependencies. Existing lowerer support sufficed. Exact roots: `cxc/tests/native/results/conditional-while-allocation-20261023.txt`. Broad G4 frame/copy acceptance remains open.

**G4 inferred direct-first chain with a later null-coalescing Task source (2026-10-26; bounded evidence):** `AwaitThreeTasksWithInferredFirstLocalAndNullCoalescingLaterSource` materializes the first Task parameter into an inferred local, awaits a direct later Task local, then stores `preferred ?? LaterTaskSource(middle, fallback)` in a third inferred local. Preferred/fallback inline branches return 76 with one output Task and fallback calls 0/1. Pending-first/second defer fallback; pending-third calls once before suspension. All pending cases force GC and return 76. Focused compiler test 1/1; full suite 1006/1006; native CTest passes all six Windows x64/x86 and Linux x64 static/shared cells; Linux shared `ldd -r` passes. Existing lowerer support sufficed. Exact roots: `cxc/tests/native/results/conditional-while-allocation-20261023.txt`. Broad G4 frame/copy acceptance remains open.
**G4 inferred direct-first chain with a later switch-selected Task source (2026-10-26; bounded evidence):** `AwaitThreeTasksWithInferredDirectFirstLocalAndSwitchLaterSource` materializes the first and second Task parameters in inferred locals, then switches between a counted later producer and the direct third Task. Inline paths allocate one output Task and return 76 with source counts 1/1 or 0/1; pending-first/second defer the producer until resume and pending-third invokes it once before suspension. Every pending position survives forced GC. Focused test 1/1; full suite 1007/1007; CTest passes all six Windows x64/x86 and Linux x64 static/shared cells; Linux shared `ldd -r` resolves dependencies. Existing lowerer support sufficed. Exact roots and run details are in `cxc/tests/native/results/conditional-while-allocation-20261023.txt`. Broad G4 frame/copy acceptance remains open.
**G4 inferred direct-first chain with a cast-wrapped later Task source (2026-10-26; bounded evidence):** AwaitThreeTasksWithInferredDirectFirstLocalAndCastLaterSource materializes the first and second Task parameters in inferred locals, then stores a cast-wrapped counted producer in a third inferred local. Inline returns 76 with one output Task and exactly-once producer counts. Pending-first/second defer the later call; pending-third invokes it before suspension. All pending positions survive forced GC. Focused test 1/1; full suite 1008/1008; native CTest passes all six Windows x64/x86 and Linux x64 static/shared cells; Linux shared ldd -r resolves dependencies. Existing lowerer support sufficed. Full roots and run details are in cxc/tests/native/results/conditional-while-allocation-20261023.txt. Broad G4 frame/copy acceptance remains open.
**G4/G7 large-payload frame refresh (2026-10-26; diagnostic evidence):** The current cross-module fixture was rebuilt with MSVC 19.51 `/O2 /FAcs` and GCC 11.4 `-O2 -fstack-usage`. GCC x64 reports 704/688 bytes for local/nested-local large `ValueTask` awaits (down from retained October 18 figures 752/736). Current MSVC x64 reserves 896/976 bytes plus saved registers (96 bytes above the earlier 800/880 listing); current Win32 reserves 208/228 bytes (the earlier local/nested figures were 208/252). Other current shapes measure 912 bytes for explicit large payload across two/three awaits, 944 for the awaited loop, and GCC x64 960 for explicit local / 1,776 and 1,968 for ValueTask-returning local/nested calls. The Linux CTest and both Windows static CTests pass. The x86 audit CTest transiently reported an access violation once; running that same executable and rerunning CTest both passed. These toolchain stack reports are not runtime high-water marks and the unresolved x64 MSVC increase has no documented budget to compare against. Physical copy traffic for the 104/52-byte payload remains unmeasured. Full commands, roots, values and limits are in `tests/native/results/milestone18-g4-frame-copy-refresh-20261026.txt`; G4/G7 remain open.
### G4 payload copy attribution follow-up (2026-10-26)

The current MSVC optimized listing ties inline `LargeTaskPayload` movement to
two 104-byte transfers on x64 and four 52-byte transfers on Win32, including
Win32 by-value stack staging. These are static instruction-level boundaries,
not runtime bus measurements. The pending path totals three 104-byte transfers
on x64 and four 52-byte transfers on Win32. These include caller staging plus
the getter sret write and result-task field write, whose bodies are present in
the retained `math.cod` listing. For this MSVC 19.51 `/O2` fixture only, the
regression ceilings are set equal to current observations: local stack
reservation ≤896/208 bytes (x64/Win32) and nested-local reservation ≤976/228
bytes; inline transfers ≤208 bytes on either target; pending transfers ≤312
bytes x64 and ≤208 bytes Win32. These zero-regression ceilings are not
optimization targets or runtime stack guarantees. The current listing meets
them; the G4 aggregate audit maps allocation, lazy materialization, and forced-GC
requirements to the checked supported-form evidence. G4 is checked for that
scope. Full attribution,
retained build roots, and limits:
`cxc/tests/native/results/milestone18-g4-payload-copy-attribution-20261026.txt`.

### G7 aggregate audit (2026-10-26)

The scheduler's exclusive payload union reduces the measured item from 136 to
112 bytes x64 and 80 to 64 bytes x86. The captured async-iterator case shares
one callback environment when completion and cancellation have identical
captures, reducing one allocation per pending await. Typed iterator probes
cover `int`, `string`, reference-containing and large reference-containing
elements; their one-vs-sixteen allocation counts remain constant, and the
optimized direct foreach paths read frame fields without calling `Current`.
Frame sizes, branch code-size deltas and large-result transfer boundaries are
recorded in the retained G7 reports. The aggregate audit found no additional
ownership-safe scheduler-node embedding or pool justified by current evidence.
G7 is checked for the measured supported shapes; the matrix and scope limits
are in `cxc/tests/native/results/milestone18-g7-aggregate-audit-20261026.txt`.

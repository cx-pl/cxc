# Native conformance checks

`fixtures/memory-arena-async` is the CX consumer for the initial
`Memory.WithArenaAsync` slice. It forces collection while a task is suspended,
then verifies arena data remains readable after resumption and checks task
fault/cancellation propagation, including a callback that throws before it
returns a task. It also checks an async child stored in a local and then awaited,
so joined-operation analysis and scheduler-context capture cover that source
shape. Build the CX project to generate its C, then
configure its CMake fixture with `CX_GENERATED_DIR` and `CXCORE_SOURCE_DIR`;
Windows x86/x64 static/shared and Linux x64 static/shared have been exercised.
The fixture also cancels an awaited operation after it acquires a `NativeBuffer`
and forces collection while its cancellation callback is registered and again
after cancellation queues the awaited continuation. It verifies the buffer is
still live before cancellation, then the awaited `finally` disposes it exactly
once before the arena scope completes; this case passes in all six Release
cells. See
`results/milestone22-async-cancellation-forced-gc-20261104.md`.
The fixture also checks `WithArenaAsync<T>` scalar and durable-reference
results, both capacity modes, generic fault/cancellation propagation, and
faulting on a callback that returns a mismatched `Task<U>` result type.
Those cases pass in all six Release cells. `fixtures/memory-arena-result-escape`
is a separate expected-failure consumer: it returns a value whose reference is
nested two structs deep through a durable result task and verifies the runtime
store barrier rejects it. This nested-value case currently passes Windows x64
static Release.
The fixture also forces `List<T>`, `Dictionary<TKey,TValue>`, `Queue<T>`, and
`Stack<T>` storage to grow while the opposite allocation context is active.
It verifies list and dictionary values, plus FIFO/LIFO values and counts. Both
durable owners under an arena and arena owners under a durable override pass in
all six Release cells; see
`results/milestone22-collection-owner-growth-20261104.md`.
The same fixture creates collection enumerators for `Array<T>`,
`Dictionary<TKey,TValue>`, and `LinkedList<T>` owners in both directions:
durable collections inside an arena and arena collections inside
`WithDurable`. It advances the scoped enumerators after the durable override
has restored the arena context. All six Release cells pass; see
`results/milestone22-collection-enumerator-owner-20261104.md`.

Joined child tasks can also be created and awaited immediately within bounded
`for` and `do while` loop bodies. The async arena fixture checks both forms in
all six Windows x64/Win32 and Linux x64 static/shared Release cells. These tests
do not establish support for arbitrary loop control flow, task graphs, or
enclosing closure captures; see
`results/milestone22-async-loop-joins-20261104.md`.

`TaskHandle.WhenAll(TaskHandle[])` joins a variable-size child array. The
compiler currently proves only fixed literal arrays populated from prior task
locals immediately before the aggregate await; any intervening statement
invalidates that proof. The forced-GC fixture covers three-child fault and
cancellation joins, input-array mutation after the join starts, empty arrays,
and rejection of null entries. This
array-backed path passes all six Windows x64/Win32 and Linux x64 static/shared
Release cells; Linux shared `ldd -r` resolves dependencies. See
`results/milestone22-async-variable-join-20261105.md`.

`run-stdlib-additions.ps1` builds and runs `fixtures/stdlib-additions/main.cx`
against `cxcore`. It checks all eight UUID versions, deterministic name-based
IDs, numeric nullable `TryParse` results, fluent StringBuilder calls, Unicode
length, immutable snapshots, and virtual `ToString`. It accepts the same build
and static/shared link options as `run-keyword-expressions.ps1`.

`run-memory-arena-linked-list.ps1` also checks collection owner behavior. It
materializes a durable `List<T>` snapshot inside an arena and an arena-owned
list snapshot inside a durable override, then verifies their values while the
owning lifetime remains active. Its linked-list checks cover mutation of a
durable list during an arena and an arena list during a durable override. The
fixture also grows a durable `List<T>` and `Dictionary<string, T>` from four to
sixteen slots inside an arena, clears their source arrays, forces collection,
and verifies all nine retained payloads through list access and dictionary
enumeration. It passes Windows x64/x86 static/shared and Linux x64
static/shared Release; its runner keeps each architecture, generator, and
linkage mode in a separate build tree. See
`results/milestone22-persistent-list-growth-during-arena-20261106.md`.

`run-memory-arena-dynamic-static-init.ps1` verifies the first call into a
generated CX module while `Memory.WithArena` is active. The generated dynamic
static initializer publishes a nested object graph into a registered static
root; after the arena closes and forced GC runs, a later module call reads the
same values. Its CX consumer also creates `MemoryArena(4096u)`, reuses it across
two successful runs with an intervening thrown exception, disposes it twice,
including through `IDisposable`, and confirms use-after-dispose throws. The
generated-core and checked-in fallback variants pass Windows x64/x86 and Linux
x64 static/shared Release cells; Linux shared `ldd -r` resolves dependencies.
See
`results/milestone22-memory-arena-object-api-20261106.md`.

`run-keyword-expressions.ps1` builds and runs
`fixtures/keyword-expressions/main.cx`. It covers safe reference/interface and
array casts, nullable unboxing, generic safe casts and type queries, exact decimal
arithmetic, conversions, properties, array storage, `nameof` without evaluation,
and `typeof` identities. It accepts `-Cxc`, `-CxCoreDir`, `-Generator`,
`-Architecture`, `-Configuration`, and `-StaticLink ON|OFF`.
The runtime's `cxcore_decimal_tests` additionally checks 400 arithmetic vectors
generated from .NET's System.Decimal; regenerate them with
`cxcore/tests/generate_decimal_vectors.ps1`.

`run-threading-e2e.ps1` verifies the Milestone 16 `System.Threading` API and GC
integration. It generates `fixtures/threading.cx`, builds through the portable
`fixtures/threading/CMakeLists.txt`, and checks normal execution plus subprocess
failures for an unobserved worker exception and exiting with a held mutex. The
positive fixture covers capture/array lifetime, pure-loop polling during GC,
virtual/multicast actions, atomic wraparound and CAS, recursive mutex use,
repeat joins, worker failures, self-join, invalid handles/cells, and foreground
completion. `-Architecture Win32` selects x86; `-StaticLink OFF` selects DLL
linking; `-Configuration Release` enables optimized execution.

The CMake fixture also builds on Linux after generating the CX source. Set
`CX_GENERATED_DIR` to the generated source directory, `CXCORE_SOURCE_DIR` to the
runtime checkout, and `CX_STATIC_LINK` to ON or OFF. All three CTest cases require
the expected process exit and, for failures, the expected diagnostic. CMake 3.31
or newer is required. Compiler snapshots allow a full harness run to continue
while the development compiler is rebuilt.

`run-di-e2e.ps1 -StaticLink BOTH` builds and runs the Milestone 19 container
fixture on Windows x86/x64. `run-di-cross-module.ps1 -Architecture x64`
checks a service whose interface and implementation live in a referenced CX
library. Both fixtures cover static and shared `cxcore` linking. The DI fixture
also passes on Linux x64 static/shared under WSL/GCC 11; the cross-module probe
uses the same four platform/link cells. Linux strict builds use `-Werror` with
documented suppressions for GCC's collector `setjmp` warning and generated
unused/no-op expressions.

`run-entry-point-e2e.ps1` compiles a temporary CX program, builds the generated
C with CMake and `cxcore`, then checks argument count and contents,
short-circuit evaluation, null coalescing, 64-bit integer limits, deep
inheritance, interface diamond dispatch, dynamic static field initialization,
generic array creation, element reads both directly and through a typed generic
local, length comparisons, constrained generic
field and property reads, class-constrained method calls, interface-constrained
method, property, and indexer reads and writes, including method results
combined and returned through an explicitly typed local and assignment values
composed from constrained property reads and method results, lengths, stdout,
dynamic construction of
classes with public parameterless constructors, boxed default values from
non-generic and supported closed generic structs, parameterized class and
struct construction from exact-type boxed arguments, null results for classes
without a public constructor,
generic virtual dispatch through overrides and inherited implementations,
including a constructed generic runtime class and generic callers forwarding
closed `int` and `string` method arguments, plus generic interface dispatch
through an interface-typed receiver and generic callers, with exact
specialization and fallback, including conditional-expression callers and
statement-level `if`/`if-else` callers that return a matching type-parameter
fallback, including block-wrapped return branches, with dispatch in either
branch and a leading explicit `bool` local, under static and DLL
project-reference linking,
and generic virtual callers that store the dispatched result in a matching
type-parameter local before returning it,
including `ITransform<int>` implementations on ordinary and closed generic
classes, plus generic virtual class and interface method values with runtime
override selection and reflection metadata, and inherited generic interface
methods called through child, three-level, and diamond interfaces, including
same-signature generic contract redeclaration and distinct-signature overloads
on a child interface, plus
interface-constrained generic callers with exact specialization and fallback,
and method-level `class`, `struct`, and interface constraints on generic
virtual interface methods,
including combined `class, IMarker` and `class, IMarker, IGenericVirtualAuditable`
constraints and `struct, IMarker`, plus cross-project interface dispatch with
`struct, IMarker, IGenericVirtualAuditable` and `class, IMarker,
IGenericVirtualAuditable` constraints with exact/fallback coverage,
and method-level `class` constraints on class virtual methods and their
overrides, with exact specialization through a base-typed receiver and primary
fallback for another reference type, plus combined `class, IMarker` constraints
and combined `class, IMarker, IGenericVirtualAuditable` constraints with exact
specialization and fallback through a base-typed receiver,
and `struct` constraints with an exact `int` specialization and `long` fallback,
primitive extension method/property/operator receivers, concrete and generic
array extension methods, concrete and concrete-signature generic read-only array
properties, non-mutating array operators, concrete and generic array
compound-operator and increment/decrement write-back, and
closed generic property
getters that return a matching field or conditional field/default value through
explicit and inferred locals, including a constructed generic interface
property thunk,
closed generic struct and class operator specialization, interface-constrained
dispatch for closed generic struct arguments, mixed closed-generic layouts with
computed const interface-property getters over concrete fields in boxed closed
generic structs, including concrete getters on constructed generic interfaces,
computed const getters over concrete fields on closed generic structs, and
computed setters that assign a concrete field from built-in expressions,
including setter dispatch through a constructed generic interface,
constructed generic struct constructors that copy a matching parameter into
multiple fields, and conditional computed `T` getters that select a matching
generic field or `default` on a closed generic struct directly and through a
constructed generic interface,
conditional-expression and boolean `if`/`else` generic-result methods through
constructed generic interfaces,
concrete-expression return branches through direct, constructed-interface, and
interface-constrained calls,
concrete and generic fields (including non-generic and closed generic structs
stored by value, plus concrete primitive, array, and non-generic struct fields in
generic reference layouts, with nested generic construction, constructor
assignment, field reads, and generic reference classes inheriting through a
multi-level closed-generic base chain). Closed generic owners also allocate
arrays of constructed generic reference and value elements such as
`ArrayBox<T>[]` and `Pair<T>[]` in field initializers and constructors; native
checks cover independent closed owners, lengths, zero-initialized elements,
reference writes, and value-type element strides across `int` and `long`
layouts. The native harness also covers constructor parameter assignments,
literal and arithmetic field initializers,
default initialization of direct and constructed generic fields (reference and
value), null
initialization of generic reference-constrained and array fields,
class- and interface-constrained generic null checks, generic array null checks,
reference-constrained generic and generic array null-coalescing,
string switch expressions with filters and null labels, inferred generic local
function specialization with interface constraints,
closed `System.Collections.Array<T>` and `List<T>` storage, including calls to
the public `Array<T>` API through both `Array<T>` and built-in `T[]` values,
matching generic array-length comparisons across both spellings, nested arrays,
and computed unsigned expressions,
safe array reads and writes, list growth and mutation, self-appending `AddRange`,
compact `ToArray`, offset `CopyTo`, and structural `foreach` for arrays, lists,
stacks, queues, linked lists, and dictionary entries; dictionary coverage also
checks null keys, string content equality, custom comparer collisions,
collision-chain growth and removal. A custom sequence exercises `foreach`
through an `IEnumerable<int>`-typed value and its constructed
`IEnumerator<int>` dispatch. Dictionary also runs `string,long` and
`CollisionKey,long` key/value combinations. The harness also checks
successful and nonzero process exit codes. Reflection
invocation checks instance and static calls, exact primitive overload selection,
boxed primitive and struct arguments and returns, const reads and mutation on a
boxed struct receiver, implicit and explicit interface conversions and dispatch
on boxed non-generic and supported closed generic structs, including closed
generic interface field accessors, generic-result methods returning
`default`, multi-field generic setters, generic field reset to `default` including
multiple fields reset by one interface method, and
conditional `if`/`else` resets and matching-parameter writes to generic fields
directly and through a constructed interface, plus branch-selected built-in
expressions assigned to concrete fields on both paths, including expressions
that read earlier writes in the same branch, and
mixed generic and concrete field mutation through direct calls and interface
dispatch, plus concrete literal-return,
field-expression, and ordered
field-mutation methods, direct field setter/getter methods on boxed
closed generic structs, plus a reflected concrete-field expression method on a
boxed closed generic struct and ordered concrete-field mutation through the same
box. It also checks reference and exact-boxed primitive setter/getter
invocation on closed generic reference classes, and rejection of mismatched
closed-generic method arguments and unsupported method shapes,
casts back to `object`,
`void` calls,
incompatible arguments, invalid targets, and missing methods. Named delegate
coverage checks ordered multicast invocation, removal, the last handler's result,
equality, and a captured lambda in generated native code. It also builds a
The harness also checks runtime-name `ToString()`, reference-identity `Equals`,
stable identity hashes, and virtual overrides called through `object`. String
operators and the `String` overrides compare UTF-8 contents, match hashes for
equal contents, and preserve the string through `ToString()` when called through
`object`; relational string operators use UTF-8 ordinal ordering. Concept tests
specialize a conditional generic maximum and direct comparison returns for class
and struct implementations, plus generic arithmetic, unary operator, and
parameterized concept-method returns and built-in return expressions that
combine required method results. Auto-property getter/setter behavior runs in
generated code. Generic concept-method requirements also run for class and struct
receivers with inferred method type arguments, checked standard and concept
constraints, and requirement defaults inserted at the call site. Concept setter
coverage also interleaves required property writes, built-in compound property
assignments whose right-hand expressions call required methods, and required
method calls as statements on class and struct receivers; the native fixture shares the required methods
through a diamond of base concepts, adds the property on the derived concept,
reads the required property into an explicitly typed local, and branches
between those mutations. The body also updates locals in bounded counted and
do/while loops.
Generic class members may use class-level concept constraints in returned
property/method/operator expressions; their `void` methods may also assign
required properties and make direct required-method calls in source order.
Assignment values and method-call arguments can combine constrained property
reads with parameters in built-in expressions. Required properties also support
all five built-in compound assignments. Native checks cover returned
expressions, mixed property/method mutation, switch selection on a required
property with filtered literal labels and a default arm, string selectors with
filtered labels and `null`, and a counted loop that passes its local counter to
a required method. Early `return;` statements in a
class-level concept `void` method are checked on both the returning and
continuing paths. A `foreach` over an array of the constrained type calls a
required method on each element.
Concrete built-in integer, boolean, and string locals in class-level concept
methods can be initialized from required member expressions and reused in
later expressions. Integer locals support built-in updates and required method
calls; boolean locals control `if` branches, and string locals select switches.
Locals of the constrained type parameter are substituted for each closed
class and can make required method calls.
Class- and interface-constrained generic `void` calls run in source order for
reference and struct arguments; struct mutations remain local to the generic
function's by-value parameter. Interface-constrained calls also execute both
`if`/`else` branches with supported built-in argument expressions.
Closed generic struct implementations of constructed interfaces also write
multiple matching generic fields in each branch, with both direct and
interface-thunk calls checked in native execution. Single-arm branches verify
that unmatched fields keep their prior values. Branches may mix generic-field
writes and resets with dependent concrete-field expressions; both call paths
preserve the source order.
Constrained interface method results from two independently specialized
parameters also combine in direct returns and typed-local returns.
It builds a
two-project app and verifies dynamic construction of a class declared in its
referenced CX library, plus public operator and method concepts with generic
functions specialized against consumer-defined class and struct types. Build
products are removed on success and retained in the reported temporary
directory on failure.
Cross-module binding and generated calls also have compiler assertions in the
`ProjectReferenceTests` xUnit group.

From the `cxc` directory, run it with the compiler DLL and CMake executable:

```powershell
dotnet build src/CxCompiler/CxCompiler.csproj --no-restore
.\tests\native\run-entry-point-e2e.ps1 `
  -Cxc .\src\CxCompiler\bin\Debug\net10.0\CxCompiler.dll `
  -CxCoreDir ..\cxcore `
  -CMake 'C:\Program Files\Microsoft Visual Studio\18\Professional\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe' `
  -Generator 'Visual Studio 18 2026'
```

The harness also accepts a CMake generator and C compiler. To run it with
Clang/Ninja on Windows, use a Visual Studio developer environment so the Windows
SDK resource compiler and C runtime libraries are available, then pass
`-Generator Ninja -CCompiler <path-to-clang.exe>`.

The entry-point fixture also verifies inferred and explicit calls to top-level,
static, class-instance, and mutating struct-instance generic specializations.
It also exercises generic extension method specializations against ordinary,
closed generic class, struct, interface (including a constructed generic
interface), enum, and primitive targets in native code.
The fixture also checks a captured generic local function specialization and
primary-body fallback for another method type argument.
Generic virtual dispatch is exercised through a base-class reference for a
derived override specialization, the override's primary-body fallback, and a
base-class runtime instance.
It checks generic binary operator specialization and fallback on a value type,
plus mutating generic specializations and constrained primary fallbacks for all
five compound assignments on a struct lvalue.
Function and method specialization declarations use keyword-free closed type
lists, matching the semantic classification rules in the language reference.
Generic methods also forward through the current instance and a matching class
receiver parameter, covering custom specializations and primary fallbacks.
They may also forward through a computed receiver expression whose class type
matches the target method owner. The native fixture uses a side-effecting
factory and checks that it runs once per forwarding call.
Forwarding also accepts multiple direct generic-parameter arguments; native
coverage verifies both a custom tuple specialization and a primary fallback.
Forwarded concrete arguments can use pure built-in expressions over concrete
parameters and literals; the native fixture checks specialized and fallback
calls with an integer offset expression.
Generic `void` callers also forward one call to a generic `void` callee. The
native fixture verifies an exact specialized callee and primary fallback for
two constrained interface implementations.
Forwarding through by-value struct receivers preserves copy semantics; the
native fixture verifies that mutations affect only the receiver copy or
temporary. Cross-project specialization tests run in both static and DLL modes.
Unmatched type arguments continue to use the primary generic bodies. The native
cases include reference, array, constructed alias, two-parameter, recursive,
and cross-specialization calls.
The project-reference fixture exercises library-owned generic function and
method specializations under both static and DLL linking.
It also calls a library-owned generic virtual method from a library method
through an app-defined override specialization, checking that the consumer
registers the closed runtime dispatch target in both link modes.
The library method also calls a generic interface contract implemented only
by the consumer, whose exact method specialization dispatches in both modes.
A library generic caller also forwards a closed caller type parameter through
that interface, checking exact specialization and primary fallback in both modes.
The same call closes an interface implemented by a consumer-defined closed
generic class, covering exact and fallback method arguments in both modes.
A generic `void` library caller also forwards through the consumer's interface
implementation, with exact and primary-fallback calls checked in both modes.
A library generic caller also forwards through a constrained class virtual method
to a consumer-defined override, checking exact specialization and primary-body
fallback in both static and DLL modes.
The same project-reference path also covers `struct` plus interface constraints
with consumer-defined value types, exact specialization, and fallback.
It also checks `class` plus two interface constraints through a consumer override
with exact specialization and primary-body fallback in both link modes.
The `struct` caller also supports two interface constraints, with exact and
fallback dispatch through the consumer override in both link modes.
Library generic callers can also choose between one virtual dispatch call and
a matching generic value in a conditional expression; native cases exercise the
call in each branch under static and DLL linking.
It also verifies consumer-project replacements for imported public generic
functions: the consumer's exact-type bodies win for direct and delegate calls,
while unmatched type arguments fall back to the primary body and the library's
own specialization remains isolated.
Same-project instance specializations on generic classes are selected for two
closed reference owner types; their bodies write a matching generic field and
unmatched method arguments use the primary body. A generic struct owner also
checks specialization and fallback against a mutable lvalue. The project-reference
fixture also instantiates a library-owned generic class method specialization for
closed class owners under both link modes. It also calls a library-owned static
generic method specialization through two closed generic class owners whose type
parameters appear in the method signature.

## Milestone 17 native interoperability

run-interop-e2e.ps1 copies and compiles native-scalar, native-text and native-abi
fixtures into an isolated BuildRoot. Run for x64 and Win32, with StaticLink ON/OFF.
It verifies exact imported symbols across a declarations-only reference, explicit
runtime loading, pointer outputs, SDK records passed/returned by value, native
allocation/destruction, 64-byte alignment, UTF-16 arrays/text, typed/static/captured
callbacks, foreign-thread reentry, GC pressure and in-flight unregistration.
SDK negative cases require incompatible signatures, layouts and fields to fail.

```powershell
cxc/tests/native/run-interop-e2e.ps1 -BuildRoot .build/interop-win64
cxc/tests/native/run-interop-e2e.ps1 -BuildRoot .build/interop-win32 -Architecture Win32
cxc/tests/native/run-interop-e2e.ps1 -BuildRoot .build/interop-dll -StaticLink OFF
cxc/tests/native/run-interop-e2e.ps1 -BuildRoot .build/interop-linux -GenerateOnly
```

For generated Linux sources, run run-interop-e2e.sh with the root, cxcore path and
gcc/clang. CMAKE selects the CMake executable and CX_STATIC_LINK selects ON/OFF.
NegativeOnly runs just SDK rejection checks on Windows. The small handwritten C
fixture is compiler conformance infrastructure; SDK bindings and consuming examples
contain only CX. Package integration runners are in ../../../examples/native-bindings.

## Milestone 18 strict generated-C checks

After regenerating `cxcore`, `cxnet`, and `streaming-read`, compile each generated
C translation unit with MSVC C11 warnings enabled for both architecture and
linkage configurations:

```powershell
.\tests\native\run-generated-c-strict.ps1 -Architecture x64
.\tests\native\run-generated-c-strict.ps1 -Architecture x86
```

The script uses `/W4 /WX`; it suppresses C4100 for unused generated thunk
parameters, C4130 for intentional constant comparisons, C4232 for imported
module-initializer function addresses stored as providers, and C4702 for
unreachable coroutine continuation paths. It writes object files to a unique
temporary directory and removes them after a successful run.

## Milestone 18 coroutine acceptance fixture

`run-coroutine-acceptance.ps1` generates the existing awaited-finally probe source
and builds it through `fixtures/coroutine-acceptance/CMakeLists.txt`. The runner
accepts `-Platform Windows|Linux`, `-Architecture x64|x86` (Linux currently x64),
and `-LinkMode static|shared|both`. The positive runtime scenario checks
synchronous iterator cleanup and reentrancy rejection; pending, faulted and
canceled Tasks with awaited cleanup; async enumeration and exactly-once
disposal; closure capture after forced GC and enumerator disposal; and shared
library relocations. Compiler acceptance and rejection tests remain in the
separate compiler test project.

Windows x64 static/shared:

```powershell
./cxc/tests/native/run-coroutine-acceptance.ps1 `
  -Cxc ./cxc/src/CxCompiler/bin/Debug/net10.0/CxCompiler.dll `
  -Platform Windows -Architecture x64 -LinkMode both
```

Linux x64 static/shared uses WSL, GCC, `ldd`, and CMake 3.31 or newer:

```powershell
./cxc/tests/native/run-coroutine-acceptance.ps1 `
  -Cxc ./cxc/src/CxCompiler/bin/Debug/net10.0/CxCompiler.dll `
  -Platform Linux -Architecture x64 -LinkMode both `
  -Distribution Ubuntu -CMake /path/in/wsl/to/cmake
```

`run-awaited-finally-linux.ps1` remains as the underlying probe generator and
accepts the same platform, architecture and link-mode options. Fixture output is
kept in the Windows temporary directory on failure and removed after all selected
builds, runtime checks and relocation checks pass. The fixture exercises positive
runtime behavior only; negative compiler tests remain separate.

## Milestone 18 cross-module task and iterator ABI fixture

`run-cross-module-abi.ps1` builds a CX library and its app consumer. It checks
public task/iterator APIs, typed results, reusable sources, forced GC,
reflection, callback ownership, and the supported imported Task/ValueTask await
forms with allocation assertions. For example, from the workspace root:

```powershell
./cxc/tests/native/run-cross-module-abi.ps1 `
  -Cxc ./cxc/src/CxCompiler/bin/Debug/net10.0/CxCompiler.dll `
  -Platform Windows -Architecture x86 -LinkMode static -KeepBuild
```

Windows also supports x64. Linux uses `-Platform Linux -Architecture x64`, WSL
Ubuntu and `-CMake /path/in/wsl/to/cmake` (3.31 or newer); shared execution also
checks `ldd -r`. Each CTest reports a failing process result code, limits app
execution to 60 seconds, and has a 90-second outer timeout. Linux process exit
codes follow the OS's eight-bit limit. Compiler structural and negative tests
remain separate from native execution.

Failures retain their unique temporary build root. `-KeepBuild` also retains a
successful root and prints `BUILD PRESERVED: ...`. Use that path to investigate
intermittent assertions, for example:

```powershell
ctest --test-dir <retained-root>/build-Windows-x86-static `
  -C Release --output-on-failure --repeat until-fail:5
```

Use a separate `-LinkMode shared -KeepBuild` run to repeat the shared build.
Both-mode runs test each mode immediately, but share the app output path; after
both modes finish, the executable there belongs to the last build. Preserve the
first failure and report repeat results independently; successful retries do not
explain an intermittent failure.

## Module metadata

`python probe-module-info.py LIBRARY MODULE_NAME [REFERENCE_NAME ...]` inspects
exported module descriptors without module initialization. It checks accessor/data
identity, public type flags, dependency providers, annotation value providers,
array-reader element descriptors, closed generic argument providers, and annotation
source coordinates. On Windows,
use the DLL copies beside the sample executable so all dependencies are available.

The CXTESTS cross-project example in `../../../cxtests/examples/basic` exercises
module-based library discovery, typed constants and reflected invocation, inline
and deferred member rows, constructor injection, class/collection fixture scopes,
output, filters, failures, and repeat execution under static/shared linking.
Run `cxtests/tests/run-native.ps1` from the workspace root as documented
in the [CXTESTS README](../../../cxtests/README.md); it independently parses JSON/XML
and checks actual process exit codes across the supported native matrix.

## System.Math

`run-math.ps1` compiles and executes all declared Math overloads and constants,
including managed errors for integer absolute-value overflow, reversed clamp
bounds, and invalid rounding precision. It accepts `-StaticLink ON` or `OFF`.
The cxcore CTest suite additionally checks NaN, infinity, signed zero,
ties-to-even rounding, and native rounding-mode independence.

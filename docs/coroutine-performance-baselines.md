# Coroutine performance baselines

The native benchmark measures synchronous and asynchronous iterator consumption,
completed and pending `Task<int>` awaits, and nested awaits. Generated operations
are compared with small handwritten C state machines that produce the same result.
The C reference keeps state on the stack and omits CX task objects, runtime
continuations, and scheduler work; the comparison exposes the cost of the full
generated/runtime path against a lower-level state machine, not equal ownership
or dispatch costs.

## Release Windows x64 results

| Operation | Generated median (range), ns/op | C median (range), ns/op | Generated / C managed allocations per op | State frame bytes, generated / C |
| --- | ---: | ---: | ---: | ---: |
| 1,000-element iterator consumption | 321,691 (311,988–398,313) | 620.40 (572–1,169.80) | 2 / 0 | 40 / 40 |
| Completed await | 2,500.80 (2,385.60–2,754.80) | 1.34 (1.16–1.48) | 8 / 0 | 0 / 8 |
| Pending await and scheduler drain | 2,365.60 (2,189.60–2,888.40) | 1.49 (1.38–1.71) | 12 / 0 | 0 / 8 |
| Nested completed await | 3,238.80 (3,016.80–4,104) | 2.89 (2.48–3.73) | 12 / 0 | 0 / 8 |
| Nested pending await and scheduler drain | 3,296.40 (2,880.80–3,840) | 2.61 (2.20–3.43) | 19 / 0 | 0 / 16 |

Generated `return await` relays do not allocate a separate CX state frame, so
their frame size is zero. Their Task and continuation allocations are included
in the allocation counts. Pending cases create a `TaskCompletionSource`, complete
it, and drain `TaskScheduler`; nested cases relay through two async methods.
Allocation counting runs in a separate pass from timing. The harness forces
collection outside each await timing sample. These samples do not measure
sustained GC pressure.

### Pending await with forced GC pressure

A follow-up Release run adds a paired pending-await workload. Both the generated
CX operation and handwritten C reference perform 128 operations per sample; after
each batch of eight, the timed loop allocates 64 KiB of unreferenced managed
garbage and explicitly collects. The runtime collection counter confirmed 16
collections in every timed sample. Allocation counts are recorded in a separate
pass with the same workload and therefore include its 2 garbage allocations per
operation.

| Operation | Median (range), ns/op | Managed allocations/op | State frame bytes | Collections/sample |
| --- | ---: | ---: | ---: | ---: |
| Generated pending await with GC pressure | 6,635.94 (6,021.09–6,707.03) | 14 | 0 | 16 |
| Handwritten C pending-await state with GC pressure | 460.94 (427.34–475) | 2 | 8 | 16 |

The C path does not create CX task objects, continuations, or scheduler entries;
its two allocations per operation are the shared garbage-pressure workload. The
generated path reports 12 await-related allocations plus those two pressure
allocations. This controlled forced-collection workload measures throughput
under repeated GC, but is not an application allocation profile or an automatic
heap-threshold benchmark.

### Completed async iterator advancement

A later run measures 100 independent 1,000-element async enumerations per sample.
`AsyncIteratorSum` directly drives `MoveNextAsync().GetResult()`, sums each
current value, and awaits `DisposeAsync()`. This exercises completed-yield
advancement and disposal; it has no pending producer await. The C reference is
a stack-only iterator state machine and does not model `Task<bool>` allocation,
interface dispatch, or async disposal, so it is a lower-bound comparison.

| Operation | Median (range), ns/enumeration | Managed allocations/enumeration | State frame bytes |
| --- | ---: | ---: | ---: |
| Generated 1,000-element async iterator | 5,822,613 (5,562,410–6,890,108) | 4,008 | 80 |
| Handwritten C stack state machine | 257 (234–309) | 0 | 12 |

The allocation count is about four allocations per `MoveNextAsync` call, plus
enumerator/disposal overhead. This is a diagnostic finding that completed async
iterator advancement currently allocates heavily; it is not a target or budget.

### Pending async iterator resume

Each operation creates an async iterator over one pending integer task, starts
`MoveNextAsync`, completes the `TaskCompletionSource`, drains the scheduler,
checks the yielded value, and disposes the enumerator. This measures an actual
pending producer await and continuation resume. The C reference is a tiny
stack-state transition and omits task completion, scheduler, and iterator
ownership costs; its sub-nanosecond timer result is a lower bound, not a useful
speedup ratio.

| Operation | Median (range), ns/op | Managed allocations/op | State frame bytes |
| --- | ---: | ---: | ---: |
| Generated pending async iterator resume | 6,275 (5,667–10,135) | 18 | 96 |
| Handwritten C pending-resume state transition | 0.70 (0.69–0.77) | 0 | 12 |

The expanded run used 500 synchronous iterator operations, 100 async iterator
enumerations per sample (1,000 elements each), 100 generated pending-resume
operations, 250 generated await operations, 128 GC-pressure operations, and
100,000 C await operations per sample to resolve timer granularity for the short
stack-only references. It used five samples on an Intel Core i9-14900HX with
Windows 11 Enterprise and CMake 4.4.4.
The generated project and runtime were Release x64 with static `cxcore`; the
compiler binary was Debug at revision `f24e1cb`, with local working tree changes.
The broad iterator timing range shows why these measurements do not yet support
budgets.

### Windows x64 shared linking

The same generated fixture was rebuilt with `CX_STATIC_LINK=OFF` under CMake
4.4.4/MSVC 19.51 and run five times on the Windows host:

| Operation | Median (range), ns/op | Managed allocations/op | Frame bytes |
| --- | ---: | ---: | ---: |
| 1,000-element synchronous iterator | 311,628 (308,609–322,586) | 2 | 40 |
| Completed await | 2,074 (2,003–2,335) | 8 | 0 |
| Pending await and scheduler drain | 2,058 (1,972–2,256) | 12 | 0 |
| Nested completed await | 2,947 (2,918–3,070) | 12 | 0 |
| Nested pending await and scheduler drain | 2,860 (2,824–2,889) | 19 | 0 |
| Pending await with forced GC | 6,873 (6,567–7,642) | 14 | 0 |
| 1,000-element completed-yield async iterator | 6,137,358 (6,006,317–7,704,335) | 4,008 | 80 |
| Pending async iterator resume | 5,637 (5,596–6,142) | 18 | 96 |

Each GC-pressure sample recorded 16 collections. The generated app build emitted
MSVC C4273 inconsistent-DLL-linkage warnings for generic helper declarations;
the build and benchmark executable still completed successfully. Treat these
single-machine figures as diagnostic only.

### Windows x86 static linking

The fixture also ran as a 32-bit Release executable with `CX_STATIC_LINK=ON`,
using five samples on the same Windows host:

| Operation | Median (range), ns/op | Managed allocations/op | Frame bytes |
| --- | ---: | ---: | ---: |
| 1,000-element synchronous iterator | 362,026 (333,544–376,597) | 2 | 28 |
| Completed await | 2,657 (2,561–2,897) | 8 | 0 |
| Pending await and scheduler drain | 2,433 (2,361–2,490) | 12 | 0 |
| Nested completed await | 3,543 (3,351–3,727) | 12 | 0 |
| Nested pending await and scheduler drain | 3,283 (3,222–3,604) | 19 | 0 |
| Pending await with forced GC | 11,457 (10,917–11,920) | 14 | 0 |
| 1,000-element completed-yield async iterator | 6,715,011 (6,430,201–7,007,359) | 4,008 | 48 |
| Pending async iterator resume | 7,017 (6,392–7,340) | 18 | 52 |

Each GC-pressure sample recorded 16 collections.

### Windows x86 shared linking

The fixture was rebuilt for Win32 with `CX_STATIC_LINK=OFF` under CMake
4.4.4/MSVC 19.51, then run for five samples on the same Windows host. The
benchmark app emitted the same C4273 generic-helper inconsistent-DLL-linkage
warnings as the x64 shared build; linking and execution succeeded.

| Operation | Median (range), ns/op | Managed allocations/op | Frame bytes |
| --- | ---: | ---: | ---: |
| 1,000-element synchronous iterator | 357,640 (347,963–362,236) | 2 | 28 |
| Completed await | 2,506 (2,254–3,109) | 8 | 0 |
| Pending await and scheduler drain | 2,383 (2,221–2,572) | 12 | 0 |
| Nested completed await | 3,487 (3,356–4,189) | 12 | 0 |
| Nested pending await and scheduler drain | 3,199 (3,148–4,008) | 19 | 0 |
| Pending await with forced GC | 8,191 (7,341–9,352) | 14 | 0 |
| 1,000-element completed-yield async iterator | 4,674,506 (4,638,279–4,720,288) | 4,008 | 48 |
| Pending async iterator resume | 6,473 (6,345–6,664) | 18 | 52 |

Each GC-pressure sample recorded 16 collections. Raw samples are in
`cxc/tests/native/results/coroutine-iterator-windows-x86-shared-20261017.csv`.
The fixture and runtime were Release Win32 with shared `cxcore`; the compiler
binary was Debug with local working-tree changes. These are platform/link-mode
measurements on the same machine, not an independent hardware sample.

## Linux x64 Release results

The same generated C fixture and workload also ran under WSL2 Ubuntu 22.04 with
GCC 11.4 and CMake 3.31.12, in static and shared `cxcore` modes. Each mode used
five samples. The shared executable passed `ldd -r`. The WSL run used the same
i9-14900HX host as the Windows run, so it adds platform and link-mode evidence,
not an independent machine sample.

| Operation | Static median, ns/op | Shared median, ns/op | Managed allocations/op | Frame bytes |
| --- | ---: | ---: | ---: | ---: |
| 1,000-element synchronous iterator | 238,840 | 301,148 | 2 | 40 |
| Completed await | 1,479 | 1,914 | 8 | 0 |
| Pending await and scheduler drain | 1,534 | 1,637 | 12 | 0 |
| Nested completed await | 2,320 | 2,432 | 12 | 0 |
| Nested pending await and scheduler drain | 2,048 | 2,093 | 19 | 0 |
| Pending await with forced GC | 9,188 | 8,156 | 14 | 0 |
| 1,000-element completed-yield async iterator | 8,214,439 | 6,776,327 | 4,008 | 80 |
| Pending async iterator resume | 4,408 | 4,961 | 18 | 96 |

The GC-pressure samples each recorded 16 collections. These Linux measurements
remain diagnostic; the static/shared timing differences are not controlled
before/after comparisons. Additional independent machines/toolchains and
performance budgets remain open.

These are diagnostic Windows and Linux results from one host. Independent
machines/toolchains, additional task nesting patterns, and performance budgets
still need evidence before setting acceptance thresholds.

## Reproduce

From a Windows developer shell at the repository root:

```powershell
./cxc/tests/native/run-coroutine-benchmarks.ps1 `
  -Cxc ./cxc/src/bin/Debug/net10.0/CxCompiler.dll `
  -Elements 1000 -Iterations 500 -AsyncIteratorIterations 100 `
  -AwaitIterations 250 `
  -HandAwaitIterations 100000 -GcPressureIterations 128 -Samples 5
```

The script writes a CSV and a text environment report under
`cxc/tests/native/results/`. Use the same machine, power plan, toolchain, and
build settings for comparisons. Avoid concurrent CPU-heavy work and use
multiple runs before drawing conclusions.

For a Windows shared-link run, pass `-KeepBuild`, then configure a separate
Release build against the retained generated project:

```powershell
$buildRoot = 'C:\path\printed\by\the\script'
$generated = Join-Path $buildRoot 'generated'
$sharedBuild = Join-Path $generated 'build-windows-shared'
cmake -S $generated -B $sharedBuild `
  "-DCXCORE_SOURCE_DIR=$PWD\cxcore" -DCX_STATIC_LINK=OFF -DBUILD_TESTING=ON
cmake --build $sharedBuild --config Release --target bench
& (Join-Path $buildRoot '.bin\Release\bench.exe') > windows-shared.csv
```

To run the generated benchmark on Linux, pass `-KeepBuild` and convert the
printed build path for WSL. In WSL, configure and run static, save its CSV, then
repeat with shared linking:

```sh
generated="$(wslpath -a 'C:\path\printed\by\the\script\generated')"
core=/mnt/c/workspace/cx-pl/cxcore
cmake -S "$generated" -B "$generated/build-linux-static" \
  -DCXCORE_SOURCE_DIR="$core" -DCMAKE_BUILD_TYPE=Release \
  -DCX_STATIC_LINK=ON -DBUILD_TESTING=ON
cmake --build "$generated/build-linux-static" --target bench --parallel
"$generated/.bin/bench" > linux-static.csv

cmake -S "$generated" -B "$generated/build-linux-shared" \
  -DCXCORE_SOURCE_DIR="$core" -DCMAKE_BUILD_TYPE=Release \
  -DCX_STATIC_LINK=OFF -DBUILD_TESTING=ON
cmake --build "$generated/build-linux-shared" --target bench --parallel
ldd -r "$generated/.bin/bench"
"$generated/.bin/bench" > linux-shared.csv
```

Use CMake 3.31 or newer for the Linux runtime build.

## Refreshed Release baseline — 2026-10-18

This refresh uses five samples for each of the six supported platform,
architecture, and link combinations. The generated compiler executable was
Debug at revision `f24e1cb` with working-tree changes; each native fixture and
runtime was Release. Windows used MSVC 19.51.36260 and CMake 4.4.4. Linux used
WSL Ubuntu 22.04, GCC 11.4.0, and CMake 3.31.8. Both Linux shared runs passed
`ldd -r`. All six runs were on the same Intel Core i9-14900HX / Windows 11
Enterprise host; WSL results are not independent hardware evidence. Raw samples,
full environment details, and timing ranges are preserved in the linked CSV and
TXT files.

Workloads used 1,000 elements per enumeration, 500 synchronous operations,
100 async enumerations, 100 pending async-iterator resumes, 250 generated await
operations, 100,000 handwritten-C await operations, 128 forced-GC operations,
and five timing samples. This table reports generated-path median ns/op; each
TXT also includes ranges, handwritten-C controls, and all 16 scenarios.

| Release configuration | Sync iterator | Completed async iterator | Pending iterator resume | Completed await | Pending await | Nested completed | Nested pending | Pending await with forced GC |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| Windows x64 static | 376,269 | 4,807,498 | 5,949 | 2,311 | 2,212 | 3,165 | 3,118 | 5,263 |
| Windows x64 shared | 368,531 | 6,995,153 | 5,677 | 2,209 | 2,177 | 2,968 | 2,890 | 6,492 |
| Windows x86 static | 426,917 | 5,264,365 | 6,826 | 2,641 | 2,506 | 3,643 | 3,302 | 8,109 |
| Windows x86 shared | 438,305 | 6,037,085 | 6,619 | 2,498 | 2,332 | 3,536 | 3,276 | 9,273 |
| Linux x64 static | 228,680 | 3,623,062 | 3,322 | 1,227 | 1,137 | 1,773 | 1,642 | 4,184 |
| Linux x64 shared | 327,342 | 8,542,845 | 4,528 | 1,764 | 1,449 | 2,500 | 1,924 | 9,064 |

Measured allocation counts are constant across link modes within an architecture.
The async await relay has no separate generated state frame. Allocation counts
and requested CX allocator payload bytes per operation are:

| Generated scenario | Allocations/op | x86 bytes/op | x64 bytes/op | Frame bytes (x86 / x64) |
| --- | ---: | ---: | ---: | ---: |
| 1,000-element synchronous iterator | 2 | 64 | 80 | 32 / 40 |
| 1,000-element completed async iterator | 4,008 | 69,201 | 121,353 | 48 / 80 |
| Pending async iterator resume | 17 | 417 | 757 | 52 / 96 |
| Completed await | 8 | 144 | 248 | 0 / 0 |
| Pending await | 11 | 240 | 436 | 0 / 0 |
| Nested completed await | 12 | 216 | 372 | 0 / 0 |
| Nested pending await | 17 | 396 | 724 | 0 / 0 |
| Pending await with forced GC, gross | 13 | 8,432 | 8,628 | 0 / 0 |
| Pending await with forced GC, pressure-adjusted | 11 | 240 | 436 | 0 / 0 |

The GC workload adds two known pressure allocations and 8,192 requested bytes
per operation; reports include gross and adjusted totals. Every GC sample recorded
16 explicit collections. Handwritten-C sync/async iteration and await controls
report zero managed allocations in ordinary workloads. The handwritten async
iterator is stack-only and omits Task allocation, interface dispatch, and async
disposal. Generated-path totals combine compiler-generated temporaries with
runtime task/continuation allocations. The test hook observes `CX Memory.Alloc`
requested payloads, not native allocator bookkeeping. The benchmark does not
yet attribute bytes separately to generated code and runtime internals. Treat
the C cases as user-work lower bounds, not allocation-equivalent implementations.
These results refresh the diagnostic baseline but do not establish budgets.

| Configuration | Raw CSV | Environment and summary | Full run log |
| --- | --- | --- | --- |
| Windows x64 static | [`coroutine-g1-windows-x64-static-20261018.csv`](../tests/native/results/coroutine-g1-windows-x64-static-20261018.csv) | [TXT](../tests/native/results/coroutine-g1-windows-x64-static-20261018.txt) | [LOG](../tests/native/results/coroutine-g1-windows-x64-static-20261018.log) |
| Windows x64 shared | [`coroutine-g1-windows-x64-shared-20261018.csv`](../tests/native/results/coroutine-g1-windows-x64-shared-20261018.csv) | [TXT](../tests/native/results/coroutine-g1-windows-x64-shared-20261018.txt) | [LOG](../tests/native/results/coroutine-g1-windows-x64-shared-20261018.log) |
| Windows x86 static | [`coroutine-g1-windows-x86-static-20261018.csv`](../tests/native/results/coroutine-g1-windows-x86-static-20261018.csv) | [TXT](../tests/native/results/coroutine-g1-windows-x86-static-20261018.txt) | [LOG](../tests/native/results/coroutine-g1-windows-x86-static-20261018.log) |
| Windows x86 shared | [`coroutine-g1-windows-x86-shared-20261018.csv`](../tests/native/results/coroutine-g1-windows-x86-shared-20261018.csv) | [TXT](../tests/native/results/coroutine-g1-windows-x86-shared-20261018.txt) | [LOG](../tests/native/results/coroutine-g1-windows-x86-shared-20261018.log) |
| Linux x64 static | [`coroutine-g1-linux-x64-static-20261018.csv`](../tests/native/results/coroutine-g1-linux-x64-static-20261018.csv) | [TXT](../tests/native/results/coroutine-g1-linux-x64-static-20261018.txt) | [LOG](../tests/native/results/coroutine-g1-linux-x64-static-20261018.log) |
| Linux x64 shared | [`coroutine-g1-linux-x64-shared-20261018.csv`](../tests/native/results/coroutine-g1-linux-x64-shared-20261018.csv) | [TXT](../tests/native/results/coroutine-g1-linux-x64-shared-20261018.txt) | [LOG](../tests/native/results/coroutine-g1-linux-x64-shared-20261018.log) |

## Final Milestone 18 refresh — 2026-10-26

The final I2 audit reran the five-sample Release coroutine benchmark on Windows
x64 and x86 static. The current reusable-source layout has these bounded await
costs:

| Scenario | Allocations/op | x86 bytes/op | x64 bytes/op |
| --- | ---: | ---: | ---: |
| Cold reusable-source pending await | 14 | 352 | 652 |
| Warm reusable-source pending await | 11 | 212 | 404 |

The benchmark asserts the architecture-specific totals. These results refresh
named-workload allocation observations; they do not set a general performance
budget or compare timings across architectures.

| Configuration | Raw CSV | Summary report |
| --- | --- | --- |
| Windows x64 static | [`coroutine-iterator-20261026-130157.csv`](../tests/native/results/coroutine-iterator-20261026-130157.csv) | [TXT](../tests/native/results/coroutine-iterator-20261026-130157.txt) |
| Windows x86 static | [`coroutine-iterator-20261026-132048.csv`](../tests/native/results/coroutine-iterator-20261026-132048.csv) | [TXT](../tests/native/results/coroutine-iterator-20261026-132048.txt) |

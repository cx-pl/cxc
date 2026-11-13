# Coroutine performance measurements

The native coroutine benchmark measures synchronous and asynchronous iterator
consumption, completed and pending awaits, and nested task calls. It compares
generated operations with small handwritten C state machines that produce the
same result.

The C reference keeps state on the stack and omits CX task objects, runtime
continuations, and scheduler work. It is a lower-level comparison, rather than
an implementation with equal ownership or dispatch costs.

## Run the harness

From a Windows developer PowerShell at the workspace root, with a built
compiler and CMake available:

```powershell
./cxc/tests/native/run-coroutine-benchmarks.ps1 `
  -Cxc ./cxc/src/CxCompiler/bin/Debug/net10.0/CxCompiler.dll `
  -Elements 1000 -Iterations 500 -AsyncIteratorIterations 100 `
  -AwaitIterations 250 -HandAwaitIterations 100000 `
  -GcPressureIterations 128 -Samples 5
```

The runner builds Release native code. `-Architecture` selects `x86` or
`x64`; `-LinkMode` selects `static` or `shared`. The defaults are Windows
x64 static linking.

For Linux x64 through WSL, select the platform and distribution explicitly:

```powershell
./cxc/tests/native/run-coroutine-benchmarks.ps1 `
  -Cxc ./cxc/src/CxCompiler/bin/Debug/net10.0/CxCompiler.dll `
  -Platform Linux -Distribution Ubuntu -Architecture x64 -LinkMode shared
```

Use CMake 3.31 or newer for the runtime build. `-CxCoreDir` selects a runtime
checkout; `-CMake` selects the native CMake executable. `-KeepBuild` retains
the generated project for inspection. `-OutputCsv` selects an output file;
otherwise, CSV samples and an environment report are written under
`cxc/tests/native/results/`.

## Workloads and counters

The harness includes generated iteration, async iterator advancement,
completed/pending awaits, nested and deep awaits, completion-ratio workloads,
shared pending tasks, value-task conversion, reusable sources, and a pending
await under forced GC pressure.

It reports elapsed time, frame bytes, managed allocation counts and requested
bytes, and collection activity for the supported scenarios. Generated workload
rows include compiler-generated temporaries and runtime infrastructure.

A frame size of zero means a particular relay path has no separate generated
state frame. Task, completion, callback, and scheduler allocations are counted
separately.

An iterator's creation cost and its per-move cost are different measurements.
Likewise, cold reusable-source setup and warmed reuse must be compared
separately. Keep reference and large-struct result cases distinct from scalar
results.

## Interpretation

Use the same machine, power plan, toolchain, compiler/runtime revisions, build
settings, and linking mode for a controlled comparison. Avoid concurrent
CPU-heavy work, repeat runs, and report medians and ranges.

Allocation counts apply to the isolated benchmark and its instrumentation.
Separate user work, captures, external I/O setup, and intentional pressure
allocations from async infrastructure.

Forced collection helps inspect retention and suspended-state rooting; it
does not represent an ordinary application collection schedule. Native stack
usage and assembly inspection can identify frame and copy costs, but their
figures are not runtime stack high-water measurements.

Use the [task representation guide](task-performance-design.md) for consumption
and source-reuse contracts and the
[runtime benchmark guide](../../cxcore/benchmarks/README.md) for memory and arena
workloads. Keep dated run reports, internal acceptance targets, and rollout
comparisons outside the source repository.

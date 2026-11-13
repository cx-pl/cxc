# CX source generators and compiler extension SDK

Configured prebuilt workers run in project-graph compilation before final
binding. The implemented path includes declaration-bound snapshots, bound
annotation targets and constant arguments, a versioned frame protocol, declared
additional files, generated sources, and diagnostics.

Executable workers use the direct snapshot-based `ICxGenerator` contract and
the CX SDK worker runtime. Snapshot protocol version 3 includes base-type
identities, generic and concept constraints, and abstract/final modifiers.
Referenced-project snapshots expose public contracts and omit referenced
source text. Payload codecs use strict UTF-8 and bounded framing.

Snapshot projection reads mutable AST state after declaration binding and
exposes read-only collections. It is not a reusable immutable semantic model.
The architecture and incremental SDK sections below describe the intended
incremental design; generic provider execution, caching, package integration,
and editor support are not part of the configured-worker path.

## Goals and boundaries

Generators are CX programs compiled to native executables for the build host.
They consume immutable compiler snapshots and declared additional inputs,
produce ordinary CX source plus diagnostics, and never call the C backend or
mutate the target compilation. Generated code goes through the same final
binding, safety checks, lowering, C generation, and native build as user code.

The compiler initially remains C#. The C# driver owns parsing, semantic
snapshots, dependency resolution, process lifecycle, and the protocol bridge;
generator authors use only the CX SDK. A target executable contains no
generator-host or .NET dependency. REST transport, JSON/XML parsing, and CXDATA
typed mapping remain owned by their runtime libraries and generators.

## Current compiler behavior

The direct `Compiler.Compile` path parses the input project or source, calls
`SemanticBinder.Bind`, emits a C/CMake project, then optionally builds it. The
project-graph path loads references recursively, compiles dependencies before
consumers, adds referenced compilation contexts, binds each project, and emits
it. Both paths need the same generation stage and must preserve their existing
emit-only/build behavior.

`SemanticBinder.BindCore` merges partial classes, creates implicit
constructors, adds core and project symbols, resolves aliases, base types,
generic constraints, declaration types and annotations, and then binds
initializers and function bodies. It subsequently specializes generics and
lowers iterators and coroutines. Binding mutates declaration objects and
project specialization state; a completed binder pass is therefore not a
read-only incremental snapshot and cannot be reused after generated source is
added.

Annotations are parsed into `AnnotationApplication` values and attached to
declarations. Binding resolves the annotation class, constructor arguments,
target and repeatability without constructing user annotation objects. The
result exposes a fully qualified resolved type name and constant arguments.
This is the existing mechanism that `@Serializable` will use.

Class declarations carry partial status, visibility, bases, generic parameters
and constraints. Function and property declarations carry signature,
visibility, constructor and accessor information, and source spans. These
objects are useful compiler implementation inputs but are mutable and are not
the public SDK contract. The SDK must project only stable immutable values.

## Compilation lifecycle

The generator pipeline is additive and runs once per target project node:

1. Resolve ordinary project references, explicit generator build assets, SDK
   versions, and declared extra files. Do not execute generators during
   restore. Build host generators with host configuration and host RID, separate
   from the target RID and target preprocessor symbols.
2. Parse original target source and combine referenced public API contracts.
   Merge partial source declarations for the read-only snapshot. Resolve
   annotations, declaration signatures, visibility, constraints, constructors,
   properties, and source provenance without binding or lowering method bodies.
3. Discover supported closed-generic requests through a shared compiler
   service. Do not make library generators scan bound expression trees or C
   output. A request has a canonical type/method identity, closed type
   arguments, source location, and owning module. Open generic contracts remain
   definitions unless a generator explicitly diagnoses or requests them.
4. Run every generator against the same immutable snapshot. A generator cannot
   inspect another generator's output in this pass. It can emit declarations
   that refer to another known generated contract; ordinary final binding
   resolves those references. Unsupported dependency cycles are diagnosed.
5. Validate output ownership, hint names, normalized paths, generated source
   size, and collisions. Write complete generated CX files into the
   manifest-owned `.obj/generators/<generator-id>/` directory. Record a
   manifest mapping stable generator/output identities to files and provenance.
6. Create a fresh semantic compilation from original plus current generated
   source. Run complete binding, constructor/definite-initialization/null
   safety, constraints, interface and override validation, specialization,
   coroutine lowering, and ordinary code generation. Generated-source errors
   are compiler errors; never emit partially bound C.
7. Export public generated declarations and ownership metadata with their
   defining project. A consumer uses the referenced library's codec rather than
   generating a duplicate implementation or native symbol.

The current binder is monolithic and mutating, so phase 2 requires a dedicated
declaration snapshot builder or a carefully extracted declaration-binding
phase. The current `GeneratorSnapshotBuilder` only projects declarations after
binding and is a bridge prototype; it must move ahead of lowering and expose
only the subset resolved by that stage. It must share existing resolution
rules rather than inventing a second type system. Final binding must start from
fresh parsed declaration objects;
reusing a prebound or lowered AST would retain stale specialization and synthetic
core state. The refactor must keep the existing non-generator path behavior
unchanged when no generators are configured.

Generation is one pass. There is no unbounded generate/rebind loop and no
registration-order visibility between generators. If a generator needs a
contract from another package, it consumes the referenced public contract or
reports the missing dependency.

## Incremental SDK design

The initial public library is `Cx.Compiler.Generators`. Generator source
projects are normal CX programs that implement `ICxIncrementalGenerator` and
reference the SDK. A generator's compiled native entry program uses the same
protocol as third-party generators; no C# generator API is exposed.

The SDK provides immutable descriptors for:

- syntax candidates with stable kind, text span, file identity, and source
  location;
- selected annotation applications keyed by fully qualified annotation type,
  with bound constant arguments and target identity;
- types, fields, properties, constructors, functions, parameters, bases,
  generic constraints, optionality/nullability, visibility, and source
  provenance;
- referenced public contracts and compiler/target options relevant to a
  generator;
- declared additional text files with normalized identity and content hash;
- closed-generic generation requests with stable owner and use-site identity.

No AST object identity, mutable symbol, native pointer, C# object, or instantiated
annotation crosses the SDK boundary.

Pipelines expose value-based `Where`, `Select`, `Combine`, and `Collect`
operations and register output callbacks. Equality for each descriptor is
defined by canonical identity plus the content that affects downstream output;
source location is tracked separately so a line move can update diagnostics
without invalidating unrelated generated text. Cancellation is passed to
transformations and output callbacks. Output operations are `AddSource` and
`ReportDiagnostic`, with severity, stable diagnostic ID, message, source
location, and optional related locations.

The SDK version is explicit and versioned independently of compiler internals.
An unsupported SDK/compiler protocol version fails with an actionable
diagnostic. Generators cannot suppress compiler diagnostics or authorize access
that handwritten CX could not access.

## Worker protocol

Use a versioned, length-prefixed binary protocol over the worker's standard
input/output. The bootstrap encoder and decoder are hand-written in the SDK and
compiler driver; they do not depend on CXDATA, `@Serializable`, JSON/XML
generation, or compiler C# object serialization.

Each frame contains a fixed magic, protocol version, message kind, request ID,
and checked payload length. Payloads use explicit UTF-8 strings, integer
widths, optional-value tags, ordered arrays, and stable identities. Reject
unknown required fields, invalid UTF-8, overflow, duplicate IDs, truncated
frames, and sizes above configured limits. Initial messages cover handshake,
snapshot, additional-file content, generation request, source output,
diagnostic, cancellation, and completion/failure. Worker stdout is protocol
only; diagnostics or ordinary logs go to stderr.

The host launches a separate executable per configured generator, checks
protocol/SDK/compiler compatibility, drains stdout/stderr, enforces time and
resource limits, sends cancellation, and terminates canceled or stale workers.
Timeout, abnormal exit, malformed protocol, and missing completion are
diagnostics. A failed run never falls back to cached stale output. Process
separation keeps compiler state isolated; it is not a security sandbox.

## Incrementality and output ownership

Start with an in-session graph cache. Cache boundaries contain immutable,
equatable values. Syntax filtering, semantic projection, domain model creation,
and source emission are separate stages. Cache keys include generator, SDK,
and compiler versions; canonical source identities; relevant declaration and
referenced-contract hashes; target/preprocessor/safety options; additional-file
hashes; and declared generator options.

Paths and iteration order are normalized. Output must not depend on timestamps,
machine-specific absolute paths, locale, randomized hashes, process IDs, or
generator registration order. A clean build and a cached build produce
equivalent generated declarations, diagnostics, and native behavior. An
unrelated method-body edit should not invalidate DTO/REST signature output;
renaming or removing an input removes only outputs owned by the current
manifest.

Generated files are inspectable CX source. Filenames derive from generator ID
and canonical output identity. Duplicate hint names, generated symbols, or
collisions with handwritten symbols are errors. Before replacing or deleting a
generated file, verify that the prior manifest owns it; do not clean arbitrary
files in `.obj` or the user workspace. Persistent caches require a later schema,
invalidation, corruption-recovery, and concurrent-writer design.

## Project and package configuration

The current `.cxproj` host accepts explicit prebuilt workers:

```yaml
generators:
  - id: cxdata-json
    executable: tools/cxdata-json-generator
    arguments: [--strict]
    additional_files: [schema.json]
    timeout_seconds: 60
```

`id` owns the output directory, `executable` is resolved from the project or
`PATH`, and additional files must be UTF-8 project-relative paths. The compiler
hashes and sends those files with the declaration snapshot. A worker may emit
only relative `.g.cx` hint paths; outputs are stored under
`.obj/generators/<id>/` (or the selected `--output-dir`) and parsed for this
build before final binding. Only explicitly configured project nodes run
workers; package/runtime references do not execute generators. This initial
configuration does not build host tools, resolve generator packages, or persist
an output manifest.

The completed configuration must also support `cxpm` lock data for generator
versions, SDK compatibility, content hashes, and host assets without executing
workers during restore. Locked/offline builds use resolved assets without
prompting.

Keep build-tool assets and their native dependencies separate from target
runtime/native assets. Support source-distributed CX generator projects built
as host-tool nodes by the current compiler and native toolchain. Prevent a
generator from compiling itself recursively and diagnose dependency cycles,
unsupported host binaries, target-only assets, and SDK mismatches. A generator
package records its host requirements and provenance in build diagnostics and
the generated-output manifest.

The command line and editor use the same project resolver, snapshot builder,
worker protocol, cache keys, and diagnostics. Generated documents retain the
originating annotation/type/call location and support navigation to the
generator input and generated declaration. Unsaved buffers may use in-memory
snapshots without writing stale files into the project's output directory.

## First consumers

CXDATA's `@Serializable` generator reads annotation and type descriptors and
emits companion serializers/deserializers that call format runtime interfaces.
The JSON/XML adapters parse and validate input at runtime. Generator output
contains no yyjson or XML backend types. REST's generator consumes annotated
interfaces and closed `RestClient.For<T>()` requests, emits concrete client
implementations and registration metadata, and dispatches through the REST
runtime. Both generators are ordinary SDK clients; neither needs a compiler
feature-specific emitter.

Generated code follows ordinary constructor, visibility, null-safety, definite
initialization, generic-constraint, interface, and override rules. Non-partial
types can be mapped through accessible members and constructors; generated
code requiring private access needs a legal partial declaration or a diagnostic.
Closed generic requests are discovered by one compiler service and keyed by
canonical closed identity and owner module.

## Current implementation limits

The current executable entry point is the direct `ICxGenerator` path.
Incremental provider execution, source-distributed worker builds, output
manifests, incremental caches, package resolution, editor integration, and
pipeline-level cancellation are not implemented by that path.

Generator output still passes ordinary binding, access checks, lifetime and
type-safety checks, lowering, and native generation. Configuring a worker does
not grant its output any additional language privileges.

Keep delivery plans, progress ledgers, audit results, and acceptance reports
outside the source repository.

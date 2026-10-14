# Reflection metadata for function values

## Current representation

Named delegates and anonymous function values are signature-specific C structs
containing an invocation adapter, an environment pointer, and an optional
multicast list. Invocation-list entries copy that value. The adapter identifies
how to invoke a handler but does not identify its source declaration or expose
its receiver.

CX already exposes `System.Reflection.FunctionInfo` for reflected type members,
but its managed value layout differs from the runtime's `cx_reflection_function`
table entry. Lambda and local-function environments are GC-managed generated
structs rather than `System.Object` instances. These differences make a direct
cast of the adapter or environment to reflection metadata incorrect.

## Proposed API

Function values and named delegate values expose read-only `Method` and
`Target` properties:

- `Method` returns a `FunctionInfo` describing the represented method. A
  lambda or captured local function receives a stable compiler-generated
  function descriptor containing its generated name, signature, and flags.
- `Target` returns the bound class or interface instance for an instance method,
  a boxed snapshot for a struct receiver, and `null` for static or top-level
  functions. A captured lambda or local function exposes its managed closure
  object.
- Each entry returned by `GetInvocationList()` keeps its own method and target
  metadata. On a multicast value, `Method` and `Target` describe the last entry,
  consistent with delegate invocation returning the last handler's result.
- Reading these properties from a null/default function value returns a default
  `FunctionInfo` or `null` target, matching CX's null-safe function-value
  introspection convention. Invocation behavior is unchanged.

The compiler must retain a stable descriptor and target when constructing every
function value, including method groups, lambdas, and local functions. Invocation
list copying, combination, removal, and equality must preserve or compare this
metadata consistently. The ABI may change freely during pre-alpha development.
Update runtime declarations, generated layouts, and dependent CX modules
together when that improves clarity or runtime performance; the ABI version
label is not a compatibility constraint.

The implementation should keep method identity separate from the invocation
adapter: adapters are compiler-generated call paths and are not the reflected
method. A generated descriptor must point to stable declaring-type and
parameter metadata, including for a closed generic method specialization.
Closure environments that are exposed as `Target` must be represented by
GC-managed object-compatible closure instances; a raw pointer to an environment
struct must never be returned as `object`.

## Acceptance

- Compiler tests cover the property types, overload selection, and metadata
  propagation through assignment and invocation-list construction.
- Native tests inspect static/top-level, class-instance, interface/virtual,
  struct, closed generic method, lambda, and captured local-function entries.
- Struct targets report a boxed snapshot; closure targets remain valid under
  garbage collection; multicast entries retain independent method/target data.
- Null/default values return the documented defaults. Existing invocation,
  equality, and multicast order continue to pass.

## Implementation sequence

### Current status

Bucket 55 implements `Method` and `Target` for generated function values and
named delegates. Every invocation-list entry stores its own
`cx_reflection_function` descriptor. Reading `Method` converts the last entry's
descriptor into the public CX `FunctionInfo` value; names, return types, and
parameter types are materialized using CX strings, `TypeInfo`, and
`FunctionParamInfo` arrays. Descriptors cover top-level/static functions,
class and interface method groups, boxed struct methods, closed generic
methods, lambdas, and local functions. Null values return a zero-initialized
`FunctionInfo`.

`Target` remains separate from method identity: static/top-level functions
expose null, class and interface method groups expose their receiver, struct
method groups expose a boxed snapshot, and captured closures expose an
object-compatible managed environment. Multicast `Method` and `Target` describe
the last entry, while copied invocation-list entries retain independent
metadata. The ABI may evolve freely during pre-alpha development, with runtime
declarations and generated layouts updated together.

The cross-project native harness inspects method names and parameter/return
types for top-level, class, interface, struct, closed generic instance, lambda,
and captured local-function values. It also checks multicast and target
behavior. The full compiler suite has 669 passing tests, and the native
entry-point harness passes.

### Focused test order

1. Completed: bind `Method` and `Target` on function types and named delegates,
   including null values.
2. Completed: convert runtime descriptors into CX `FunctionInfo`, including
   closed generic signatures and stable descriptors for methods, lambdas, and
   local functions.
3. Completed: verify boxed struct targets, closure targets, and independent
   metadata on multicast and invocation-list entries.
4. Completed: run the compiler suite and native delegate/reflection harness
   with the function-value layout update.

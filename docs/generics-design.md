# Generic representation (Milestone 4)

Milestone 4 uses **monomorphization**: each reachable closed generic type and
function gets a concrete C declaration and definition. Closed generic types also
get distinct runtime type metadata. Specialization names use deterministic
canonical identities containing the defining module, qualified declaration,
arity, and recursively canonicalized type arguments. Identity does not depend
on source spelling, declaration order, process-local hashes, or build paths.

The binder substitutes type parameters recursively through arrays, nullable
types, and constructed named types before overload selection and call binding.
Explicit and inferred type arguments converge on the same specialization.
Recursive construction is deduplicated by canonical identity. A project owns
its closed definitions and emits each once across imported source modules and
compilation contexts. An imported `Lib.Identity<T>` specialization called from
`App` is emitted once by that project and used by the consumer. Separate-project
ownership and imported project generic definitions are part of Milestone 5's
separate-compilation boundary.

## Supported slices

- Generic type parameters and generic function/method parameters substitute
  through supported signatures, fields, arrays, nested constructed types, and
  reflected members. Primitive type arguments are accepted.
- Top-level generic functions and supported generic methods can be specialized
  from inferred or explicit arguments. Generic methods on generic classes use a
  receiver ABI tied to the closed value layout when needed.
- Supported generic function bodies are: `extern` declarations; returning a
  matching type-parameter value, array, nullable, or nested constructed value;
  selecting between two matching parameters with a boolean `if`; copying a
  parameter into one local and returning it; and assigning another matching
  parameter to that local before returning it. Bodies outside these shapes are
  rejected rather than emitted with unresolved open types.
- Generic classes support empty layouts, reference-backed `T` fields, `T[]`
  fields, and closed value layouts with direct `T` fields. Closed value
  arguments include primitive values and named non-generic structs. Multi-field
  and multi-parameter layouts preserve field order and concrete C types.
- Supported constructors are empty parameterless constructors and constructors
  with one direct assignment per generic field parameter. Direct generic-field
  getters and setters are specialized for closed value layouts. Other fields,
  initializers, bases, virtual/interface dispatch, and constructor/method bodies
  that exceed these patterns remain diagnosed as unsupported.

Closed descriptors carry substituted field and method types and distinct
runtime identities. Native tests exercise concrete size and field offsets,
field read/write, constructor assignment, specialization identity, and generic
function results. Focused substitution and canonical-identity tests were added
before code generation. The latest full compiler suite contains 228 passing
tests; the closed generic field getter/setter fixture also compiles under MSVC
with `/W4 /WX /wd4100` and runs successfully.

The C ABI for *open* generic declarations remains provisional. Do not use that
pointer-compatible representation for closed value layouts, which require
concrete size, alignment, and runtime identity.

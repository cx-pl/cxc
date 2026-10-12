# Built-in arrays and `System.Collections.Array<T>`

## Current behavior

`T[]` uses the compiler-special `ArrayType`, resolved as the named public type
`System.Collections.Array<T>` and lowered to the runtime's
`System.Collections.Array*` ABI object. The public CX API operates over that
same runtime value. Its source
declaration no longer owns a `T[]` field or causes a
wrapper allocation; construction, C parameter/storage types, `Length`, and
indexing use the ABI array object. `Array<T>` has no language indexer of its
own; its public `Get` and `Set` methods lower to built-in indexed access.

## Target model

Make `System.Collections.Array<T>` the public semantic type and treat `T[]` as
its source-level shorthand. Both spellings must resolve to one canonical array
type before overload selection, generic inference, constraints, assignment,
nullability, reflection metadata, and specialization identity are computed.
Nested spellings such as `T[][]` canonicalize recursively to
`Array<Array<T>>`.

Use the public `System.Collections.Array<T>` identity for the C storage type,
type-info symbol, and runtime type hash. The structure remains the same
reference object with length and data fields, allocated and indexed through
`cx_array_new`, `cx_array_at`, and array helpers. `Array<T>` does not contain a
`T[]` field after canonicalization; that would be recursive. ABI version stays
1 as requested, and the in-memory structure layout and helper entry points
remain stable while the type name and hash move to the public identity.

## Source behavior

- `T[]` and `System.Collections.Array<T>` are interchangeable in declarations,
  arguments, returns, assignments, aliases, and generic type arguments.
- `new T[length]` and `new System.Collections.Array<T>(length)` allocate the
  same canonical array value.
- `items[index]` and `items.Get(index)` read the same element; indexed writes
  and `Set(index, value)` mutate the same storage. Existing bounds behavior is
  preserved.
- `Length`, null/default behavior, jagged arrays, `foreach`, reference/value
  elements, generic arrays, and reflection all retain their current semantics.
- `Array<T>` operations are available for values written with either spelling.
  No wrapper allocation or copy occurs when converting between the spellings.

## Implementation order

1. Add semantic tests for interchangeability across locals, parameters,
   returns, generic inference, overload resolution, null/default, and nested
   arrays. These should assert canonical type identity, not only matching C
   output strings.
2. Add a single canonicalization helper used by alias resolution, generic
   substitution/identity, assignability, and reflection naming. Preserve the
   existing array-specific binder and generator paths by making them operate
   on the canonical array type.
3. Keep the public `Array<T>` declaration as the API surface while lowering
   `new Array<T>(length)`, `Get`, `Set`, and array members to the existing
   array runtime operations. Keep the enumerator's `T[]` field as an array
   reference, not as storage owned by `Array<T>`.
4. Update cxcore collection source to use the public spelling, and update
   project references, docs, and generated type metadata. Verify ABI-v1
   declarations and runtime structure remain unchanged.

## Current progress

The compiler-special `ArrayType` is now a resolved `NamedType` for
`cxcore.System.Collections.Array<T>`. The parser still uses it for `T[]`
syntax so the existing array lowering paths remain available, while resolved
`System.Collections.Array<T>` syntax canonicalizes to that same type. Bound
declaration signatures, fields, properties, accessor signatures, delegate
signatures, local symbols, generic substitutions, function symbols, and
expression inferred types therefore carry the public generic type identity.
Nested arrays inside constructed generic types normalize recursively.
The source request on `new Array<T>(n)` remains a `NamedType` long enough to
resolve the public constructor, while its inferred semantic type is the
specialized, resolved `ArrayType`/`NamedType` for `Array<T>`.
`ArrayType.AreEquivalent` remains as a boundary comparison for unresolved source
types and values represented in existing generic identities. A focused generic
identity-body test exposed an early validator that runs before name resolution;
it now recognizes either spelling. `new Array<T>(n)` lowers to
`cx_array_new`; its C type and receiver signatures use
`System.Collections.Array*`; `Get`
and `Set` lower to `cx_array_at`; built-in `T[]` receivers bind the public
`Array<T>` methods, including `GetOrDefault`, `TrySet`, `Fill`, `TryCopyTo`, and
`GetEnumerator`. `Length` uses the ABI getter. The wrapper's
recursive backing field was removed. Native tests assign, return, index, and
mutate shared storage through both `T[]` and `Array<T>` spellings, including
inside the collection suite. Generic functions may return a matching element
through `Get(uint)` with either array parameter spelling. Both spellings resolve
to the same runtime type information entry,
`cxcore.System.Collections.Array.__typeinfo`, whose hash is derived from the
public type identity. Reflection reports `Array` in the `System.Collections`
namespace, and generated C uses the same public identity for storage and
dispatch. The ABI version remains 1, with no change to the structure layout or
runtime helper entry points.

The binder now uses the shared array comparison in type checks, and generic
inference recognizes either spelling recursively. Explicit semantic tests for
overload selection, transparent aliases, null assignment/comparison, and
`default` values now cover both spellings. Runtime reflection invocation
through the public API remains untested.

The compiler suite passes with 623 tests. The native
`System.Collections.Array<T>`/`T[]` fixture also passes with strict native
compilation and runtime checks, including allocation through a closed generic
owner field. Source syntax and constructor lookup retain `NamedType` nodes where
the class declaration is needed; these are lookup artifacts and do not escape
into the inferred semantic array type. Public reflection invocation can invoke
the array `Length` getter, returning a boxed `uint`; wrong argument counts are
rejected. Generic element operations such as `Get` and `Set` are not exposed by
runtime reflection because ABI-v1 array objects carry no element-type metadata.

## Focused acceptance tests

- A function taking `Array<int>` accepts an `int[]`, and a function taking
  `int[]` accepts an `Array<int>`.
- `Array<T>` and `T[]` produce the same canonical generic specialization and
  reflection type name, including for `T[][]`.
- `new Array<int>(n)` and `new int[n]` can be assigned to either spelling and
  share mutations through indexing, `Get`, and `Set`.
- Public `Array<T>` methods can be called through a value declared as `T[]`.
- Generic functions may return `Get(uint)` results from either array spelling.
- Overload selection accepts either spelling; aliases, null, and `default`
  preserve the shared array identity.
- Existing native tests for `Array<int>`, `Array<long>`, and `Array<string>`
  continue to pass without an extra wrapper allocation.
- The ABI-v1 array structure layout and runtime helper entry points remain
  stable; its C and reflection identity is `System.Collections.Array`.

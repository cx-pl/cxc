# Closed generic class array methods

## Scope

The first standard-collection storage primitive is a reference class with a
`T[]` field. Its constructor allocates the array, a read-only property returns
the array length, and instance methods read or write one indexed element. The
compiler now admits this exact array-backed implementation for supported closed
generic instantiations and specializes its methods using the closed element
type.

This slice adds native lowering for that exact shape. It uses the closed type
arguments when choosing the element C type and preserves the built-in array
runtime ABI. It does not generalize arbitrary closed generic class bodies,
allocation expressions, loops, or helper calls. Those are separate follow-up
slices needed for `List<T>` and higher-level collections. The native harness
also compiles the live `cxcore/src/System/Collections/Array.cx` in a temporary
project, so moving that declaration does not silently leave its consumers or
tests behind.

## Acceptance

- A `Buffer<T>` can allocate its `T[]` field from a `uint` constructor
  parameter when closed over supported value and reference element types.
- `Length`, `Get`, and `Set` lower with the closed element type and runtime
  array helpers.
- Closed value instantiations have correctly typed, distinct methods and do not
  emit `_unknowntype_`; reference instantiations retain the shared class layout.
- Generated C builds and runs natively, checking allocation, read/write, and
  length behavior.
- Shared-layout reference instantiations retain their existing ABI and preserve
  reference elements in the managed array buffer.

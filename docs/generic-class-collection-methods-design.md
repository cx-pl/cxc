# Generic class collection method bodies

## Scope

Make the existing `System.Collections.List<T>` implementation executable for
closed value and reference element types. Preserve its dynamic array behavior:
construction starts with capacity four, `Add` grows capacity, and `Get`, `Set`,
`Insert`, `RemoveAt`, and `Clear` operate on the logical count. This is a
generic class body specialization slice, not a replacement collection
implementation or a new language construct.

The binder and generator close constructed object creations in generic
constructor field assignments, such as `Value = new Array<T>(length)`. Each
closed owner gets a closed target type, constructor specialization, and vtable
identity. The native `ArrayFactory<T>` fixture verifies construction and calls
through the resulting field.

The live `List<T>` and `Array<T>` sources now compile and run together. Closed
generic owner methods are emitted with substituted signatures, locals, calls,
and object creations. Closed reflection invokers target those method
specializations instead of unresolved open `List<T>` names. The native fixture
covers growth, get/set, insert, removal, clear, and reference-slot reset for
both `int` and `string` elements. `Array<T>.GetOrDefault` returns `default`
outside its bounds, and `TrySet` returns whether an in-range write occurred;
the native fixture checks both operations for integer and string elements. The
array wrapper also supports `Fill` and range-safe `TryCopyTo`, with native
coverage for integer and reference elements. The
list also has `TrySet`, `TryInsert` (accepting insertion at `Count`), and
`TryCopyTo`; each reports invalid ranges without writing. Native coverage
checks successful and rejected operations for value and reference elements.
source uses typed `T` locals when clearing slots so `default` does not depend on
call-argument target inference.

Closed constructor registration now follows object creations recursively, so a
closed `Stack<T>` or `Queue<T>` constructor can register its `List<T>` owner and
that owner's nested `Array<T>` construction. Closed generic layouts also admit
non-static owner property accessors and constructors that initialize supported
nested collection fields. Native LIFO/FIFO checks now pass for both `int` and
`string` elements. `Stack<T>`, `Queue<T>`, and `LinkedList<T>` also expose
`Clear`; native checks cover clearing non-empty instances, and LinkedList can be
used again after clearing.

`LinkedList<T>` now passes native end-insertion/removal checks for `int` and
`string`, including constructed node links. `Dictionary<string, int>` passes
native lookup, overwrite, capacity growth, removal, and clear checks; its
reference-key constraint uses virtual equality, including content equality for
strings and explicit handling of null keys.
`TryAdd` adds an absent key and leaves the dictionary unchanged when a key
already exists, including content-equal strings and null.
Dictionary iteration now yields constructed `KeyValuePair<TKey, TValue>`
structs in insertion order, exercising closed generic class reflection wrappers
that return constructed generic value types.
`Array<T>`, `List<T>`, `Stack<T>`, `Queue<T>`, and `LinkedList<T>` also support
the structural `foreach` protocol. `List<T>.AddRange` handles self-append,
`ToArray` returns a compact copy, and `CopyTo` copies to a caller-provided
array offset.

## Design constraints

- Lower the original generic class body using the closed owner type arguments;
  do not add a `List<T>`-specific runtime implementation.
- Preserve receiver and argument evaluation order in calls to `Array<T>` and
  `EnsureCapacity`.
- Support the existing `uint` loop variables and conditions, array reads/writes,
  assignments, and local `T` values used by these methods.
- Preserve generic reference and value storage layouts and their runtime type
  identities.
- Keep bounds and non-empty preconditions documented; checked exceptions are
  outside this slice because the library currently defines no such contract.

## Acceptance

- Compile the live cxcore `List<T>` and `Array<T>` sources in one native
  project.
- Exercise add through capacity growth, indexed read/write, insertion,
  removal, and clear for `int` and `string` elements.
- Confirm copied elements stay in source order and removed/cleared references
  are reset to `default`.
- Verify closed reflection invokers call closed method specializations.
- Exercise `LinkedList<T>` end insertion and removal for `int` and `string`.
- Exercise a multi-parameter owner, `Dictionary<string, int>`, through growth,
  lookup, overwrite, removal, and clear.
- Exercise structural `foreach` on `Array<T>`, `List<T>`, `Stack<T>`,
  `Queue<T>`, `LinkedList<T>`, and dictionary entries.
- Exercise native `foreach` through constructed `IEnumerable<T>` values for
  List, Stack, Queue, LinkedList, and Dictionary; exercise ArrayEnumerator
  through `IEnumerator<T>` and structural array `foreach`.
- Exercise list self-append, compact `ToArray`, and offset `CopyTo` for value
  and reference element types.
- Closed generic enumerator metadata now supports ArrayEnumerator,
  LinkedListEnumerator, and DictionaryEnumerator. The core collections expose
  constructed `IEnumerable<T>` contracts, and a same-module native fixture
  verifies built-in `T[]` conversion to `IEnumerable<T>` and interface
  enumeration. Remaining collection acceptance work includes broader dictionary
  key/value combinations. Dictionary comparers are implemented.

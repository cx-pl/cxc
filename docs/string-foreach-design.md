# String foreach

## Scope

Make `string` usable directly in `foreach` without changing the language
grammar or runtime ABI. `String.Length` counts Unicode scalars and `String.Item`
returns the scalar at a zero-based index, so string iteration must use that same
index model rather than walking UTF-8 bytes. The compiler lowers a string
foreach to a single-evaluation collection temporary and an indexed loop using
the existing runtime accessors; it does not add a runtime enumerator object.

## Acceptance criteria

- `foreach (char scalar in text)` and inferred `foreach (var scalar in text)`
  iterate string values directly.
- Iteration yields each ASCII and non-ASCII Unicode scalar exactly once and in
  source order; the number of yielded values equals `String.Length`.
- Empty and null strings produce no values (`String.Length` is zero for both).
- Generated code compiles and executes against cxcore with ABI version 1.

Behavior for malformed UTF-8 is outside this slice.

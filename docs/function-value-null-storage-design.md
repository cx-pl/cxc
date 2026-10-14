# Null storage for anonymous function values

## Scope

`null` is assignable to an anonymous `FunctionType`, whose native C
representation is a struct. Emit a typed zero-value compound literal whenever a
null literal is converted to that function type, including local initialization
and later assignment. Preserve ordinary pointer-like `CX_NULL` output for
reference types.

This closes a lowering mismatch without adding runtime state, changing
function-value layout, or changing ABI v1. A zeroed function value remains the
runtime null value; `GetInvocationList()` returns an empty typed array for it.

## Acceptance criteria

- A null literal can initialize and later assign an anonymous function-type
  local and generates a C value matching the function struct type.
- Native execution can call `GetInvocationList()` on that value and observes an
  empty array.
- Existing named-delegate null behavior remains unchanged.
- Runtime ABI declarations remain at version 1.

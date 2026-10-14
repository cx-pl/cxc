# Composable invocation-list expressions

## Scope

`GetInvocationList()` results must be usable as nested expressions, including
member access such as `value.GetInvocationList().Length`. Temporary identifiers
must not depend on parser source spans: expression trees synthesized or nested
by binding may have no span even when the source call itself is valid.

Use the binder's existing unique temporary counter to name the receiver copy.
Preserve the receiver signature, invocation order, empty-list behavior, and ABI
v1 representation.

## Acceptance criteria

- Named delegates and anonymous function values can access `Length` directly
  on a `GetInvocationList()` call result.
- Existing local-variable invocation-list access remains unchanged.
- Nested access compiles and runs natively; no source location is required to
  generate its temporary name.
- Runtime ABI declarations remain at version 1.

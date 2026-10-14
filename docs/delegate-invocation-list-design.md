# Invocation lists for method values

## Scope

`GetInvocationList()` returns the same typed, single-cast entries for named
delegates and anonymous function values regardless of whether a handler came
from a lambda or a method group. Static methods, instance methods, and closed
generic method values retain their existing invocation and receiver semantics.

The compiler represents these values with the same signature-specific function
value structure and invocation-list node. This feature therefore uses the
existing representation and does not add runtime ABI declarations or change
ABI version 1.

## Acceptance

- Compiler tests cover static and instance method values, return the expected
  one-entry list, and invoke the returned element.
- Native tests cover a top-level generic specialization and a closed instance
  method specialization through `GetInvocationList()`.
- Existing lambda, multicast ordering, null/default, and direct-invocation
  behavior remains covered by the full compiler and native suites.

# Invocation lists for anonymous function types

## Scope

Make `GetInvocationList()` available on both named delegates and anonymous
function-type values. Both use the same runtime function-value representation
and multicast list. The result is an array of the receiver's exact function
type, containing one single-cast value per handler in order. This is a compile-
time API over the existing representation; it adds no runtime object or ABI
change.

Keep the existing behavior for null values (an empty array); a zeroed
`default` function value represents that state in native coverage. Preserve
handler identity, closure environments, invocation order, and last-handler
return values.

## Acceptance criteria

- Anonymous function-type values can call `GetInvocationList()` with no
  arguments; calls with arguments remain diagnosed.
- The result can be indexed and each handler invoked with the original
  signature.
- Native tests cover a multicast lambda value, ordering, and a zeroed
  null/default function value.
- Runtime ABI declarations remain at version 1.

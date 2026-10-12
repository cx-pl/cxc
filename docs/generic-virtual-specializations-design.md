# Generic method specializations and virtual dispatch

## Current behavior

Generic method specializations are selected after ordinary overload resolution
and primary generic constraints. Calls to supported non-virtual methods can then
use a custom body for the exact closed method arguments, with the generic body as
fallback. A specialization declaration cannot target a virtual, override, or
abstract generic method. The binder reports this explicitly. Specializations
are scoped to a primary on the same declaring type; a derived type cannot
declare a same-named closed specialization of an inherited generic method as if
it were an override.

## Dispatch constraint

A virtual call has two independent choices: the runtime override selected from
the receiver's vtable, and the closed generic method arguments selected at the
call site. The current vtable ABI has fixed slots and carries neither closed
generic arguments nor a specialization lookup key. Adding a per-type-argument
implementation therefore cannot be represented by the current slot alone,
especially when the receiver's concrete type is defined in another project.

Keep runtime ABI version 1 unchanged. Under that constraint, do not lower a
specialized virtual call as a direct call: doing so would skip override dispatch.
Likewise, do not place a specialization declaration in an overload set or treat
it as an override.

## Possible implementation direction

If virtual specialization support is pursued, first design a compile-time
dispatch path that preserves both decisions without changing the runtime ABI.
It must work across project boundaries, where the caller cannot enumerate all
derived receiver types. If that cannot be done with ABI 1, keep this feature
unsupported and make the language limitation explicit rather than silently
selecting the wrong implementation.

## Acceptance cases

Before enabling this feature, tests must cover:

- a virtual generic primary with a specialization in its declaring class;
- an override in a derived class, with and without a matching specialization;
- a same-named specialization in a derived type with no local generic primary;
- calls through base-typed and derived-typed expressions, including fallback;
- abstract generic methods and overrides;
- constraints, overloads, recursion, and calls across project references;
- generated C proving runtime override dispatch is preserved for custom and
  fallback bodies;
- native execution with at least two derived runtime types and two closed method
  argument sets.

Until that matrix passes, the semantic diagnostic rejecting virtual, override,
and abstract generic method specializations remains the supported behavior.

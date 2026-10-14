# Constructed interface fields in closed generic reference classes

## Scope

Closed generic reference classes may store a constructed interface whose type
arguments use the class type parameters, such as `IEqualityComparer<TKey>` in
`Dictionary<TKey, TValue>`. A constructed interface value uses the existing
`cx_iface_ref` representation, so the field has the same layout for every
closed type argument and requires no runtime ABI change.

Support the field when the class initializes it from a constructor parameter
with the matching constructed interface type. Keep the current restrictions on
other fields, constructors, and method bodies. This is needed for a closed
`Dictionary<TKey, TValue>` to carry its optional comparer through generic
runtime metadata generation.

## Acceptance criteria

- A closed generic class can store, initialize, and return a constructed
  interface field whose arguments are its own type parameters.
- Generated closed layouts use `cx_iface_ref` for the field and preserve the
  existing interface dispatch table and type identity.
- A null field value and a non-null implementation both work at runtime.
- The native dictionary fixture compiles and passes its comparer, collision,
  and entry-enumeration checks.
- Runtime ABI declarations remain at version 1.

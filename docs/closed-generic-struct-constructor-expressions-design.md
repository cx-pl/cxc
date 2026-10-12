# Built-in expressions in closed generic struct constructors

## Scope

A supported constructor on a closed generic struct may initialize a concrete
field from the existing built-in expression subset. Expressions may use
concrete fields, concrete constructor parameters, and literals. Assignments to
generic fields remain direct copies from a matching type-parameter value.

The constructor remains a straight-line sequence of assignments. Each field is
assigned at most once, every constructor parameter must be used, and unassigned
fields keep their zero or field-initialized value. Calls, overloaded operators,
branches, and other open generic expressions remain unsupported. Runtime ABI
version 1 is unchanged.

## Acceptance

- Bind a closed generic struct constructor that copies a generic parameter to a
  matching generic field and computes a concrete field from a concrete parameter.
- Reject a computed assignment to a generic field and an expression that reads
  a generic field or generic parameter.
- Emit the expression in the closed constructor specialization.
- Verify generated C and native execution.

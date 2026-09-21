# cxc
cx language compiler

## Runtime reference conversions

CX uses C-style syntax for explicit checked reference casts, for example
`(Derived)value`, and `value is Derived` for a run-time type test. Checked casts
support class downcasts, interface-to-class conversions, and interface-to-interface
conversions. Casting `null` produces `null`; a type test against `null` is false.

Generated type information includes declared fields, functions, constructors,
property accessors, parameters, implemented interfaces, nested types, and generic
arity. Metadata is read-only; reflection invocation and dynamic construction are
not currently supported.

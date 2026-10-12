# Compound assignments to concept properties

## Scope

Permit a generic concept-constrained `void` function to apply a built-in
compound assignment to a required read/write property on its constrained
receiver. Support `+=`, `-=`, `*=`, `/=`, and `%=` when the right-hand side is
a supported expression of literals, function parameters, and required property
reads from the same receiver. The getter runs
before the right-hand expression and the setter runs afterward. The receiver
is a generic parameter identifier, so it is evaluated once; struct arguments
retain by-value mutation semantics.

Overloaded operators, indexers, additional statements, and control-flow changes
are outside this slice.

## Acceptance criteria

- A concept must require both a getter and setter for the target property.
- Class and struct implementations support all five built-in compound
  operators with the documented right-hand expression subset.
- Generated code emits getter, operation, then setter, preserving source order.
- Invalid read-only properties and unsupported right-hand expressions are
  rejected.
- Focused compiler tests and native class/struct checks pass.

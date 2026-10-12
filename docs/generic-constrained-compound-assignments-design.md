# Compound assignments in constrained generic bodies

## Scope

Milestone 15 already permits a generic function to assign built-in expressions
to properties and indexers exposed by a class or interface constraint. This
slice extends that body form to built-in compound assignment operators on the
same constrained members. It uses the existing assignment and getter/setter
lowering, and does not add syntax or change runtime ABI 1.

The first supported operators are `+=`, `-=`, `*=`, `/=`, and `%=`. Targets
must be a writable non-indexed property or an indexer on the constrained
receiver. Right-hand sides may contain literals, non-generic function
parameters, and built-in binary or conditional expressions composed from
them. They may also read a property or indexer, or call a method, on the same
constrained receiver. Such calls may take arguments from the literal,
parameter, and built-in-expression subset. Member calls on other receivers,
generic operands, user-defined compound operators, and other targets remain
outside this slice.

## Evaluation and dispatch

The ordinary assignment lowering defines compound-assignment order: evaluate
the receiver and each index once, call the getter, evaluate the right-hand
side, apply the built-in operator, then call the setter. Specialization keeps
class/interface dispatch and struct receiver behavior consistent with existing
constrained property/indexer access. The constrained-body validator admits the
compound form only when its receiver is a generic parameter with a class or
interface type constraint and its right-hand side is in the supported subset.

## Acceptance criteria

- Class-constrained and interface-constrained generic bodies bind compound
  property and indexer assignments.
- Assignment right-hand sides may combine constrained member reads and method
  results with the supported built-in expressions.
- Generated C uses the established getter/operator/setter lowering and retains
  source evaluation order.
- Existing diagnostics continue to reject unsupported compound operators,
  non-constrained receivers, unsupported right-hand expressions, and generic
  body shapes outside this slice.
- Focused semantic and generated-C tests cover the new forms.

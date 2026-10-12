# Computed receivers for generic method forwarding

## Scope

Allow a generic forwarding body to return a call to an inferred generic instance
method when the receiver is a computed class or struct expression. The target
must be an instance method on a non-generic class or struct, and the receiver's
resolved type must be the target method's exact owner. Generic containing
classes, virtual dispatch, and extension methods remain outside this slice.

The forwarding argument remains one caller parameter whose type is the same
function type parameter as the return type. This keeps closed-target inference
and argument conversion unchanged.

## Evaluation and lowering

The receiver expression is emitted exactly once as the first argument to the
closed target method. The forwarded value is a parameter reference and is also
emitted exactly once. A computed receiver with side effects runs once per
forwarding call. Struct lvalues pass their address; an rvalue struct receiver
is copied into a local temporary and the call uses that temporary's address.
This preserves CX by-value semantics. No runtime generic dispatch is introduced.
The target body is still selected per closed caller identity, with the existing
custom-specialization/fallback rule.

The binder validates the expression's resolved owner against the closed
generic target before registering the forwarded call. Unsupported owner or
receiver categories receive the existing focused diagnostic.

## Focused acceptance

- A computed receiver of the exact target class is accepted.
- A computed struct receiver is evaluated once into a temporary.
- A receiver factory with a visible side effect runs once per call.
- Custom and primary fallback targets are selected for different closed caller
  types.
- Generated C includes the receiver expression once in the direct closed call.
- A receiver whose resolved type differs from the target owner is rejected.
- A struct lvalue retains mutation behavior; an rvalue receiver is evaluated
  once and mutation remains local to its temporary.
- The full compiler suite and native forwarding fixture pass.

## Multiple forwarded parameters

A forwarding body may pass several parameters from the caller's matching generic
type-parameter set to the target, in positional order. It still returns one
generic target call, and its substituted return type must match the caller's
return type. Each argument must be a direct parameter reference; computed
arguments, reordering conversions, and arbitrary expressions remain separate
work. Inference and specialization selection use the complete forwarded
argument list. Each parameter is emitted once, and the target is selected for
each closed caller identity.

Focused acceptance covers two generic parameters, an exact custom target for
one closed tuple, primary fallback for another tuple, and direct generated calls.

## Computed concrete arguments

Forwarded arguments may also be literals or pure built-in unary, binary, or
conditional expressions over literals and non-generic caller parameters. At
least one direct caller parameter whose type is a caller type parameter must
remain in the argument list so the generic target can be inferred from the
closed caller. Overloaded operators, calls, assignments, increments, and
expressions that depend on an open type parameter remain unsupported. These
restrictions keep forwarded argument evaluation free of user code and side
effects.

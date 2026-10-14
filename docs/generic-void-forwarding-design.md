# Generic void-call forwarding

## Goal

Allow a primary generic function or supported generic method to consist of one
call to another generic function or method when it has no return value. This is
the side-effecting counterpart to the existing generic forwarding form that
returns one inferred generic call, for example:

```cx
public void Forward<T>(T value) {
    Store(value);
}
```

The forwarded call closes its method arguments for each caller specialization
and selects the same exact replacement or primary fallback as a direct call.
No runtime generic dispatch or ABI change is introduced.

## Initial supported shape

- The caller is a top-level generic function, or a non-static generic method
  on a non-generic class or struct, following the existing forwarding boundary.
- The body contains exactly one expression statement whose expression is a call
  to a generic function or method.
- At least one direct caller parameter has a caller generic type parameter and
  is forwarded unchanged, so method type arguments can be inferred.
- Other arguments may be direct caller parameters or pure built-in expressions
  over concrete caller parameters and literals, matching existing forwarding.
- Existing instance receiver rules apply. The selected callee specialization
  is emitted for each closed caller and recursive forwarding is registered.

Locals, multiple statements, non-generic callees, and forwarding arguments that
depend on an open generic value remain outside this slice. The body must be
rejected during binding if it does not match the supported shape.

## Acceptance criteria

- A generic void caller invokes the closed callee with matching concrete C
  parameter types and emits no `return` expression.
- Exact generic method replacements take precedence, with primary fallback for
  unmatched closed arguments.
- Focused binder and generated-C tests verify unsupported body shapes receive a
  source diagnostic and supported calls contain no open-type placeholders.
- Native coverage verifies side effects for multiple closed type arguments and
  confirms ABI declarations remain at version 1.

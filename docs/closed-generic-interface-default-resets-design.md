# Multiple generic-field resets through constructed interfaces

## Scope

Allow a parameterless `void` method on a closed generic struct to assign
`default` to more than one field whose type contains an enclosing generic type
parameter. The generated interface thunk specializes each field type against
the constructed interface and emits assignments in declaration-body order.

```cx
public interface IResetPair<T> { void Clear(); }

public struct Pair<T> : IResetPair<T> {
    public T First;
    public T Second;
    public void Clear() {
        First = default;
        Second = default;
    }
}
```

Methods with parameters, non-default values, duplicate field assignments, or
other statements remain outside the straight-line form. A separate conditional
reset form allows one boolean parameter and an `if`/`else` with one assignment
per branch; each assignment resets a matching generic field to `default`. It
works directly on a closed struct and through a constructed interface thunk.
The corresponding branch assignment form also assigns a method parameter whose
type matches the target generic field. Existing single-field reset,
matching-parameter setters, and concrete-field mutation forms are unchanged.

## Acceptance criteria

- A compiler test checks the specialized closed-layout assignments and their
  source order.
- The native harness resets multiple generic fields through a constructed
  interface backed by a boxed closed generic struct, then reads both fields.
- Direct and constructed-interface calls verify each branch of the conditional
  reset form and read both fields after mutation.
- Direct and constructed-interface calls verify branch-selected assignments
  from a matching generic parameter.
- Unsupported reset bodies remain rejected during closed-layout validation.

# Conditional generic results through constructed interfaces

## Scope

Allow a closed generic struct to call a method whose return type is the struct's
type parameter and whose body selects between two result arms. The selection may
use a conditional expression or one boolean `if`/`else` with a single return in
each branch. The condition may use concrete fields and non-generic method
parameters through the existing concrete-expression subset. Each result arm
must be a matching `T` field or `default`, and at least one arm must be a
matching field. Other control flow remains unsupported by this form.
The generated interface thunk
specializes the result type and reads fields from the closed value layout.

```cx
public interface IOptionalValue<T> { T Read(bool usePayload) const; }

public struct OptionalValue<T> : IOptionalValue<T> {
    public bool HasValue;
    public T Payload;
    public T Fallback;
    public T Read(bool usePayload) const {
        if (usePayload) { return Payload; }
        else { return default; }
    }
}
```

The method may be called directly on a closed struct or through a constructed
generic interface. Other generic-result method bodies, arbitrary control flow,
and broader generic interface implementations remain unsupported.

## Acceptance criteria

- Focused compiler coverage verifies direct specialization and generated
  closed-layout thunk expressions for both supported selection forms.
- The native harness boxes a closed generic struct and verifies the branch
  result through the constructed interface and a constrained generic caller.
- Unsupported result arms, conditions, and method bodies remain rejected.

# Mixed generic and concrete field mutations through constructed interfaces

## Scope

Allow a `void` method on a closed generic struct to mix writes of matching
generic fields from matching type-parameter parameters with writes to concrete
fields using the existing concrete-expression subset. Direct closed-layout
calls and constructed-interface thunks evaluate assignments in method-body
source order against the closed value layout.

```cx
public interface ICounter<T> { void Set(T value, int count); }

public struct Counter<T> : ICounter<T> {
    public T Value;
    public int Count;
    public int Total;
    public void Set(T value, int count) {
        Value = value;
        Count = count;
        Total = Count + count;
    }
}
```

The straight-line body contains only direct field assignments. Generic fields
receive a matching parameter; concrete fields use literals, concrete
parameters, and concrete field expressions. Boolean `if` and `if`/`else`
shapes permit ordered field assignments; branch conditions and concrete values
use the supported concrete-expression subset. A branch may assign one or more
matching generic parameters to matching generic fields, reset matching generic
fields to `default`, and assign concrete fields from built-in expressions in
the same body, preserving source order. Without an `else`, fields not assigned
by the `if` remain unchanged. Later assignments in a branch may read fields
written earlier in that branch. Other control flow remains unsupported.

## Acceptance criteria

- A compiler test verifies generated assignments for both generic and concrete
  fields.
- Focused coverage checks branch-selected concrete expressions directly and
  through a constructed interface thunk, including source-order dependencies
  between assignments in one branch.
- Conditional branches may write multiple matching generic fields from matching
  generic parameters, and mix those writes with matching-field `default`
  resets; single-arm `if` bodies leave fields unchanged when their condition is
  false. Focused and native coverage checks direct calls and constructed-
  interface dispatch.
- A single branch may mix generic-field writes/resets with dependent concrete-
  field assignments, preserving order in direct calls and the interface thunk.
- The native harness invokes the method directly and through a constructed
  interface backed by a boxed closed generic struct, checking the computed
  concrete field that depends on the preceding write.
- Unsupported field expressions and mixed method bodies remain rejected during
  closed-layout validation.

# Default results from constructed generic interfaces

## Scope

Allow a non-generic method on a closed generic struct to return `default` for
the enclosing type parameter through a direct closed-layout call or a
constructed generic interface. Its parameters may use enclosing type
parameters because the interface thunk closes them against the constructed
interface. Both paths emit the closed result type's default value.

```cx
public interface IOptional<T> { T Empty(T ignored); }

public struct Optional<T> : IOptional<T> {
    public T Empty(T ignored) { return default; }
}
```

Concrete signatures and signatures closed from the enclosing type parameters
are supported; generic method definitions and other return expressions remain
unsupported by this form. Existing field-return and
conditional field/default result forms are unchanged.

## Acceptance criteria

- A compiler test verifies direct and thunk specialization of the default
  result.
- The native harness calls the method directly and through a constructed
  interface backed by a boxed closed generic struct and checks the result.
- Unsupported method shapes remain rejected by closed-layout validation.

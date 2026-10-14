# Array field initializers in closed generic structs

## Scope

Allow an explicit instance constructor on a generic struct to initialize an
array field after the struct is closed. The allocation runs on each constructor
call before the constructor body and uses the closed element type's size:

```cx
public struct Buffer<T> {
    public T[] Items = new T[2u];
    public constructor() {}
}
```

The initial slice supports concrete element types, the owner's type parameter,
and constructed generic elements that already have a supported closed layout.
Lengths are `uint` literals or supported built-in expressions over earlier
concrete fields and literals, matching generic reference-owner initializers.
Earlier field values are read after their own initializer has run, in source
declaration order, before the array allocation is emitted.
Each constructed struct value gets independent array storage. `default(Buffer<T>)`
remains zero-initialized and does not run a constructor or field initializer.
Static initializers, implicit constructor synthesis, and constructors without a
closed instantiation remain outside this slice. Keep ABI declarations at v1.

## Acceptance

- Binder tests accept supported field and explicit constructor forms and retain
  diagnostics for unsupported initializer shapes.
- Generated C closes the array element type and allocates with its concrete
  `sizeof` for each constructor specialization.
- Native checks construct two values with different element types and lengths,
  verify independent storage and writes, and confirm `default` stays null.
- Native checks also derive the array length from an earlier initialized field,
  across multiple instances and closed element types, including reflection-based
  constructor invocation.
- Reflection-based construction of supported closed generic structs executes
  the same initializer through the constructor invoker.

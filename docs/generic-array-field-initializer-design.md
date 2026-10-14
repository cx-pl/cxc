# Concrete array field initializers in closed generic owners

## Scope

Allow a closed generic reference class to allocate a concrete array field in
its instance initializer:

```cx
public class Holder<T> {
    public int[] Values = new int[3u];
}
```

The element type is concrete or exactly the owner type parameter, and the
allocation length is a `uint` literal or a supported built-in expression over
earlier concrete instance fields and literals. Each owner instance receives
its own array allocation before its constructor body. Array elements may be
concrete types, the owner type parameter, or constructed generic class and
struct types whose arguments close with the owner (for example, `Box<T>[]` or
`Pair<T>[]`). Constructed generic struct elements require a supported closed
value layout. Static array initializers for non-generic owners are covered by
[`static-array-field-initializers-design.md`](static-array-field-initializers-design.md);
generic static fields remain outside this slice. Keep ABI v1.

Closed generic class constructors may also allocate a concrete or matching
generic array field, including an array of a constructed generic class or struct,
with a `uint` literal or a supported built-in expression over concrete
constructor parameters and literals. The constructor allocation expression
does not read instance fields.

## Acceptance

- Generated C contains the array allocation in the closed owner constructor.
- Native execution verifies independent owner arrays, their lengths, and writes
  through each array.
- A computed length reads an earlier `uint` field and combines it with a
  literal; generated C and native execution verify the evaluated length.
- A `T[]` field initialized in a generic owner substitutes its closed element
  type in the allocation size; generated C and native execution cover value
  types of different sizes.
- A `Box<T>[]` field initialized in a generic owner closes its element type
  arguments for each owner; generated C and native execution cover independent
  arrays and reference element storage.
- A `Pair<T>[]` field initialized in a generic owner uses the closed struct
  size and stride; generated C and native execution cover adjacent element
  reads and writes for closed layouts with different sizes.
- Existing constructor-based array field allocation remains supported.
- Generated-C and native checks cover constructor lengths computed from
  different values for two closed owner types.

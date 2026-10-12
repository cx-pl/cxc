# Generic property getter local copies

## Scope

This slice permits a narrowly defined local copy in a getter whose property
type is the containing type's generic parameter. The getter must be a
non-static, parameterless getter with one local of the same generic type,
initialized from a matching instance field or a conditional selection between
matching fields and/or `default`, then returned unchanged. The local may declare
`T` explicitly or use `var`, which infers `T`. Field reads may be written
directly or qualified with `this`. For example:

```cx
public class Box<T> {
    public T Value;
    public T Copy { const get { T copy = Value; return copy; } }
    public bool HasValue;
    public T OptionalCopy {
        const get { var copy = HasValue ? Value : default; return copy; }
    }
    public T SelectedCopy {
      const get { var copy = HasValue ? Value : Fallback; return copy; }
    }
}
```

The local has no observable behavior beyond reading the field, so code
generation emits the same field read used by the direct-field getter. This
keeps the shared generic layout and runtime ABI unchanged.

Conditional initialization accepts boolean fields, boolean literals, `!`,
`&&`, and `||`. The getter may not add another statement, initialize from a property or method
call, reassign the local, or return a different value. Those forms need
separate design and closed-layout lowering. When the
owner implements a constructed generic interface property with the same
signature, the thunk lowers the getter as the matching field read.

## Acceptance

- Bind explicit and inferred locals against the open type parameter and verify
  the initializer is a matching instance field or supported conditional field
  selection with matching fields and/or `default`, whether unqualified or
  `this`-qualified.
- Require the return expression to name that local.
- Lower the getter as a direct read of the matching field in generated C.
- Cover direct closed generic class and struct property reads, plus a
  constructed-interface property thunk, in focused compiler and native tests.
- Preserve diagnostics for other generic-result accessor bodies.
- Keep runtime ABI version 1.

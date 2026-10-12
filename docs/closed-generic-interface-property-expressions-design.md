# Computed properties on closed generic structs

## Scope

Allow a closed generic struct property getter to return a constant built-in
expression over concrete fields, parameters, and literals. A closed generic
struct may also have a const `T` getter that selects between matching generic
fields or `default` based on a concrete boolean-field condition; that form is
supported for direct closed-layout reads and constructed generic interface
thunks when the constructed result type is supported. The same concrete getter may
implement a property on a non-generic or constructed generic interface,
provided the getter has a concrete return type and concrete parameters after
interface construction. Interface thunks evaluate it against the closed struct
layout, as they do for concrete-signature methods. Concrete property setters may also assign
a concrete field from a built-in expression over the setter value, parameters,
and literals when written through direct closed-layout storage or through a
constructed generic interface thunk with a concrete signature after
construction. Virtual accessors and broader accessor bodies remain outside
this slice.

```cx
public interface IScore { int Score { const get; } }

public struct Counter<T> : IScore {
    public int Count;
    public int Score { const get { return Count + 1; } }
}
```

The thunk must read `Count` from the boxed closed value, so this works for
interface dispatch without relying on the open generic C layout.

## Acceptance criteria

- Closed generic value layouts accept a concrete-expression const getter on a
  concrete property, whether or not it implements an interface property.
- Constructed generic interface tests cover both a direct field getter whose
  type is the interface type parameter and a computed getter with a concrete
  return type.
- The thunk lowers the expression against the closed layout and preserves the
  receiver's value semantics for concrete results and the supported conditional
  generic-result form.
- Compiler tests cover generated thunk output; native execution verifies the
  computed concrete and generic result values after boxing and interface conversion.
- Compiler tests cover concrete-expression setters on direct closed-layout
  storage and through a constructed generic interface; native execution
  verifies the boxed field mutation.
- Unsupported generic fields and arbitrary accessor bodies remain rejected.

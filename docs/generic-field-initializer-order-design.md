# Closed generic field initializer order

## Scope

Allow a closed generic layout to use a previously declared concrete instance
field in a later concrete field initializer. For example:

```cx
public class Holder<T> {
    public int Seed = 17;
    public int Copy = Seed + 1;
}
```

The reference must target an earlier non-static field on the same declaration;
the referenced field type and destination field type must be concrete after
owner substitution. Literals and the existing built-in unary/binary operators
remain supported. A later initializer may also call a non-generic instance
method on an earlier concrete reference field, with concrete return and
parameter types; its arguments may use the same supported expressions. Emit
initialization in field declaration order, after base initialization and before
the constructor body. Reject self references, forward references, static
fields, generic-typed field references, and calls through any other receiver.
Keep ABI v1.

## Acceptance

- A generated-C test verifies the earlier field is assigned before the later
  initializer reads it.
- Native execution verifies a later initializer can call a method through an
  earlier initialized reference field and receive the computed result.
- Native execution verifies the computed value for two closed owner types.
- Existing closed and owner-dependent constructed-generic object initializers
  continue to work.

# Constructed generic field initializers

## Scope

Allow a closed generic reference class to initialize a concrete reference or
value field with a constructed generic class or struct, such as:

```cx
public class Holder<T> {
    public Seed<int> Value = new Seed<int>(17);
}
```

The constructed target must be a class or struct with an available constructor.
Its type arguments may be fully closed in the declaration or may use type
parameters from the containing generic class; in the latter case, close the
target separately for each owner specialization. Constructor arguments may use
literals and supported built-in expressions over earlier concrete instance
fields, and must conform to the constructor after substitution. Reference
targets are distinct per owner instance; struct targets initialize value
storage. Both are initialized before the owner's constructor body. Keep ABI
declarations at version 1.

This slice does not add expressions that capture the containing instance,
static field initializers, or new generic layout/body forms for the constructed
type. Those require separate closure and initialization rules.

## Acceptance

- A focused compiler test verifies that the nested field initializer is emitted
  before the owner's constructor assignments.
- Native execution constructs two owner instances with separate replacement
  arguments and verifies constructed generic calls and independent field values.
- An owner parameter used in the constructed target closes correctly for two
  different owner types, and each initializer construction runs before the
  owner constructor replaces the field.
- Constructor arguments can read earlier concrete fields and combine them with
  literals using built-in operators.
- Existing non-generic parameterless-object initializers and closed generic
  field default initialization continue to pass.

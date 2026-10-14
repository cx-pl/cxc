# Object creation in closed generic class field initializers

## Scope

Allow a closed generic reference class to initialize a concrete reference field
with a parameterless construction of a non-generic class, for example
`Seed Value = new Seed()`. The initializer executes for each new owner instance,
after base initialization and before the owner's constructor body, following the
existing instance-field order.

Keep the first slice independent of owner type parameters: do not close generic
construction arguments or allow constructor arguments, calls, or struct owner
layouts in this change. Preserve the existing generic owner layout and ABI v1.

## Acceptance criteria

- A closed generic reference owner can contain a field of a non-generic class
  initialized with its parameterless constructor.
- Each owner instance receives a distinct initialized object, and the field is
  ready before the owner constructor body runs.
- Calls with constructor arguments, generic constructions, and the unsupported
  generic field-initializer forms remain diagnosed.
- Native execution and generated-C tests cover the supported form.

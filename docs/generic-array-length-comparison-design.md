# Comparing lengths of matching generic arrays

## Scope

Allow a generic function body to compare the `Length` of two matching generic
array expressions, for example:

```cx
bool SameLength<T>(T[] first, System.Collections.Array<T> second) {
    return first.Length == second.Length;
}
```

The array operands must have the same generic type pattern after canonicalizing
`T[]` and `Array<T>`. The pattern may be a nested array access whose result is
itself a matching generic array. Comparisons may be composed with the already
supported boolean operators and conditional expressions, including null guards.
The comparison is lowered using each array's existing `Length` accessor. An
array length may be compared with a built-in `uint` arithmetic expression
composed from `uint` parameters and non-negative integer literals using `+`,
`-`, `*`, `/`, or `%`. It does not add runtime type metadata or change ABI v1.

## Acceptance

- Focused generated-C tests cover `==`/`!=`, mixed `T[]` and `Array<T>` spellings,
  and nested array accesses.
- Generic array lengths may be compared within supported null-guarded boolean
  expressions.
- Native tests cover equal and unequal lengths for `int` and `long` arrays,
  nested arrays, null operands guarded before access, and both array spellings.
- Existing comparisons against non-negative integer literals and `uint`
  parameters remain supported.
- Computed comparisons cover supported unsigned arithmetic from `uint`
  parameters and literals; native tests exercise `+` and `*`.

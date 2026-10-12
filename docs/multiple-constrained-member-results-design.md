# Results from multiple constrained parameters

## Scope

Constrained generic functions can combine method results from a class- or
interface-constrained parameter, then return the expression directly or
through one explicitly typed local. The current body matcher requires every
member access in that expression to use the same receiver. This slice permits
the expression to combine results from two or more generic parameters when
each receiver has its own class or interface type constraint.

```cx
public int Sum<TLeft, TRight>(TLeft left, TRight right)
    where TLeft : IValueReader
    where TRight : IValueReader {
    return left.Read() + right.Read();
}
```

Each constrained call uses the existing non-generic method requirement and
argument rules. Built-in binary and conditional expressions remain supported;
arbitrary generic expressions, additional statements, and inferred locals are
outside this slice. Struct arguments retain generic by-value dispatch semantics.

## Acceptance criteria

- Direct returns may combine method results from multiple independently
  constrained generic parameters.
- The expression may initialize one explicitly typed local that is returned.
- Interface dispatch works for reference and supported struct arguments.
- Existing argument limits and diagnostics remain in force.
- Focused compiler tests, full suite, and native execution pass.

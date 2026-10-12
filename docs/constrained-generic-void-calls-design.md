# Constrained generic void calls

## Scope

Generic functions can return constrained method results and can mutate a
constrained receiver through property and indexer setters. They do not yet
accept statement-form calls to methods exposed by a class or interface
constraint. This slice allows a `void` generic function to contain one or more
direct calls on a constrained parameter, for example:

```cx
public interface IResettable {
    void Add(int amount);
    void Reset();
}

public void Update<T>(T value, int amount) where T : IResettable {
    value.Add(amount);
    value.Reset();
}
```

Each statement must call a `void` instance method on a constrained generic
parameter, with arguments made from literals, non-generic function parameters,
and supported built-in binary or conditional expressions. Calls may target
different parameters, each of which must have a class or interface type
constraint. Calls can be grouped in blocks and selected by `if`/`else` branches
whose conditions use the same supported built-in expression subset. A class
reference is mutated as usual. A struct argument keeps the
existing generic by-value semantics, so the mutation is confined to the
specialized function's parameter copy.

Calls on unrelated receivers, non-void calls, and local declarations remain
outside this slice. No grammar, runtime ABI, or associated types change is
required.

## Acceptance criteria

- Class- and interface-constrained `void` generic functions can call one or
  more required methods on one or more constrained parameters with supported
  arguments.
- Interface dispatch works for class and supported struct arguments; struct
  mutation follows by-value parameter semantics.
- Supported `if`/`else` branches emit and execute calls on constrained
  parameters.
- A non-void method call or a call on a non-constrained receiver remains
  rejected as an unsupported generic body.
- Focused compiler tests, the full compiler suite, and native execution pass.

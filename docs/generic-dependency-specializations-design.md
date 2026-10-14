# Consumer-project generic function specializations

## Scope

Allow an application to specialize a public top-level generic function imported
from a referenced project. The specialization is declared in the same
namespace and matches the primary's fully closed signature:

```cx
// Library
public T Identity<T>(T value) { return value; }

// Consumer
public int Identity<int>(int value) { return value + 100; }
```

The consumer's body is selected for matching calls and delegate bindings in
that project only. The library output and other consumers remain unchanged.
The primary's overload resolution and generic constraints run before selection.
For a type argument without a consumer replacement, an exported library
specialization remains eligible; otherwise the primary body is used. A
consumer replacement for an exact type argument takes precedence over the
library's replacement for that same argument.

This slice is limited to public top-level generic functions. Extending methods
on dependency types and virtual methods have separate binding and dispatch
constraints; virtual generic specialization remains unsupported until its
runtime override path can preserve ABI v1 semantics.

## Implementation

Register a consumer replacement under the consumer module's specialization
identity while retaining the imported primary as the overload and constraint
authority. During closure, look up the consumer identity before the defining
project identity. Emit the replacement body and its closed C symbol in the
consumer module. Do not export it as a library specialization or mutate
referenced-project metadata.

## Acceptance

- Focused project-reference tests cover a replacement of a library-provided
  specialization, a second closed type argument, and primary-body fallback.
- Native project-reference coverage invokes replacements and fallback under
  both static and DLL linking, including binding a replaced function to a
  delegate.
- Generated C puts replacement bodies in the consumer and leaves the library
  implementation unchanged.
- Runtime ABI declarations remain at version 1.

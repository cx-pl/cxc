# Typed locals for constrained generic member results

## Scope

Constrained generic functions can return a supported property read, indexer
read, method result, or built-in expression combining such results directly.
This slice allows that expression to initialize one explicitly typed local,
which the function then returns. The local type must not contain a function
type parameter. The initializer keeps the existing constrained member
expression rules and uses one constrained receiver parameter.

This adds no grammar or runtime behavior. It uses the existing generic
specialization and constrained class/interface dispatch paths. `var`, multiple
locals, assignments after initialization, and other control flow remain out of
scope.

## Acceptance criteria

- A constrained method result, property/indexer read, or supported built-in
  expression can initialize one explicitly typed local that is then returned.
- Class- and interface-constrained receivers specialize and emit correct C;
  interface dispatch receiver temporaries are declared.
- Mismatched return types, inferred locals, multiple locals, and unsupported
  expression/body forms continue to produce diagnostics.
- Native coverage verifies the result for an interface-constrained receiver.

# Concepts design (Milestone 15)

## Purpose

Concepts provide compile-time structural constraints for generic parameters.
They let a generic body use operators and methods that a concrete type supplies
without adding an interface table or other runtime representation. The initial
scope is the operator-based syntax already shown in
`examples/9. Concepts/Concepts.cx`, now including const instance method
requirements described below.

The declaration and generic-suffix grammar, semantic validation, structural
satisfaction, and specialized operator and method lowering are implemented for
the subset described below. Concepts are compile-time declarations and emit no
runtime types, symbols, reflection records, or dispatch tables.

## Source contract

A top-level declaration has the form:

```cx
concept CEquatable {
    bool operator ==(const CEquatable other) const;
}
```

A concept may inherit from one or more concepts with the `: CBase, COther`
clause. Each base must resolve in the declaration's namespace or imports.
Inherited operator, method, and property requirements apply transitively to
generic arguments constrained by the derived concept. A shared requirement
reached through a diamond is counted once. Independently declared requirements
with the same operator or method signature, or the same property name, across
unrelated bases are rejected, as is a child redeclaration of an inherited
requirement. Inheritance cycles are diagnosed.
Inheritance is compile-time only; generic base concepts are not supported.

Concepts accept operator and method requirement signatures. Operator
requirements declare their token, result type, parameter count and types, and
receiver constness. A property requirement may require a const getter, a
setter, or both:

```cx
concept CHasValue {
    int Value { const get; set; }
}
```

Property requirements currently require a concrete, non-generic type. Method
requirements may be const or mutating, take parameters without default values,
and return concrete types or use their own unconstrained method type parameters
in parameter and return types. Requirements have no body and cannot be used as
a runtime type, class base, interface, field type, or object-construction
target.

Generic functions attach concepts to a named type parameter with the existing
sample syntax:

```cx
public static T Max<T>(T first, T second)
    : T = CEquatable, CComparable {
    return (first > second) ? first : second;
}
```

The suffix may name one or more concepts for a type parameter declared by that
function. Unknown concepts, unknown type parameters, duplicate requirements,
and malformed suffixes are compile-time errors. This syntax is separate from
`where` constraints; a parameter may have both forms.
To constrain more than one generic parameter, repeat the suffix clause, for
example `: T = CComparable : U = CComparable`. Repeating the same concept on
different parameters is valid; listing it twice on one parameter is an error.

Generic classes, structs, and interfaces can use the same suffix after their generic
parameters:

```cx
class Box<T> : T = CReadable { }
interface IReadBox<T> : T = CReadable { T Value { get; } }
```

The compiler checks these requirements when a closed constructed class or
interface type is resolved. This first type-declaration slice validates the
type arguments; it does not yet make the required members available in member
bodies through the enclosing type parameter. That body-binding integration
remains a separate gap.

Concept names used as parameter types inside a concept requirement mean the
same concrete type that is being checked. For a concrete type's operator
declaration, such a parameter is therefore bound as that containing type. This
matches the sample's `Address.operator >(const CEquatable other)`: inside its
body, `other` is an `Address`, so its properties can be accessed normally.

## Binding and specialization

At a generic call, each named concept is checked structurally against the
closed type argument. The type must provide every required operator with the
same token, receiver constness, result type, and parameter types after
substituting each concept parameter with the closed type. Existing exact
operator matching applies; numeric or reference conversions do not satisfy a
requirement. A method requirement must match one public, non-virtual instance
method declared directly on the concrete class or struct, with the same name,
parameter types, and return type. Requirements may declare
unconstrained method type parameters and use them in parameter and return
types. Calls accept explicit type arguments or infer them from arguments; the
implementation must have the same generic arity and exact signature after
substitution. Standard class, interface, `class`, and `struct` constraints are
supported when the requirement and implementation declare equivalent
constraints. Calls validate those constraints for each closed specialization.
Concept constraints on method type parameters are also supported when the
requirement and implementation name the same concepts; implementations must
match them after method type parameter renaming. These constraints use the same
structural satisfaction rules as concept constraints on generic functions.
Requirements may provide literal or qualified enum-member defaults for trailing
parameters; these values are inserted at the concept call site, so the
implementation does not need to repeat the defaults.

Supported generic bodies contain direct return forms, straight-line property
assignment bodies, or a straight-line `void` mutation sequence. The returned expression
may be a required comparison or arithmetic operation between two matching
type-parameter values, a unary `!`, `~`, or `-` operation on one type-parameter value,
or a direct call to a required method on one type-parameter value. Built-in
binary and conditional return expressions may also combine multiple required
method calls on the same receiver with literals and concrete function
parameters. Method-call arguments are checked against the concrete requirement
signature and may use built-in expressions over literals, concrete function
parameters, required property reads, and nested required method calls on the
same receiver. It may
also be a conditional whose condition invokes a required comparison and whose
two result arms return those matching values. Comparison tokens are `==`,
`!=`, `<`, `<=`, `>`, and `>=`; arithmetic tokens are `+`, `-`, `*`, `/`, and `%`.
Comparison and `!` requirements return `bool`; binary arithmetic and unary
`~` or `-` requirements return the constrained self type. Operators and methods resolve
for each closed specialization and emit as ordinary calls to the concrete
type's implementation. Property assignments write required properties on the
constrained receiver; each value must be assignable to the property's concrete
type. Class receivers mutate the referenced object, while struct parameters
follow ordinary value-copy semantics.

Only top-level concepts with const comparison, arithmetic, or `!`/`~`/`-` operator
requirements, instance method requirements with concrete or generic signatures
using supported standard or concept constraints, and
property requirements are accepted. Method requirement constness is matched
exactly by the implementation, so a requirement can describe a mutating method.
Required accessors must be public,
non-static, non-virtual, non-extern, and declared directly on a concrete class
or struct, with an exact property type. Required getters must be const. A
generic body may directly return a required getter or contain only direct
assignments to one or more required properties.
Generic concept constraints are supported on generic functions and methods,
including public concepts consumed from a referenced project. Binary body
forms support two matching `T` parameters; unary forms support one `T`
parameter, and method forms use one `T` receiver followed by parameters that
forward the required method arguments. Generic `void` bodies may contain
assignments to required properties and calls to required methods on the
constrained receiver. Nested blocks and `if`/`else` are supported when
conditions contain boolean parameters, literals, or boolean locals combined
with `!`, `&&`, and `||`. Explicitly typed locals may be initialized from
supported expressions and reassigned. While and do/while loops support the
documented conditions and local-assignment forms. A class- or struct-level
concept `void` method may also use a counted loop that declares one `int`
counter initialized from a supported expression, checks a supported condition,
and steps it with prefix or postfix `++` or `--`; the counter may appear in
supported body expressions. The initializer may instead assign a supported
start expression to an existing `int` local, as in
`for (index = count; index > 0; index--)`. Class- and
struct-level concept `void` methods also support switches on integer, boolean,
character, enum, or string concept expressions with literal or enum labels,
including `null` and `default`; sections may contain the same supported
mutation and call forms. Filters may use supported boolean concept expressions.
These methods may use a bare `return;` for early exit.
They may also use `foreach (var item in values)` over an array of the
constrained type parameter, with supported concept mutations and calls on each
iteration variable. They may also iterate arrays with concrete element types,
such as `int[]`, and use the inferred element local in supported method
arguments and built-in expressions.
Explicitly typed built-in integer, boolean, and string locals may be initialized
from supported concept expressions and reused in later expressions. Integer
locals also support built-in compound assignments and `++`/`--`; boolean locals
may control an `if`, and string locals may select a switch.
An explicitly typed local of the constrained type parameter may copy a method
parameter and make required method calls; the local type closes with the
containing class or struct.
Assignment values and method arguments use
the supported expressions above. Class
receivers mutate the referenced object; struct receivers retain by-value
parameter semantics. Required read/write properties also support built-in
compound assignments (`+=`, `-=`, `*=`, `/=`, and `%=`) when the right-hand
side is a supported expression of literals, function parameters, required
property reads, and calls to required methods on the same receiver. Method-call
arguments may also use nested required method calls and required property reads
on that receiver. Lowering reads the target
property, evaluates the right-hand side (including the required method call),
applies the operator, and writes it back in source order. Overloaded operators
and indexers are not supported. Bare return statements are supported in void bodies;
value-return statements and other statement forms remain unsupported. Method requirements may be
overloaded by parameter signature and generic arity; duplicate signatures are
rejected.
Concept satisfaction currently uses the compiler's exact class/struct operator
lookup and does not apply conversions.

Generic class and struct declarations may attach concept constraints to their
type parameters. Closed constructed types are checked structurally against
those requirements. A non-generic method body in such a type may return one
expression using the required properties, methods, or operators, including
supported built-in binary and conditional combinations; each closed class
specializes that method against its concrete implementation. A `void` member
may also assign required properties and make direct calls to required methods
in source order. Assignment values and method-call arguments may combine
literals and member parameters with required property reads and nested required
method calls using built-in expressions. Required properties also support
built-in `+=`, `-=`, `*=`, `/=`, and `%=` assignments with a literal or member
parameter on the right-hand side. A `while` loop may use a supported concept
expression as its condition and contain these mutations or required method
calls; `do`/`while` supports the same condition and body forms. Other class
member-body statements using class-level concepts remain unsupported.

## Acceptance criteria

- The checked-in Concepts example compiles and runs, with no concept-related
  runtime type emitted.
- Generic specialization succeeds when all requirements are present and have
  exact signatures for both class and struct implementations; missing
  requirements fail at the generic call.
- Multiple generic type parameters can satisfy the same concept independently.
- A public concept and a constrained generic function can be imported from a
  referenced project and satisfied by consumer-defined class and struct types.
- A derived concept requires every inherited operator, method, and property;
  diamond paths deduplicate shared requirements, while duplicate requirements
  from unrelated bases, unresolved bases, cycles, and redeclarations are rejected.
- A public derived concept can be imported from a referenced project and used
  to specialize consumer-defined class and struct implementations.
- The conditional generic body and a direct generic comparison return compile
  to C and run for both class and struct implementations.
- A class-level concept `void` method can use supported `while` or `do`/`while`
  conditions with required method calls or property mutations in its body.
- Generic structs may attach concept constraints to their type parameters and
  specialize supported concept-based methods for each closed struct type.
- A direct generic arithmetic return compiles to a call to the concrete class or
  struct operator implementation.
- Parameterized const and mutating method requirements resolve for class and
  struct arguments; generic bodies can return a required method call with
  forwarded parameters or literal arguments, or issue multiple void
  required-method calls in sequence. Missing or mismatched methods fail
  structural checking.
- Unconstrained generic method requirements accept explicit and inferred type
  arguments and match generic class and struct implementations by exact
  signature; mismatched arity or signatures fail structural checking.
- Generic method requirements enforce equivalent standard constraints on the
  implementation and validate closed method type arguments, including
  interface constraints on struct arguments.
- Generic method requirements enforce equivalent concept constraints and check
  structural satisfaction when closing the method type arguments.
- Optional trailing concept-method parameters use the requirement's default
  literal or enum member at the call site even if the implementation omits its
  own default.
- Overloaded method requirements select the implementation with the exact
  argument signature for class and struct arguments; duplicate and mismatched
  signatures are rejected.
- Getter and setter requirements bind and emit for class and struct arguments;
  missing, non-public, or mismatched accessors fail structural checking.
  Generic setter bodies can assign one or more required properties.
- These bodies can declare explicitly typed locals initialized from literals,
  parameters, required property reads, and supported built-in expressions.
- They can also use while, do/while, and counted for loops with supported
  conditions, assign local variables inside loops, and use break/continue
  statements within loops.
- Foreach loops over concrete array parameters and nested arrays of the
  constrained type parameter are supported when their bodies use the supported
  mutation statements. A constrained type may also appear within an array
  parameter of a required method, such as `Merge(CValue[] values)`; the concrete
  class or struct array type is substituted during checking and specialization.
- A switch over an integer, character, boolean, or enum parameter may contain
  literal labels and mutation statements; break exits the switch.
- A generic `void` body can interleave required property assignments and
  required method calls on the same class or struct receiver.
- Such a mutation body can branch on boolean parameters, literals, and boolean
  locals, including
  nested `if`/`else`, `!`, `&&`, and `||` conditions.
- Generic bodies can return built-in binary expressions combining multiple
  required property reads, such as `value.High - value.Low`, for class and
  struct arguments.
- Generic setter bodies can assign a required property from built-in binary and
  conditional expressions combining required property reads and concrete
  parameters, such as `value.High = value.Low < 10 ? value.Low + delta : delta`.
- Generic unary `!`, `~`, and `-` bodies compile to calls to the concrete
  operator implementation, including for struct arguments.
- A generic body can return a required method result for class and struct
  arguments, forward concrete parameters, and combine multiple required method
  results in a built-in binary or conditional expression; missing, mismatched,
  inherited, virtual, or non-public implementations fail specialization.
- An unrelated generic operator parameter remains unchanged when concepts are
  present in the same compilation unit.

## Deferred scope

Broader property-assignment bodies, associated types, default implementations,
generic base concepts, concept values, and concepts on class or
interface declarations remain outside this slice. They need separate semantics and
acceptance criteria.

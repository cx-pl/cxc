# CX language reference (development edition)

This is the concise reference for the language currently implemented by `cxc`.
The compiler and runtime are in active development; unsupported syntax is rejected
and this document is not a promise of source or binary compatibility. The checked-in
ANTLR grammars are the detailed syntax source. Runtime ownership rules are in
[`standard-library-contracts.md`](../../cxcore/docs/standard-library-contracts.md).

## Source files and names

A source file contains optional `import` directives, an optional `namespace`
declaration, and declarations. Namespaces and imports qualify type and function
names. Identifiers use ASCII letters or `_` initially, followed by ASCII letters,
digits, or `_`. Whitespace, `//` line comments, and `/* ... */` comments are ignored.

Integer literals support decimal, octal (`0...`), hexadecimal (`0x...`), and
binary (`0b...`) forms with optional `u` and `l` suffixes. Floating literals use
a decimal point and optional exponent, with optional `f`, `d`, or `m` suffix.
Character and string literals accept the escapes `\\`, `\'`, `\"`, `\n`, `\r`,
`\t`, `\b`, and `\0`. String storage and character behavior are described in the
runtime contracts.

## Types and declarations

Built-in scalar types are `bool`, `char`, signed `sbyte`, `short`, `int`, `long`,
unsigned `byte`, `ushort`, `uint`, `ulong`, `float`, and `double`. `string` and
`object` are reference types; `ptr` is an opaque pointer. `void` is a return type.
Types can be nullable with `?` and arrays with `[]`. CX also supports enums,
structs, classes, interfaces, properties, constructors, generic declarations,
and generic methods. Generic implementations are specialized for closed types;
only the supported generic body and layout patterns described by compiler
diagnostics are accepted.

Classes support single class inheritance and multiple interfaces. Interfaces can
inherit multiple interfaces. Members can be `public`, `protected`, `private`, or
`internal`. Methods support `static`, `virtual`, `abstract`, `override`, and
`final`; `const` methods/accessors promise not to mutate the receiver through
that member. Properties and indexers use `get` and `set` accessors. Accessors
without bodies on class or struct properties synthesize private backing storage.
Structs and
classes use constructors; constructor initializers can call `this(...)` or
`base(...)`. A class with no declared constructor receives an implicit
parameterless constructor. Declaring any constructor suppresses that implicit
constructor. An implicit derived-class constructor calls the base class's
parameterless constructor.

Classes inherit `Object.ToString()`, which returns the runtime type name as a
`string`. This method is available on class instances and uses runtime type
metadata, including for derived classes.

`Object.Equals(object)` compares reference identity by default, and
`GetHashCode()` returns a hash derived from that identity, so the hash stays
consistent for the same object while it remains alive. All three object methods
are virtual and can be overridden to provide value-based equality and a matching
hash implementation. `String` overrides them: `ToString()` returns the string,
`Equals(object)` compares string contents, and `GetHashCode()` hashes the UTF-8
contents. String `==` and `!=` also compare contents using ordinal,
case-sensitive comparison.

Locals may state their type or use `var` with an initializer. Local functions
are available inside blocks. They can call each other and recurse. A local
function can capture a preceding local variable or parameter by reference, so
later assignments are visible to the local function. Generic local functions
can infer type arguments from a call and use the supported generic function
body patterns and type constraints in [`generics-design.md`](generics-design.md).
They may also declare exact closed specializations in the same block as the
primary local function. The specialization inherits the primary's constraints,
can capture the same enclosing locals, and is selected after ordinary local
function overload resolution; other closed type arguments use the primary body.
Generic functions and methods may also define a closed replacement by listing
closed type arguments after the function name, for example
`public int Adjust<int>(int value) { return value + 1; }`. The compiler
classifies declaration lists after visible types and imports are collected:
unresolved simple identifiers introduce generic parameters, while a visible
type name or any non-identifier type form makes the list a specialization.
Specialization lists must provide the primary's full generic arity and use
closed type arguments; partial lists and arguments that depend on an open
containing type are rejected. Specializations inherit default parameter values
from the primary and cannot declare different defaults.
Calls
that select the matching generic declaration use that body after checking the
primary declaration's constraints; other closed type arguments use the primary
body. The signature must match after substitution. This slice currently applies
to top-level functions and static or instance methods on non-generic classes and
structs. Public specializations on these targets can be consumed through project
references and are emitted by their defining project. Virtual methods and
consumer-defined replacements are not supported yet; declaring a specialization
for a virtual, override, or abstract generic method produces a semantic error.
The virtual-dispatch constraint and acceptance matrix are recorded in
[`generic-virtual-specializations-design.md`](generic-virtual-specializations-design.md).
A primary generic function
or method on a non-static, non-generic class or struct may also return one
inferred generic call that forwards one or more direct parameters whose types
are matching caller type parameters to a generic function or method; the target
resolves separately for each closed caller instance. Instance-method forwarding
supports the current receiver of the same non-generic class or struct, a class
receiver expression whose resolved type exactly matches the target method's
non-generic owner, or a struct receiver expression whose type exactly matches
its owner. Class expressions are evaluated once; struct lvalues retain their
location and struct rvalues are copied to one temporary. Forwarded arguments may
include pure built-in expressions over literals and concrete parameters, as long as a direct
generic parameter argument is also present. Calls, assignments, increments,
overloaded operators and other open-body forms remain unsupported.
Binding a generic function or a supported static/instance generic method to a
delegate uses the expected signature to close it and selects the matching
custom implementation or primary body.
Static generic methods on generic classes can be called through a closed owner,
for example `Utility<string>.Select<int>(value)`. The owner type arguments are
checked against the class constraints and close the method signature before
ordinary overload resolution and specialization selection. Static calls do not
construct an instance of the owner type.
Instance fields are
initialized to zero unless assigned by a constructor. Static fields can have
expressions initialized during module startup, after referenced modules
initialize. A class or struct may declare one `static constructor()`. It cannot
have an access modifier, parameters, or a constructor initializer. Static field
initializers run before static constructors; static constructors then run once in
generated type declaration order and before the executable entry point. A static
constructor does not suppress an implicit instance parameterless constructor.

Compatible `partial` class, struct, and interface declarations merge their
members into one type. The declarations must agree on kind, generic parameters,
visibility, and type modifiers. Base types from each declaration are combined;
the normal inheritance rules still reject conflicting class bases or duplicate
interfaces.

`alias Alias = ExistingName;` declares a transparent alias for a named type.
The target may include type arguments, as in `alias IntBox = Box<int>;`. An alias
may also declare generic parameters and substitute them in its target, as in
`alias BoxOf<T> = Box<T>;`. Aliases may refer to types declared later, in another
source file, or through another alias. They do not create a distinct runtime
type. Alias targets must resolve to named types.

An extension declaration adds instance methods and properties to a class, struct,
interface, or enum. Generic targets use the target's type parameters in the same order,
for example `public extension BoxExtensions<T> : Box<T>`. Concrete array types
such as `int[]` may also be targets for ordinary extension methods. Generic
element patterns such as `T[]`, `T[][]`, and `Box<T>[]` may target extension
methods and close their generic arguments from the receiver. Array extension
properties on concrete and generic array targets must be read-only, and their
property type and accessor parameters must be concrete and independent of the
element type. Concrete and generic array targets support non-mutating unary
and binary extension operators. Concrete and generic array targets may define
`+=`, `-=`, `*=`, `/=`, or `%=` when the operator parameter and return type
match the array target; generic operators close from the receiver type. The
returned array reference is assigned back to the left operand. Array `++` and
`--` operators may also return the matching array target type; postfix forms
preserve the old reference while writing back the returned one.
Public extensions can
target public types from referenced projects and are available to consumers that
import the extension's namespace:

```cx
import Types;
namespace Extensions;

public extension FooExtensions : Foo {
    public int DoubledValue { const get => Value * 2; }
    public void Invert() { Value = -Value; }
}
```

The compiler emits extension methods and property accessors in the declaring
module, with the target value passed as the receiver. Primitive and enum targets
support instance methods and read-only properties with value receivers; their
extension operators support non-mutating unary and binary forms. Primitive and
enum extension `++`, `--`, and compound assignment operators take and return
values; the compiler writes the result back to the assignable operand. Postfix
increment and decrement preserve the previous operand value. For an interface
target, the interface value is passed directly and calls inside the extension
preserve interface dispatch.
Member lookup remains a compile-time operation; no runtime extension wrapper is
created. Extensions cannot declare fields or constructors; target parameters
substitute through method signatures, and method-level generic functions can be
inferred and specialized by consuming projects, subject to the existing generic
body restrictions. An extension may declare an operator for a class, struct, interface,
or enum target; the operator is considered only when the extension's namespace
is imported, and lookup uses the left operand's type with exact parameter
matching.

Generic extension methods on class, struct, interface, enum, primitive, and
concrete or generic array targets may declare exact closed
method specializations using the same keyword-free form as other generic methods. The
compiler selects a matching specialization after resolving the primary method
and checking its constraints; unmatched type arguments use the primary body.
This is supported for ordinary and constructed generic class targets, struct
targets, interface targets including constructed generic interfaces, enum
targets, primitive targets, and concrete or generic array targets.
Generic binary operator methods may also declare closed specializations. The
compiler infers their method arguments from the operands, checks the primary
constraints, and selects a matching custom body; other type arguments use the
primary operator body. This includes generic mutating `+=`, `-=`, `*=`, `/=`,
and `%=` methods: a matching specialization can mutate the struct receiver,
while an unmatched operand uses the constrained primary body.
Generic non-mutating unary operator methods infer their method arguments from
the expected result type, since they have no explicit operands. The result type
must determine every method type argument. The primary constraints are checked
before specialization selection; unmatched closed arguments use the primary
body. Mutating `++` and `--` do not infer method arguments from a result context.

## Expressions, conversions, and overloads

Expressions include member access, calls, indexing, object and array creation,
unary and binary operators, assignment and compound assignment, conditional
expressions, `??`, `is`, and explicit `as` casts. `&&`, `||`, `??`, and the
conditional expression evaluate only the selected operand/branch.

Writable properties and indexers support prefix and postfix `++` and `--`.
Mutation reads through the getter, applies the increment or decrement, and writes
through the setter. The receiver and each index expression are evaluated once.
Prefix expressions produce the updated property value; postfix expressions
produce the previous value. User-defined mutating operators on struct-valued
properties are applied to the retrieved value before it is written back.
For simple property and indexer assignment, the receiver is evaluated first,
then the indices from left to right, then the right-hand side, each exactly once;
the setter runs after those evaluations complete.

Built-in operators require compatible operand types. User-defined operators are
instance methods declared on a class or struct with `operator` followed by the
operator token. Lookup uses only the left operand's type and requires an exact
parameter-type match; implicit conversions do not select an overload. Unary
operators have no explicit parameter, while binary operators have one. On
classes and structs, `++` and `--` have no explicit parameter, return `void`,
and mutate the receiver; prefix expressions produce the updated value and
postfix expressions produce the previous value. Compound assignment operators
such as `+=` have one explicit parameter, return `void`, and cannot be chained.
Primitive and enum extension mutation operators instead return the receiver's
value type, and the compiler writes the returned value back to the operand.
Equality
and comparison overloads return `bool`. A non-generic operator member declared
on a generic class or struct can be used after the type is closed over value
types; its operator parameter and return types are substituted for that closed
type. Operator methods may also declare method-level generic parameters inferred
from the explicit operand; constraints on those parameters are checked and each
closed method is emitted as a specialization. This also works on a generic class
with a supported closed layout. Bodies follow the documented generic function
limits. Supported tokens are `+`, `-`,
`*`, `/`, `%`, `!`, `~`, `&`, `|`, `^`, `<<`, `>>`, `==`, `!=`, `<`, `<=`,
`>`, `>=`, `++`, `--`, and compound assignment forms of the binary operators.
CX strings implement `+` by concatenating their UTF-8 bytes; a null operand is
treated as empty. They implement all four relational operators using ordinal
UTF-8 byte ordering, which preserves Unicode scalar ordering for valid UTF-8
text. A null string sorts before a non-null string when compared through typed
string values.
Compound assignments to properties are supported when both a getter and setter
are available. The compiler evaluates the receiver and index expressions once,
reads the property, applies the compound operator, and writes the result through
the setter. This includes indexed and interface-dispatched properties; named
delegate properties use the same multicast combination and removal behavior as
delegate variables.
Function types use the syntax `delegate(parameter-types) => return-type`, for
example `delegate(int, string) => bool`. Function values can be passed,
stored in locals, and invoked, including directly from an indexed array access.
A named static or top-level function can be used
as a function value. When an expected function type is available, it selects an
overload by exact parameter types and a compatible return type; generic
parameters can be inferred from the expected parameter types.
Lambda expressions use `x => expression` or `(x, y) => expression`, with
optional block bodies. Parameter types come from the target function type or
can be written explicitly, as in `(int x) => x + 1`. Lambdas can capture local
variables, parameters, and `this`. Captured variables are shared by reference,
so updates made by the enclosing function and by the lambda are visible to one
another, even when the lambda outlives the enclosing stack frame. Capture
storage is managed by the runtime garbage collector. A captured class `this`
refers to the same object; a captured struct `this` is copied into managed
closure storage when the closure is created. An unambiguous instance method
can be used as a function value through a receiver, as in `item.Read`; class
receivers retain the object, struct receivers are copied, and interface and
virtual dispatch are preserved. An instance method can also be referenced by
name inside its class instance context, including inside a lambda. An expected
function type selects an instance method overload by exact parameter types and
a compatible return type; generic parameters can be inferred from the expected
parameter types. Static class methods can also be used as function values.
Named delegate types use the declaration syntax `delegate ReturnType Name(Type parameter, ...);`, for example `public delegate int Transform(int value);`.
They are nominal types, so separately declared delegate types remain distinct
even when their signatures match. A lambda or function value with a compatible
signature can initialize a named delegate variable. Delegate values support
`+` and `-` to combine invocation lists or remove the last matching contiguous
sequence, and `+=` and `-=` to update a variable; compound assignment returns
`void`. Invocation runs handlers in order, and a non-void invocation returns
the last handler's result. Equality compares the ordered invocation lists, and
delegates can be compared with `null`. `GetInvocationList()` returns a typed
array containing one single-cast delegate per entry, in invocation order; a null
delegate returns an empty array. Named delegates currently use the compiler's
closure and invocation-list representation, without a common object base type.
For mixed numeric operands, the compiler chooses a common type when one operand
type accepts an implicit numeric conversion from the other; otherwise it
reports an error. The current implicit numeric conversions are widening
conversions among the built-in numeric types; an explicit cast is required
where no such conversion applies. Reference
upcasts and class-to-interface conversions are implicit. Checked downcasts and
interface conversions use explicit casts and fail at runtime if the non-null
value is incompatible; a null cast remains null. Type tests on null are false.

Overload resolution filters candidates by name, supplied arity, generic constraints
supported by the implementation, and argument convertibility. It then selects
the candidate with the better conversion for each argument. If no candidate
applies, or multiple candidates remain equally good, binding reports an error;
ambiguous diagnostics include candidate signatures. Literal and `default` parameter
values and qualified enum member constants are supported on functions, methods,
and constructors. The `default` literal takes its type from the parameter or another
contextual target and zero-initializes that type; it is rejected where no target type
is available. Omitted trailing values are inserted at each call site after overload
selection. Once one
parameter has a default, all following parameters must also have defaults. The
compiler does not use declaration order to break ties.
Generic functions, methods, classes, and interfaces may declare one class
constraint followed by interface constraints, as in `where T : Base, IReadable`.
The special constraints `where T : class` and `where T : struct` require a
reference type or a non-nullable value type, respectively; either can be followed
by interface constraints. Each generic parameter has one `where` clause. A type
argument must satisfy every listed constraint at a function call or when a
constructed generic type is used. Forwarding a generic parameter to a constrained
type also requires compatible constraints on that parameter. Constraints
may name constructed classes or interfaces. Member lookup closes those
signatures against the constraint arguments, including for generic function
parameters whose concrete type is a supported closed generic struct.
Concept suffix constraints can also be declared on generic classes, structs,
and interfaces. Their requirements are checked when a closed constructed type
is resolved. In a generic class or struct member body, a single returned expression may use
the constrained type parameter's required properties, methods, and operators,
including built-in expressions combining those results. The body is specialized
for the closed class arguments. A `void` member may also assign required
properties and make direct required-method calls in source order. Assignment
values and method-call arguments may combine literals and method parameters with
required property reads and nested required method calls using built-in
expressions. Required properties also support built-in `+=`, `-=`, `*=`, `/=`,
and `%=` assignments when the right-hand side is a literal or method parameter.
Other statement and control-flow forms using class-level concept requirements
remain unsupported, except that a `void` member may group supported property
assignments and required method calls in nested blocks and boolean `if`/`else`
branches. Branch conditions may use the supported built-in expression forms
over parameters, required members, and supported locals. A `void` member may
declare and reassign explicitly typed built-in integer, boolean, and string
locals using supported concept expressions. Integer locals also support built-in
compound assignments and `++`/`--`; boolean locals may control conditions, and
string locals may be switch selectors. A
local of the constrained type parameter may copy a method parameter and then
use the same required methods on that local.
`while` loop may use those expressions
as its condition and contain supported property assignments or required method
calls. A `do`/`while` loop has the same supported body and condition forms. A
counted `for` loop may declare one `int` counter initialized from a supported
expression, test it in a supported condition, and step it with prefix or postfix
`++` or `--`; the counter may be used in supported loop-body expressions.
The initializer may also assign a supported start expression to an existing
`int` local. A
bare `return;` may exit a class- or
struct-level concept `void` method early. `foreach (var item in values)` may
iterate an array whose element type is the constrained type parameter or a
concrete element type such as `int`; its body may use the supported concept
mutations and calls on the iteration variable. A
switch statement may select on a supported integer, boolean, character, enum,
or string concept expression and use literal or enum labels, including `null`
and `default`; sections may contain the same supported mutations, calls, nested
blocks, conditionals, and loops. Switch filters may use supported boolean
concept expressions.
make class members available in a narrow generic-function body pattern:
returning a non-generic field or property value, an instance method result, or
a built-in binary/conditional expression combining multiple such method calls
from a parameter with a non-generic class constraint; a class-constrained
function may also write multiple exposed properties or indexers in a
straight-line assignment body. A supported constrained member result or
expression may initialize one explicitly typed local that the function returns.
Those constrained property and indexer writes also accept built-in compound
assignments (`+=`, `-=`, `*=`, `/=`, and `%=`) when their right-hand sides use
the same supported literal, parameter, binary, or conditional expressions.
Assignment values may also read properties/indexers or call methods on the
same constrained receiver, and combine those results with the supported
built-in expressions.
Compound writes evaluate receiver and indices once, read the current value,
apply the built-in operator, then invoke the setter.
Generic `void` functions may contain one or more statement-form calls to `void`
methods on class- or interface-constrained parameters. Calls may target more
than one constrained parameter. Their arguments
may be literals, non-generic parameters, or supported built-in binary or
conditional expressions. Calls execute in source order. Struct arguments keep
the generic function's by-value parameter semantics.
Constrained member-result expressions may combine calls from more than one
independently constrained generic parameter, either as a direct return or as
the initializer of one explicitly typed local that is returned.
With a `class` constraint, a generic function may return a chain of `??`
expressions over matching type-parameter values and `null`. Generic array
parameters may use the same form without a constraint, as in
`T[] FirstNonNull<T>(T[] first, T[] fallback) { return first ?? fallback; }`.
Generic array functions may return an array's `Length` or compare it with an
unsigned literal or parameter. Return predicates may combine multiple such
comparisons with `!`, `&&`, `||`, or a conditional expression, and combine
them with null checks on the same array. Short-circuit operators guard the
length access, as in `values != null && values.Length >= minimum`.
Method arguments may be literals, non-generic function parameters, or built-in
binary/conditional expressions composed from them. A generic function may also return an instance
method result exposed by a non-generic interface constraint, including a
built-in binary/conditional expression combining multiple such calls, using the same
argument restrictions when the inferred type argument is a reference type or a
non-generic struct. Closed generic struct arguments support this method-result
form and interface property reads/writes when their closed value layout and
implemented interface members fit the closed-layout patterns described below.
Interface-constrained property reads and indexers
support direct-return bodies and built-in binary or conditional expressions
composed from those reads; writes support multiple straight-line assignments
for reference-type, non-generic struct, and supported closed generic struct
arguments. Class- and
interface-constrained assignment values may be literals, non-generic function
parameters, or built-in binary/conditional expressions composed from them.
Struct dispatch takes a temporary reference to the specialized value parameter.
Boxed closed generic structs can dispatch through implemented non-generic
interfaces. Constructed generic interfaces support `T`-returning direct field
getters, methods returning `default`, and conditional
generic-result getters and methods, plus `void` slots
that assign one or more matching parameters directly to fields in source order;
a matching parameter may appear at any position in a multi-parameter method.
Generic interface properties whose accessors directly access a matching field
are also supported. Interface methods implemented by a closed generic struct may
also directly return a parameter when its substituted type matches the return
type, including an enclosing generic type argument. Other generic interface member
bodies remain unsupported, except non-generic methods may return `default` for
the enclosing type parameter when called directly on a closed layout or through
a constructed interface with a closed signature,
and concrete-signature methods may return a built-in
expression composed from concrete fields, parameters, and literals, or use a
straight-line body of assignments to concrete fields with built-in expressions
composed from concrete fields, parameters, and literals. These expressions are
lowered against the closed struct layout in source order, and concrete-signature
`void` methods may also use built-in `+=`, `-=`, `*=`, `/=`, and `%=`
assignments on concrete fields when the right-hand side uses that same
expression subset. Parameterless `void` methods may reset one or more matching
generic fields to `default` in
source order. A `void` method with one boolean parameter may use an `if`/`else`
whose branches each reset a matching generic field to `default`, directly or
through a constructed generic interface. Boolean `if` and `if`/`else` methods
may assign one or more matching generic parameters to matching generic fields
and reset matching generic fields to `default`, preserving source order. In an
`if` without `else`, fields not assigned in the body remain unchanged. Branch
bodies may mix these writes and resets with supported concrete-field
assignments; later expressions observe earlier writes in source order.
`if`/`else` branches may also use built-in compound assignments on concrete
fields; later statements in a branch observe the updated values.
A single-arm `if` may contain the same concrete-field assignments and leaves
the fields unchanged when its condition is false.
Concrete-signature `void` methods may also consist of one `while` loop whose
condition uses concrete fields and parameters and whose body contains
straight-line assignments to concrete fields, including built-in compound
assignments. A single `do/while` loop supports the same condition and body
forms, executing the body before checking its condition.
One counted `for` loop form may assign a concrete field in its initializer,
test a concrete condition, update a concrete field in its iterator, and run
straight-line concrete-field assignments in its body.
`void` methods may also mix assignments from matching generic
parameters to generic fields with supported concrete expressions assigned to
concrete fields, directly or through a constructed interface; both paths
preserve source order. A boolean `if`/`else` may assign one or more concrete
fields per branch using the same built-in concrete-expression subset; each
branch's assignments preserve source order. A const getter
on a concrete property may return a built-in expression over concrete fields,
parameters, and literals. When it implements a property from a non-generic or
constructed generic interface, its thunk evaluates the expression against the
closed layout. For a constructed generic interface, the getter signature must
be concrete after construction; generic-result getters support the conditional
field-selection form described below. Other generic-typed computed getters
remain unsupported. Concrete property setters may assign a concrete field from
a built-in expression over the setter value, parameters, and literals through
direct closed-layout storage or a constructed generic interface thunk with a
concrete signature after construction. A generic
parameter with a reference-type or class/interface type constraint, or a matching
generic array parameter, may be compared with `null` using `==` or `!=`.
Return expressions may combine such comparisons with `!`, `&&`, `||`, or a
conditional expression.

## Statements and control flow

The implemented statements include blocks, local declarations and local
functions, expression
statements, `if`/`else`, `while`, `do`/`while`, `for`, `foreach`, `switch`,
`break`, `continue`, and `return`. Exceptions use `try`, typed `catch` clauses,
optional `when` filters, `throw`, `throw;` rethrow, and `finally`. A `finally`
block executes when control leaves its protected region, including return, break,
continue, and exception propagation. A transfer performed inside `finally`
replaces the pending transfer. Switch statements accept integer, boolean, char,
enum, and string selectors. String cases compare string contents using an
ordinal, case-sensitive comparison. A case label may use `when` followed by a
boolean filter; the filter runs only after its case value matches. Switch
expressions use `selector switch { label [when filter] => value, _ => fallback }`.
Labels are literals, enum members, or strings; string labels compare by content,
and `null` is a valid string label. Arm values must have a compatible common
type, and an unfiltered `_` discard arm must appear last.

`foreach` iterates built-in arrays directly. It also accepts a collection with a
zero-argument `GetEnumerator()` whose result has a `bool MoveNext()` method and
either a readable `Current` property or a zero-argument `Current()` method.
The collection expression and enumerator are each evaluated once. `Array<T>`,
`List<T>`, `Stack<T>`, `Queue<T>`, and `LinkedList<T>` currently use the method
form; `Dictionary<TKey, TValue>` yields `KeyValuePair<TKey, TValue>` values in
insertion order. Explicit iteration-variable types are resolved like local
declaration types, including constructed generic types. An interface-typed
`IEnumerable<T>` value can be iterated when its `GetEnumerator()` result
implements `IEnumerator<T>`. The core generic collections still expose their
concrete enumerator types; closed generic enumerator classes cannot yet emit the
required interface metadata.

## Initialization, dispatch, and errors

Value fields have zero-initialized storage unless assigned by a constructor.
Cross-file and cross-project access follows visibility rules, and
project-reference initialization order is deterministic.

Virtual calls dispatch through class vtables. Interface values carry both a
dispatch table and an instance pointer; generated interface tables support
interface upcasts and method/property dispatch. Runtime type metadata supports
type tests, checked casts, and read-only reflection enumeration. Reflection
metadata supports dynamic construction of concrete classes that declare or inherit
a public parameterless constructor, including closed generic reference classes
whose supported layout is shared with their generic declaration.
For non-generic and supported closed generic structs,
`TypeInfo.CreateInstance()` returns a boxed default value with zero-initialized
fields.
Supported primitive, enum, and struct values can be assigned or returned as
`object`; this creates a managed copy. An explicit cast from `object` restores a
value only when its exact runtime type matches. `Object.Type` reports the boxed
value type. Non-generic structs and supported closed generic structs can be
boxed to implemented interfaces; generic interface support follows the limited
member patterns described above.
`TypeInfo.CreateInstance()` returns the new object, or `null` when the type has no
supported public parameterless constructor.
`TypeInfo.CreateInstance(arguments)` invokes a unique matching public constructor
on a non-generic class or on a non-generic or supported closed generic struct.
Constructor parameters may be reference types, including arrays, non-generic
interfaces, or supported primitive, enum, and struct values supplied as
exact-type boxes. Struct results are boxed. Unsupported or ambiguous constructor
signatures return `null`.
`TypeInfo.Invoke(target, name, arguments)` invokes a uniquely matching method
declared on the reflected type or its base classes, preferring the nearest
declaration. It supports non-generic methods with reference-type or non-generic
interface parameters and a reference, interface, or `void` return, including
supported non-virtual methods on closed generic reference classes. Interface
returns are exposed as their underlying object references. Virtual methods
dispatch through the target object's runtime vtable, including overrides in
derived classes. Static methods are supported and ignore `target`. It returns `null` for a missing or
ambiguous method, an unsupported signature, an invalid target, or incompatible
arguments. A `null` result also represents a `void` return or a method that
returns `null`. Interface return values are exposed as their underlying object
references. Primitive, enum, and struct parameters and results are boxed in
`object` values, matched by exact runtime type, and can be explicitly unboxed.
Instance methods on boxed non-generic structs are supported, including mutation
of the boxed payload. Closed generic struct reflection currently supports
non-generic instance methods that assign matching generic parameters directly
to matching fields in source order,
parameterless methods returning a field value, and concrete-signature methods
returning a built-in expression composed from concrete fields, parameters, and
literals; parameterless `void` methods may reset one generic field to
`default`, and other `void` methods may assign built-in expressions to concrete
fields in source order. These methods mutate or read the boxed closed-layout payload.
Generic methods and virtual methods declared on closed generic classes are not
supported for reflection invocation.

Lexical, parse, and semantic errors include source locations where available.
Syntax errors are aggregated when parser recovery can safely continue; semantic
binding is fail-fast to avoid cascaded reports from invalid symbols. A failed
checked cast and an uncaught exception terminate the process with diagnostics.
Bounds failures and allocation failures also terminate according to the current
runtime contract; they are not specified as typed CX exceptions.

## Implementation-defined behavior and current limits

CX currently targets C11 runtimes on Windows (MSVC, x86/x64) and Linux (GCC,
x64); macOS is not implemented. Native behavior affected by C rules, including
signed integer overflow and floating-point edge cases, is not yet specified as
a portable CX guarantee. String data is UTF-8; `char` represents a 32-bit
Unicode scalar, while several `Char` classification helpers are ASCII-only.

The `cxcore` managed-memory collector is non-moving and conservative. It supports
one OS thread per process; native C static roots that retain managed pointers
must be registered, and dynamically unloading a module that registered roots is
unsupported. See the linked runtime contracts and managed-memory design for
ownership, `Memory.Free`, and rooting rules.

The current compiler gaps include reflection-based delegate APIs, `async`/`await`,
concept features outside the operator-, method-, and property-accessor-based
generic subset in
[`concepts-design.md`](concepts-design.md), and the unsupported extension
target and member forms described above. An associated-types direction is
recorded only as a suggestion in
[`associated-types-design.md`](associated-types-design.md); it has not been adopted.
Concept-constrained `void` bodies can
interleave required-property assignments, built-in compound assignments, and required method calls on
one receiver, with `if`/`else` branches controlled by boolean parameters,
literals, or boolean locals and `!`, `&&`, or `||`. These bodies support explicitly typed locals
with initializers built from literals, parameters, required property reads, and
supported built-in expressions. These bodies also support while, do/while, and
counted for loops with supported conditions, plus assignments to local
variables and break/continue statements inside loops; other control-flow forms
remain unsupported in those bodies. Bare return statements are supported for
early exit; returning a value remains unsupported in these void bodies.
Compound property assignments support `+=`, `-=`, `*=`, `/=`, and `%=` with
right-hand expressions composed from literals, function parameters, required
property reads, and required method calls on the same receiver. Method-call
arguments may contain literals, function parameters, supported built-in
expressions, and nested required method calls or property reads on that receiver.
The target property is read
before the right-hand expression and written afterward; overloaded operators
and indexers are not supported by this concept-body form.
Foreach loops over arrays with concrete element types or nested arrays of the
constrained type parameter are also supported. Required method signatures may
use the constrained self type inside an array parameter, with the concrete type
substituted during specialization.
Integer, character, boolean, and enum parameters can also be selected with a
switch statement containing literal labels and mutation statements.
The grammar accepts an `async` modifier, but it has no async semantics; `await`
is not implemented.
Generic support is restricted to the body and layout
patterns in [`generics-design.md`](generics-design.md), including selecting
between matching generic values with a conditional expression, returning an
indexed element from a matching generic array parameter directly or through an
explicitly typed or inferred matching generic local followed by matching local
copies or reassignments, or returning the element from `Get(uint)` on a matching
`T[]` or `Array<T>` parameter, reading its length, and comparing a
matching generic array's length with a non-negative integer literal
(with or without a `u` suffix) or `uint` parameter, assigning a matching value to one indexed element or
copying one indexed element between matching generic array parameters,
constructing and filling a matching generic array, comparing a reference-constrained
generic value or matching generic array with `null`, and copying a matching
generic array with the documented indexed `for` or `while` loop, or a `foreach`
over the source array. Jagged array
types preserve all array ranks, and allocation supports trailing unsized ranks
such as `new int[1][]`. Generic functions may return, assign, or copy nested
array elements, read a
nested array's length, or compare it with a non-negative integer literal (with
or without a `u` suffix) or a `uint` operand; generic property accessors
and using closed generic properties in expressions are supported for the
documented field-backed patterns, including nested constructed class-reference
types. Closed generic layouts may also contain concrete primitive, reference,
enum, or array fields, and non-generic or closed constructed generic structs
stored by value. Closed generic reference layouts also support concrete
primitive, array, and non-generic struct fields, including matching constructor
parameter assignments and direct-field properties. They may derive from one
generic or non-generic base class when the base hierarchy uses only inherited
`System.Object` virtual slots and no interfaces. Each closed class in that
hierarchy must satisfy the shared-reference-layout constraints, and forwarded
type arguments must satisfy each base class's generic constraints. Closed
generic layouts are emitted in dependency order.
Constructed generic struct fields support empty parameterless constructors and
constructor assignments from a matching constructed generic struct parameter;
their type arguments may depend on the enclosing type parameter.
In a supported closed generic struct constructor, one matching parameter may
initialize multiple fields; fields not assigned by the constructor retain their
zero-initialized or expression-initialized values.
Concrete fields in closed generic layouts may have initializers composed of
literals and built-in unary or binary operators; these run before constructor
assignments. Null initializers are supported for array fields and `T` fields with
reference-type or class constraints. A direct `T` field in a supported closed
generic layout may also use the `default` initializer. Constructed generic
fields may also use `default`, for example `Box<T> Value = default;` or
`Pair<T> Value = default;`.
Concrete properties on closed generic structs may use const getters that return
built-in expressions over concrete fields, parameters, and literals. The
expression is evaluated against the closed layout, including for supported
interface dispatch. A concrete property setter may assign a concrete field
from a built-in expression over its value, parameters, and literals when
written through direct closed-layout storage or through a constructed generic
interface thunk when its signature is concrete after construction. A const `T`
getter may conditionally return a matching `T`
field or `default`, or select between two matching `T` fields, based on a
concrete boolean-field expression, when read directly from a closed generic
struct. This generic-result form also supports constructed generic interface
thunks when the constructed result type is supported. A generic-typed getter
may also initialize one explicitly typed or inferred local from a matching
field (written directly or as `this.field`), or select a matching field or
`default` using boolean fields, literals, `!`, `&&`, or `||`, and return that local; this lowers
to the same field read or conditional selection as a direct getter,
including through a constructed-interface thunk. Other generic-typed
computed getters and broader accessor bodies remain unsupported.
Closed generic structs may also return this conditional generic-result form
from a method with concrete parameters, called directly or through a constructed
generic interface; both paths evaluate against the closed layout. Such a method
may express the selection either as a conditional expression or as an `if`/`else`
whose branches each return one matching generic field or `default`. It may also
be called through a generic function constrained by that constructed interface
type. Methods with concrete return types may use the same `if`/`else` shape when
each branch returns a supported built-in expression over concrete fields,
parameters, and literals; direct calls, constructed-interface thunks, and
interface-constrained generic calls are supported.
Initializers with calls, variable references, object creation, overloaded operators, and other
generic-typed field initializers remain unsupported. Broader accessor bodies and
layouts remain limited. Instance field
initializers may use expressions and are evaluated in the constructor after base
initialization. Static field initializers may use expressions; dynamic values
are assigned during module initialization after referenced modules initialize
and before the executable entry point. Within a module, initializers follow the
deterministic generated type order and each type's field declaration order.
Consult compiler diagnostics and tests for the exact supported subset of each
feature.

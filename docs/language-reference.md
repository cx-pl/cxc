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
that member. Properties and indexers use `get` and `set` accessors. Structs and
classes use constructors; constructor initializers can call `this(...)` or
`base(...)`.

Locals may state their type or use `var` with an initializer. Local functions
are available inside blocks. They can call each other and recurse, but cannot
capture local variables or parameters from the containing function. Instance
fields are initialized to zero unless assigned by a constructor; static fields
accept literal or zero initialization. Dynamic static initialization and static
constructor ordering are not implemented.

Compatible `partial` class, struct, and interface declarations merge their
members into one type. The declarations must agree on kind, generic parameters,
visibility, and type modifiers. Base types from each declaration are combined;
the normal inheritance rules still reject conflicting class bases or duplicate
interfaces.

## Expressions, conversions, and overloads

Expressions include member access, calls, indexing, object and array creation,
unary and binary operators, assignment and compound assignment, conditional
expressions, `??`, `is`, and explicit `as` casts. `&&`, `||`, `??`, and the
conditional expression evaluate only the selected operand/branch.

Built-in operators require compatible operand types. User-defined operators are
instance methods declared on a class or struct with `operator` followed by the
operator token. Lookup uses only the left operand's type and requires an exact
parameter-type match; implicit conversions do not select an overload. Unary
operators have no explicit parameter, while binary operators have one. `++`
and `--` have no explicit parameter, return `void`, and mutate the receiver;
prefix expressions produce the updated value and postfix expressions produce
the previous value for structs. Compound assignment operators such as `+=`
have one explicit parameter, return `void`, and cannot be chained. Equality
and comparison overloads return `bool`. Operator extension methods and generic
operator declarations are not implemented. Supported tokens are `+`, `-`,
`*`, `/`, `%`, `!`, `~`, `&`, `|`, `^`, `<<`, `>>`, `==`, `!=`, `<`, `<=`,
`>`, `>=`, `++`, `--`, and compound assignment forms of the binary operators.
Function types use the syntax `delegate(parameter-types) => return-type`, for
example `delegate(int, string) => bool`. Function values can be passed,
stored in locals, and invoked. A named function can be used as a function value
when its name resolves to one non-generic static or top-level overload.
Instance-method values, lambdas, closures, and delegate object semantics are
not implemented. For mixed numeric operands, the compiler chooses a common type
when one operand type accepts an implicit numeric
conversion from the other; otherwise it reports an error. The current implicit
numeric conversions are widening conversions among the built-in numeric types;
an explicit cast is required where no such conversion applies. Reference
upcasts and class-to-interface conversions are implicit. Checked downcasts and
interface conversions use explicit casts and fail at runtime if the non-null
value is incompatible; a null cast remains null. Type tests on null are false.

Overload resolution filters candidates by name, arity, generic constraints
supported by the implementation, and argument convertibility. It then selects
the candidate with the better conversion for each argument. If no candidate
applies, or multiple candidates remain equally good, binding reports an error;
ambiguous diagnostics include candidate signatures. Default parameter values
are supported. The compiler does not use declaration order to break ties.

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
Labels are literals or enum members, arm values must have a compatible common
type, and an unfiltered `_` discard arm must appear last.

## Initialization, dispatch, and errors

Value fields have zero-initialized storage unless assigned by a constructor.
Cross-file and cross-project access follows visibility rules, and
project-reference initialization order is deterministic.

Virtual calls dispatch through class vtables. Interface values carry both a
dispatch table and an instance pointer; generated interface tables support
interface upcasts and method/property dispatch. Runtime type metadata supports
type tests, checked casts, and read-only reflection enumeration. Reflection
invocation and dynamic object construction are not implemented.

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

The current compiler gaps include lambdas, closures, instance-method function
values, delegate object semantics, `async`/`await`, concepts, extension
declarations, and `typedef` declarations. The grammar accepts an `async`
modifier, but it has no async semantics; `await` is not implemented. Generic
support is restricted to the body and layout
patterns in [`generics-design.md`](generics-design.md); generic property accessors
and using generic properties in expressions remain limited. Field initializers
are limited to literals, and dynamic static initialization is not available.
Consult compiler diagnostics
and tests for the exact supported subset of each feature.

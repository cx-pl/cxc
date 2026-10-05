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

Locals may state their type or use `var` with an initializer. Fields currently
support literal or zero initialization for static storage. Compatible partial
declaration merging is not implemented. A complete declaration list and feature
limits are enforced by the compiler rather than silently approximated.

## Expressions, conversions, and overloads

Expressions include member access, calls, indexing, object and array creation,
unary and binary operators, assignment and compound assignment, conditional
expressions, `??`, `is`, and explicit `as` casts. `&&`, `||`, `??`, and the
conditional expression evaluate only the selected operand/branch.

Numeric operators require numeric operands. For mixed numeric operands, the
compiler chooses a common type when one operand type accepts an implicit numeric
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

The implemented statements include blocks, local declarations, expression
statements, `if`/`else`, `while`, `do`/`while`, `for`, `foreach`, `switch`,
`break`, `continue`, and `return`. Exceptions use `try`, typed `catch` clauses,
optional `when` filters, `throw`, `throw;` rethrow, and `finally`. A `finally`
block executes when control leaves its protected region, including return, break,
continue, and exception propagation. A transfer performed inside `finally`
replaces the pending transfer.

## Initialization, dispatch, and errors

Value fields have zero-initialized storage unless assigned by a constructor.
Static fields currently have literal or zero initializers; there is no dynamic
static constructor ordering. Cross-file and cross-project access follows
visibility rules, and project-reference initialization order is deterministic.

Virtual calls dispatch through class vtables. Interface values carry both a
dispatch table and an instance pointer; generated interface tables support
interface upcasts and method/property dispatch. Runtime type metadata supports
type tests, checked casts, and read-only reflection enumeration.

Lexical, parse, and semantic errors include source locations where available.
Syntax errors are aggregated when parser recovery can safely continue; semantic
binding is fail-fast to avoid cascaded reports from invalid symbols. A failed
checked cast and an uncaught exception terminate the process with diagnostics.
Bounds failures and allocation failures also terminate according to the current
runtime contract; they are not specified as typed CX exceptions.

## Implementation-defined behavior and current limits

CX currently targets C11 runtimes with fixed-width CX scalar typedefs. The only
implemented platform runtime is Windows. Native behavior affected by C rules,
including signed integer overflow and floating-point edge cases, is not yet
specified as a portable CX guarantee. String data is UTF-8; `char` represents a
32-bit Unicode scalar, while several `Char` classification helpers are ASCII-only.
Heap ownership is manual and provisional pending the managed-memory milestone.

The compiler rejects unsupported language constructs instead of defining their
behavior. Async/await, delegates, operators, concepts, and several grammar
productions remain unimplemented. Consult compiler diagnostics and the tests for
the exact supported subset of a feature.

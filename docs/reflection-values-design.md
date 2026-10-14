# Reflection invocation for value types

## Current boundary

`TypeInfo.Invoke` takes an `object[]` and returns `object`. The array stores
managed object references, while CX primitives and structs are represented as
inline C values. Casting value bits to `cx_ptr` is not a valid bridge: zero is
also `null`, a `long` does not fit in a pointer on 32-bit targets, and the
runtime cannot distinguish overloads from an untagged pointer.

Reflection supports reference and interface parameters/returns, `void` returns,
and boxed primitive, enum, and struct parameters/returns on methods declared by
classes, supported non-virtual methods on closed generic reference classes,
plus instance methods on boxed non-generic structs. On boxed closed
generic structs, it supports non-generic instance methods that assign matching
generic parameters directly to fields in source order, even when other
parameters precede or follow them, or a parameterless method that returns a
field.
It also supports a parameterless `void` method that resets one generic field
to `default`.
Concrete-signature methods may also return a built-in expression composed from
concrete fields, parameters, and literals; their parameters and value results
use exact-type boxes. `void` methods may also perform straight-line assignments
to concrete fields using built-in expressions over concrete fields, parameters,
and literals. Other generic struct method bodies and generic method definitions
remain unsupported. Reflected construction of generic structs without supported closed
layouts also remains outside the implementation.

Managed boxing, checked explicit unboxing, and value reflection invocation are
implemented: assignment or return of a supported
value type to `object` copies its bytes into a managed box, explicit casts from
`object` to a supported value type validate the exact runtime type, and
`Object.Type` reports the boxed value's type. Reflection invocation matches
primitive, enum, and struct parameters by exact runtime type, unboxes them for
generated calls, and boxes primitive, enum, and struct returns. The box payload is aligned and
reachable through the existing private `Object._box` field. These helpers require
runtime ABI 1. Invoking methods on boxed structs is supported.
`TypeInfo.CreateInstance()` also returns a boxed, zero-initialized value for
non-generic and supported closed generic structs. A boxed non-generic struct
can be checked-cast to an implemented interface; interface dispatch uses its
boxed payload while the box remains the owning managed object. Implicit
conversion from a non-generic struct expression to an implemented interface
also boxes the value. Supported closed generic structs also dispatch through
implemented non-generic interfaces. They can also dispatch through a constructed
generic interface when `T`-returning slots are direct field getters and `void`
slots taking `T` directly assign it to a matching field. Generic interface
properties are also supported when their accessors directly access a matching
field. Concrete-signature methods also support direct matching-field
getters/setters or literal return bodies. The
`arguments` overload can invoke public constructors on
non-generic and supported closed generic structs, matching supported value
arguments by exact boxed runtime type and boxing the constructed result.
Other generic interface members remain unsupported.

## Proposed behavior

Keep the `TypeInfo.Invoke(target, name, arguments)` surface. Value types placed
in an `object` location are boxed into a managed object that records the exact
closed CX value type and stores an aligned copy of its payload. A value result
from `Invoke` is boxed the same way. Callers explicitly cast the returned
`object` back to the expected value type; an incompatible cast follows the
language's checked-cast failure behavior.

Reflection matching is exact for value-type parameters. It does not apply
numeric conversions or user-defined conversions. Existing reference and
interface matching keeps its assignability rules. A boxed struct can match an
interface parameter only when that struct implements the interface; the
generated invoker builds the interface reference over the boxed payload for the
duration of the call.

Invoking an instance method on a boxed struct uses the payload as its receiver.
Mutating methods update the stored boxed value. Const methods receive a const
payload pointer. Primitive methods and struct methods use their ordinary CX
method declarations and metadata.

## Runtime and compiler representation

- Add a managed box header with the normal object runtime identity, the exact
  closed value-type metadata, payload size, and payload offset. The offset must
  satisfy the alignment of every supported primitive and closed struct. The
  initial header and payload allocation is implemented; unusually over-aligned
  user structs remain to be validated.
- Generate or register a box descriptor for each reflected primitive and
  supported closed struct type. Descriptors provide box, unbox, and payload
  access operations; no user code treats the payload as an object pointer.
- Add implicit boxing when a primitive or struct is assigned to `object`,
  including an `object[]` element. Add checked explicit unboxing from `object`
  to a primitive or struct. Do not add implicit numeric conversions during
  reflection matching.
- Extend reflection invocation metadata so an invoker can identify value
  parameters and returns. Runtime matching compares the boxed value type with
  the declared parameter type before calling generated code.
- Generated invokers unbox arguments into correctly typed C temporaries, call
  the method through the existing static, instance, or virtual path, then box
  non-void value results. `void` still returns `null`; reference and interface
  results retain their existing behavior.
- Keep boxes managed and visible to the conservative collector. Payloads that
  contain references must remain scannable, and box allocation must use the
  runtime allocator. If this changes public runtime layouts, increment the C
  ABI and update generated-code compatibility checks.

Struct construction through `TypeInfo.CreateInstance` is a follow-up slice. It
can reuse the same box descriptor and invoker path, but should not be enabled
until method invocation and value casts are stable.

## Acceptance criteria

- `object value = 42` and `object[] values = { 42 }` produce managed boxes;
  explicit `(int)value` restores the exact value, and unboxing a different
  primitive or struct type fails predictably.
- Reflection invokes instance and static methods with exact primitive
  parameters, including signed and unsigned values and 64-bit values on x86 and
  x64. `null`, a box of another type, or a numeric value requiring conversion
  does not match.
- Reflection invokes methods returning primitive values and returns boxes that
  can be explicitly unboxed. `void`, reference, interface, and `null` results
  keep their current behavior.
- Overloads distinguish `int`, `uint`, `long`, and `double` boxes exactly;
  ambiguous matches still return `null`.
- Reflection invokes a boxed struct's const and mutating methods. Mutation
  changes the box payload and can be observed after the invocation.
- Reflection invokes a concrete-signature method on a boxed closed generic
  struct when its return body is a built-in expression over concrete fields,
  parameters, and literals; values still match and return through exact-type
  boxes.
- Reflection invokes a `void` method on a boxed closed generic struct when its
  body contains straight-line assignments to concrete fields using built-in
  expressions. The mutations are visible through the same box after invocation.
- Reflection invokes a parameterless `void` method on a boxed closed generic
  struct when its body resets one generic field to `default`; the default
  value is visible through the same box after invocation.
- Reflection invokes a `void` method on a boxed closed generic struct when its
  body assigns one or more matching generic parameters to matching generic
  fields in source order, even when assigned parameters are not first in the
  signature. Exact boxed arguments are selected by bound parameter names.
- A boxed struct argument can satisfy an implemented interface parameter and
  dispatch through the interface table while the box remains rooted.
- Boxed values containing managed references remain live through collection;
  focused GC tests cover nested reference fields.
- Native tests compile generated invokers with strict warnings and run on x86
  and x64. The full compiler suite, `cxcore` build, and ABI checks pass.

# Generic representation (Milestone 4)

Milestone 4 uses **monomorphization**: each reachable closed generic type and
function gets a concrete C declaration and definition. Closed generic types also
get distinct runtime type metadata. Specialization names use deterministic
canonical identities containing the defining module, qualified declaration,
arity, and recursively canonicalized type arguments. Identity does not depend
on source spelling, declaration order, process-local hashes, or build paths.

The binder substitutes type parameters recursively through arrays, nullable
types, and constructed named types before overload selection and call binding.
Explicit and inferred type arguments converge on the same specialization.
Recursive construction is deduplicated by canonical identity. A project owns
its closed definitions and emits each once across imported source modules and
compilation contexts. An imported `Lib.Identity<T>` specialization called from
`App` is emitted once by that project and used by the consumer. Separate-project
ownership and imported project generic definitions are part of Milestone 5's
separate-compilation boundary.

## Explicit generic function specialization

A specialized implementation uses the function name followed by its fully
closed type-argument list:

```cx
public T Adjust<T>(T value) { return value; }
public int Adjust<int>(int value) { return value + 1; }
```

The binder classifies the declaration list after indexing visible types and
aliases. An all-simple-identifier list whose names do not resolve to visible
types declares generic parameters. A visible type name or any non-identifier
type form makes the entire list a specialization; every entry must then resolve
to a closed type. This means adding a type or import can change classification,
and generic parameter names cannot shadow visible types. An unknown simple
identifier remains a generic parameter because it cannot be distinguished from
a misspelled type name without additional syntax.

Calls first resolve the ordinary generic declaration and enforce its
constraints; the compiler then selects the replacement for the exact closed
type arguments, or instantiates the primary body when no replacement exists.
The custom body is bound and emitted as ordinary concrete CX code under the
primary instantiation's canonical identity. It does not add runtime type tests
or boxing.

The initial implementation supports top-level functions and static or instance
methods on non-generic classes and structs. It also supports instance methods on
generic classes and structs, and static generic methods selected through a
closed generic class qualifier such as `Utility<string>.Select<int>(value)`.
The closed qualifier closes the containing type for method signature checking
and specialization selection; it does not create a runtime type instance for the
static call. Public specializations on those
targets can also be consumed through project references: the defining project
emits and exports the closed body, and the consumer links to that symbol. The
native entry-point harness verifies closed-owner static method specialization.
specialized signature must exactly match the primary after substitution, and
the replacement must preserve its modifiers and return type. Default arguments
come from the primary declaration. Generic virtual methods dispatch by the
receiver's runtime type and closed method arguments in non-generic callers.
Exact virtual and override specializations replace the selected implementation;
the override's primary body is the fallback. Generic interface methods,
documented generic caller shapes, and supported closed generic runtime classes
also dispatch through the runtime registry; the current constraints, caller
bodies, closed layouts, and cross-project limits are listed in
[`generic-virtual-specializations-design.md`](generic-virtual-specializations-design.md).
Generic specializations cannot provide implementations for abstract generic
methods.
Focused tests cover primitive,
reference, array, and constructed alias arguments, multiple type arguments,
declaration order, ordinary overload precedence, recursion, and a custom body
that calls another generic function. A consuming project may declare a
project-local replacement for an imported public top-level generic function;
its exact closed-type body wins over the library replacement for calls and
delegate bindings in that consumer, while unmatched types use the primary
body. The library and other consumers remain unchanged. See
[`generic-dependency-specializations-design.md`](generic-dependency-specializations-design.md).
Binding a generic function or a supported static/instance generic method to a
delegate closes it against the expected signature and selects the same custom
or primary implementation used by direct calls.
A primary generic function may return one inferred call that forwards one or
more direct parameters whose types are matching caller type parameters to
another generic function. The call may be returned directly or stored in one
explicitly typed or inferred local matching the caller's return type and then
returned.
A `void` generic caller may also consist of one expression statement that
forwards to a generic `void` function or method. These forms are supported in
generic methods on non-generic classes and structs. The binder resolves the call
separately for each closed caller instance, so a shared declaration body can
select a custom callee for one closed caller and a primary callee for another.
Other local call shapes, multiple statements, assignments, and side-effecting
argument expressions remain outstanding.
The forwarded call may have multiple generic arguments. Focused coverage
verifies a two-parameter custom specialization and primary fallback.
Arguments may also contain pure built-in unary, binary, or conditional
expressions over literals and non-generic caller parameters. At least one direct
matching type-parameter argument must remain for inference. Calls, assignments,
increments, overloaded operators, and expressions depending on an open type
parameter remain unsupported.

The `void` forwarding form uses the same argument and receiver restrictions and
must forward at least one direct caller type-parameter value. It does not add a
runtime generic dispatch mechanism.

Forwarding to an instance generic method accepts the current receiver or a class
or struct receiver expression whose resolved type exactly matches the target
method's non-generic owner. Class expressions are emitted once; struct lvalues
retain their storage location and struct rvalues use one local temporary.
Focused and native tests cover side effects, custom/fallback selection, and
struct mutation semantics.

## Supported slices

- Generic type parameters and generic function/method parameters substitute
  through supported signatures, fields, arrays, nested constructed types, and
  reflected members. Primitive type arguments are accepted.
- Generic functions, methods, classes, and interfaces may constrain each type
  parameter to one base class and multiple interfaces, or use the special
  `class`/`struct` reference/value constraints with optional interface
  constraints. Concrete arguments and forwarded open parameters are checked.
  A generic function may return a non-generic field or property value exposed
  by a class constraint, or one or more constrained instance-method results,
  including built-in binary or conditional expressions combining multiple
  calls. Call arguments may be literals, non-generic function parameters, or
  built-in binary/conditional expressions composed from them, as in
  `int Read<T>(T value, int offset) where T : Base { return value.Read(offset); }`.
  A class-constrained generic function may write multiple exposed properties in
  a straight-line body and test its parameter against `null` with `==` or `!=`.
  Interface constraints also allow a returned instance-method result with the
  same argument restrictions for reference-type arguments and non-generic struct
  arguments. Closed generic struct arguments work for this form when the closed
  value layout and implemented interface members fit the documented
  closed-layout patterns. Their generated interface reference points to a
  closed-type dispatch table and the specialized value parameter.
  Constructed class and interface constraints close member signatures against
  their type arguments. Supported closed generic struct values can use
  constructed interface constraints to call members specialized by the closed
  interface thunk.
  Generic class and struct declarations may also attach concept constraints to
  their type parameters. A non-generic method on such a type may return one expression
  formed from required property reads, required method calls, or required
  operators on those type parameters, including supported built-in binary or
  conditional expressions. The method specializes for each closed class or
  struct type.
  A `void` method may also assign required properties and make direct calls to
  required methods in source order. Assignment values and method-call arguments
  may combine literals and method parameters with required property reads and
  nested required method calls using built-in expressions. Required properties
  also support built-in `+=`, `-=`, `*=`, `/=`, and `%=` assignments with a
  literal or method parameter on the right-hand side. The additional supported
  class/struct `void` body forms are described in
  [`concepts-design.md`](concepts-design.md); they include branches and loops,
  scalar switches, array iteration over the constrained type, early return, and
  supported concrete locals. Other member-body statements remain unsupported.
  Boxed closed generic structs can also dispatch through implemented
  non-generic interfaces when their closed layout is supported. A constructed
  generic interface is supported when `T`-returning slots are implemented as
  direct field or conditional generic-result getters and methods, and `void`
  slots assign one or more matching parameters directly to fields in source
  order. A matching parameter may appear at any
  position in a multi-parameter method. Generic interface properties are supported
  when their getter/setter directly accesses a matching field. Non-generic
  interface methods with concrete parameter types and literal return bodies
  or direct matching-field getters/setters are also supported. Methods on a
  closed generic struct that implement an interface slot may directly return
  a parameter whose substituted type matches the return type, including an
  enclosing type parameter. A no-argument `void` interface method may assign a
  built-in constant expression to one concrete field through its closed-type
  thunk. A closed generic interface method with concrete return and parameter
  types may return a built-in expression of concrete fields, parameters, and
  literals, or use a straight-line body of assignments to concrete fields with
  built-in expressions composed from concrete fields, parameters, and literals;
  its thunk reads and writes fields through the closed layout in source order.
  A const getter on a concrete property may return a built-in expression over
  concrete fields, parameters, and literals. When it implements a property from
  a non-generic or constructed generic interface, its thunk evaluates the
  expression against the closed layout. For constructed generic interfaces,
  this applies only when the getter signature is concrete after construction;
  generic-result getters support the conditional field-selection form, while
  generic-typed getters may also copy one matching field into an explicitly
  typed or inferred local and return that local. The initializer may select a
  matching field or `default` using boolean fields, literals, `!`, `&&`, or
  `||`, and field reads may use direct or `this`-qualified syntax. Other generic-typed computed getters remain
  unsupported. This local-copy form lowers as a direct field read, including
  through constructed generic interface property thunks. Concrete property
  setters may assign concrete fields from built-in expressions through direct
  storage or a constructed generic interface thunk when their signature is
  concrete after construction. Conditional generic-result methods may use
  concrete parameters and are callable through direct closed layouts and
  constructed-interface constraints.
  A non-generic method may return `default` for its enclosing type parameter
  directly or through a constructed generic interface thunk; its signature may
  use enclosing type parameters that close at the interface construction.
  A parameterless `void` method may also reset one or more matching generic
  fields to `default` in source order. Other generic interface member bodies
  remain unsupported.
  `void` methods may mix matching generic-field parameter writes with
  concrete-field assignments from the concrete-expression subset, directly or
  through a constructed-interface thunk; both paths preserve body order.
  Concrete-field assignments in these closed generic interface thunks may also
  use built-in `+=`, `-=`, `*=`, `/=`, and `%=` operations with the same
  concrete expression subset on the right-hand side. Conditional concrete
  field assignments support the same compound operators in each branch, and
  later branch statements observe the updated field values. A single-arm
  `if` may contain the same concrete-field assignments; when its condition is
  false, the fields remain unchanged.
  A concrete-signature `void` method may consist of a single `while` loop
  whose condition uses the concrete expression subset and whose body contains
  one or more straight-line assignments to concrete fields. The loop body may
  use built-in compound assignments; direct calls and constructed-interface
  thunks preserve the loop condition and assignment order.
  A single `do/while` loop supports the same condition and assignment subset,
  with its body executed before the first condition check.
  A counted `for` loop may initialize and update a concrete field with one
  assignment each, test a concrete condition, and execute straight-line
  concrete-field assignments in its body. Both direct closed-layout methods
  and constructed-interface thunks preserve initializer, condition, body, and
  iterator order.
  Interface-
  constrained property reads and indexers are supported in direct-return bodies
  and in built-in binary or conditional expressions composed from those reads;
  property and indexer writes support straight-line bodies with multiple
  assignments for reference-type arguments, non-generic struct arguments, and
  closed generic struct arguments whose interface property accessors directly
  read/write a matching field.
  Assignment values may be literals, non-generic function parameters, or
  built-in binary/conditional expressions composed from them. Each type
  parameter may declare only one constraint clause.
  Class- and interface-constrained property and indexer writes also accept
  built-in compound assignments (`+=`, `-=`, `*=`, `/=`, and `%=`) when the
  right-hand side uses that same expression subset. Those right-hand sides may
  also read a property/indexer or call a method on the same constrained
  receiver, combining the result with literals, non-generic parameters, and
  built-in expressions. Receiver and indices are evaluated once; lowering
  reads the current value, applies the operator, and invokes the setter.
- Top-level generic functions and supported generic methods can be specialized
  from inferred or explicit arguments. Generic methods on generic classes use a
  receiver ABI tied to the closed value layout when needed. Generic local
  functions are specialized from inferred or explicit arguments and use these
  same supported body patterns and generic type constraints.
- Supported generic function bodies are: `extern` declarations; returning a
  matching type-parameter value or contextual `default` value, array, nullable,
  or nested constructed value; initializing one explicitly typed matching
  generic local with `default` and returning it;
  returning a non-generic field or property value from a class constraint;
  returning a constrained instance-method result, optionally composed with
  other constrained method results by built-in binary or conditional
  expressions from one or more independently constrained parameters, including
  an interface constraint with a reference-type or non-generic struct
  argument; these results may also initialize one explicitly typed local which
  is returned.
  Class- and interface-constrained property/indexer writes may contain multiple straight-line assignments. These
  assignments may use `=`, `+=`, `-=`, `*=`, `/=`, or `%=` and take a literal,
  non-generic function parameter, constrained member read/method result, or
  built-in binary/conditional expression composed from those values as the
  right-hand side. Interface property reads and indexers may be composed with built-in
  binary or conditional expressions in return bodies. Method call arguments may be literals, non-generic parameters,
  or built-in binary/conditional expressions composed from them, such as
  `int Read<T>(T value, int offset) where T : Base { return value.Read(offset + 1); }`;
  a generic `void` function may also call one or more `void` methods on
  class- or interface-constrained parameters in source order, using those
  supported argument expressions. Calls may target multiple constrained
  parameters. Struct arguments preserve by-value dispatch semantics;
  selecting between matching parameters with a conditional expression, either
  as the return expression or through one typed or inferred local that is
  returned, or with boolean
  return-only control flow
  (including nested `if` statements, blocks, boolean parameters/literals and
  their `!`, `&&`, `||`, `==`, and `!=` combinations, top-level boolean locals
  initialized from those expressions, and a conditional return followed by a
  fallback return); and straight-line sequences of matching generic
  local copies/reassignments followed by returning a matching local. A matching
  `var` local copy is inferred separately for each concrete function
  instantiation, including array and nested constructed-type return patterns;
  one declaration may contain multiple matching local copies. A generic function
  may also return one indexed element from a matching array parameter, as in
  `T First<T>(T[] values) { return values[0]; }`, or call `Get(uint)` on a
  matching `T[]` or `Array<T>` parameter and return the element, as in
  `T First<T>(Array<T> values) { return values.Get(0u); }`. It may return the
  length of a matching array parameter as `uint`, as in
  `uint Count<T>(T[] values) { return values.Length; }`. It may also compare a
  matching array's length to a non-negative integer literal (with or without a
  `u` suffix) or a `uint` parameter and return the boolean result. Multiple such
  comparisons may be composed with `!`, `&&`, `||`, or conditional expressions,
  and can be combined with null checks on the same array, as in
  `bool HasLength<T>(T[] values, uint minimum) { return values != null && values.Length >= minimum; }`.
  The lengths of two matching generic arrays may also be compared, including
  mixed `T[]` and `Array<T>` spellings and nested array access, as in
  `bool SameLength<T>(T[] first, Array<T> second) { return first.Length == second.Length; }`.
  A generic array's length may also be compared with a built-in unsigned
  arithmetic expression over `uint` parameters and non-negative integer
  literals, using `+`, `-`, `*`, `/`, or `%`.
  Jagged array types preserve each `[]` rank, allocation may specify trailing
  unsized ranks such as `new int[1][]`, and a generic function may return a
  nested element or assign a value through nested indices, as in
  `T FirstNested<T>(T[][] values) { return values[0][0]; }` and
  `void SetNested<T>(T[][] values, int row, int column, T value) { values[row][column] = value; }`.
  The length of a nested generic array can also be read, as in
  `uint FirstRowLength<T>(T[][] values) { return values[0].Length; }`, and
  compared with an unsigned value, as in
  `bool HasNonEmptyFirstRow<T>(T[][] values) { return values[0].Length > 0u; }`.
  Generic functions can also copy an element between matching jagged arrays,
  as in `void CopyNested<T>(T[][] source, T[][] target, int row, int column) { target[row][column] = source[row][column]; }`.
  A parameter with a reference-type or class/interface type constraint, or a
  matching generic array parameter may also be compared with `null` using `==`
  or `!=`, and those comparisons may be combined with `!`, `&&`, `||`, or
  conditional expressions in a return expression, as in
  `bool IsNull<T>(T value) where T : class { return value == null; }` or
  `bool IsNull<T>(T[] values) { return values == null; }`.
  A function with a `class` constraint may return a `??` chain over matching
  type-parameter parameters and `null`. Generic array parameters may use the
  same form without a constraint, as in
  `T[] FirstNonNull<T>(T[] first, T[] fallback) { return first ?? fallback; }`.
  A generic function may assign a matching `T` value through one or more
  nested array indices, as in
  `void SetAt<T>(T[] values, int index, T value) { values[index] = value; }` or
  `void SetNested<T>(T[][] values, int row, int column, T value) { values[row][column] = value; }`.
  It may also copy one indexed element between matching `T[]` parameters or
  copy through matching nested array indices, as in
  `void CopyAt<T>(T[] source, T[] target, int sourceIndex, int targetIndex) { target[targetIndex] = source[sourceIndex]; }` or
  `void CopyNested<T>(T[][] source, T[][] target, int row, int column) { target[row][column] = source[row][column]; }`.
  It may also allocate a matching `T[]`, store one matching `T` value at a
  parameter-supplied index, and return the array, as in
  `T[] Create<T>(uint length, uint index, T value)`.
  It may copy a matching `T[]` with a canonical indexed `for` or `while`
  loop, or a `foreach` over `source`: allocate a result using `source.Length`,
  initialize one `uint` index to `0u`, copy each current element to the result,
  and advance the index. The indexed loops use postfix `++` or
  `index = index + 1u`; other loop shapes and
  generic expressions remain unsupported; arbitrary generic locals remain
  limited to the documented copy and reassignment forms. A generic function may
  also read one matching array element into a typed or `var` local, then copy or
  reassign the value through matching typed or inferred locals before returning
  one of them, such as
  `T At<T>(T[] values, uint index) { var item = values[index]; var copy = item; return copy; }`.
- Generic classes support empty layouts, reference-backed `T` fields, nested
  generic array fields such as `T[][]`, and reference fields whose type is a
  constructed generic class such as `Box<T>`. Closed reference layouts also
  support concrete primitive, array, and non-generic struct fields, with
  concrete field initializers composed of literals and built-in unary or
  binary operators, and calls to non-generic instance methods through earlier
  concrete reference fields. A constructed interface field such as
  `IEqualityComparer<TKey>` may use the existing `cx_iface_ref` representation
  and be initialized from a matching constructor parameter. Mixed constructors
  may also initialize concrete array fields with literal lengths, including
  arrays whose element is a constructed generic class or struct type such as
  `Box<T>[]` or `Pair<T>[]`; element types close for each owner, and struct
  elements require a supported closed value layout. Their
  supported constructors may assign matching concrete fields as well as
  generic fields, and concrete field-backed properties may read or assign their
  matching field. Closed generic reference classes may
  derive through generic or non-generic base classes when the base hierarchy uses
  only inherited `System.Object` virtual slots and no interfaces. Each closed
  class in the hierarchy must satisfy the shared-reference-layout constraints,
  and forwarded type arguments must meet the base class's generic constraints.
  Closed value layouts support
  direct generic fields, recursively nested generic arrays, and concrete
  primitive, reference, enum, or array fields, and non-generic or closed
  constructed generic structs stored by value. Nested layouts are emitted in
  dependency order. An explicit constructor in a closed generic struct may
  initialize an array field whose element is concrete, the owner type
  parameter, or a supported constructed generic type; each instance allocates
  storage using the closed element size, while `default` remains zero-initialized.
  Constructed generic struct fields can use an empty
  parameterless constructor, including when their type arguments depend on the
  enclosing type parameter. Constructors may copy a matching constructed
  generic struct parameter into its field.
  Constructors may initialize other selected fields with exactly
  matching concrete or generic parameters, and concrete fields may also be
  initialized from built-in expressions over concrete fields, constructor
  parameters, and literals. One parameter may initialize multiple matching
  fields; unassigned fields retain their
  zero-initialized or expression-initialized values. An empty parameterless body
  is also supported. Matching
  field-backed properties use the same
  recursive generic type patterns. Generic
  property getters and setters may use an unqualified matching field or
  `this.field`; a concrete property may also have a const getter returning a
  built-in expression over concrete fields, parameters, and literals. Such a
  getter is evaluated against the closed value layout, including when called
  through a supported non-generic or constructed generic interface. A concrete
  property setter may assign a concrete field from a built-in expression over
  the setter value, parameters, and literals when written through direct
  closed-layout storage or through a constructed generic interface thunk when
  its signature is concrete after construction. Closed
  generic structs also allow a const `T` getter to select between matching `T`
  fields or `default` based on a concrete boolean-field condition. This computed
  generic result is supported for direct closed-layout property reads and
  through constructed generic interface thunks when the interface is closed
  over a supported concrete result type. A method with concrete parameters may
  return the same conditional generic-result form when called directly on a
  closed struct or through a constructed generic interface thunk. Closed
  value arguments include primitive values and named non-generic structs.
  Multi-field and multi-parameter layouts preserve field order and concrete C
  types.
- Supported constructors are empty parameterless constructors and constructors
  whose body assigns a matching parameter to one or more fields (including
  nested generic array patterns and concrete fields). Every parameter must be
  used at least once and each field can be assigned once. Closed-layout concrete fields
  may have initializers composed of literals and built-in unary or binary
  operators; they run before constructor assignments. Initializers with calls,
  variable references, object creation, or overloaded operators remain
  unsupported. A direct generic-parameter field may also use the `default`
  initializer in a supported closed value layout or a reference layout whose
  type argument is a class. Storage is already zero-initialized, so no
  pointer-shaped assignment is emitted for value fields. Other initializers on
  generic-typed fields remain unsupported, except `default` on a constructed
  generic reference or value field such as `Box<T>` or `Pair<T>`, and supported
  array allocations in closed generic reference and value constructors. Direct
  generic-field getters and
  setters are specialized for closed value layouts. Null initializers are
  supported for array fields and `T` fields with reference-type or class
  constraints. Other initializers on
  generic-typed fields,
  bases, virtual/interface dispatch, and constructor/method bodies
  that exceed these patterns remain diagnosed as unsupported.
- Generic classes and structs may declare non-generic operators for closed
  value-type arguments. Operator lookup substitutes the receiver's type arguments
  before exact parameter matching, and emits a specialized body for each closed
  value layout. Operator methods can also declare method-level generic parameters
  inferred from their explicit operand, with generic constraints checked and
  each closed method emitted as a specialization. These methods also work on
  generic classes with supported closed layouts. Their bodies follow the
  documented generic function body limits. A generic unary operator has no
  explicit operand from which to infer method arguments, so it infers them from
  the expression's expected result type instead. Every method type parameter
  must be determined by that result type; otherwise the generic operator is
  inapplicable. The primary constraints are checked before selecting a closed
  specialization, and unmatched arguments use the primary body. Mutating
  `++`/`--` do not use result-context inference.

Closed descriptors carry substituted field and method types and distinct
runtime identities. Native tests exercise concrete size and field offsets,
field read/write, constructor assignment, specialization identity, and generic
function results, including native copies of integer and long arrays. Focused
substitution and canonical-identity tests were added before code generation.
The full compiler suite currently contains 576 passing
tests; the closed generic field getter/setter fixture also compiles under MSVC
with `/W4 /WX /wd4100` and runs successfully.

The C ABI for *open* generic declarations remains provisional. Do not use that
pointer-compatible representation for closed value layouts, which require
concrete size, alignment, and runtime identity.

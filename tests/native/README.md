# Native conformance checks

`run-entry-point-e2e.ps1` compiles a temporary CX program, builds the generated
C with CMake and `cxcore`, then checks argument count and contents,
short-circuit evaluation, null coalescing, 64-bit integer limits, deep
inheritance, interface diamond dispatch, dynamic static field initialization,
generic array creation, element reads both directly and through a typed generic
local, length comparisons, constrained generic
field and property reads, class-constrained method calls, interface-constrained
method, property, and indexer reads and writes, including method results
combined and returned through an explicitly typed local and assignment values
composed from constrained property reads and method results, lengths, stdout,
dynamic construction of
classes with public parameterless constructors, boxed default values from
non-generic and supported closed generic structs, parameterized class and
struct construction from exact-type boxed arguments, null results for classes
without a public constructor,
primitive extension method/property/operator receivers, concrete and generic
array extension methods, concrete and concrete-signature generic read-only array
properties, non-mutating array operators, concrete and generic array
compound-operator and increment/decrement write-back, and
closed generic property
getters that return a matching field or conditional field/default value through
explicit and inferred locals, including a constructed generic interface
property thunk,
closed generic struct and class operator specialization, interface-constrained
dispatch for closed generic struct arguments, mixed closed-generic layouts with
computed const interface-property getters over concrete fields in boxed closed
generic structs, including concrete getters on constructed generic interfaces,
computed const getters over concrete fields on closed generic structs, and
computed setters that assign a concrete field from built-in expressions,
including setter dispatch through a constructed generic interface,
constructed generic struct constructors that copy a matching parameter into
multiple fields, and conditional computed `T` getters that select a matching
generic field or `default` on a closed generic struct directly and through a
constructed generic interface,
conditional-expression and boolean `if`/`else` generic-result methods through
constructed generic interfaces,
concrete-expression return branches through direct, constructed-interface, and
interface-constrained calls,
concrete and generic fields (including non-generic and closed generic structs
stored by value, plus concrete primitive, array, and non-generic struct fields in
generic reference layouts, with nested generic construction, constructor
assignment, field reads, and generic reference classes inheriting through a
multi-level closed-generic base chain),
constructor parameter assignments, literal and arithmetic field initializers,
default initialization of direct and constructed generic fields (reference and
value), null
initialization of generic reference-constrained and array fields,
class- and interface-constrained generic null checks, generic array null checks,
reference-constrained generic and generic array null-coalescing,
string switch expressions with filters and null labels, inferred generic local
function specialization with interface constraints,
closed `System.Collections.Array<T>` and `List<T>` storage, including calls to
the public `Array<T>` API through both `Array<T>` and built-in `T[]` values,
safe array reads and writes, list growth and mutation, self-appending `AddRange`,
compact `ToArray`, offset `CopyTo`, and structural `foreach` for arrays, lists,
stacks, queues, linked lists, and dictionary entries; dictionary coverage also
checks null keys, string content equality, custom comparer collisions,
collision-chain growth and removal. A custom sequence exercises `foreach`
through an `IEnumerable<int>`-typed value and its constructed
`IEnumerator<int>` dispatch. Dictionary also runs `string,long` and
`CollisionKey,long` key/value combinations. The harness also checks
successful and nonzero process exit codes. Reflection
invocation checks instance and static calls, exact primitive overload selection,
boxed primitive and struct arguments and returns, const reads and mutation on a
boxed struct receiver, implicit and explicit interface conversions and dispatch
on boxed non-generic and supported closed generic structs, including closed
generic interface field accessors, generic-result methods returning
`default`, multi-field generic setters, generic field reset to `default` including
multiple fields reset by one interface method, and
conditional `if`/`else` resets and matching-parameter writes to generic fields
directly and through a constructed interface, plus branch-selected built-in
expressions assigned to concrete fields on both paths, including expressions
that read earlier writes in the same branch, and
mixed generic and concrete field mutation through direct calls and interface
dispatch, plus concrete literal-return,
field-expression, and ordered
field-mutation methods, direct field setter/getter methods on boxed
closed generic structs, plus a reflected concrete-field expression method on a
boxed closed generic struct and ordered concrete-field mutation through the same
box. It also checks reference and exact-boxed primitive setter/getter
invocation on closed generic reference classes, and rejection of mismatched
closed-generic method arguments and unsupported method shapes,
casts back to `object`,
`void` calls,
incompatible arguments, invalid targets, and missing methods. Named delegate
coverage checks ordered multicast invocation, removal, the last handler's result,
equality, and a captured lambda in generated native code. It also builds a
The harness also checks runtime-name `ToString()`, reference-identity `Equals`,
stable identity hashes, and virtual overrides called through `object`. String
operators and the `String` overrides compare UTF-8 contents, match hashes for
equal contents, and preserve the string through `ToString()` when called through
`object`; relational string operators use UTF-8 ordinal ordering. Concept tests
specialize a conditional generic maximum and direct comparison returns for class
and struct implementations, plus generic arithmetic, unary operator, and
parameterized concept-method returns and built-in return expressions that
combine required method results. Auto-property getter/setter behavior runs in
generated code. Generic concept-method requirements also run for class and struct
receivers with inferred method type arguments, checked standard and concept
constraints, and requirement defaults inserted at the call site. Concept setter
coverage also interleaves required property writes, built-in compound property
assignments whose right-hand expressions call required methods, and required
method calls as statements on class and struct receivers; the native fixture shares the required methods
through a diamond of base concepts, adds the property on the derived concept,
reads the required property into an explicitly typed local, and branches
between those mutations. The body also updates locals in bounded counted and
do/while loops.
Generic class members may use class-level concept constraints in returned
property/method/operator expressions; their `void` methods may also assign
required properties and make direct required-method calls in source order.
Assignment values and method-call arguments can combine constrained property
reads with parameters in built-in expressions. Required properties also support
all five built-in compound assignments. Native checks cover returned
expressions, mixed property/method mutation, switch selection on a required
property with filtered literal labels and a default arm, string selectors with
filtered labels and `null`, and a counted loop that passes its local counter to
a required method. Early `return;` statements in a
class-level concept `void` method are checked on both the returning and
continuing paths. A `foreach` over an array of the constrained type calls a
required method on each element.
Concrete built-in integer, boolean, and string locals in class-level concept
methods can be initialized from required member expressions and reused in
later expressions. Integer locals support built-in updates and required method
calls; boolean locals control `if` branches, and string locals select switches.
Locals of the constrained type parameter are substituted for each closed
class and can make required method calls.
Class- and interface-constrained generic `void` calls run in source order for
reference and struct arguments; struct mutations remain local to the generic
function's by-value parameter. Interface-constrained calls also execute both
`if`/`else` branches with supported built-in argument expressions.
Closed generic struct implementations of constructed interfaces also write
multiple matching generic fields in each branch, with both direct and
interface-thunk calls checked in native execution. Single-arm branches verify
that unmatched fields keep their prior values. Branches may mix generic-field
writes and resets with dependent concrete-field expressions; both call paths
preserve the source order.
Constrained interface method results from two independently specialized
parameters also combine in direct returns and typed-local returns.
It builds a
two-project app and verifies dynamic construction of a class declared in its
referenced CX library, plus public operator and method concepts with generic
functions specialized against consumer-defined class and struct types. Build
products are removed on success and retained in the reported temporary
directory on failure.
Cross-module binding and generated calls also have compiler assertions in the
`ProjectReferenceTests` xUnit group.

From the `cxc` directory, run it with the compiler DLL and CMake executable:

```powershell
dotnet build src/CxCompiler.csproj --no-restore
.\tests\native\run-entry-point-e2e.ps1 `
  -Cxc .\src\bin\Debug\net10.0\CxCompiler.dll `
  -CxCoreDir ..\cxcore `
  -CMake 'C:\Program Files\Microsoft Visual Studio\18\Professional\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe' `
  -Generator 'Visual Studio 18 2026'
```

The harness also accepts a CMake generator and C compiler. To run it with
Clang/Ninja on Windows, use a Visual Studio developer environment so the Windows
SDK resource compiler and C runtime libraries are available, then pass
`-Generator Ninja -CCompiler <path-to-clang.exe>`.

The entry-point fixture also verifies inferred and explicit calls to top-level,
static, class-instance, and mutating struct-instance generic specializations.
It also exercises generic extension method specializations against ordinary,
closed generic class, struct, interface (including a constructed generic
interface), enum, and primitive targets in native code.
The fixture also checks a captured generic local function specialization and
primary-body fallback for another method type argument.
It checks generic binary operator specialization and fallback on a value type,
plus mutating generic specializations and constrained primary fallbacks for all
five compound assignments on a struct lvalue.
Function and method specialization declarations use keyword-free closed type
lists, matching the semantic classification rules in the language reference.
Generic methods also forward through the current instance and a matching class
receiver parameter, covering custom specializations and primary fallbacks.
They may also forward through a computed receiver expression whose class type
matches the target method owner. The native fixture uses a side-effecting
factory and checks that it runs once per forwarding call.
Forwarding also accepts multiple direct generic-parameter arguments; native
coverage verifies both a custom tuple specialization and a primary fallback.
Forwarded concrete arguments can use pure built-in expressions over concrete
parameters and literals; the native fixture checks specialized and fallback
calls with an integer offset expression.
Forwarding through by-value struct receivers preserves copy semantics; the
native fixture verifies that mutations affect only the receiver copy or
temporary. Cross-project specialization tests run in both static and DLL modes.
Unmatched type arguments continue to use the primary generic bodies. The native
cases include reference, array, constructed alias, two-parameter, recursive,
and cross-specialization calls.
The project-reference fixture exercises library-owned generic function and
method specializations under both static and DLL linking.
Same-project instance specializations on generic classes are selected for two
closed reference owner types; their bodies write a matching generic field and
unmatched method arguments use the primary body. A generic struct owner also
checks specialization and fallback against a mutable lvalue. The project-reference
fixture also instantiates a library-owned generic class method specialization for
closed class owners under both link modes. It also calls a library-owned static
generic method specialization through two closed generic class owners whose type
parameters appear in the method signature.

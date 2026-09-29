# Generic representation (milestone 4)

Choose **monomorphization** as the target representation: each closed generic
type and function has a concrete C definition, a deterministic name, and (for
types) its own runtime `TypeInfo`.
The canonical identity includes the defining module, qualified declaration name,
generic arity, and recursively canonicalized type arguments. It must never depend
on source spelling, declaration order, process-local hash codes, or the directory
where compilation runs. Qualifiers and array/nullable constructors remain part of
the semantic type; `const` is not part of a runtime type identity.

The compiler will bind type parameters as symbols, substitute them recursively
through arrays, nullable types, member signatures, and nested constructed types,
then select overloads using the substituted signature. Emission should discover
reachable closed instantiations and emit each once per owning module. Headers
declare instantiated symbols; a single source file owns each definition. An
importing module refers to that symbol and must not emit another definition.
Recursive instantiation needs a visited set keyed by canonical identity.

Current foundation: the binder substitutes class parameters in field and
function signatures (including arrays), resolves simple named type arguments,
checks arity, and distinguishes closed references in type comparisons. The
identity key and C symbol spelling are implemented and unit tested, but are
now wired to emitted closed `TypeInfo` and vtable symbols for empty generic
marker classes. Field reflection points to the closed descriptor, and the
native fixture verifies distinct hashes and vtable identity for two type
arguments. An empty class with an empty parameterless constructor can now be
allocated: the open constructor runs and the allocation receives the closed
vtable afterward. This is safe for that constructor shape because it cannot
observe or replace the final runtime identity. With the same constructor
shape, `T[]` fields use array-reference storage, and direct `T` fields are
supported when every corresponding argument is a class reference. Both cases
have pointer-compatible layouts. Their closed `TypeInfo` records fields with
substituted types, while the current C struct layout is shared. Named,
non-generic struct arguments now have a concrete closed C struct when the
generic class has one direct `T` field and an empty parameterless constructor.
The closed descriptor uses its specialized size and field offset, and field
reads and writes copy the struct value. Empty generic classes with a named
struct argument also use a closed layout. Other value-type layouts and explicit
bases remain unsupported. A `constructor(T initial) { value = initial; }` now accepts a
closed class-reference argument and uses the shared pointer ABI; the closed
descriptor reflects the substituted constructor parameter type. Other
constructor bodies and signatures remain unsupported. The grammar currently
accepts identifier arguments only. A two-field `Pair<T,U>` with
one direct field assignment per constructor parameter shares one pointer
layout, while its closed field and constructor metadata keep argument order.
Repeated closed references across CX compilation contexts produce one header
declaration and one source definition per identity. A native
fixture links two separately compiled C consumers of that generated header to
catch duplicate definitions. Cross-module ownership and imported generic
instantiations remain for the module work.
Instance `void Set(T value) { field = value; }` methods on a generic class
now use the shared pointer ABI for class-reference `T`. Member lookup closes
their parameter types at the receiver, and each closed descriptor reflects
the substituted method parameter. Other methods on generic classes still need
their own body and ABI treatment.
The corresponding `T Get() { return field; }` body uses a shared pointer
return ABI for class-reference arguments. Its call-site type and reflected
return type are closed separately for each `Box<T>` instance.
A read-only `T` property whose getter returns a matching `T` field uses the
same pointer return ABI. Property access closes its value type from the
receiver, and closed reflection records the getter return type. Generic
setters with a single `field = value` assignment now accept class-reference
arguments through the same shared pointer ABI; their closed reflection
parameters retain the concrete type. Other accessor bodies remain unsupported.
Top-level `extern` generic functions now infer
type parameters from call arguments and emit one concrete prototype for each
used signature; a native implementation supplies the body. Top-level generic
functions whose body directly returns a type-parameter argument are also
emitted once for each used signature. A direct `T[]` return from a matching
`T[]` parameter uses the same
specialization path. Array element types participate in inference and
specialization identity, while the C array representation remains a pointer.
A body with one local `T copy = value`
followed by `return copy` now emits a concrete local for each inferred
instantiation, including functions with multiple inferred type parameters.
Other CX bodies need per-instantiation binding and are rejected with a
diagnostic. Static and instance generic methods on a non-generic class now use
these same two body shapes and call-site inference. Instance specializations
carry a receiver parameter and have a distinct identity from static methods
with the same source signature. Methods that introduce additional type parameters
on generic classes remain unsupported.
Explicit type
arguments, and overloads requiring contextual return-type inference remain.
Generated C still emits an open generic descriptor and pointer-compatible
reference fields; its generic arity and reference-field layout have a native
smoke test. Layout sharing is limited to empty classes and the supported
reference fields. The named-struct field case uses a distinct closed layout.

The existing pointer-based output for open generic signatures is provisional.
Do not use it as the ABI for closed value types: it loses layout and runtime
identity. First establish substitution, arity checking, and canonical identity in
focused tests; then add concrete layout and function emission in vertical slices.

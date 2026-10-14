# Generic method specializations on generic containing types

## Declaration semantics and implementation status

Allow a method specialization inside a generic class or struct to list the
method's closed type arguments, for example:

```cx
public class Router<TState> {
    public U Route<U>(TState state, U value) { return value; }
    public int Route<int>(TState state, int value) { return value + 1; }
}
```

The declaration specializes only the method's type parameters. It applies to
each closed construction of the containing type, and its body is instantiated
with that construction's type arguments. The specialization declaration remains
owned by the generic type's defining module; consumers cannot add replacements.
Full method argument lists are required. Partial specialization remains out of
scope.

Same-project instance specializations on generic classes and structs are
implemented. One method specialization template is selected after the
containing type and method arguments close. The closed function identity
includes the closed containing type, substituted signature, and method
arguments, so distinct closed owners produce distinct generated functions. The
native fixture verifies two reference class owner types, a custom body that
mutates a matching generic field, primary-body fallback, and a value-type
generic struct owner whose lvalue mutation is preserved. Imports through
project references reuse the library-owned specialization declaration and
instantiate the closed body in the consumer that closes the generic owner. The
native project-reference fixture verifies this under static and DLL linking.
The generated receiver and reflection layout use the generic type's defining
module even though the closed function body is emitted by the consuming module.
If a consumer-defined generic derived class invokes a referenced generic
base's constructor, the binder registers the closed constructor body in the
consumer project so it links with the defining module's shared class layout.
Native static and DLL coverage also dispatches exact and fallback generic
virtual overrides through that closed owner.
Consumers cannot add replacement specialization declarations. Static generic
methods on generic classes can be called through a closed owner, for example
`Utility<string>.Select<int>(value)`. The owner closes containing-type parameters
in the signature and function identity, so one method template can produce
distinct custom bodies for `Utility<string>` and `Utility<int>`. The static owner
qualifier is used for type checking and selection without registering runtime
instance layout metadata. Native coverage exercises both owner types.
For a referenced library, the specialization declaration stays library-owned,
while the consumer emits the closed static body for each owner/method identity.
Native project-reference runs verify this under static and DLL linking.

## Identity and selection

Use a specialization template identity consisting of the defining module,
primary method declaration identity, and canonical closed method type
arguments. At a call site, selection additionally closes the containing type
and constructs the ordinary closed-function identity from the closed owner,
method arguments, substituted signature, and static/instance kind. This keeps
the template reusable across `Router<int>` and `Router<string>` while giving
each generated body a distinct closed owner identity.

Primary and replacement methods may live in separate partial declarations of
the same generic class. Registration and member lookup match their merged owner
by module-scoped type name, kind, and generic parameters rather than requiring
both methods to retain the same partial-fragment object.

Resolve the primary through the generic containing declaration and its member
scope, then compare the specialization signature after substituting the method
arguments while preserving the containing type parameters. Apply the primary's
constraints to every closed call before selecting the custom body, so a
specialization cannot bypass constraints that depend on the containing type.
The custom body may use the containing type parameters and is emitted only for
reachable closed owner/method tuples. Calls with other method type arguments
continue to use the primary body.

Extension methods on class targets, including constructed generic class targets,
struct, interface, and enum targets, and primitive targets can also have exact
generic method specializations. The extension specialization is selected after ordinary
overload resolution and the primary method's constraints, and otherwise falls
back to the primary extension body. Focused and native coverage checks ordinary
and closed generic class targets, struct, interface, enum, and primitive
targets. A constructed generic interface target also closes its owner arguments
before selecting the method specialization. Interface extension specializations
use the interface receiver representation in their generated function
signatures. Concrete array extension targets support exact generic method
specializations, and open array-element patterns such as `T[]`, `T[][]`, and
`Box<T>[]` infer and close their generic arguments from the receiver.

Generic binary operator methods also use specialization selection after closing
the operator's generic arguments from its operands and checking the primary's
constraints. The custom body remains a method implementation, so existing
receiver and value-copying rules apply. Generic mutating `+=`, `-=`, `*=`, `/=`,
and `%=` operators select closed specializations while preserving mutation of a
struct lvalue. Unmatched operand types use the constrained primary body; the
focused and native suites cover all five forms. Non-mutating unary operator
methods infer their method type arguments from the expected result type; the
type context must determine every argument. Uncontextualized uses are rejected,
while mutating `++`/`--` do not infer from a result context. Focused coverage
checks specialization, primary fallback, typed-local inference, and rejection
without a result context.

Generic virtual and override specializations are supported on closed generic
reference-class owners: dispatch resolves the runtime override first, then
selects an exact closed method specialization when present and otherwise uses
that override's primary body. A focused binder test and native execution
fixture cover exact selection and fallback through a `Base<T>` reference to a
`Derived<T>` instance. This support is currently bounded by the closed generic
reference-class layouts accepted by the runtime; it does not imply support for
all virtual methods on generic owners. A referenced library's concrete closed
generic class owner also registers exact and primary-fallback dispatch in the
consuming project under static and DLL linking. Generic abstract methods fail earlier
under the current generic method-body restrictions and have separate rejection
coverage. Coroutine method compatibility is deferred to Milestone 18, which
introduces coroutine bodies. Local generic functions use the lexical declaration scope instead
of the project-level method registry: a specialization matches one generic
local function in the same block, inherits its constraints and captures, and
does not enter the local overload set. Calls select it by the same closed
argument identity, with the generic local body as fallback.

### Virtual generic methods

Virtual and override generic specializations are supported by a runtime
registry keyed by runtime implementation, inherited contract identity, and
closed method type arguments. The primary virtual slot still resolves the
runtime override first; an exact specialization replaces that implementation's
body for the matching arguments, and unmatched arguments use that override's
primary body. A concrete override may specialize an abstract generic contract.
A specialization declared directly against an abstract primary is rejected,
since it cannot supply the missing override implementation. Native coverage
includes class and interface dispatch, exact and fallback bodies, constructed
generic interfaces, supported constraints, generic callers, project
references, method values, and closed generic reference-class owners. The
runtime and compiler still diagnose unsupported owner layouts and caller,
constraint, and inheritance shapes; see
[`generic-virtual-specializations-design.md`](generic-virtual-specializations-design.md).

## Acceptance

- Instance specializations on generic classes and structs can read and write
  fields of the containing type after closure; native coverage verifies a
  matching generic field write and struct lvalue behavior.
- One instance specialization template is selected for multiple closed class or
  struct constructions; custom and primary fallback selection is tested per
  full closed identity.
- Instance methods preserve receiver behavior and exact signatures. Static
  methods on generic classes use a closed owner qualifier to close containing
  type parameters and select the per-owner specialization body.
- Constraints, aliases, declaration order, duplicates, ambiguity, and full method
  arity are checked before reachable bodies are emitted.
- Project-reference calls reuse the defining module's template and link under
  static and DLL modes; the consuming module emits one body for each closed
  generic-owner/method-argument identity.
- Generated C contains one deterministic body per reachable closed owner/method
  tuple and no runtime type dispatch for specialization.

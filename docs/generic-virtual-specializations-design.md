# Generic method specializations and virtual dispatch

## Goal

Support generic virtual, override, and abstract methods while preserving both
dispatch decisions at a call site: select the override from the receiver's
runtime type, then select the closed generic method implementation for the
call's type arguments. This applies to ordinary generic method bodies and
explicit generic method specializations.

ABI declarations may change during this work. ABI version 1 is the current
development label, not a compatibility promise. Prefer a representation with
clear ownership and fast direct calls for non-virtual methods; virtual generic
calls may use additional metadata or a dispatch thunk. Do not silently lower a
generic virtual call to a direct call, since that skips override dispatch.

## Current behavior

Generic method specializations work for supported non-virtual methods. Generic
virtual and abstract declarations can bind, and alpha-renamed overrides share
the primary vtable slot after generic arity, signature, and constraint checks.
Generic methods use a null ordinary vtable entry because one fixed C function
pointer cannot represent all closed method signatures. Generic interface
methods also use the runtime registry; interface values provide the
implementing object's runtime TypeInfo through their interface vtable. Generic
interface slots remain null in fixed interface tables. Closed calls in a
non-generic caller now register typed thunks for known concrete non-generic
runtime classes and supported constructed generic runtime classes, then look
up the implementation using the runtime type, stable method identity, and
ordered closed type descriptors. A constructed generic class with a supported
reference layout gets a per-instantiation vtable whose first entry points at
its closed TypeInfo; object creation installs that vtable after its
constructor. This allows an inherited generic virtual method to resolve
against the constructed runtime class while retaining the shared object
layout. Virtual and override
specializations are selected per runtime implementation; a missing exact
specialization falls back to that implementation's primary body. Focused
compiler tests and a native execution fixture cover override selection,
specialization, and fallback. A project-reference fixture also verifies that a
library-owned generic virtual call reaches an app-defined override in both
static and DLL builds. Registration descriptors are initialized by the module
initializer, so imported type descriptors work in DLL builds; static modules
with registrations initialize them on their first CX function entry.

Generic virtual methods declared on a generic reference class, and overrides
on a derived generic reference class, also support exact method
specializations when called through a closed owner. The runtime class vtable
identifies the implementation on the constructed runtime class; closed method
arguments then select that implementation's exact specialization or its
primary body. A native fixture covers `Base<T>` / `Derived<T>` through a
`Base<string>` reference, including both exact specialization and fallback.
This coverage does not establish support for every virtual method shape on a
generic owner; the closed-owner metadata validator still limits supported
layouts.

When a referenced library exposes a concrete closed generic class owner, the
consuming project registers that closed runtime owner for each reachable
generic virtual method argument set. This includes exact method
specializations and primary-body fallback on the library implementation.
Focused project-reference compilation and native execution verify both paths
for `GenericVirtualOwner<string>` under static and DLL linking.
The consumer may also declare a generic derived class over a library-owned
generic base and specialize its override. When the derived constructor calls
the closed generic base constructor, the consumer emits that reachable closed
constructor body and registers the derived runtime type. Native static and DLL
runs verify the override's exact specialization and primary-body fallback.

Generic interface method dispatch supports both non-generic interfaces and
constructed generic interfaces, including exact specialization and primary-body
fallback. Native checks cover direct calls and method values through
`ITransform<int>`, implemented by both an ordinary class and a closed generic
class such as `TransformImpl<int>`. An inherited generic method called through
child or grandchild interface also selects the implementing class's exact
specialization and primary fallback through a three-level single-inheritance
chain. The selected closed owner and method signatures are
preserved in generated thunks. Generic callers constrained to a constructed
interface can also forward exact and fallback method arguments to the runtime
registry. Generic virtual interface methods with `class`, `struct`, interface,
and combined `class, interface` constraints select exact valid specializations
and fall back to their primary bodies. Interface methods also support a `class`
constraint combined with two interface constraints; native coverage checks an
exact specialization and primary fallback for another type satisfying all
three constraints. Class virtual declarations and overrides support the same
`class` plus two-interface combination with exact specialization and fallback.
They also support `struct` combined with an interface
constraint, including exact specialization for one implementing struct and
primary-body fallback for another. Class override chains also support
method-level `class`, `struct`, and combined `class, interface` constraints,
with exact specializations and primary-body fallbacks. A project-reference
fixture also covers class virtual methods with `class` plus three interface
constraints, with a consumer override selecting an exact specialization and
falling back to the override's primary body. Cross-project class virtual
methods also support `struct` plus three interface constraints, with exact
specialization for one consumer-defined struct and primary-body fallback for
another. Diamond interface
inheritance through two branches that share one generic base contract is
supported with one implementation target and exact/fallback behavior. Interface
child interfaces may redeclare an inherited generic method with the same
signature; the redeclaration reuses the inherited dispatch slot, and lookup
prefers it over the inherited declaration. Exact specialization and primary
fallback work through the child interface. A child interface may also add a
same-named generic overload with a distinct parameter signature; both slots
resolve independently, including exact specialization and primary fallback.
Both branches of a diamond may independently redeclare the same inherited
generic method signature. The shared contract resolves to one implementation
target through the diamond, preserving exact specialization and primary-body
fallback.
The same diamond shape also works across a project reference: a library-owned
generic forwarding caller reaches an app-defined implementation. Native static
and DLL runs cover its exact specialization and primary-body fallback.
Same-signature redeclarations that change the return type or generic constraints
are rejected during binding. A library method may call a generic interface
contract whose implementation and exact method specialization are defined by a
consuming application; native static and DLL builds verify the consumer
registers and dispatches that implementation. A library generic caller may also
forward its closed type parameter through that interface contract; native
coverage checks exact specialization and primary fallback under both link modes.
The same caller path works through a constructed generic interface implemented
by a closed generic class defined in the consumer; native tests close the owner
and method arguments explicitly and verify exact and fallback bodies in both
link modes. A generic `void` caller may also forward one matching type parameter
through a generic virtual interface method; exact specialization and primary
fallback are verified natively across the project boundary.
Calls made recursively from a generic virtual override use the inherited contract identity for runtime lookup, so self-calls find the same runtime registration as calls through a base-typed receiver. Compiler binding and native execution cover exact-specialization recursion.
Cross-project class virtual dispatch supports a `class` plus interface constraint
when a library generic caller reaches a consumer-defined override. Native static
and DLL builds verify an exact specialization and primary fallback. The same
caller path supports `struct` plus one or two interface constraints for
consumer-defined value types, and `class` plus two-interface constraints for
consumer-defined reference types. Generic virtual interface calls support
both `class` and `struct` with one interface constraint and both constraints
with two interfaces in cross-project callers. Other combined constraint cases
and cross-project combinations remain open.
Generic virtual interface dispatch also supports `struct` combined with two
interface constraints across a project reference. A library caller dispatches
to a consumer implementation with an exact specialization and primary-body
fallback under static and DLL linking. The corresponding `class` plus two
interface constraints also dispatches to a consumer implementation with exact
specialization and primary fallback under both link modes. The `class` plus
one-interface case is also covered across the project boundary, with a library
caller reaching a consumer implementation's exact specialization and using its
primary body for another implementing class.
The `struct` plus one-interface case follows the same cross-project path: a
library caller reaches a consumer implementation's exact specialization and
uses its primary body for another implementing struct under static and DLL
linking.
Cross-project interface dispatch also supports `class` plus three interface
constraints. Native static and DLL builds verify an exact consumer-defined
specialization and primary-body fallback for another implementation satisfying
all four constraints.
The corresponding `struct` plus three-interface case also dispatches across a
project boundary, with a consumer-defined exact specialization and primary
fallback for another struct satisfying all four constraints in static and DLL
native runs.
A child interface declared in a library may inherit a generic method and add a
same-named overload with a distinct parameter signature. A library caller can
dispatch both contracts to a consumer implementation; native static and DLL
checks cover exact specialization and primary fallback for each signature.
Class override chains also support method-level `class` constraints: an exact
closed specialization on the derived override is selected through a base-typed
receiver, while another reference type uses the derived primary body.
They also support the combined `class, interface` constraint, with an exact
closed specialization selected through a base-typed receiver and primary-body
fallback for another implementing class.
Class virtual methods and overrides support the special `struct` constraint as
well, with an exact `int` specialization and primary-body fallback for `long`.
Generic caller bodies may directly return one supported forwarding call, store
the call in one matching type-parameter local and return it, or select between
one virtual call and a matching type-parameter value in a conditional
expression. The virtual call can occupy either branch. Closed method arguments
and receiver types must be known for each caller specialization. Calls through
constructed generic interfaces substitute the caller's type arguments and the
receiver interface's owner arguments before closing the contract signature.
Statement-level forwarding also supports an `if` branch that returns the
virtual call and a matching type-parameter fallback, plus `if/else` forms with
the call in either branch. These return branches may be wrapped in a single
statement block. Leading explicit `bool` locals with supported boolean
initializers and inferred `var` locals with supported boolean initializers may
provide the branch condition. Inferred locals are emitted as `cx_bool` after
the initializer is validated as boolean. Other local and control-flow shapes
remain diagnosed.
Before a direct forwarding return or generic `void` forwarding statement, a
caller may copy its matching type-parameter argument into one explicitly typed
local and pass that local to the virtual method. Native cross-project checks
verify exact specialization and fallback for both caller forms in static and
DLL builds.
The same alias may be used by one branch of a conditional return, with the
other branch returning the alias value. Cross-project native checks cover the
dispatch branch's exact specialization and fallback and the no-dispatch branch
under both link modes.
Statement-level `if` flow also supports one explicitly typed alias local before
the branch, when the virtual call consumes the alias and the fallback returns
that same alias. Static and DLL cross-project native checks cover exact dispatch
and fallback.
The same alias may be returned from the `if` branch while the virtual call runs
in `else`; a leading supported boolean local may control that branch. Static
and DLL cross-project native checks cover both outcomes.
The boolean local may also precede the explicitly typed alias local; both
initializers are emitted before the statement-level branch, and cross-project
native checks cover exact dispatch and alias fallback under static and DLL
linking.
An inferred `var` local may copy the caller's matching type parameter and feed
the virtual-call branch while the other branch returns that alias. The compiler
closes the local with each caller specialization; native checks cover exact
specialization and primary-body fallback through a project reference under
static and DLL linking.
Typed aliases may copy from an earlier typed alias of the same caller type
parameter, forming a local copy chain before statement-level forwarding. The
compiler resolves and emits each local in declaration order; cross-project
native checks cover exact dispatch and fallback in both link modes.
The same supported forwarding-call body may appear in a generic method on a
closed generic class. The caller's closed owner arguments are substituted along
with its method arguments before closing the virtual contract; native tests
cover an owner argument forwarded as a generic method argument, with exact
specialization and primary fallback across a project reference.
Constructed generic runtime classes work when their closed reference layout is
representable, including a generic derived class inheriting a generic virtual
method from a non-generic base. Other generic caller body shapes remain
diagnosed. Generic classes requiring unsupported closed value layouts or
unsupported virtual overrides remain diagnosed. Runtime loading of derived
types outside the compiled project graph is not supported. Generic virtual
method values use the same runtime registry as calls: adapters preserve the
closed signature, dispatch against the receiver's runtime type, and retain
method/target reflection metadata. Native checks cover class and interface
method values, derived specialization selection, invocation, and reflected
method names. Broader cross-project combinations, constraints, and generic
additional containing-class cases remain open when the owner type is not closed
at the call site or owner arguments occur in unsupported receiver positions.

## Design requirements

- Give each generic virtual method a stable method identity that survives
  inheritance, generic containing-type closure, and project references.
- Carry the closed method type arguments to runtime dispatch, including when a
  generic caller forwards its own closed type parameters.
- Resolve the implementation against the receiver's runtime class, including
  inherited implementations and overrides that have no explicit
  specialization for the requested type arguments.
- Use the selected override's ordinary generic body as fallback when no exact
  specialization exists. A specialization is not an overload and cannot
  bypass the primary method's constraints.
- Preserve exact closed parameter and return layouts for value types, structs,
  arrays, and constructed generic types.
- Keep virtual calls across separately compiled project boundaries; the
  consumer must be able to register derived implementations for closed
  method-argument tuples required by its referenced module graph.
- Keep ordinary non-virtual generic calls direct when possible.
- Define reflection metadata and method-value behavior consistently with the
  selected closed implementation.

## Proposed runtime representation

Keep ordinary vtable slots for non-generic virtual methods. Add a runtime
registry for closed generic virtual implementations. A registry key contains
the runtime type descriptor, a stable identity for the generic virtual method,
and the ordered closed method type descriptors. Its value is a generated
typed thunk plus a stable selected-implementation identity. Repeated module
registrations for the same implementation are idempotent; conflicting bodies
for the same dispatch key are rejected. The thunk adapts the base receiver
pointer to the implementation owner and calls the ordinary typed
generic-method instance.

At a generic virtual call, the compiler passes the receiver's runtime type and
the statically closed method type descriptors to a runtime lookup, then calls
the returned thunk using the exact closed C signature. This keeps value types
unboxed and avoids routing method arguments through a universal boxed ABI.
For every known runtime class, binding selects the implementation from that
class's virtual slot, including inherited implementations, and registers it
under the runtime class's own type descriptor. When an override has no explicit
specialization, its primary body is the registered target. An exact
specialization replaces that primary body only for its closed argument tuple.

Registration belongs to the module that can emit the typed implementation.
The compiler must therefore export generic virtual call requirements through
project references, and consuming modules must register applicable closed
implementations for concrete classes they define. Module initialization must
be safe when a registration appears before or after the base module's
initialization. Registration keys use type descriptor identity, not only
hashes, so collisions cannot select the wrong method.

This design preserves typed bodies and supports separately compiled base and
derived classes. It does require the linked module graph to register each
closed type-argument tuple used by generic virtual call sites. Runtime loading
of an unreferenced module that introduces a new derived type is a separate
open-world/JIT problem and must not be claimed as supported by this slice.

Project-reference contexts retain the bound generic virtual call and method
identity. A consuming project scans those calls against its complete known
class graph and emits registrations for concrete derived classes it owns. This
covers the closed compiled project graph without requiring a runtime hierarchy
walk or a call-site type switch.

## Acceptance cases

- A virtual generic primary and exact specialization in its declaring class.
- A derived override with its own exact specialization, plus fallback to its
  generic body when no specialization matches.
- A method-level `class` constraint on a virtual declaration and its derived
  override, with an exact specialization selected through a base-typed receiver
  and primary-body fallback for another reference type.
- A combined `class, interface` constraint on a virtual declaration and its
  derived override, with exact specialization and primary-body fallback through
  a base-typed receiver.
- The special `struct` constraint on a virtual declaration and its derived
  override, with exact specialization for one value type and primary-body
  fallback for another.
- Base-typed and derived-typed calls selecting the same runtime override.
- An inherited generic method called through a derived receiver without a new
  override.
- Generic constraints, overload selection, recursive calls, and closed generic
  containing classes.
- Abstract generic declarations and concrete overrides.
- A generic caller selects between one virtual call and a matching type-parameter value in a conditional expression, with the call in either branch.
- A generic caller uses statement-level `if` or `if/else` control flow to return one virtual call and a matching type-parameter fallback, with the call in either branch.
- Calls from generic callers whose method type arguments are forwarded.
- A generic method declared on non-generic and constructed generic interfaces,
  implemented by ordinary and closed generic classes, and called through an
  interface value, with exact specialization and primary-body fallback; also
  cover forwarding through a generic caller.
- A generic virtual method called through a three-level interface inheritance
  chain, with exact specialization and primary-body fallback.
- A generic virtual method called through a diamond interface that shares one
  base contract, with one implementation target and exact/fallback behavior,
  including same-signature redeclarations on both branches.
- Base and derived declarations split across referenced projects, including a
  base library whose non-generic method contains a generic virtual call.
- Generated C confirms the runtime receiver determines the override for both
  custom and fallback closed bodies.
- Native execution with multiple derived runtime types and multiple closed
  method argument sets, including at least one value type and one reference
  type.
- Delegate binding, invocation, and reflection report the closed method
  signature and runtime target consistently for class and interface receivers.

Keep explicit diagnostics for unsupported generic caller body shapes,
constructed generic interface combinations, and generic runtime classes whose
closed layouts or overrides are not supported. Never lower these calls as
direct non-virtual calls.

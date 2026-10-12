# Extension methods on array types

## Scope

An extension declaration may target a concrete array type such as `int[]` or
`string[][]`, or an open element pattern such as `T[]`, `T[][]`, or
`Box<T>[]`. Concrete targets match exactly. Open targets infer their generic
arguments during extension lookup and close them through the extension owner and method signatures. Methods
use the array value as the extension receiver and follow the existing extension
namespace, visibility, overload, and code-generation rules.

Array targets support ordinary extension methods and non-mutating unary and
binary operators. Concrete and generic array targets also support read-only
extension properties when the property type and accessor parameters are
concrete and do not depend on the array element type. Concrete and generic
array targets may also define `+=`, `-=`, `*=`, `/=`, and `%=` when each
operator accepts its array target type and returns that same type; generic
operators close from the receiver element type. The assignment writes the
returned reference back to the left operand. Increment/decrement operators
may also return the matching array target type; prefix and postfix expressions
write that reference back while preserving the postfix old value. Constructors
remain unsupported.
Open patterns may use a generic leaf
element directly or through a constructed generic element type, and may include
supported jagged array ranks. This design does not add a new array runtime
representation.

## Acceptance

- Parse and bind concrete and generic array-target extension methods, including
  jagged arrays, constructed generic element types, and multiple element type
  parameters.
- Resolve concrete methods by exact array type and generic methods by matching
  the element type, when the extension namespace is imported.
- Permit the extension body to use the receiver's existing array operations,
  including `Length` and indexing.
- Emit a concrete read-only array extension property that reads the array
  receiver.
- Permit generic array-target properties when property and accessor signatures
  are concrete, and reject element-type-dependent property signatures.
- Emit concrete and generic non-mutating array extension operators with the
  array as the left receiver.
- Emit concrete and generic array compound assignments with matching array
  parameter and return types, and write the returned reference back to the
  writable left operand.
- Emit concrete and generic array increment/decrement operators with matching
  array return types, including prefix/postfix write-back behavior.
- Emit an ordinary extension function taking the array receiver; runtime ABI
  version 1 remains unchanged.
- Emit closed generic array-extension methods with substituted element types.
- Reject a call on a different array element type.
- Preserve existing primitive, enum, class, struct, and interface extensions.

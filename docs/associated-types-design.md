# Suggestion: associated types for concepts

This is a candidate language direction only. The syntax and semantics below
are not adopted, implemented, or part of the current language contract. Keep
the grammar and compiler unchanged unless the user chooses this feature.

## Purpose and scope

Associated types let a compile-time concept name a type selected by each
implementation. They extend the existing structural concept system without
adding runtime metadata, interface tables, or dispatch. This design covers the
first usable slice: one required associated type, a public mapping on a class or
struct, and its use in required method signatures and generic function bodies.

The first slice does not infer associated types from method bodies, support
generic associated types, or permit associated types as runtime values. Multiple
associated types and inherited associated-type requirements can follow once the
single-type rules are stable.

## Source contract

A concept declares an associated type with a name-only requirement:

```cx
concept CParse {
    associated type Result;
    Result Parse(const string text) const;
}
```

A class or struct that satisfies the concept supplies a public type mapping:

```cx
public struct IntParser {
    public alias Result = int;

    public int Parse(const string text) const {
        return 0;
    }
}
```

`associated type Name;` is legal only inside a concept. `associated` is a
keyword; `type` is recognized contextually here and remains a valid identifier
elsewhere.
`public alias Name = Type;` is legal inside a class or struct as an associated-type mapping;
ordinary aliases remain module declarations. Mappings must name a concrete,
non-void type that is valid in a field, parameter, or return position. A type
parameter may not map itself or form an alias cycle.

Within a concept requirement or a generic body whose parameter `T` is
constrained by that concept, `T.Name` refers to the selected associated type.
For example:

```cx
public T.Result Parse<T>(T parser, const string text) : T = CParse {
    return parser.Parse(text);
}
```

`T.Name` is a type expression, not a runtime member access. Outside a scope
where `T` has a satisfied concept constraint declaring `Name`, it is an error.

## Binding and specialization

At each generic specialization, resolve every associated type requirement from
the concrete class or struct mapping, substitute that concrete type throughout
the required signatures and supported generic body, then perform the existing
exact concept-member matching. The implementation method must have the exact
substituted parameter and return types and the required constness, visibility,
and instance/static shape. No numeric, reference, or user-defined conversion
can make a mismatched associated type implementation satisfy a requirement.

Associated-type mappings are part of concept satisfaction, not ordinary
inheritance. A mapping may reference a previously declared module alias, but
two mappings for the same name in one type are rejected. Public concepts and
public implementing types can be used across project references; the mapping
must be exported with the implementing type's semantic metadata.

The initial body subset substitutes `T.Name` in explicit local declarations,
parameters and return types, and direct required-method calls and returns.
Generic method inference may infer an associated type only after the concrete
type argument has been chosen from ordinary parameters; it must not use a
return type alone to guess `T`.

## Diagnostics

Binding reports an error for an absent or non-public mapping, duplicate
mapping, mapping to `void`, unresolved mapping, cyclic alias, undeclared
`T.Name`, a concept requirement whose associated type is not supplied, or an
implementation method whose substituted signature does not match. Diagnostics
identify the concept, associated name, and concrete implementing type when
available.

## Acceptance criteria

- A concept may declare one associated type and use it in a required method's
  parameter and return types.
- A public class and struct may satisfy the concept with different concrete
  mappings, and a generic function may call the required method and return its
  associated result for both implementations.
- Explicit locals and method parameters using `T.Name` lower to the selected
  concrete C type without emitting an associated-type runtime object.
- Missing, duplicate, inaccessible, unresolved, cyclic, and `void` mappings
  produce source-located diagnostics.
- A method with a mismatched substituted parameter or return type fails
  structural satisfaction even when an implicit conversion exists.
- A public concept, its generic consumer, and the implementing mapping work
  across a project reference.
- Existing concepts without associated types compile and preserve generated C
  and runtime behavior.

## Implementation sequence

1. Extend the syntax model for concept type requirements and type mappings;
   add parser tests and duplicate/placement diagnostics.
2. Add associated-name resolution to generic type binding and substitute the
   closed mapping into concept method signatures.
3. Extend concept satisfaction and generic specialization for the initial
   body subset; add positive, negative, and generated-C tests.
4. Export mapping metadata across project references and run a native fixture
   using both a class and struct implementation.
5. Update the language reference and roadmap only after each supported form is
   implemented and verified.

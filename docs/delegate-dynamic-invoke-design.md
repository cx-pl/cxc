# Dynamic invocation for function values

## Scope

`DynamicInvoke(object[] arguments)` provides runtime invocation for a named
delegate or anonymous function value when its signature has a concrete,
reflection-supported CX type. It reuses the typed invocation path, including
multicast order and last-handler return behavior.

- `arguments` must contain exactly one boxed value per parameter. Reference
  arguments may be `null`; value arguments must have the exact runtime type.
- A wrong argument count or incompatible runtime type returns `null` without
  invoking the delegate. A null/default function value also returns `null`.
- Value-type returns are boxed as `object`; reference and array returns are
  returned directly. A `void` delegate returns `null`.
- Open generic signatures, pointers, nested function/delegate values, and other
  runtime-only signatures are rejected during binding.

The compiler emits one signature-specific bridge that validates and unboxes
arguments, invokes the existing typed helper, and boxes a value result. This
uses existing runtime boxing/type-checking declarations and keeps ABI version 1.

## Acceptance

- Compiler tests cover value arguments and returns, `void` returns, instance
  method values, wrong argument count, and incompatible boxed values.
- Native tests verify runtime boxing/unboxing and that invalid count/type cases
  do not invoke a handler.
- Existing typed invocation, multicast ordering, and null/default behavior
  remain unchanged.

# Static array field initializers

Non-generic static fields may allocate arrays in their module initializer:

```cx
public static class SharedValues {
    public static int[] Values = new int[2];
    public static string[] Names = new string[0];
}
```

The compiler registers static root regions before running dynamic field
initializers, so the allocated arrays remain visible to the collector. Module
initialization follows referenced-module order, then declaration order within a
module; static constructors run after dynamic field initializers. Array element
types and length expressions follow the ordinary array creation rules. This
does not add generic static fields to generic classes, which remain outside the
supported generic layout model.

## Acceptance

- Generated C allocates static arrays in the dynamic module initializer after
  static-root registration and before the module is marked initialized.
- Native execution reads lengths and writes elements of static `int[]` and
  `string[]` fields.
- A project-reference native fixture verifies that the consumer observes the
  initialized arrays with both static and DLL linking.

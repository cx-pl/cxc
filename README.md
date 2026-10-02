# cxc
cx language compiler

## Runtime reference conversions

CX uses C-style syntax for explicit checked reference casts, for example
`(Derived)value`, and `value is Derived` for a run-time type test. Checked casts
support class downcasts, interface-to-class conversions, and interface-to-interface
conversions. Casting `null` produces `null`; a type test against `null` is false.

Generated type information includes declared fields, functions, constructors,
property accessors, parameters, implemented interfaces, nested types, and generic
arity. Metadata is read-only; reflection invocation and dynamic construction are
not currently supported.

## Generated build files

Compiling a `.cx` source or `.cxproj` writes the generated C source, header, and
`CMakeLists.txt` to the project’s `.obj/` directory. The generated CMake target
also includes native `.c` files under the project source tree, excluding `.obj/`,
`.bin/`, and `.git/`. Runtime executables and shared libraries are written to
`.bin/`; archive and object build files remain under `.obj/`.

Build the generated target with CMake from the project directory:

```powershell
cmake -S .obj -B .obj/build
cmake --build .obj/build
```

The compiler adds root-anchored `/.obj/` and `/.bin/` entries to the project’s
`.gitignore` so generated files are not added to Git by accident.

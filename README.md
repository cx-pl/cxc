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

## Conditional compilation

CX supports `#if`, `#elif`, `#else`, `#endif`, `#define`, `#undef`, `#error`,
and `#warning` directives. Conditional expressions accept symbols, `true` and
`false`, parentheses, `!`, `&&`, `||`, `==`, and `!=`. Symbols are
case-sensitive. A source-level `#define` or `#undef` applies from that line to
the end of that source file.

Pass target symbols to the compiler with `-D` or `--define`:

```powershell
cxc -D CX_CPU_X64 --define CX_OS_WINDOWS app.cxproj
```

For example, source can select platform-specific CX code without a C shim:

```cx
#if CX_OS_WINDOWS
public void UsePlatformApi() { /* Windows implementation */ }
#elif CX_OS_LINUX
public void UsePlatformApi() { /* Linux implementation */ }
#else
#error Unsupported operating system
#endif
```

Inactive branches are omitted before parsing, and source line numbers are
preserved for diagnostics.

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

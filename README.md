# cxc
cx language compiler

The current supported language subset is summarized in the
[`language reference`](docs/language-reference.md). CX and its generated-code
ABI are still experimental; binary compatibility is not promised.

Show compiler version and CLI usage with:

```powershell
cxc --version
cxc --help
```

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

## Command line

The compiler emits generated C and CMake files without invoking a native build
by default. Use `--compile` to configure and build them with CMake; `cxcore` is
located from `CXCORE_SOURCE_DIR` or a nearby `cxcore` checkout, or can be selected
with `--cxcore-dir`.

```powershell
cxc --output-dir .build/generated --module-name SampleApp --compile app.cx
```

`--emit-only` selects generation without building. `--verbosity` accepts
`quiet`, `normal`, or `verbose`, and `--diagnostics-format` accepts `text` or
`json` (JSON Lines on standard error). `--configuration Debug` builds with
native debug information; `Release` is the default. Generated C includes CX
source line directives so native debugger symbols can map statements back to
their `.cx` files. Command-line errors return exit code 2;
source, generation, and native-build failures return exit code 1.

## Executable entry point

For executable projects, one top-level CX `Main` function generates the native
C entry point. Supported signatures are `void Main()`, `int Main()`,
`void Main(string[] args)`, and `int Main(string[] args)`. The `args` array
contains command-line arguments without the executable path; on Windows,
arguments are converted from UTF-16 to CX's UTF-8 string representation. An
`int` result becomes the process exit code, while a `void` result exits with 0.
Library projects do not generate a native entry point.

## Generated build files

Compiling a `.cx` source or `.cxproj` writes the generated C source, header, and
`CMakeLists.txt` to the project’s `.obj/` directory unless `--output-dir` is
provided. The generated CMake target
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

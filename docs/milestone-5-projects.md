# Milestone 5 project references

The compiler uses explicit `project_references` paths for local CX project
edges. Each path is relative to the referring `.cxproj`. This stays separate
from `dependencies`, which cxpm uses for package IDs and version ranges.

Project references form a directed acyclic graph. The compiler resolves paths,
rejects missing, duplicate, and cyclic references, and visits dependencies in
ordinal project-name order. It parses referenced declarations as public API
metadata while emitting each project's own source in its own `.obj` directory.
Generated CMake adds and links referenced project targets. For a project graph,
referenced CX libraries are compiled as static libraries and linked into the
consuming executable or shared library. This keeps each final native image on
one canonical set of CX runtime type-info objects. Building a library project
by itself still produces a shared library in `.bin`. Top-level declarations
and members from a referenced project must be public to participate in semantic
lookup. Source files within each project are also visited in ordinal path order.

Generated CMake can add a local `cxcore` checkout by passing
`-DCXCORE_SOURCE_DIR=<path-to-cxcore>` during configure. It builds cxcore as a
static target for the final native image by default. Setting CX_STATIC_LINK to
OFF builds cxcore as a shared library with referenced projects. With no
CXCORE_SOURCE_DIR value, the generated project can still be used where
cxcore is supplied by the surrounding build; that setup is suitable for C
function APIs that do not expose CX class or interface data.

Public generic functions and generic type layouts are available across projects.
When a referenced project's public API already exposes a closed specialization,
consumers reuse that specialization's declarations and implementation. Otherwise,
the consuming project emits the closed layout and runtime metadata locally.
Specialized metadata owns its name and namespace strings, so it does not require
imported data references.

Cross-project classes, interfaces, public value layouts, interface tables, and
dispatch thunks are covered in both static and dynamic project-reference builds.
Static linking remains the default. Configure generated CMake with
-DCX_STATIC_LINK=OFF to build referenced CX projects and cxcore as DLLs. In
dynamic mode, each generated module exports a thread-safe initializer, initializes
its dependencies first, and fills runtime metadata that contains imported data
references. Generated CX functions call the initializer before executing, so
class pointers and interface values can cross project DLLs.

The language has no source-level static initialization blocks. Generated module
initializers therefore follow the deterministic dependency graph and do not
need a separate source-initializer ordering scheme.

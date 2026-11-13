"""Inspect an exported module descriptor without calling module initialization.

Usage: python probe-module-info.py LIBRARY MODULE_NAME [REFERENCE_NAME ...]
"""
import ctypes
import os
from pathlib import Path
import sys


class ModuleInfo(ctypes.Structure):
    pass


class TypeInfoPrefix(ctypes.Structure):
    _fields_ = [("hash", ctypes.c_uint64), ("flags", ctypes.c_uint32),
                ("size", ctypes.c_uint32)]


class AnnotationArgument(ctypes.Structure):
    _fields_ = [("name", ctypes.c_char_p), ("expression", ctypes.c_char_p), ("value", ctypes.c_void_p)]


class AnnotationInfo(ctypes.Structure):
    _fields_ = [("declaring_type", ctypes.POINTER(TypeInfoPrefix)),
                ("function_index", ctypes.c_int32), ("parameter_index", ctypes.c_int32),
                ("target", ctypes.c_char_p), ("member_name", ctypes.c_char_p),
                ("type_name", ctypes.c_char_p),
                ("arguments", ctypes.POINTER(AnnotationArgument)),
                ("argument_count", ctypes.c_uint32)]


Provider = ctypes.CFUNCTYPE(ctypes.POINTER(ModuleInfo))
TypeProvider = ctypes.CFUNCTYPE(ctypes.POINTER(TypeInfoPrefix))


class ArrayReader(ctypes.Structure):
    _fields_ = [("declaring_type", ctypes.POINTER(TypeInfoPrefix)),
                ("function_index", ctypes.c_uint32), ("read", ctypes.c_void_p),
                ("element_type", TypeProvider)]


class GenericType(ctypes.Structure):
    _fields_ = [("type", TypeProvider), ("arguments", ctypes.POINTER(TypeProvider)),
                ("argument_count", ctypes.c_uint32)]


class AnnotationSource(ctypes.Structure):
    _fields_ = [("file", ctypes.c_char_p), ("line", ctypes.c_uint32),
                ("column", ctypes.c_uint32)]


ModuleInfo._fields_ = [
    ("format_version", ctypes.c_uint32),
    ("name", ctypes.c_char_p), ("version", ctypes.c_char_p),
    ("description", ctypes.c_char_p), ("author", ctypes.c_char_p),
    ("types", ctypes.POINTER(ctypes.POINTER(TypeInfoPrefix))),
    ("type_count", ctypes.c_uint32),
    ("references", ctypes.POINTER(Provider)),
    ("reference_count", ctypes.c_uint32),
    ("annotations", ctypes.POINTER(AnnotationInfo)),
    ("annotation_count", ctypes.c_uint32),
    ("array_readers", ctypes.POINTER(ArrayReader)),
    ("array_reader_count", ctypes.c_uint32),
    ("generic_types", ctypes.POINTER(GenericType)),
    ("generic_type_count", ctypes.c_uint32),
    ("annotation_sources", ctypes.POINTER(AnnotationSource)),
]

library_path = Path(sys.argv[1]).resolve()
module_name = sys.argv[2]
dll_directory = os.add_dll_directory(str(library_path.parent)) if os.name == "nt" else None
library = ctypes.CDLL(str(library_path))
token = "".join(c.upper() if c.isalnum() else "_" for c in module_name)
provider = getattr(library, "__cx_module_info_" + token)
provider.restype = ctypes.POINTER(ModuleInfo)
pointer = provider()
module = pointer.contents
assert module.format_version == 1
assert module.name.decode() == module_name
assert module.type_count > 0
assert ctypes.addressof(provider().contents) == ctypes.addressof(module)
exported = ModuleInfo.in_dll(library, token + "___module_info")
assert ctypes.addressof(exported) == ctypes.addressof(module)
assert all(module.types[i].contents.flags & 0x100 for i in range(module.type_count))
references = [module.references[i]().contents.name.decode()
              for i in range(module.reference_count)]
assert references == sorted(set(sys.argv[3:]))
annotation_count = module.annotation_count
for index in range(annotation_count):
    annotation = module.annotations[index]
    assert annotation.declaring_type.contents.flags & 0x100
    assert annotation.type_name and annotation.target
    for argument_index in range(annotation.argument_count):
        argument = annotation.arguments[argument_index]
        assert argument.name and argument.expression is not None
        assert argument.value
if annotation_count:
    assert module.annotation_sources
    for index in range(annotation_count):
        source = module.annotation_sources[index]
        assert source.file and source.line > 0 and source.column > 0
for index in range(module.array_reader_count):
    reader = module.array_readers[index]
    assert reader.declaring_type.contents.flags & 0x100
    assert reader.read and reader.element_type().contents.hash
for index in range(module.generic_type_count):
    generic = module.generic_types[index]
    assert generic.type().contents.hash
    assert generic.argument_count > 0
    assert all(generic.arguments[i]().contents.hash for i in range(generic.argument_count))
print(f"{module_name}: {module.type_count} public types; references={references}; "
      f"annotations={annotation_count}")

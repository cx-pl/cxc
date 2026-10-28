#include <assert.h>
#include <stdlib.h>

#include "generic_primitive_box.h"

struct cxcore_System_Reflection_TypeInfo cxcore_System_Object___typeinfo;
struct cxcore_System_Reflection_TypeInfo cxcore_System_Void___typeinfo;
struct cxcore_System_Reflection_TypeInfo cxcore_System_Int___typeinfo;
union cx_vtable_entry cxcore_System_String___vtable[1];

cx_ptr cxcore_System_Memory_Alloc(cx_uint size) {
    return calloc(1, size);
}

void cxcore_System_Object___constructor(struct cxcore_System_Object * value) {
    (void)value;
}

int main(void) {
    cx_ptr box = generic_primitive_box_Create();
    const struct cxcore_System_Reflection_TypeInfo *info = &CX_GET_TYPEINFO(box);

    assert(box != NULL);
    generic_primitive_box_Write(box, 71);
    assert(generic_primitive_box_Read(box) == 71);
    assert(((const struct cx_runtime_type_info *)info->RuntimeTypeInfo)->fieldCount == 1);
    assert(info->Size > 0);
    free(box);
    return 0;
}

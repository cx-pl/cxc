#include <assert.h>
#include <stdlib.h>

#include "generic_primitive_box.h"

struct CX_ID_4(cxcore, System, Reflection, TypeInfo) CX_ID_4(cxcore, System, Object, __typeinfo);
struct CX_ID_4(cxcore, System, Reflection, TypeInfo) CX_ID_4(cxcore, System, Void, __typeinfo);
struct CX_ID_4(cxcore, System, Reflection, TypeInfo) CX_ID_4(cxcore, System, Int, __typeinfo);
union cx_vtable_entry CX_ID_4(cxcore, System, String, __vtable)[1];

cx_ptr CX_ID_4(cxcore, System, Memory, Alloc)(cx_uint size) {
    return calloc(1, size);
}

void CX_ID_4(cxcore, System, Object,
             __constructor)(struct CX_ID_3(cxcore, System, Object) * value) {
    (void)value;
}

int main(void) {
    cx_ptr box = CX_ID_2(generic_primitive_box, Create)();
    const struct CX_ID_4(cxcore, System, Reflection, TypeInfo) *info = &CX_GET_TYPEINFO(box);

    assert(box != NULL);
    CX_ID_2(generic_primitive_box, Write)(box, 71);
    assert(CX_ID_2(generic_primitive_box, Read)(box) == 71);
    assert(((const struct cx_runtime_type_info *)info->RuntimeTypeInfo)->fieldCount == 1);
    assert(info->Size > 0);
    free(box);
    return 0;
}

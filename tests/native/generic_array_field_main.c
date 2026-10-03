#include <assert.h>
#include <stdlib.h>

#include "generic_array_field.h"

struct CX_ID_4(cxcore, System, Reflection, TypeInfo) CX_ID_4(cxcore, System, Object, __typeinfo);
struct CX_ID_4(cxcore, System, Reflection, TypeInfo) CX_ID_4(cxcore, System, Void, __typeinfo);
struct CX_ID_4(cxcore, System, Reflection, TypeInfo) CX_ID_4(cxcore, System, Int, __typeinfo);
struct CX_ID_4(cxcore, System, Reflection, TypeInfo) CX_ID_4(cxcore, System, Array, __typeinfo);
union cx_vtable_entry CX_ID_4(cxcore, System, String, __vtable)[1];

cx_ptr CX_ID_4(cxcore, System, Memory, Alloc)(cx_uint size) {
    return calloc(1, size);
}

void CX_ID_4(cxcore, System, Object,
             __constructor)(struct CX_ID_3(cxcore, System, Object) * value) {
    (void)value;
}

int main(void) {
    cx_ptr buffer = CX_ID_2(generic_array_field, Create)();
    cx_int token = 0;
    struct CX_ID_3(cxcore, System, Array) *values = (struct CX_ID_3(cxcore, System, Array) *)&token;
    assert(buffer != NULL);
    CX_ID_2(generic_array_field, Store)(buffer, values);
    assert(CX_ID_2(generic_array_field, Load)(buffer) == values);
    free(buffer);
    return 0;
}

#include <assert.h>
#include <stdlib.h>

#include "generic_array_field.h"

struct cxcore_System_Reflection_TypeInfo cxcore_System_Object___typeinfo;
struct cxcore_System_Reflection_TypeInfo cxcore_System_Void___typeinfo;
struct cxcore_System_Reflection_TypeInfo cxcore_System_Int___typeinfo;
struct cxcore_System_Reflection_TypeInfo cxcore_System_Collections_Array___typeinfo;
union cx_vtable_entry cxcore_System_String___vtable[1];

cx_ptr cxcore_System_Memory_Alloc(cx_uint size) {
    return calloc(1, size);
}

void cxcore_System_Object___constructor(struct cxcore_System_Object * value) {
    (void)value;
}

int main(void) {
    cx_ptr buffer = generic_array_field_Create();
    cx_int token = 0;
    struct cxcore_System_Collections_Array *values = (struct cxcore_System_Collections_Array *)&token;
    assert(buffer != NULL);
    generic_array_field_Store(buffer, values);
    assert(generic_array_field_Load(buffer) == values);
    free(buffer);
    return 0;
}

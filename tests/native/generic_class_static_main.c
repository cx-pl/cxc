#include <assert.h>
#include <stdlib.h>

#include "generic_class_static.h"

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
    assert(generic_class_static_Utility_Run() == 17);
    return 0;
}

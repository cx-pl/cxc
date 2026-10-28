#include <assert.h>
#include <stdlib.h>

#include "generic_instance_method.h"

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
    struct generic_instance_method_Utility *utility =
        generic_instance_method_Create();
    assert(utility != NULL);
    assert(generic_instance_method_RunInt(utility) == 7);
    assert(generic_instance_method_RunLong(utility) == 9);
    assert(generic_instance_method_RunCopy(utility) == 11);
    assert(generic_instance_method_Utility_RunImplicit(utility) == 17);
    free(utility);
    return 0;
}

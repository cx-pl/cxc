#include <assert.h>
#include <stdlib.h>

#include "generic_instance_method.h"

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
    struct CX_ID_2(generic_instance_method, Utility) *utility =
        CX_ID_2(generic_instance_method, Create)();
    assert(utility != NULL);
    assert(CX_ID_2(generic_instance_method, RunInt)(utility) == 7);
    assert(CX_ID_2(generic_instance_method, RunLong)(utility) == 9);
    assert(CX_ID_2(generic_instance_method, RunCopy)(utility) == 11);
    assert(CX_ID_3(generic_instance_method, Utility, RunImplicit)(utility) == 17);
    free(utility);
    return 0;
}

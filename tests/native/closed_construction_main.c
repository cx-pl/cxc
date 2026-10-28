#include <assert.h>
#include <stdlib.h>

#include "closed_construction.h"

struct cxcore_System_Reflection_TypeInfo cxcore_System_Object___typeinfo;
struct cxcore_System_Reflection_TypeInfo cxcore_System_Void___typeinfo;
union cx_vtable_entry cxcore_System_String___vtable[1];

cx_ptr cxcore_System_Memory_Alloc(cx_uint size) {
    return malloc(size);
}

void cxcore_System_Object___constructor(struct cxcore_System_Object * value) {
    (void)value;
}

int main(void) {
    struct closed_construction_Marker *first =
        closed_construction_CreateFirst();
    struct closed_construction_Marker *second =
        closed_construction_CreateSecond();

    assert(first != NULL && second != NULL);
    assert(CX_GET_VTABLE(first) ==
           cx_generic_c1bc3a22b2fee75987cc761d98926a2721fb11f81fa79e816423796cf6744c45___vtable);
    assert(CX_GET_VTABLE(second) ==
           cx_generic_7e02eb5a1592938da5069eac0c8123125a2026a9c92d109e4aedb047cb222e93___vtable);
    assert(CX_GET_TYPEINFO(first).Hash != CX_GET_TYPEINFO(second).Hash);
    free(first);
    free(second);
    return 0;
}

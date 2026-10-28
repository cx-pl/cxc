#include <assert.h>
#include <stdlib.h>

#include "generic_struct_pair.h"

struct cxcore_System_Reflection_TypeInfo cxcore_System_Object___typeinfo;
struct cxcore_System_Reflection_TypeInfo cxcore_System_Void___typeinfo;
struct cxcore_System_Reflection_TypeInfo cxcore_System_Int___typeinfo;
struct cxcore_System_Reflection_TypeInfo cxcore_System_Long___typeinfo;
union cx_vtable_entry cxcore_System_String___vtable[1];

cx_ptr cxcore_System_Memory_Alloc(cx_uint size) {
    return calloc(1, size);
}

void cxcore_System_Object___constructor(struct cxcore_System_Object * value) {
    (void)value;
}

int main(void) {
    struct generic_struct_pair_First first = {17};
    struct generic_struct_pair_Second second = {29};
    cx_ptr pair = generic_struct_pair_Create();

    assert(pair != NULL);
    generic_struct_pair_WriteFirst(pair, first);
    generic_struct_pair_WriteSecond(pair, second);
    assert(generic_struct_pair_ReadFirst(pair).value == 17);
    assert(generic_struct_pair_ReadSecond(pair).value == 29);
    free(pair);
    return 0;
}

#include <assert.h>
#include <stdlib.h>

#include "generic_value_constructor.h"

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
    struct cx_generic_77e8b95e7995dc48479a0ff6af48ad58a1e4ecbbc8257bb28ab1a613f07e1663 *pair =
        generic_value_constructor_Create();
    assert(pair != NULL);
    assert(generic_value_constructor_ReadFirst(pair) == 23);
    assert(generic_value_constructor_ReadSecond(pair) == 41L);
    free(pair);
    return 0;
}

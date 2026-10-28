#include <assert.h>
#include <stdlib.h>

#include "generic_value_pair.h"

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
    struct generic_value_pair_Payload first = {1, 3, 5};
    struct generic_value_pair_Payload second = {2, 4, 6};
    struct generic_value_pair_Payload firstRead;
    struct generic_value_pair_Payload secondRead;
    cx_ptr box = generic_value_pair_Create();
    const struct cxcore_System_Reflection_TypeInfo *info = &CX_GET_TYPEINFO(box);

    assert(box != NULL);
    generic_value_pair_WriteFirst(box, first);
    generic_value_pair_WriteSecond(box, second);
    firstRead = generic_value_pair_ReadFirst(box);
    secondRead = generic_value_pair_ReadSecond(box);
    assert(firstRead.first == 1 && firstRead.second == 3 && firstRead.third == 5);
    assert(secondRead.first == 2 && secondRead.second == 4 && secondRead.third == 6);
    assert(((const struct cx_runtime_type_info *)info->RuntimeTypeInfo)->fieldCount == 2);
    assert(info->Size > sizeof(struct generic_value_pair_Box));
    free(box);
    return 0;
}

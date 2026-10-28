#include <assert.h>
#include <stdlib.h>

#include "generic_property_getter.h"

struct cxcore_System_Reflection_TypeInfo cxcore_System_Object___typeinfo;
struct cxcore_System_Reflection_TypeInfo cxcore_System_Void___typeinfo;
union cx_vtable_entry cxcore_System_String___vtable[1];

cx_ptr cxcore_System_Memory_Alloc(cx_uint size) {
    return calloc(1, size);
}

void cxcore_System_Object___constructor(struct cxcore_System_Object * value) {
    (void)value;
}

int main(void) {
    struct generic_property_getter_First *firstValue =
        (struct generic_property_getter_First *)malloc(1);
    struct generic_property_getter_Second *secondValue =
        (struct generic_property_getter_Second *)malloc(1);
    struct generic_property_getter_Box *first =
        generic_property_getter_CreateFirst(firstValue);
    struct generic_property_getter_Box *second =
        generic_property_getter_CreateSecond(secondValue);
    const struct cxcore_System_Reflection_TypeInfo *firstInfo = &CX_GET_TYPEINFO(first);
    const struct cxcore_System_Reflection_TypeInfo *secondInfo =
        &CX_GET_TYPEINFO(second);
    const struct cx_reflection_function *firstFunctions =
        (const struct cx_reflection_function *)((const struct cx_runtime_type_info *)
                                                    firstInfo->RuntimeTypeInfo)
            ->functions;
    const struct cx_reflection_function *secondFunctions =
        (const struct cx_reflection_function *)((const struct cx_runtime_type_info *)
                                                    secondInfo->RuntimeTypeInfo)
            ->functions;

    assert(firstValue != NULL && secondValue != NULL);
    assert(first != NULL && second != NULL);
    assert(generic_property_getter_ReadFirst(first) == firstValue);
    assert(generic_property_getter_ReadSecond(second) == secondValue);
    assert(firstInfo->Hash != secondInfo->Hash);
    assert(((const struct cx_runtime_type_info *)firstInfo->RuntimeTypeInfo)->functionCount == 2);
    assert(((const struct cx_runtime_type_info *)secondInfo->RuntimeTypeInfo)->functionCount == 2);
    assert(firstFunctions[1].returnTypeInfo ==
           &generic_property_getter_First___typeinfo);
    assert(secondFunctions[1].returnTypeInfo ==
           &generic_property_getter_Second___typeinfo);

    free(firstValue);
    free(secondValue);
    free(first);
    free(second);
    return 0;
}

#include <assert.h>
#include <stdlib.h>

#include "generic_property_setter.h"

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
    struct generic_property_setter_First *firstValue =
        (struct generic_property_setter_First *)malloc(1);
    struct generic_property_setter_Second *secondValue =
        (struct generic_property_setter_Second *)malloc(1);
    struct generic_property_setter_Box *first =
        generic_property_setter_CreateFirst();
    struct generic_property_setter_Box *second =
        generic_property_setter_CreateSecond();
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
    generic_property_setter_WriteFirst(first, firstValue);
    generic_property_setter_WriteSecond(second, secondValue);
    assert(first->stored == firstValue && second->stored == secondValue);
    assert(generic_property_setter_ReadFirst(first) == firstValue);
    assert(generic_property_setter_ReadSecond(second) == secondValue);
    assert(firstInfo->Hash != secondInfo->Hash);
    assert(((const struct cx_runtime_type_info *)firstInfo->RuntimeTypeInfo)->functionCount == 3);
    assert(((const struct cx_runtime_type_info *)secondInfo->RuntimeTypeInfo)->functionCount == 3);
    assert(firstFunctions[2].parameterCount == 1);
    assert(secondFunctions[2].parameterCount == 1);
    assert(firstFunctions[2].parameters[0].typeInfo ==
           &generic_property_setter_First___typeinfo);
    assert(secondFunctions[2].parameters[0].typeInfo ==
           &generic_property_setter_Second___typeinfo);

    free(firstValue);
    free(secondValue);
    free(first);
    free(second);
    return 0;
}

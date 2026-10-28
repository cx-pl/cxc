#include <assert.h>
#include <stddef.h>
#include <stdlib.h>

#include "generic_constructor_value.h"

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
    struct generic_constructor_value_First *firstValue =
        (struct generic_constructor_value_First *)malloc(1);
    struct generic_constructor_value_Second *secondValue =
        (struct generic_constructor_value_Second *)malloc(1);
    struct generic_constructor_value_Box *first =
        generic_constructor_value_CreateFirst(firstValue);
    struct generic_constructor_value_Box *second =
        generic_constructor_value_CreateSecond(secondValue);
    const struct cxcore_System_Reflection_TypeInfo *firstInfo = &CX_GET_TYPEINFO(first);
    const struct cxcore_System_Reflection_TypeInfo *secondInfo =
        &CX_GET_TYPEINFO(second);
    const struct cx_reflection_field *firstFields =
        (const struct cx_reflection_field *)((const struct cx_runtime_type_info *)
                                                 firstInfo->RuntimeTypeInfo)
            ->fields;
    const struct cx_reflection_field *secondFields =
        (const struct cx_reflection_field *)((const struct cx_runtime_type_info *)
                                                 secondInfo->RuntimeTypeInfo)
            ->fields;
    const struct cx_reflection_function *firstFunctions =
        (const struct cx_reflection_function *)((const struct cx_runtime_type_info *)
                                                    firstInfo->RuntimeTypeInfo)
            ->functions;
    const struct cx_reflection_function *secondFunctions =
        (const struct cx_reflection_function *)((const struct cx_runtime_type_info *)
                                                    secondInfo->RuntimeTypeInfo)
            ->functions;
    const struct cx_reflection_parameter *firstParameters =
        (const struct cx_reflection_parameter *)firstFunctions[0].parameters;
    const struct cx_reflection_parameter *secondParameters =
        (const struct cx_reflection_parameter *)secondFunctions[0].parameters;

    assert(firstValue != NULL && secondValue != NULL);
    assert(first != NULL && second != NULL);
    assert(first->value == firstValue && second->value == secondValue);
    assert(firstInfo->Hash != secondInfo->Hash);
    assert(firstFields[0].typeInfo == &generic_constructor_value_First___typeinfo);
    assert(secondFields[0].typeInfo == &generic_constructor_value_Second___typeinfo);
    assert(firstFunctions[0].parameterCount == 1);
    assert(secondFunctions[0].parameterCount == 1);
    assert(firstParameters[0].typeInfo == &generic_constructor_value_First___typeinfo);
    assert(secondParameters[0].typeInfo == &generic_constructor_value_Second___typeinfo);

    free(firstValue);
    free(secondValue);
    free(first);
    free(second);
    return 0;
}

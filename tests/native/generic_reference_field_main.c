#include <assert.h>
#include <stddef.h>
#include <stdlib.h>

#include "generic_reference_field.h"

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
    struct generic_reference_field_Box *first =
        generic_reference_field_CreateFirst();
    struct generic_reference_field_Box *second =
        generic_reference_field_CreateSecond();
    struct generic_reference_field_First *firstValue =
        (struct generic_reference_field_First *)malloc(1);
    struct generic_reference_field_Second *secondValue =
        (struct generic_reference_field_Second *)malloc(1);
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

    assert(first != NULL && second != NULL);
    assert(firstValue != NULL && secondValue != NULL);
    assert(CX_GET_VTABLE(first) ==
           cx_generic_125d31660ee8967b10f3aa3a47f7f7fb0543dcebb47ce46b66308249691eeff1___vtable);
    assert(CX_GET_VTABLE(second) ==
           cx_generic_8d155e088624f0bab65feabbbdfe872db6e53c44d8291996d8b935a0e93ef991___vtable);
    assert(firstInfo->Hash != secondInfo->Hash);
    assert(((const struct cx_runtime_type_info *)firstInfo->RuntimeTypeInfo)->fieldCount == 1 &&
           ((const struct cx_runtime_type_info *)secondInfo->RuntimeTypeInfo)->fieldCount == 1);
    assert(firstFields[0].offset == offsetof(struct generic_reference_field_Box, value));
    assert(firstFields[0].typeInfo == &generic_reference_field_First___typeinfo);
    assert(secondFields[0].typeInfo == &generic_reference_field_Second___typeinfo);
    first->value = firstValue;
    second->value = secondValue;
    assert(first->value == firstValue && second->value == secondValue);

    free(firstValue);
    free(secondValue);
    free(first);
    free(second);
    return 0;
}

#include <assert.h>
#include <stddef.h>
#include <stdlib.h>

#include "generic_reference_field.h"

struct CX_ID_4(cxcore, System, Reflection, TypeInfo) CX_ID_4(cxcore, System, Object, __typeinfo);
struct CX_ID_4(cxcore, System, Reflection, TypeInfo) CX_ID_4(cxcore, System, Void, __typeinfo);
union cx_vtable_entry CX_ID_4(cxcore, System, String, __vtable)[1];

cx_ptr CX_ID_4(cxcore, System, Memory, Alloc)(cx_uint size) {
    return calloc(1, size);
}

void CX_ID_4(cxcore, System, Object,
             __constructor)(struct CX_ID_3(cxcore, System, Object) * value) {
    (void)value;
}

int main(void) {
    struct CX_ID_2(generic_reference_field, Box) *first =
        CX_ID_2(generic_reference_field, CreateFirst)();
    struct CX_ID_2(generic_reference_field, Box) *second =
        CX_ID_2(generic_reference_field, CreateSecond)();
    struct CX_ID_2(generic_reference_field, First) *firstValue =
        (struct CX_ID_2(generic_reference_field, First) *)malloc(1);
    struct CX_ID_2(generic_reference_field, Second) *secondValue =
        (struct CX_ID_2(generic_reference_field, Second) *)malloc(1);
    const struct CX_ID_4(cxcore, System, Reflection, TypeInfo) *firstInfo = &CX_GET_TYPEINFO(first);
    const struct CX_ID_4(cxcore, System, Reflection, TypeInfo) *secondInfo =
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
           CX_ID_2(cx_generic_125d31660ee8967b10f3aa3a47f7f7fb0543dcebb47ce46b66308249691eeff1,
                   __vtable));
    assert(CX_GET_VTABLE(second) ==
           CX_ID_2(cx_generic_8d155e088624f0bab65feabbbdfe872db6e53c44d8291996d8b935a0e93ef991,
                   __vtable));
    assert(firstInfo->Hash != secondInfo->Hash);
    assert(((const struct cx_runtime_type_info *)firstInfo->RuntimeTypeInfo)->fieldCount == 1 &&
           ((const struct cx_runtime_type_info *)secondInfo->RuntimeTypeInfo)->fieldCount == 1);
    assert(firstFields[0].offset == offsetof(struct CX_ID_2(generic_reference_field, Box), value));
    assert(firstFields[0].typeInfo == &CX_ID_3(generic_reference_field, First, __typeinfo));
    assert(secondFields[0].typeInfo == &CX_ID_3(generic_reference_field, Second, __typeinfo));
    first->value = firstValue;
    second->value = secondValue;
    assert(first->value == firstValue && second->value == secondValue);

    free(firstValue);
    free(secondValue);
    free(first);
    free(second);
    return 0;
}

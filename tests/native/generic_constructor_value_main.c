#include <assert.h>
#include <stddef.h>
#include <stdlib.h>

#include "generic_constructor_value.h"

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
    struct CX_ID_2(generic_constructor_value, First) *firstValue =
        (struct CX_ID_2(generic_constructor_value, First) *)malloc(1);
    struct CX_ID_2(generic_constructor_value, Second) *secondValue =
        (struct CX_ID_2(generic_constructor_value, Second) *)malloc(1);
    struct CX_ID_2(generic_constructor_value, Box) *first =
        CX_ID_2(generic_constructor_value, CreateFirst)(firstValue);
    struct CX_ID_2(generic_constructor_value, Box) *second =
        CX_ID_2(generic_constructor_value, CreateSecond)(secondValue);
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
    assert(firstFields[0].typeInfo == &CX_ID_3(generic_constructor_value, First, __typeinfo));
    assert(secondFields[0].typeInfo == &CX_ID_3(generic_constructor_value, Second, __typeinfo));
    assert(firstFunctions[0].parameterCount == 1);
    assert(secondFunctions[0].parameterCount == 1);
    assert(firstParameters[0].typeInfo == &CX_ID_3(generic_constructor_value, First, __typeinfo));
    assert(secondParameters[0].typeInfo == &CX_ID_3(generic_constructor_value, Second, __typeinfo));

    free(firstValue);
    free(secondValue);
    free(first);
    free(second);
    return 0;
}

#include <assert.h>
#include <stdlib.h>

#include "generic_property_setter.h"

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
    struct CX_ID_2(generic_property_setter, First) *firstValue =
        (struct CX_ID_2(generic_property_setter, First) *)malloc(1);
    struct CX_ID_2(generic_property_setter, Second) *secondValue =
        (struct CX_ID_2(generic_property_setter, Second) *)malloc(1);
    struct CX_ID_2(generic_property_setter, Box) *first =
        CX_ID_2(generic_property_setter, CreateFirst)();
    struct CX_ID_2(generic_property_setter, Box) *second =
        CX_ID_2(generic_property_setter, CreateSecond)();
    const struct CX_ID_4(cxcore, System, Reflection, TypeInfo) *firstInfo = &CX_GET_TYPEINFO(first);
    const struct CX_ID_4(cxcore, System, Reflection, TypeInfo) *secondInfo =
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
    CX_ID_2(generic_property_setter, WriteFirst)(first, firstValue);
    CX_ID_2(generic_property_setter, WriteSecond)(second, secondValue);
    assert(first->stored == firstValue && second->stored == secondValue);
    assert(CX_ID_2(generic_property_setter, ReadFirst)(first) == firstValue);
    assert(CX_ID_2(generic_property_setter, ReadSecond)(second) == secondValue);
    assert(firstInfo->Hash != secondInfo->Hash);
    assert(((const struct cx_runtime_type_info *)firstInfo->RuntimeTypeInfo)->functionCount == 3);
    assert(((const struct cx_runtime_type_info *)secondInfo->RuntimeTypeInfo)->functionCount == 3);
    assert(firstFunctions[2].parameterCount == 1);
    assert(secondFunctions[2].parameterCount == 1);
    assert(firstFunctions[2].parameters[0].typeInfo ==
           &CX_ID_3(generic_property_setter, First, __typeinfo));
    assert(secondFunctions[2].parameters[0].typeInfo ==
           &CX_ID_3(generic_property_setter, Second, __typeinfo));

    free(firstValue);
    free(secondValue);
    free(first);
    free(second);
    return 0;
}

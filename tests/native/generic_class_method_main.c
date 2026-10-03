#include <assert.h>
#include <stdlib.h>

#include "generic_class_method.h"

struct CX_ID_4(cxcore, System, Reflection, TypeInfo)
    CX_ID_4(cxcore, System, Object, __typeinfo);
struct CX_ID_4(cxcore, System, Reflection, TypeInfo)
    CX_ID_4(cxcore, System, Void, __typeinfo);
union cx_vtable_entry CX_ID_4(cxcore, System, String, __vtable)[1];

cx_ptr CX_ID_4(cxcore, System, Memory, Alloc)(cx_uint size)
{
    return calloc(1, size);
}

void CX_ID_4(cxcore, System, Object, __constructor)(
    struct CX_ID_3(cxcore, System, Object)* value)
{
    (void)value;
}

int main(void)
{
    struct CX_ID_2(generic_class_method, First)* firstValue =
        (struct CX_ID_2(generic_class_method, First)*)malloc(1);
    struct CX_ID_2(generic_class_method, Second)* secondValue =
        (struct CX_ID_2(generic_class_method, Second)*)malloc(1);
    struct CX_ID_2(generic_class_method, Box)* first =
        CX_ID_2(generic_class_method, CreateFirst)();
    struct CX_ID_2(generic_class_method, Box)* second =
        CX_ID_2(generic_class_method, CreateSecond)();
    const struct CX_ID_4(cxcore, System, Reflection, TypeInfo)* firstInfo =
        &CX_GET_TYPEINFO(first);
    const struct CX_ID_4(cxcore, System, Reflection, TypeInfo)* secondInfo =
        &CX_GET_TYPEINFO(second);
    const struct cx_reflection_function* firstFunctions =
        (const struct cx_reflection_function*)((const struct cx_runtime_type_info*)firstInfo->RuntimeTypeInfo)->functions;
    const struct cx_reflection_function* secondFunctions =
        (const struct cx_reflection_function*)((const struct cx_runtime_type_info*)secondInfo->RuntimeTypeInfo)->functions;

    assert(firstValue != NULL && secondValue != NULL);
    assert(first != NULL && second != NULL);
    CX_ID_2(generic_class_method, SetFirst)(first, firstValue);
    CX_ID_2(generic_class_method, SetSecond)(second, secondValue);
    assert(first->value == firstValue && second->value == secondValue);
    assert(firstInfo->Hash != secondInfo->Hash);
    assert(((const struct cx_runtime_type_info*)firstInfo->RuntimeTypeInfo)->functionCount == 2);
    assert(((const struct cx_runtime_type_info*)secondInfo->RuntimeTypeInfo)->functionCount == 2);
    assert(firstFunctions[1].parameters[0].typeInfo ==
        &CX_ID_3(generic_class_method, First, __typeinfo));
    assert(secondFunctions[1].parameters[0].typeInfo ==
        &CX_ID_3(generic_class_method, Second, __typeinfo));

    free(firstValue);
    free(secondValue);
    free(first);
    free(second);
    return 0;
}

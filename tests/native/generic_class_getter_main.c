#include <assert.h>
#include <stdlib.h>

#include "generic_class_getter.h"

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
    struct CX_ID_2(generic_class_getter, First)* firstValue =
        (struct CX_ID_2(generic_class_getter, First)*)malloc(1);
    struct CX_ID_2(generic_class_getter, Second)* secondValue =
        (struct CX_ID_2(generic_class_getter, Second)*)malloc(1);
    struct CX_ID_2(generic_class_getter, Box)* first =
        CX_ID_2(generic_class_getter, CreateFirst)(firstValue);
    struct CX_ID_2(generic_class_getter, Box)* second =
        CX_ID_2(generic_class_getter, CreateSecond)(secondValue);
    const struct CX_ID_4(cxcore, System, Reflection, TypeInfo)* firstInfo =
        &CX_GET_TYPEINFO(first);
    const struct CX_ID_4(cxcore, System, Reflection, TypeInfo)* secondInfo =
        &CX_GET_TYPEINFO(second);
    const struct cx_reflection_function* firstFunctions =
        (const struct cx_reflection_function*)firstInfo->RuntimeFunctions;
    const struct cx_reflection_function* secondFunctions =
        (const struct cx_reflection_function*)secondInfo->RuntimeFunctions;

    assert(firstValue != NULL && secondValue != NULL);
    assert(first != NULL && second != NULL);
    assert(CX_ID_2(generic_class_getter, ReadFirst)(first) == firstValue);
    assert(CX_ID_2(generic_class_getter, ReadSecond)(second) == secondValue);
    assert(firstInfo->Hash != secondInfo->Hash);
    assert(firstInfo->RuntimeFunctionCount == 2);
    assert(secondInfo->RuntimeFunctionCount == 2);
    assert(firstFunctions[1].returnTypeInfo ==
        &CX_ID_3(generic_class_getter, First, __typeinfo));
    assert(secondFunctions[1].returnTypeInfo ==
        &CX_ID_3(generic_class_getter, Second, __typeinfo));

    free(firstValue);
    free(secondValue);
    free(first);
    free(second);
    return 0;
}

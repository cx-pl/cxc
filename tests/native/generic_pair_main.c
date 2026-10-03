#include <assert.h>
#include <stdlib.h>

#include "generic_pair.h"

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
    struct CX_ID_2(generic_pair, First)* firstValue =
        (struct CX_ID_2(generic_pair, First)*)malloc(1);
    struct CX_ID_2(generic_pair, Second)* secondValue =
        (struct CX_ID_2(generic_pair, Second)*)malloc(1);
    struct CX_ID_2(generic_pair, Pair)* forward =
        CX_ID_2(generic_pair, CreateForward)(firstValue, secondValue);
    struct CX_ID_2(generic_pair, Pair)* reverse =
        CX_ID_2(generic_pair, CreateReverse)(secondValue, firstValue);
    const struct CX_ID_4(cxcore, System, Reflection, TypeInfo)* forwardInfo =
        &CX_GET_TYPEINFO(forward);
    const struct CX_ID_4(cxcore, System, Reflection, TypeInfo)* reverseInfo =
        &CX_GET_TYPEINFO(reverse);
    const struct cx_reflection_field* forwardFields =
        (const struct cx_reflection_field*)((const struct cx_runtime_type_info*)forwardInfo->RuntimeTypeInfo)->fields;
    const struct cx_reflection_field* reverseFields =
        (const struct cx_reflection_field*)((const struct cx_runtime_type_info*)reverseInfo->RuntimeTypeInfo)->fields;
    const struct cx_reflection_function* forwardFunctions =
        (const struct cx_reflection_function*)((const struct cx_runtime_type_info*)forwardInfo->RuntimeTypeInfo)->functions;
    const struct cx_reflection_function* reverseFunctions =
        (const struct cx_reflection_function*)((const struct cx_runtime_type_info*)reverseInfo->RuntimeTypeInfo)->functions;
    const struct CX_ID_4(cxcore, System, Reflection, TypeInfo)* firstType =
        &CX_ID_3(generic_pair, First, __typeinfo);
    const struct CX_ID_4(cxcore, System, Reflection, TypeInfo)* secondType =
        &CX_ID_3(generic_pair, Second, __typeinfo);

    assert(firstValue != NULL && secondValue != NULL);
    assert(forward != NULL && reverse != NULL);
    assert(forward->first == firstValue && forward->second == secondValue);
    assert(reverse->first == secondValue && reverse->second == firstValue);
    assert(forwardInfo->Hash != reverseInfo->Hash);
    assert(((const struct cx_runtime_type_info*)forwardInfo->RuntimeTypeInfo)->fieldCount == 2 && ((const struct cx_runtime_type_info*)reverseInfo->RuntimeTypeInfo)->fieldCount == 2);
    assert(forwardFields[0].typeInfo == firstType);
    assert(forwardFields[1].typeInfo == secondType);
    assert(reverseFields[0].typeInfo == secondType);
    assert(reverseFields[1].typeInfo == firstType);
    assert(forwardFunctions[0].parameterCount == 2);
    assert(reverseFunctions[0].parameterCount == 2);
    assert(forwardFunctions[0].parameters[0].typeInfo == firstType);
    assert(forwardFunctions[0].parameters[1].typeInfo == secondType);
    assert(reverseFunctions[0].parameters[0].typeInfo == secondType);
    assert(reverseFunctions[0].parameters[1].typeInfo == firstType);

    free(firstValue);
    free(secondValue);
    free(forward);
    free(reverse);
    return 0;
}

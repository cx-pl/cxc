#include <assert.h>
#include <stdlib.h>

#include "generic_pair.h"

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
    struct generic_pair_First *firstValue =
        (struct generic_pair_First *)malloc(1);
    struct generic_pair_Second *secondValue =
        (struct generic_pair_Second *)malloc(1);
    struct generic_pair_Pair *forward =
        generic_pair_CreateForward(firstValue, secondValue);
    struct generic_pair_Pair *reverse =
        generic_pair_CreateReverse(secondValue, firstValue);
    const struct cxcore_System_Reflection_TypeInfo *forwardInfo =
        &CX_GET_TYPEINFO(forward);
    const struct cxcore_System_Reflection_TypeInfo *reverseInfo =
        &CX_GET_TYPEINFO(reverse);
    const struct cx_reflection_field *forwardFields =
        (const struct cx_reflection_field *)((const struct cx_runtime_type_info *)
                                                 forwardInfo->RuntimeTypeInfo)
            ->fields;
    const struct cx_reflection_field *reverseFields =
        (const struct cx_reflection_field *)((const struct cx_runtime_type_info *)
                                                 reverseInfo->RuntimeTypeInfo)
            ->fields;
    const struct cx_reflection_function *forwardFunctions =
        (const struct cx_reflection_function *)((const struct cx_runtime_type_info *)
                                                    forwardInfo->RuntimeTypeInfo)
            ->functions;
    const struct cx_reflection_function *reverseFunctions =
        (const struct cx_reflection_function *)((const struct cx_runtime_type_info *)
                                                    reverseInfo->RuntimeTypeInfo)
            ->functions;
    const struct cxcore_System_Reflection_TypeInfo *firstType =
        &generic_pair_First___typeinfo;
    const struct cxcore_System_Reflection_TypeInfo *secondType =
        &generic_pair_Second___typeinfo;

    assert(firstValue != NULL && secondValue != NULL);
    assert(forward != NULL && reverse != NULL);
    assert(forward->first == firstValue && forward->second == secondValue);
    assert(reverse->first == secondValue && reverse->second == firstValue);
    assert(forwardInfo->Hash != reverseInfo->Hash);
    assert(((const struct cx_runtime_type_info *)forwardInfo->RuntimeTypeInfo)->fieldCount == 2 &&
           ((const struct cx_runtime_type_info *)reverseInfo->RuntimeTypeInfo)->fieldCount == 2);
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

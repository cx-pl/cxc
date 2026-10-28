#include <assert.h>
#include <stdlib.h>

#include "generic_class_method.h"

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
    struct generic_class_method_First *firstValue =
        (struct generic_class_method_First *)malloc(1);
    struct generic_class_method_Second *secondValue =
        (struct generic_class_method_Second *)malloc(1);
    struct generic_class_method_Box *first = generic_class_method_CreateFirst();
    struct generic_class_method_Box *second =
        generic_class_method_CreateSecond();
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
    generic_class_method_SetFirst(first, firstValue);
    generic_class_method_SetSecond(second, secondValue);
    assert(first->value == firstValue && second->value == secondValue);
    assert(firstInfo->Hash != secondInfo->Hash);
    assert(((const struct cx_runtime_type_info *)firstInfo->RuntimeTypeInfo)->functionCount == 2);
    assert(((const struct cx_runtime_type_info *)secondInfo->RuntimeTypeInfo)->functionCount == 2);
    assert(firstFunctions[1].parameters[0].typeInfo ==
           &generic_class_method_First___typeinfo);
    assert(secondFunctions[1].parameters[0].typeInfo ==
           &generic_class_method_Second___typeinfo);

    free(firstValue);
    free(secondValue);
    free(first);
    free(second);
    return 0;
}

#include <assert.h>
#include <stdlib.h>

#include "generic_value_box.h"

struct cxcore_System_Reflection_TypeInfo cxcore_System_Object___typeinfo;
struct cxcore_System_Reflection_TypeInfo cxcore_System_Void___typeinfo;
struct cxcore_System_Reflection_TypeInfo cxcore_System_Int___typeinfo;
union cx_vtable_entry cxcore_System_String___vtable[1];

cx_ptr cxcore_System_Memory_Alloc(cx_uint size) {
    return calloc(1, size);
}

void cxcore_System_Object___constructor(struct cxcore_System_Object * value) {
    (void)value;
}

int main(void) {
    cx_ptr box = generic_value_box_Create();
    cx_ptr otherBox = generic_value_box_CreateOther();
    struct generic_value_box_Payload input = {7, 11, 13};
    struct generic_value_box_OtherPayload otherInput = {2, 3, 5, 8, 13};
    struct generic_value_box_Payload output;
    struct generic_value_box_OtherPayload otherOutput;
    const struct cxcore_System_Reflection_TypeInfo *info = &CX_GET_TYPEINFO(box);
    const struct cxcore_System_Reflection_TypeInfo *otherInfo =
        &CX_GET_TYPEINFO(otherBox);
    const struct cx_reflection_field *fields =
        (const struct cx_reflection_field *)((const struct cx_runtime_type_info *)
                                                 info->RuntimeTypeInfo)
            ->fields;
    const struct cx_reflection_field *otherFields =
        (const struct cx_reflection_field *)((const struct cx_runtime_type_info *)
                                                 otherInfo->RuntimeTypeInfo)
            ->fields;

    assert(box != NULL && otherBox != NULL);
    generic_value_box_Write(box, input);
    generic_value_box_WriteOther(otherBox, otherInput);
    output = generic_value_box_Read(box);
    otherOutput = generic_value_box_ReadOther(otherBox);
    assert(output.first == 7 && output.second == 11 && output.third == 13);
    assert(otherOutput.first == 2 && otherOutput.second == 3 && otherOutput.third == 5 &&
           otherOutput.fourth == 8 && otherOutput.fifth == 13);
    assert(info->Size > sizeof(struct generic_value_box_Box));
    assert(otherInfo->Size > info->Size);
    assert(info->Hash != otherInfo->Hash);
    assert(((const struct cx_runtime_type_info *)info->RuntimeTypeInfo)->fieldCount == 1);
    assert(((const struct cx_runtime_type_info *)otherInfo->RuntimeTypeInfo)->fieldCount == 1);
    assert(fields[0].typeInfo == &generic_value_box_Payload___typeinfo);
    assert(otherFields[0].typeInfo == &generic_value_box_OtherPayload___typeinfo);

    free(box);
    free(otherBox);
    return 0;
}

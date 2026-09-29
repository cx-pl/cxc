#include <assert.h>
#include <stdlib.h>

#include "generic_value_box.h"

struct CX_ID_4(cxcore, System, Reflection, TypeInfo)
    CX_ID_4(cxcore, System, Object, __typeinfo);
struct CX_ID_4(cxcore, System, Reflection, TypeInfo)
    CX_ID_4(cxcore, System, Void, __typeinfo);
struct CX_ID_4(cxcore, System, Reflection, TypeInfo)
    CX_ID_4(cxcore, System, Int, __typeinfo);
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
    cx_ptr box = CX_ID_2(generic_value_box, Create)();
    cx_ptr otherBox = CX_ID_2(generic_value_box, CreateOther)();
    struct CX_ID_2(generic_value_box, Payload) input = { 7, 11, 13 };
    struct CX_ID_2(generic_value_box, OtherPayload) otherInput = { 2, 3, 5, 8, 13 };
    struct CX_ID_2(generic_value_box, Payload) output;
    struct CX_ID_2(generic_value_box, OtherPayload) otherOutput;
    const struct CX_ID_4(cxcore, System, Reflection, TypeInfo)* info =
        &CX_GET_TYPEINFO(box);
    const struct CX_ID_4(cxcore, System, Reflection, TypeInfo)* otherInfo =
        &CX_GET_TYPEINFO(otherBox);
    const struct cx_reflection_field* fields =
        (const struct cx_reflection_field*)info->RuntimeFields;
    const struct cx_reflection_field* otherFields =
        (const struct cx_reflection_field*)otherInfo->RuntimeFields;

    assert(box != NULL && otherBox != NULL);
    CX_ID_2(generic_value_box, Write)(box, input);
    CX_ID_2(generic_value_box, WriteOther)(otherBox, otherInput);
    output = CX_ID_2(generic_value_box, Read)(box);
    otherOutput = CX_ID_2(generic_value_box, ReadOther)(otherBox);
    assert(output.first == 7 && output.second == 11 && output.third == 13);
    assert(otherOutput.first == 2 && otherOutput.second == 3 &&
        otherOutput.third == 5 && otherOutput.fourth == 8 &&
        otherOutput.fifth == 13);
    assert(info->Size > sizeof(struct CX_ID_2(generic_value_box, Box)));
    assert(otherInfo->Size > info->Size);
    assert(info->Hash != otherInfo->Hash);
    assert(info->RuntimeFieldCount == 1);
    assert(otherInfo->RuntimeFieldCount == 1);
    assert(fields[0].typeInfo ==
        &CX_ID_3(generic_value_box, Payload, __typeinfo));
    assert(otherFields[0].typeInfo ==
        &CX_ID_3(generic_value_box, OtherPayload, __typeinfo));

    free(box);
    free(otherBox);
    return 0;
}

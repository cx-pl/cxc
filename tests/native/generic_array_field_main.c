#include <assert.h>
#include <stddef.h>
#include <stdlib.h>

#include "generic_array_field.h"

struct CX_ID_4(cxcore, System, Reflection, TypeInfo)
    CX_ID_4(cxcore, System, Object, __typeinfo);
struct CX_ID_4(cxcore, System, Reflection, TypeInfo)
    CX_ID_4(cxcore, System, Void, __typeinfo);
struct CX_ID_4(cxcore, System, Reflection, TypeInfo)
    CX_ID_4(cxcore, System, Array, __typeinfo);
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
    struct CX_ID_2(generic_array_field, Box)* first =
        CX_ID_2(generic_array_field, CreateFirst)();
    struct CX_ID_2(generic_array_field, Box)* second =
        CX_ID_2(generic_array_field, CreateSecond)();
    const struct CX_ID_4(cxcore, System, Reflection, TypeInfo)* firstInfo =
        &CX_GET_TYPEINFO(first);
    const struct CX_ID_4(cxcore, System, Reflection, TypeInfo)* secondInfo =
        &CX_GET_TYPEINFO(second);
    const struct cx_reflection_field* firstFields =
        (const struct cx_reflection_field*)firstInfo->RuntimeFields;
    const struct cx_reflection_field* secondFields =
        (const struct cx_reflection_field*)secondInfo->RuntimeFields;
    struct CX_ID_3(cxcore, System, Array)* array =
        (struct CX_ID_3(cxcore, System, Array)*)malloc(1);

    assert(first != NULL && second != NULL && array != NULL);
    assert(CX_GET_VTABLE(first) ==
        CX_ID_2(cx_generic_48a8b0fc8dd4ad46bb4d66ea7ba5640d834061274a613ad7731edb9e9233da76,
            __vtable));
    assert(CX_GET_VTABLE(second) ==
        CX_ID_2(cx_generic_2cda92b4fd6c2ae7bb6a382a9806c51f61b8ce53ab213f8cc976c9c8c3cb6a8c,
            __vtable));
    assert(firstInfo->Hash != secondInfo->Hash);
    assert(firstInfo->Size == secondInfo->Size);
    assert(firstInfo->RuntimeFieldCount == 1);
    assert(secondInfo->RuntimeFieldCount == 1);
    assert(firstFields[0].offset ==
        offsetof(struct CX_ID_2(generic_array_field, Box), values));
    assert(secondFields[0].offset == firstFields[0].offset);
    first->values = array;
    assert(first->values == array);

    free(array);
    free(first);
    free(second);
    return 0;
}

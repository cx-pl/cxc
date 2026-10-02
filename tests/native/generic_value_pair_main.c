#include <assert.h>
#include <stdlib.h>

#include "generic_value_pair.h"

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
    struct CX_ID_2(generic_value_pair, Payload) first = { 1, 3, 5 };
    struct CX_ID_2(generic_value_pair, Payload) second = { 2, 4, 6 };
    struct CX_ID_2(generic_value_pair, Payload) firstRead;
    struct CX_ID_2(generic_value_pair, Payload) secondRead;
    cx_ptr box =
        CX_ID_2(generic_value_pair, Create)();
    const struct CX_ID_4(cxcore, System, Reflection, TypeInfo)* info =
        &CX_GET_TYPEINFO(box);

    assert(box != NULL);
    CX_ID_2(generic_value_pair, WriteFirst)(box, first);
    CX_ID_2(generic_value_pair, WriteSecond)(box, second);
    firstRead = CX_ID_2(generic_value_pair, ReadFirst)(box);
    secondRead = CX_ID_2(generic_value_pair, ReadSecond)(box);
    assert(firstRead.first == 1 && firstRead.second == 3 && firstRead.third == 5);
    assert(secondRead.first == 2 && secondRead.second == 4 && secondRead.third == 6);
    assert(info->RuntimeFieldCount == 2);
    assert(info->Size > sizeof(struct CX_ID_2(generic_value_pair, Box)));
    free(box);
    return 0;
}

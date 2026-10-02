#include <assert.h>
#include <stdlib.h>

#include "generic_value_constructor.h"

struct CX_ID_4(cxcore, System, Reflection, TypeInfo)
    CX_ID_4(cxcore, System, Object, __typeinfo);
struct CX_ID_4(cxcore, System, Reflection, TypeInfo)
    CX_ID_4(cxcore, System, Void, __typeinfo);
struct CX_ID_4(cxcore, System, Reflection, TypeInfo)
    CX_ID_4(cxcore, System, Int, __typeinfo);
struct CX_ID_4(cxcore, System, Reflection, TypeInfo)
    CX_ID_4(cxcore, System, Long, __typeinfo);
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
    struct cx_generic_77e8b95e7995dc48479a0ff6af48ad58a1e4ecbbc8257bb28ab1a613f07e1663* pair =
        CX_ID_2(generic_value_constructor, Create)();
    assert(pair != NULL);
    assert(CX_ID_2(generic_value_constructor, ReadFirst)(pair) == 23);
    assert(CX_ID_2(generic_value_constructor, ReadSecond)(pair) == 41L);
    free(pair);
    return 0;
}

#include <assert.h>
#include <stdlib.h>

#include "closed_construction.h"

struct CX_ID_4(cxcore, System, Reflection, TypeInfo)
    CX_ID_4(cxcore, System, Object, __typeinfo);
struct CX_ID_4(cxcore, System, Reflection, TypeInfo)
    CX_ID_4(cxcore, System, Void, __typeinfo);
union cx_vtable_entry CX_ID_4(cxcore, System, String, __vtable)[1];

cx_ptr CX_ID_4(cxcore, System, Memory, Alloc)(cx_uint size)
{
    return malloc(size);
}

void CX_ID_4(cxcore, System, Object, __constructor)(
    struct CX_ID_3(cxcore, System, Object)* value)
{
    (void)value;
}

int main(void)
{
    struct CX_ID_2(closed_construction, Marker)* first =
        CX_ID_2(closed_construction, CreateFirst)();
    struct CX_ID_2(closed_construction, Marker)* second =
        CX_ID_2(closed_construction, CreateSecond)();

    assert(first != NULL && second != NULL);
    assert(CX_GET_VTABLE(first) ==
        CX_ID_2(cx_generic_c1bc3a22b2fee75987cc761d98926a2721fb11f81fa79e816423796cf6744c45,
            __vtable));
    assert(CX_GET_VTABLE(second) ==
        CX_ID_2(cx_generic_7e02eb5a1592938da5069eac0c8123125a2026a9c92d109e4aedb047cb222e93,
            __vtable));
    assert(CX_GET_TYPEINFO(first).Hash != CX_GET_TYPEINFO(second).Hash);
    free(first);
    free(second);
    return 0;
}

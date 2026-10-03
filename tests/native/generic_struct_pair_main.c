#include <assert.h>
#include <stdlib.h>

#include "generic_struct_pair.h"

struct CX_ID_4(cxcore, System, Reflection, TypeInfo) CX_ID_4(cxcore, System, Object, __typeinfo);
struct CX_ID_4(cxcore, System, Reflection, TypeInfo) CX_ID_4(cxcore, System, Void, __typeinfo);
struct CX_ID_4(cxcore, System, Reflection, TypeInfo) CX_ID_4(cxcore, System, Int, __typeinfo);
struct CX_ID_4(cxcore, System, Reflection, TypeInfo) CX_ID_4(cxcore, System, Long, __typeinfo);
union cx_vtable_entry CX_ID_4(cxcore, System, String, __vtable)[1];

cx_ptr CX_ID_4(cxcore, System, Memory, Alloc)(cx_uint size) {
    return calloc(1, size);
}

void CX_ID_4(cxcore, System, Object,
             __constructor)(struct CX_ID_3(cxcore, System, Object) * value) {
    (void)value;
}

int main(void) {
    struct CX_ID_2(generic_struct_pair, First) first = {17};
    struct CX_ID_2(generic_struct_pair, Second) second = {29};
    cx_ptr pair = CX_ID_2(generic_struct_pair, Create)();

    assert(pair != NULL);
    CX_ID_2(generic_struct_pair, WriteFirst)(pair, first);
    CX_ID_2(generic_struct_pair, WriteSecond)(pair, second);
    assert(CX_ID_2(generic_struct_pair, ReadFirst)(pair).value == 17);
    assert(CX_ID_2(generic_struct_pair, ReadSecond)(pair).value == 29);
    free(pair);
    return 0;
}

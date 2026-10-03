#include <assert.h>
#include <stdlib.h>

#include "generic_array_identity.h"

struct CX_ID_4(cxcore, System, Reflection, TypeInfo) CX_ID_4(cxcore, System, Object, __typeinfo);
union cx_vtable_entry CX_ID_4(cxcore, System, String, __vtable)[1];

int main(void) {
    struct CX_ID_3(cxcore, System, Array) *first =
        (struct CX_ID_3(cxcore, System, Array) *)malloc(1);
    struct CX_ID_3(cxcore, System, Array) *second =
        (struct CX_ID_3(cxcore, System, Array) *)malloc(1);

    assert(first != NULL && second != NULL);
    assert(first != second);
    assert(CX_ID_2(generic_array_identity, RunFirst)(first) == first);
    assert(CX_ID_2(generic_array_identity, RunSecond)(second) == second);

    free(first);
    free(second);
    return 0;
}

#include <assert.h>
#include <stdlib.h>

#include "generic_array_identity.h"

struct cxcore_System_Reflection_TypeInfo cxcore_System_Object___typeinfo;
union cx_vtable_entry cxcore_System_String___vtable[1];

int main(void) {
    struct cxcore_System_Collections_Array *first =
        (struct cxcore_System_Collections_Array *)malloc(1);
    struct cxcore_System_Collections_Array *second =
        (struct cxcore_System_Collections_Array *)malloc(1);

    assert(first != NULL && second != NULL);
    assert(first != second);
    assert(generic_array_identity_RunFirst(first) == first);
    assert(generic_array_identity_RunSecond(second) == second);

    free(first);
    free(second);
    return 0;
}

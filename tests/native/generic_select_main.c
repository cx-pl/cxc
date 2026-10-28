#include <assert.h>

#include "generic_select.h"

int main(void) {
    assert(generic_select_RunInt() == 7);
    assert(generic_select_RunLong() == 13L);
    cx_int firstValue = 2;
    cx_int secondValue = 5;
    struct cxcore_System_Collections_Array *first =
        (struct cxcore_System_Collections_Array *)&firstValue;
    struct cxcore_System_Collections_Array *second =
        (struct cxcore_System_Collections_Array *)&secondValue;
    assert(generic_select_RunIntArray(CX_TRUE, first, second) == first);
    assert(generic_select_RunIntArray(CX_FALSE, first, second) == second);
    return 0;
}

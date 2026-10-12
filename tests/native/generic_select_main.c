#include <assert.h>

#include "generic_select.h"

int main(void) {
    assert(CX_ID_2(generic_select, RunInt)() == 7);
    assert(CX_ID_2(generic_select, RunLong)() == 13L);
    cx_int firstValue = 2;
    cx_int secondValue = 5;
    struct CX_ID_4(cxcore, System, Collections, Array) *first =
        (struct CX_ID_4(cxcore, System, Collections, Array) *)&firstValue;
    struct CX_ID_4(cxcore, System, Collections, Array) *second =
        (struct CX_ID_4(cxcore, System, Collections, Array) *)&secondValue;
    assert(CX_ID_2(generic_select, RunIntArray)(CX_TRUE, first, second) == first);
    assert(CX_ID_2(generic_select, RunIntArray)(CX_FALSE, first, second) == second);
    return 0;
}

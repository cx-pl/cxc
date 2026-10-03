#include <assert.h>

#include "generic_method.h"

struct CX_ID_4(cxcore, System, Reflection, TypeInfo) CX_ID_4(cxcore, System, Object, __typeinfo);
union cx_vtable_entry CX_ID_4(cxcore, System, String, __vtable)[1];

int main(void) {
    assert(CX_ID_2(generic_method, RunInt)() == 7);
    assert(CX_ID_2(generic_method, RunLong)() == 9);
    assert(CX_ID_2(generic_method, RunCopy)() == 13);
    return 0;
}

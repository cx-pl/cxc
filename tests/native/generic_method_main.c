#include <assert.h>

#include "generic_method.h"

struct cxcore_System_Reflection_TypeInfo cxcore_System_Object___typeinfo;
union cx_vtable_entry cxcore_System_String___vtable[1];

int main(void) {
    assert(generic_method_RunInt() == 7);
    assert(generic_method_RunLong() == 9);
    assert(generic_method_RunCopy() == 13);
    return 0;
}

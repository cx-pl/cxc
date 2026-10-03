#include <assert.h>

#include "generic_cross_file.h"

struct CX_ID_4(cxcore, System, Reflection, TypeInfo) CX_ID_4(cxcore, System, Int, __typeinfo);
union cx_vtable_entry CX_ID_4(cxcore, System, String, __vtable)[1];

int main(void) {
    assert(CX_ID_3(generic_cross_file, App, Run)() == 37);
    return 0;
}

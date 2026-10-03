#include <assert.h>

#include "generic_instance_explicit.h"

struct CX_ID_4(cxcore, System, Reflection, TypeInfo) CX_ID_4(cxcore, System, Object, __typeinfo);
struct CX_ID_4(cxcore, System, Reflection, TypeInfo) CX_ID_4(cxcore, System, Void, __typeinfo);
struct CX_ID_4(cxcore, System, Reflection, TypeInfo) CX_ID_4(cxcore, System, Int, __typeinfo);
union cx_vtable_entry CX_ID_4(cxcore, System, String, __vtable)[1];

int main(void) {
    struct CX_ID_2(generic_instance_explicit, Utility) utility = {0};
    assert(CX_ID_2(generic_instance_explicit, Run)(&utility) == 29);
    return 0;
}

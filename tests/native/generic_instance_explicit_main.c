#include <assert.h>

#include "generic_instance_explicit.h"

struct cxcore_System_Reflection_TypeInfo cxcore_System_Object___typeinfo;
struct cxcore_System_Reflection_TypeInfo cxcore_System_Void___typeinfo;
struct cxcore_System_Reflection_TypeInfo cxcore_System_Int___typeinfo;
union cx_vtable_entry cxcore_System_String___vtable[1];

int main(void) {
    struct generic_instance_explicit_Utility utility = {0};
    assert(generic_instance_explicit_Run(&utility) == 29);
    return 0;
}

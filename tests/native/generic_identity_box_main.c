#include <assert.h>

#include "generic_identity_box.h"

struct cxcore_System_Reflection_TypeInfo cxcore_System_Object___typeinfo;
union cx_vtable_entry cxcore_System_String___vtable[1];

int main(void) {
    struct cx_generic_284bb4ebbebe4e0068150892111c314fe9134df2912a3bf997b6ee23810b3b95 value = {0};
    assert(generic_identity_box_Run(&value) == &value);
    return 0;
}

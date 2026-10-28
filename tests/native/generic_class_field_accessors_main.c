#include <assert.h>

#include "generic_class_field_accessors.h"

struct cxcore_System_Reflection_TypeInfo cxcore_System_Object___typeinfo;
struct cxcore_System_Reflection_TypeInfo cxcore_System_Int___typeinfo;
struct cxcore_System_Reflection_TypeInfo cxcore_System_Void___typeinfo;
union cx_vtable_entry cxcore_System_String___vtable[1];

int main(void) {
    struct cx_generic_f9111d49fb1104e761f9bae2154988e93906a6dd1780823001d0ba6aa1b61a57 box = {0};
    assert(generic_class_field_accessors_Read(&box) == 0);
    generic_class_field_accessors_Write(&box, 47);
    assert(generic_class_field_accessors_Read(&box) == 47);
    return 0;
}

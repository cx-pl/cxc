#include <assert.h>

#include "generic_class_field_accessors.h"

struct CX_ID_4(cxcore, System, Reflection, TypeInfo)
    CX_ID_4(cxcore, System, Object, __typeinfo);
struct CX_ID_4(cxcore, System, Reflection, TypeInfo)
    CX_ID_4(cxcore, System, Int, __typeinfo);
struct CX_ID_4(cxcore, System, Reflection, TypeInfo)
    CX_ID_4(cxcore, System, Void, __typeinfo);
union cx_vtable_entry CX_ID_4(cxcore, System, String, __vtable)[1];

int main(void)
{
    struct cx_generic_f9111d49fb1104e761f9bae2154988e93906a6dd1780823001d0ba6aa1b61a57 box = { 0 };
    assert(CX_ID_2(generic_class_field_accessors, Read)(&box) == 0);
    CX_ID_2(generic_class_field_accessors, Write)(&box, 47);
    assert(CX_ID_2(generic_class_field_accessors, Read)(&box) == 47);
    return 0;
}

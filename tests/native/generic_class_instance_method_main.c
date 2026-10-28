#include <assert.h>

#include "generic_class_instance_method.h"

struct cxcore_System_Reflection_TypeInfo cxcore_System_Object___typeinfo;
union cx_vtable_entry cxcore_System_String___vtable[1];

int main(void) {
    struct cx_generic_3ffbeb2067478a8a5a195eb4c0a7f408658680146e0bf81240fb9dc6082f8fd4 intBox = {0};
    struct cx_generic_044a595fb90fbd1128577fd61cee572e48b53a46ad99515eef1b445dce56a13d longBox = {
        0};
    assert(generic_class_instance_method_RunInt(&intBox) == 41);
    assert(generic_class_instance_method_RunLong(&longBox) == 43L);
    return 0;
}

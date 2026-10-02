#include <assert.h>

#include "generic_identity_box.h"

struct CX_ID_4(cxcore, System, Reflection, TypeInfo)
    CX_ID_4(cxcore, System, Object, __typeinfo);
union cx_vtable_entry CX_ID_4(cxcore, System, String, __vtable)[1];

int main(void)
{
    struct cx_generic_284bb4ebbebe4e0068150892111c314fe9134df2912a3bf997b6ee23810b3b95 value = { 0 };
    assert(CX_ID_2(generic_identity_box, Run)(&value) == &value);
    return 0;
}

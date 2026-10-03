#include <assert.h>

#include "generic_nested_inference.h"

struct CX_ID_4(cxcore, System, Reflection, TypeInfo) CX_ID_4(cxcore, System, Object, __typeinfo);
union cx_vtable_entry CX_ID_4(cxcore, System, String, __vtable)[1];

cx_int cx_generic_c00b2f5e6b6d3aa7de1177843c7397d1819740200debda1ef1b7af5fcabbf683(
    struct CX_ID_2(generic_nested_inference, Box) * value) {
    return value != NULL ? 83 : 0;
}

int main(void) {
    struct cx_generic_ec14c3686fb05e8a5689632425d6bba85149d17aafd0ff3d476a0dc1f3587dea value = {0};
    assert(CX_ID_2(generic_nested_inference, Run)(&value) == 83);
    return 0;
}

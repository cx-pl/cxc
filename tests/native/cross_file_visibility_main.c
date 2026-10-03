#include <assert.h>

#include "cross_file_library.h"

struct CX_ID_4(cxcore, System, Reflection, TypeInfo) CX_ID_4(cxcore, System, Int, __typeinfo);
union cx_vtable_entry CX_ID_4(cxcore, System, String, __vtable)[1];

int main(void) {
    struct CX_ID_3(cross_file_library, Lib, Token) token = {37};
    assert(CX_ID_3(cross_file_library, App, Run)(token) == 37);
    return 0;
}

#include <assert.h>

#include "cross_file_library.h"

struct cxcore_System_Reflection_TypeInfo cxcore_System_Int___typeinfo;
union cx_vtable_entry cxcore_System_String___vtable[1];

int main(void) {
    struct cross_file_library_Lib_Token token = {37};
    assert(cross_file_library_App_Run(token) == 37);
    return 0;
}

#include <assert.h>

#include "exception_control_flow.h"

/* The generated module's reflection records reference cxcore metadata that is
 * currently internal to the DLL. These definitions isolate this executable
 * control-flow test from that separate export-policy limitation. */
struct cxcore_System_Reflection_TypeInfo cxcore_System_Object___typeinfo;
struct cxcore_System_Reflection_TypeInfo cxcore_System_Int___typeinfo;
union cx_vtable_entry cxcore_System_String___vtable[1];

int main(void) {
    assert(exception_control_flow_RunFinallyTransfers() == 7);
    assert(exception_control_flow_ExceptionProbe_State == 11111);
    assert(exception_control_flow_ReturnFromFinally() == 2);
    return 0;
}

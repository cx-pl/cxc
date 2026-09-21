#include <assert.h>

#include "exception_control_flow.h"

/* The generated module's reflection records reference cxcore metadata that is
 * currently internal to the DLL. These definitions isolate this executable
 * control-flow test from that separate export-policy limitation. */
struct CX_ID_4(cxcore, System, Reflection, TypeInfo)
    CX_ID_4(cxcore, System, Object, __typeinfo);
struct CX_ID_4(cxcore, System, Reflection, TypeInfo)
    CX_ID_4(cxcore, System, Int, __typeinfo);
union cx_vtable_entry CX_ID_4(cxcore, System, String, __vtable)[1];

int main(void)
{
    assert(CX_ID_2(exception_control_flow, RunFinallyTransfers)() == 7);
    assert(CX_ID_3(exception_control_flow, ExceptionProbe, State) == 11111);
    assert(CX_ID_2(exception_control_flow, ReturnFromFinally)() == 2);
    return 0;
}

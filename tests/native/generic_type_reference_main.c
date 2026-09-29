#include <assert.h>
#include <stddef.h>

#include "generic_type_reference.h"

/* The generated module references the core Object descriptor. Supply that
 * one external record so this metadata test links without the full runtime. */
struct CX_ID_4(cxcore, System, Reflection, TypeInfo)
    CX_ID_4(cxcore, System, Object, __typeinfo);
union cx_vtable_entry CX_ID_4(cxcore, System, String, __vtable)[1];

extern struct CX_ID_4(cxcore, System, Reflection, TypeInfo)
    CX_ID_3(generic_type_reference, Box, __typeinfo);
extern struct CX_ID_4(cxcore, System, Reflection, TypeInfo)
    CX_ID_3(generic_type_reference, Holder, __typeinfo);

int main(void)
{
    const struct CX_ID_4(cxcore, System, Reflection, TypeInfo)* box =
        &CX_ID_3(generic_type_reference, Box, __typeinfo);
    const struct CX_ID_4(cxcore, System, Reflection, TypeInfo)* holder =
        &CX_ID_3(generic_type_reference, Holder, __typeinfo);
    const struct cx_reflection_field* fields =
        (const struct cx_reflection_field*)holder->RuntimeFields;

    assert(box->GenericArity == 1);
    assert(holder->RuntimeFieldCount == 1);
    assert(fields[0].offset ==
        offsetof(struct CX_ID_2(generic_type_reference, Holder), value));
    return 0;
}

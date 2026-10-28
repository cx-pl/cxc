#include <assert.h>
#include <stddef.h>

#include "generic_type_reference.h"

/* The generated module references the core Object descriptor. Supply that
 * one external record so this metadata test links without the full runtime. */
struct cxcore_System_Reflection_TypeInfo cxcore_System_Object___typeinfo;
union cx_vtable_entry cxcore_System_String___vtable[1];

extern struct cxcore_System_Reflection_TypeInfo
    generic_type_reference_Box___typeinfo;
extern struct cxcore_System_Reflection_TypeInfo
    generic_type_reference_Holder___typeinfo;

int main(void) {
    const struct cxcore_System_Reflection_TypeInfo *box =
        &generic_type_reference_Box___typeinfo;
    const struct cxcore_System_Reflection_TypeInfo *holder =
        &generic_type_reference_Holder___typeinfo;
    const struct cx_reflection_field *fields =
        (const struct cx_reflection_field *)((const struct cx_runtime_type_info *)
                                                 holder->RuntimeTypeInfo)
            ->fields;

    assert(box->GenericArity == 1);
    assert(((const struct cx_runtime_type_info *)holder->RuntimeTypeInfo)->fieldCount == 1);
    assert(fields[0].offset == offsetof(struct generic_type_reference_Holder, value));
    return 0;
}

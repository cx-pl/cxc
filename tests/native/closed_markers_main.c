#include <assert.h>

#include "closed_markers.h"

struct cxcore_System_Reflection_TypeInfo cxcore_System_Object___typeinfo;
union cx_vtable_entry cxcore_System_String___vtable[1];

#define FIRST cx_generic_47e3da48c7e666c974ab9a5c69701022c853f43e7bc6e7c38f0b9504bcc99ea4
#define SECOND cx_generic_e8ea69c6cbd0e4cada561bce33d9d980481d118bd7f89985f1c5f8c6be0e96c5

const struct cxcore_System_Reflection_TypeInfo *
    closed_markers_first_from_consumer(void);
const struct cxcore_System_Reflection_TypeInfo *
    closed_markers_second_from_consumer(void);

int main(void) {
    const struct cxcore_System_Reflection_TypeInfo *first = &CX_TYPEINFO_NAME(FIRST);
    const struct cxcore_System_Reflection_TypeInfo *second = &CX_TYPEINFO_NAME(SECOND);

    assert(first != second);
    assert(first == closed_markers_first_from_consumer());
    assert(second == closed_markers_second_from_consumer());
    assert(first->Hash != second->Hash);
    assert(first->Size == sizeof(struct closed_markers_Marker));
    assert(second->Size == first->Size);
    assert(first->GenericArity == 1 && second->GenericArity == 1);
    assert(cx_generic_47e3da48c7e666c974ab9a5c69701022c853f43e7bc6e7c38f0b9504bcc99ea4___vtable[0]
               .data == first);
    assert(cx_generic_e8ea69c6cbd0e4cada561bce33d9d980481d118bd7f89985f1c5f8c6be0e96c5___vtable[0]
               .data == second);
    return 0;
}

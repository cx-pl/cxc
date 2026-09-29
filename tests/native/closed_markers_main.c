#include <assert.h>

#include "closed_markers.h"

struct CX_ID_4(cxcore, System, Reflection, TypeInfo)
    CX_ID_4(cxcore, System, Object, __typeinfo);
union cx_vtable_entry CX_ID_4(cxcore, System, String, __vtable)[1];

#define FIRST cx_generic_47e3da48c7e666c974ab9a5c69701022c853f43e7bc6e7c38f0b9504bcc99ea4
#define SECOND cx_generic_e8ea69c6cbd0e4cada561bce33d9d980481d118bd7f89985f1c5f8c6be0e96c5

const struct CX_ID_4(cxcore, System, Reflection, TypeInfo)*
closed_markers_first_from_consumer(void);
const struct CX_ID_4(cxcore, System, Reflection, TypeInfo)*
closed_markers_second_from_consumer(void);

int main(void)
{
    const struct CX_ID_4(cxcore, System, Reflection, TypeInfo)* first =
        &CX_TYPEINFO_NAME(FIRST);
    const struct CX_ID_4(cxcore, System, Reflection, TypeInfo)* second =
        &CX_TYPEINFO_NAME(SECOND);

    assert(first != second);
    assert(first == closed_markers_first_from_consumer());
    assert(second == closed_markers_second_from_consumer());
    assert(first->Hash != second->Hash);
    assert(first->Size == sizeof(struct CX_ID_2(closed_markers, Marker)));
    assert(second->Size == first->Size);
    assert(first->GenericArity == 1 && second->GenericArity == 1);
    assert(CX_ID_2(cx_generic_47e3da48c7e666c974ab9a5c69701022c853f43e7bc6e7c38f0b9504bcc99ea4,
        __vtable)[0].data == first);
    assert(CX_ID_2(cx_generic_e8ea69c6cbd0e4cada561bce33d9d980481d118bd7f89985f1c5f8c6be0e96c5,
        __vtable)[0].data == second);
    return 0;
}

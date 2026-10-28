#include <stdlib.h>
#include "app.h"
#include "cx.h"
#include "cxthreading.h"

extern size_t cx_memory_test_allocation_count_get(void);
extern void cx_memory_test_allocation_tracking_set(cx_bool enabled);

static size_t allocation_start;
static cx_ptr rooted_objects[64];
static const struct cx_gc_root_region object_roots[] = {
    {rooted_objects, (cx_uint)sizeof(rooted_objects)},
};
static cx_uint rooted_object_count;
static cx_bool roots_registered;

void app_App_cx_test_allocation_probe_begin(void) {
    cx_memory_test_allocation_tracking_set(CX_TRUE);
    allocation_start = cx_memory_test_allocation_count_get();
}

cx_int app_App_cx_test_allocation_probe_end(void) {
    size_t allocations =
        cx_memory_test_allocation_count_get() - allocation_start;
    cx_memory_test_allocation_tracking_set(CX_FALSE);
    return (cx_int)allocations;
}

void app_App_cx_test_root_object(
    struct cxcore_System_Object *value) {
    if (!roots_registered) {
        cx_gc_register_static_roots(
            object_roots,
            (cx_uint)(sizeof(object_roots) / sizeof(object_roots[0])));
        roots_registered = CX_TRUE;
    }
    if (rooted_object_count >=
        (cx_uint)(sizeof(rooted_objects) / sizeof(rooted_objects[0]))) {
        abort();
    }
    rooted_objects[rooted_object_count++] = (cx_ptr)value;
}

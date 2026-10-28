#include <cx.h>
#include <stdlib.h>
#include "app.h"
#include "math.internal.h"
#include "cxthreading.h"

extern size_t cx_memory_test_allocation_count_get(void);
extern void cx_memory_test_allocation_tracking_set(cx_bool enabled);

static cx_ptr cx_test_rooted_objects[4096];
static const struct cx_gc_root_region cx_test_object_roots[] = {
    {cx_test_rooted_objects, (cx_uint)sizeof(cx_test_rooted_objects)},
};
static cx_uint cx_test_rooted_object_count;
static cx_bool cx_test_object_roots_registered;

void app_App_cx_test_root_object(
    struct cxcore_System_Object *value) {
    if (!cx_test_object_roots_registered) {
        cx_gc_register_static_roots(
            cx_test_object_roots,
            (cx_uint)(sizeof(cx_test_object_roots) /
                sizeof(cx_test_object_roots[0])));
        cx_test_object_roots_registered = CX_TRUE;
    }
    if (cx_test_rooted_object_count >=
        (cx_uint)(sizeof(cx_test_rooted_objects) /
            sizeof(cx_test_rooted_objects[0]))) {
        abort();
    }
    cx_test_rooted_objects[cx_test_rooted_object_count++] = (cx_ptr)value;
}

static size_t cx_test_allocation_probe_start;
static cx_int cx_test_conditional_branch_source_counts[2];

void app_App_cx_test_reset_conditional_branch_source_counts(void) {
    cx_test_conditional_branch_source_counts[0] = 0;
    cx_test_conditional_branch_source_counts[1] = 0;
}

void app_App_cx_test_record_conditional_branch_source(
    cx_int branch) {
    if (branch >= 1 && branch <= 2) {
        cx_test_conditional_branch_source_counts[branch - 1]++;
    }
}

cx_int app_App_cx_test_conditional_branch_source_count(
    cx_int branch) {
    if (branch < 1 || branch > 2) {
        return -1;
    }
    return cx_test_conditional_branch_source_counts[branch - 1];
}

void app_App_cx_test_allocation_probe_begin(void) {
    cx_memory_test_allocation_tracking_set(1);
    cx_test_allocation_probe_start = cx_memory_test_allocation_count_get();
}

cx_int app_App_cx_test_allocation_probe_end(void) {
    size_t allocations =
        cx_memory_test_allocation_count_get() - cx_test_allocation_probe_start;
    cx_memory_test_allocation_tracking_set(0);
    return (cx_int)allocations;
}

cx_int app_App_cx_test_large_payload_size(void) {
    return (cx_int)sizeof(struct math_Math_LargeTaskPayload);
}

cx_int app_App_cx_test_pointer_size(void) {
    return (cx_int)sizeof(void*);
}

cx_bool app_App_cx_test_reusable_source_result_is_null(
    struct cxcore_System_Object *target) {
    const struct cxcore_System_Threading_Tasks_ReusableValueTaskSource *source =
        (const struct cxcore_System_Threading_Tasks_ReusableValueTaskSource *)target;
    cx_ptr value = source->_result;
    return value == NULL ? CX_TRUE : CX_FALSE;
}

void app_App_cx_test_reusable_source_set_version(
    struct cxcore_System_Object *target,
    cx_uint version) {
    struct cxcore_System_Threading_Tasks_ReusableValueTaskSource *source =
        (struct cxcore_System_Threading_Tasks_ReusableValueTaskSource *)target;
    source->_version = version;
}

#include "memoryArenaDynamicStaticInit.h"
#include "cxruntime.h"

extern struct cxcore_System_Reflection_TypeInfo
    memoryArenaDynamicStaticInit_DynamicStaticInitNode___typeinfo;

struct reflection_invocation_context {
    int result_survived_collection;
    int reference_argument_return_matched;
    int reference_argument_survived_collection;
};

static void read_during_arena(void *context) {
    int *result = (int *)context;
    *result = memoryArenaDynamicStaticInit_DynamicStaticInitRoots_ReadChecksum();
}

static void invoke_reflection_in_arena(void *environment) {
    struct reflection_invocation_context *context =
        (struct reflection_invocation_context *)environment;
    volatile struct memoryArenaDynamicStaticInit_DynamicStaticInitNode *result =
        (struct memoryArenaDynamicStaticInit_DynamicStaticInitNode *)
            cxcore_System_Reflection_TypeInfo_InvokeFunction(
                &memoryArenaDynamicStaticInit_DynamicStaticInitNode___typeinfo,
                NULL, 2u, NULL);
    volatile cx_ptr next = cxcore_System_Reflection_TypeInfo_InvokeFunction(
        &memoryArenaDynamicStaticInit_DynamicStaticInitNode___typeinfo,
        NULL, 3u, NULL);
    struct cxcore_System_Collections_Array *arguments =
        cx_array_new(1u, (cx_uint)sizeof(cx_ptr));
    volatile cx_ptr returned;
    cx_gc_store_reference((cx_ptr *)arguments->_data, next);
    returned = cxcore_System_Reflection_TypeInfo_InvokeFunction(
        &memoryArenaDynamicStaticInit_DynamicStaticInitNode___typeinfo,
        (cx_ptr)result, 1u, arguments);
    context->reference_argument_return_matched = returned == next;
    cx_gc_store_reference((cx_ptr *)arguments->_data, NULL);
    next = NULL;
    returned = NULL;
    cx_gc_collect();
    context->result_survived_collection = result != NULL && result->Value == 29;
    context->reference_argument_survived_collection =
        result != NULL && result->Next != NULL && result->Next->Value == 31;
}

int main(void) {
    cx_action action = {0};
    int first = 0;
    int second;
    struct reflection_invocation_context reflection = {0};
    action.__function = read_during_arena;
    action.__environment = &first;
    action.__target = NULL;
    action.__method = NULL;
    action.__invocation_list = NULL;
    cxcore_System_Memory_WithArena(action);
    if (first != 59) return 1;

    cx_gc_collect();
    second = memoryArenaDynamicStaticInit_DynamicStaticInitRoots_ReadChecksum();
    if (second != 59) return 2;
    if (memoryArenaDynamicStaticInit_DynamicStaticInitRoots_ExerciseReusableArena() != 0) {
        return 3;
    }
    if (memoryArenaDynamicStaticInit_DynamicStaticInitRoots_ExerciseBoundedArena() != 0) {
        return 4;
    }

    action.__environment = &reflection;
    action.__function = invoke_reflection_in_arena;
    cxcore_System_Memory_WithArena(action);
    if (!reflection.result_survived_collection ||
        !reflection.reference_argument_return_matched ||
        !reflection.reference_argument_survived_collection) return 5;

    return 0;
}

#define _POSIX_C_SOURCE 200809L

#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>

extern char *getenv(const char *name);

extern size_t cx_memory_test_allocation_count_get(void);
extern size_t cx_memory_test_allocation_bytes_get(void);
extern size_t cx_memory_benchmark_heap_bytes_get(void);
extern void cx_memory_test_allocation_tracking_set(char enabled);

static size_t allocation_count_before;
static size_t allocation_bytes_before;
static size_t allocation_count_sample;
static size_t allocation_bytes_sample;

uint32_t CollectionQueryBenchmarks_BenchmarkNativeClock_ShouldMeasureCategory(
    uint32_t category) {
    const char *retained_only = getenv("CX_QUERY_RETAINED_ONLY");
    if (retained_only != NULL && retained_only[0] == '1') return 0u;
    const char *sort_growth_only = getenv("CX_QUERY_SORT_GROWTH_ONLY");
    if (sort_growth_only != NULL && sort_growth_only[0] == '1') {
        return (category >= 27u && category <= 40u) ||
            category == 43u || category == 44u;
    }
    const char *low_sort_growth_only = getenv("CX_QUERY_LOW_SORT_GROWTH_ONLY");
    if (low_sort_growth_only != NULL && low_sort_growth_only[0] == '1') {
        return (category >= 27u && category <= 30u) ||
            category == 43u || category == 44u;
    }
    const char *callbacks_only = getenv("CX_QUERY_CALLBACKS_ONLY");
    if (callbacks_only != NULL && callbacks_only[0] == '1') {
        switch (category) {
            case 8u:
            case 9u:
            case 41u:
            case 42u:
                return 1u;
            default:
                return 0u;
        }
    }
    const char *map_filter_take_only = getenv("CX_QUERY_MAP_FILTER_TAKE_ONLY");
    if (map_filter_take_only != NULL && map_filter_take_only[0] == '1') {
        return category == 45u || category == 46u ||
            category == 71u || category == 72u;
    }
    const char *map_take_only = getenv("CX_QUERY_MAP_TAKE_ONLY");
    if (map_take_only != NULL && map_take_only[0] == '1') {
        return category >= 47u && category <= 52u;
    }
    const char *map_skip_take_only = getenv("CX_QUERY_MAP_SKIP_TAKE_ONLY");
    if (map_skip_take_only != NULL && map_skip_take_only[0] == '1') {
        return category >= 65u && category <= 70u;
    }
    const char *filter_take_only = getenv("CX_QUERY_FILTER_TAKE_ONLY");
    if (filter_take_only != NULL && filter_take_only[0] == '1') {
        return category >= 53u && category <= 56u;
    }
    const char *skip_take_only = getenv("CX_QUERY_SKIP_TAKE_ONLY");
    if (skip_take_only != NULL && skip_take_only[0] == '1') {
        return category >= 57u && category <= 60u;
    }
    const char *filter_skip_take_only = getenv("CX_QUERY_FILTER_SKIP_TAKE_ONLY");
    if (filter_skip_take_only != NULL && filter_skip_take_only[0] == '1') {
        return category >= 61u && category <= 64u;
    }
    const char *budget_only = getenv("CX_QUERY_BUDGETS_ONLY");
    if (budget_only == NULL || budget_only[0] != '1') return 1u;
    switch (category) {
        case 8u:
        case 9u:
        case 7u:
        case 13u:
        case 14u:
        case 15u:
        case 17u:
        case 18u:
        case 23u:
        case 24u:
        case 29u:
        case 30u:
        case 31u:
        case 32u:
        case 33u:
        case 34u:
        case 35u:
        case 36u:
        case 37u:
        case 38u:
        case 39u:
        case 40u:
        case 25u:
        case 26u:
        case 27u:
        case 28u:
        case 41u:
        case 42u:
        case 43u:
        case 44u:
        case 45u:
        case 46u:
        case 71u:
        case 72u:
        case 47u:
        case 48u:
        case 49u:
        case 50u:
        case 51u:
        case 52u:
        case 53u:
        case 54u:
        case 55u:
        case 56u:
        case 57u:
        case 58u:
        case 59u:
        case 60u:
        case 65u:
        case 66u:
        case 67u:
        case 68u:
        case 69u:
        case 70u:
            return 1u;
        default:
            return 0u;
    }
}

#if defined(_WIN32)
#include <windows.h>

int64_t CollectionQueryBenchmarks_BenchmarkNativeClock_GetNanoseconds(void) {
    static LARGE_INTEGER frequency;
    static int initialized;
    LARGE_INTEGER counter;
    if (!initialized) {
        if (!QueryPerformanceFrequency(&frequency) || frequency.QuadPart == 0)
            return 0;
        initialized = 1;
    }
    if (!QueryPerformanceCounter(&counter)) return 0;
    return (int64_t)(counter.QuadPart / frequency.QuadPart) * INT64_C(1000000000) +
        ((int64_t)(counter.QuadPart % frequency.QuadPart) * INT64_C(1000000000)) /
            frequency.QuadPart;
}
#else
#include <time.h>

int64_t CollectionQueryBenchmarks_BenchmarkNativeClock_GetNanoseconds(void) {
    struct timespec now;
    if (clock_gettime(CLOCK_MONOTONIC, &now) != 0) return 0;
    return (int64_t)now.tv_sec * INT64_C(1000000000) + now.tv_nsec;
}
#endif

void CollectionQueryBenchmarks_BenchmarkNativeClock_StartAllocationTracking(void) {
    allocation_count_before = cx_memory_test_allocation_count_get();
    allocation_bytes_before = cx_memory_test_allocation_bytes_get();
    cx_memory_test_allocation_tracking_set(1);
}

void CollectionQueryBenchmarks_BenchmarkNativeClock_StopAllocationTracking(void) {
    cx_memory_test_allocation_tracking_set(0);
    allocation_count_sample =
        cx_memory_test_allocation_count_get() - allocation_count_before;
    allocation_bytes_sample =
        cx_memory_test_allocation_bytes_get() - allocation_bytes_before;
}

int64_t CollectionQueryBenchmarks_BenchmarkNativeClock_GetAllocationCount(void) {
    return (int64_t)allocation_count_sample;
}

int64_t CollectionQueryBenchmarks_BenchmarkNativeClock_GetAllocationBytes(void) {
    return (int64_t)allocation_bytes_sample;
}

int64_t CollectionQueryBenchmarks_BenchmarkNativeClock_GetHeapBytes(void) {
    return (int64_t)cx_memory_benchmark_heap_bytes_get();
}

#if defined(_MSC_VER)
__declspec(noinline)
#elif defined(__GNUC__)
__attribute__((noinline))
#endif
void CollectionQueryBenchmarks_BenchmarkNativeClock_ScrubConservativeStack(
    void) {
    /* Clear returned CX helper frames before a conservative GC measurement. */
    volatile uintptr_t scratch[8192];
    for (size_t index = 0; index < sizeof(scratch) / sizeof(scratch[0]); ++index) {
        scratch[index] = 0;
    }
}

uint32_t
CollectionQueryBenchmarks_BenchmarkNativeClock_SupportsRetainedReleaseBudget(
    void) {
#if defined(_WIN32)
    return 1u;
#else
    return 0u;
#endif
}

uint32_t
CollectionQueryBenchmarks_BenchmarkNativeClock_SupportsPrefixCompletionReleaseBudget(
    void) {
    return 1u;
}

void CollectionQueryBenchmarks_BenchmarkNativeClock_WriteResult(
    uint32_t category,
    uint32_t sample,
    uint32_t iterations,
    int64_t elapsed_nanoseconds,
    int64_t checksum,
    int64_t allocation_count,
    int64_t allocation_bytes) {
    static const char *names[] = {
        "handwritten-array",
        "fused-filter-array",
        "layered-filter-array-interface",
        "layered-filter-list-interface",
        "handwritten-streaming-filter-map-take",
        "query-streaming-filter-map-take",
        "handwritten-page-map",
        "query-page-map",
        "handwritten-captured-filter-map",
        "query-captured-filter-map",
        "query-sort-array",
        "handwritten-single-filter-map-take",
        "query-single-filter-map-take-fused",
        "query-single-filter-map-take-layered",
        "query-indexed-array-page-map",
        "query-indexed-array-filter-map-take",
        "handwritten-indexed-list-filter-map-take",
        "query-indexed-list-filter-map-take",
        "query-interface-list-filter-map-take",
        "query-sort-array-128-shuffled",
        "query-sort-array-512-shuffled",
        "query-sort-array-2048-shuffled",
        "handwritten-indexed-list-page-map",
        "query-indexed-list-page-map",
        "query-interface-list-page-map",
        "query-sort-array-backed-interface",
        "query-sort-list-backed-interface",
        "query-sort-multi-key-array-backed",
        "query-sort-multi-key-list-backed",
        "query-sort-three-key-array-backed",
        "query-sort-three-key-list-backed",
        "query-sort-four-key-array-backed",
        "query-sort-four-key-list-backed",
        "query-sort-five-key-array-backed",
        "query-sort-five-key-list-backed",
        "query-sort-six-key-array-backed",
        "query-sort-six-key-list-backed",
        "query-sort-seven-key-array-backed",
        "query-sort-seven-key-list-backed",
        "query-sort-eight-key-array-backed",
        "query-sort-eight-key-list-backed",
        "handwritten-noncaptured-filter-map",
        "query-noncaptured-filter-map",
        "query-sort-one-key-bucket-array",
        "query-sort-one-key-bucket-list",
        "query-indexed-array-map-filter-take",
        "query-interface-array-map-filter-take",
        "handwritten-array-map-take",
        "query-indexed-array-map-take",
        "query-interface-array-map-take",
        "handwritten-list-map-take",
        "query-indexed-list-map-take",
        "query-interface-list-map-take",
        "query-indexed-array-filter-take",
        "query-interface-array-filter-take",
        "query-indexed-list-filter-take",
        "query-interface-list-filter-take",
        "query-indexed-array-skip-take",
        "query-interface-array-skip-take",
        "query-indexed-list-skip-take",
        "query-interface-list-skip-take",
        "query-indexed-array-filter-skip-take",
        "query-interface-array-filter-skip-take",
        "query-indexed-list-filter-skip-take",
        "query-interface-list-filter-skip-take",
        "handwritten-array-map-skip-take",
        "query-indexed-array-map-skip-take",
        "query-interface-array-map-skip-take",
        "handwritten-list-map-skip-take",
        "query-indexed-list-map-skip-take",
        "query-interface-list-map-skip-take",
        "query-indexed-list-map-filter-take",
        "query-interface-list-map-filter-take"
    };
    if (category >= sizeof(names) / sizeof(names[0])) return;
    printf("%s,%u,%u,%lld,%lld,%lld,%lld\n",
        names[category], sample, iterations,
        (long long)elapsed_nanoseconds,
        (long long)checksum,
        (long long)allocation_count,
        (long long)allocation_bytes);
}

void CollectionQueryBenchmarks_BenchmarkNativeClock_WriteRetained(
    uint32_t workload,
    int64_t retained_bytes) {
    static const char *names[] = {
        "static-filter-map-query",
        "captured-filter-map-query",
        "seven-key-sort-scratch-released-on-dispose",
        "eight-key-sort-scratch-released-on-dispose",
        "indexed-array-map-take-current-released-on-dispose",
        "indexed-list-map-take-current-released-on-dispose",
        "indexed-array-filter-map-take-current-released-on-dispose",
        "indexed-list-filter-map-take-current-released-on-dispose",
        "indexed-array-filter-take-capture-released-on-prefix-completion",
        "indexed-list-filter-take-capture-released-on-prefix-completion",
        "indexed-array-filter-skip-take-capture-released-on-prefix-completion",
        "indexed-list-filter-skip-take-capture-released-on-prefix-completion",
        "indexed-array-map-skip-take-capture-released-on-prefix-completion",
        "indexed-list-map-skip-take-capture-released-on-prefix-completion"
    };
    if (workload >= sizeof(names) / sizeof(names[0])) return;
    printf("# retained,%s,%lld\n", names[workload], (long long)retained_bytes);
}

void CollectionQueryBenchmarks_BenchmarkNativeClock_WriteHeader(void) {
    puts("workload,sample,iterations,elapsed_ns,checksum,managed_allocations,managed_bytes");
}

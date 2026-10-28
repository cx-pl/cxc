#include "bench.h"
#include "cxruntime.h"

void bench_App_ForceCollection(void) {
    cx_gc_collect();
}

int main(void) {
    return bench_App_Run();
}

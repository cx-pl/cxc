#include "threadingFixture.h"
#ifdef _WIN32
#include <windows.h>
static volatile LONG running = 1;
cx_bool threadingFixture_RunLoop(void) {
    return InterlockedCompareExchange(&running, 0, 0) != 0;
}
void threadingFixture_EndLoop(void) { InterlockedExchange(&running, 0); }
#else
#include <stdatomic.h>
static _Atomic int running = 1;
cx_bool threadingFixture_RunLoop(void) {
    return atomic_load_explicit(&running, memory_order_acquire) != 0;
}
void threadingFixture_EndLoop(void) {
    atomic_store_explicit(&running, 0, memory_order_release);
}
#endif
void threadingFixture_Collect(void) { cx_gc_collect(); }

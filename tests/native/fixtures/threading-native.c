#include "threading_fixture.h"
#ifdef _WIN32
#include <windows.h>
static volatile LONG running = 1;
cx_bool CX_ID_2(threading_fixture, RunLoop)(void) {
    return InterlockedCompareExchange(&running, 0, 0) != 0;
}
void CX_ID_2(threading_fixture, EndLoop)(void) { InterlockedExchange(&running, 0); }
#else
#include <stdatomic.h>
static _Atomic int running = 1;
cx_bool CX_ID_2(threading_fixture, RunLoop)(void) {
    return atomic_load_explicit(&running, memory_order_acquire) != 0;
}
void CX_ID_2(threading_fixture, EndLoop)(void) {
    atomic_store_explicit(&running, 0, memory_order_release);
}
#endif
void CX_ID_2(threading_fixture, Collect)(void) { cx_gc_collect(); }

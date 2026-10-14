#include "native_fixture.h"
#include <errno.h>
struct fixture_owner { int value; };
fixture_point fixture_make(int x, int y) { fixture_point result = {x, y}; return result; }
fixture_aligned fixture_aligned_echo(fixture_aligned value) { return value; }
int fixture_sum(fixture_point point) { return point.x + point.y; }
void fixture_output(int *value) { *value = 42; }
fixture_owner *fixture_open(void) { fixture_owner *owner = malloc(sizeof(*owner)); if (owner) owner->value = 42; return owner; }
void fixture_close(fixture_owner *owner) { free(owner); }
int fixture_fail(void) { errno = EDOM; return -1; }
int fixture_call(fixture_callback callback) { return callback(20, 22); }

struct fixture_callback_context { fixture_callback callback; int result; };
struct fixture_retained_context { fixture_retained_callback callback; void *context; int result; };
#if defined(_WIN32)
#include <windows.h>
#include <process.h>
static unsigned __stdcall fixture_worker(void *data) {
    struct fixture_callback_context *context = data;
    context->result = context->callback(20, 22);
    return 0;
}
static unsigned __stdcall fixture_retained_worker(void *data) {
    struct fixture_retained_context *context = data;
    context->result = context->callback(20, context->context);
    return 0;
}
#else
#include <pthread.h>
static void *fixture_worker(void *data) {
    struct fixture_callback_context *context = data;
    context->result = context->callback(20, 22);
    return NULL;
}
static void *fixture_retained_worker(void *data) {
    struct fixture_retained_context *context = data;
    context->result = context->callback(20, context->context);
    return NULL;
}
#endif
int fixture_preserve_error(fixture_callback callback) {
    errno = 123;
#if defined(_WIN32)
    SetLastError(123);
#endif
    int result = callback(20, 22);
    if (errno != 123) return -3;
#if defined(_WIN32)
    if (GetLastError() != 123) return -4;
#endif
    return result;
}
int fixture_fail_last_error(void) {
#if defined(_WIN32)
    SetLastError(1234);
#else
    errno = 1234;
#endif
    return -1;
}
int fixture_foreign_call(fixture_callback callback) {
    struct fixture_callback_context context = {callback, 0};
#if defined(_WIN32)
    HANDLE worker = (HANDLE)_beginthreadex(NULL, 0, fixture_worker, &context, 0, NULL);
    if (worker == NULL) return -2;
    WaitForSingleObject(worker, INFINITE); CloseHandle(worker);
#else
    pthread_t worker;
    if (pthread_create(&worker, NULL, fixture_worker, &context) != 0) return -2;
    pthread_join(worker, NULL);
#endif
    return context.result;
}
int fixture_retained_foreign(fixture_retained_callback callback, void *data) {
    struct fixture_retained_context context = {callback, data, 0};
#if defined(_WIN32)
    HANDLE worker = (HANDLE)_beginthreadex(NULL, 0, fixture_retained_worker, &context, 0, NULL);
    if (worker == NULL) return -2;
    WaitForSingleObject(worker, INFINITE); CloseHandle(worker);
#else
    pthread_t worker;
    if (pthread_create(&worker, NULL, fixture_retained_worker, &context) != 0) return -2;
    pthread_join(worker, NULL);
#endif
    return context.result;
}

#ifndef CX_TEST_NATIVE_FIXTURE_H
#define CX_TEST_NATIVE_FIXTURE_H
#include <stdint.h>
#include <stdlib.h>
typedef struct fixture_point { int x; int y; } fixture_point;
typedef struct fixture_aligned { _Alignas(64) int value; } fixture_aligned;
fixture_aligned fixture_aligned_echo(fixture_aligned value);
typedef struct fixture_owner fixture_owner;
typedef int (*fixture_callback)(int number, unsigned int other);
int fixture_call(fixture_callback callback);
int fixture_preserve_error(fixture_callback callback);
int fixture_foreign_call(fixture_callback callback);
typedef int (*fixture_retained_callback)(int number, void *context);
int fixture_retained_foreign(fixture_retained_callback callback, void *context);
fixture_point fixture_make(int x, int y);
int fixture_sum(fixture_point point);
void fixture_output(int *value);
fixture_owner *fixture_open(void);
void fixture_close(fixture_owner *owner);
int fixture_fail(void);
int fixture_fail_last_error(void);
#endif

#include <assert.h>

#include "generic_explicit_call.h"

int main(void) {
    assert(generic_explicit_call_RunInt() == 23);
    assert(generic_explicit_call_RunIntInferred() == 29);
    assert(generic_explicit_call_RunLong() == 41L);
    return 0;
}

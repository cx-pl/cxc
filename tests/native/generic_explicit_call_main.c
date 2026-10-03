#include <assert.h>

#include "generic_explicit_call.h"

int main(void) {
    assert(CX_ID_2(generic_explicit_call, RunInt)() == 23);
    assert(CX_ID_2(generic_explicit_call, RunIntInferred)() == 29);
    assert(CX_ID_2(generic_explicit_call, RunLong)() == 41L);
    return 0;
}

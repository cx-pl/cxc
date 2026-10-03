#include <assert.h>

#include "generic_copy.h"

int main(void) {
    assert(CX_ID_2(generic_copy, RunInt)() == 7);
    assert(CX_ID_2(generic_copy, RunLong)() == 9);
    assert(CX_ID_2(generic_copy, RunIntAgain)() == 11);
    assert(CX_ID_2(generic_copy, RunPairInt)() == 13);
    assert(CX_ID_2(generic_copy, RunPairLong)() == 15);
    return 0;
}

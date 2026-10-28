#include <assert.h>

#include "generic_copy.h"

int main(void) {
    assert(generic_copy_RunInt() == 7);
    assert(generic_copy_RunLong() == 9);
    assert(generic_copy_RunIntAgain() == 11);
    assert(generic_copy_RunPairInt() == 13);
    assert(generic_copy_RunPairLong() == 15);
    return 0;
}

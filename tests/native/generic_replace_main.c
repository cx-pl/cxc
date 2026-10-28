#include <assert.h>

#include "generic_replace.h"

int main(void) {
    assert(generic_replace_RunInt() == 7);
    assert(generic_replace_RunLong() == 13L);
    return 0;
}

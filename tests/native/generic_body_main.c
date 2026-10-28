#include <assert.h>

#include "generic_body.h"

int main(void) {
    assert(generic_body_RunInt() == 7);
    assert(generic_body_RunLong() == 9);
    assert(generic_body_RunIntAgain() == 11);
    return 0;
}

#include <assert.h>

#include "generic_body.h"

int main(void)
{
    assert(CX_ID_2(generic_body, RunInt)() == 7);
    assert(CX_ID_2(generic_body, RunLong)() == 9);
    assert(CX_ID_2(generic_body, RunIntAgain)() == 11);
    return 0;
}

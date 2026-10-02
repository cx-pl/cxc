#include <assert.h>

#include "generic_replace.h"

int main(void)
{
    assert(CX_ID_2(generic_replace, RunInt)() == 7);
    assert(CX_ID_2(generic_replace, RunLong)() == 13L);
    return 0;
}

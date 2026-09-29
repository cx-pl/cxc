#include <assert.h>

#include "generic_function.h"

cx_long cx_generic_0bda995f6a526332e497eee604798bdb6f2c07f851d01964c54540d6fd7dd9bc(cx_long value)
{
    return value + 2;
}

cx_int cx_generic_5d67cbd1703a5f9084351a81b25b73979e724f95431aa1d8773baaab3c8f6cb3(cx_int value)
{
    return value + 1;
}

int main(void)
{
    assert(CX_ID_2(generic_function, RunInt)() == 42);
    assert(CX_ID_2(generic_function, RunIntAgain)() == 41);
    assert(CX_ID_2(generic_function, RunLong)() == 43);
    return 0;
}

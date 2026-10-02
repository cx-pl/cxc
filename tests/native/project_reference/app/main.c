#include "app.h"

int main(void) {
    return CX_ID_3(app, App, Run)() == 42 ? 0 : 1;
}

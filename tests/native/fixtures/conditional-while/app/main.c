#include "app.h"
#include "cxruntime.h"

void app_App_ForceCollection(void) {
    cx_gc_collect();
}

int main(void) {
    return app_App_Run();
}

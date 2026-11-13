#include "app.h"

struct cxcore_System_Reflection_TypeInfo app_App_CrossModuleDiProbe_ForceTypeCollision(
    struct cxcore_System_Reflection_TypeInfo typeInfo,
    struct cxcore_System_Reflection_TypeInfo registeredType) {
    typeInfo.Hash = registeredType.Hash;
    typeInfo.Name = registeredType.Name;
    typeInfo.Namespace = registeredType.Namespace;
    typeInfo.GenericArity = registeredType.GenericArity;
    typeInfo.GenericParams = registeredType.GenericParams;
    return typeInfo;
}

int main(void) {
    return app_App_CrossModuleDiProbe_Run();
}

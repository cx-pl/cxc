#include "di.h"
#include "di.internal.h"
#include "cxruntime.h"
#include <string.h>

struct di_arena_singleton_probe {
    struct cxcore_System_DependencyInjection_ServiceProvider *provider;
    struct cxcore_System_Reflection_TypeInfo service_type;
    cx_uint identity;
    cx_bool failed;
};

static void di_probe_singleton_during_arena(void *environment) {
    struct di_arena_singleton_probe *probe =
        (struct di_arena_singleton_probe *)environment;
    struct cxcore_System_Object *result =
        cxcore_System_DependencyInjection_ServiceProvider_GetService(
            probe->provider, probe->service_type);
    if (result == NULL) {
        probe->failed = CX_TRUE;
        return;
    }

    struct di_Di_ArenaCreatedSingleton *singleton =
        (struct di_Di_ArenaCreatedSingleton *)result;
    if (singleton->Payload == NULL || singleton->Payload->Value != 73u) {
        probe->failed = CX_TRUE;
        return;
    }
    probe->identity = singleton->Identity;
    cx_gc_collect();
    if (singleton->Payload == NULL || singleton->Payload->Value != 73u) {
        probe->failed = CX_TRUE;
    }
}

cx_uint di_Di_ResolveSingletonUnderArena(
    struct cxcore_System_DependencyInjection_ServiceProvider *provider,
    struct cxcore_System_Reflection_TypeInfo service_type) {
    struct di_arena_singleton_probe probe = { provider, service_type, 0u, CX_FALSE };
    CX_FN_136FFEE706DF35B2 action = {
        di_probe_singleton_during_arena, &probe, CX_NULL, NULL, NULL
    };
    cxcore_System_Memory_WithArena(action);
    if (probe.failed || probe.identity == 0u) return 0u;

    cx_gc_collect();
    struct cxcore_System_Object *result =
        cxcore_System_DependencyInjection_ServiceProvider_GetService(
            provider, service_type);
    if (result == NULL) return 0u;
    struct di_Di_ArenaCreatedSingleton *singleton =
        (struct di_Di_ArenaCreatedSingleton *)result;
    return singleton->Identity == probe.identity &&
        singleton->Payload != NULL && singleton->Payload->Value == 73u
        ? probe.identity
        : 0u;
}

void di_Di_ForceCollection(void) {
    cx_gc_collect();
}

cx_bool di_Di_ProviderReferencesCleared(
    struct cxcore_System_DependencyInjection_ServiceProvider *provider) {
    return provider != NULL && provider->_providerRoot == NULL &&
        provider->_descriptors == NULL && provider->_options == NULL &&
        provider->_root == NULL && provider->_scopedInstances == NULL &&
        provider->_scopedCreated == NULL && provider->_singletonInstances == NULL &&
        provider->_singletonCreated == NULL && provider->_activationPlans == NULL &&
        provider->_activationPlanCreated == NULL && provider->_owned == NULL &&
        provider->_activeScopes == NULL && provider->_activeContext == NULL
        ? CX_TRUE
        : CX_FALSE;
}

int main(int argc, char **argv) {
    if (argc == 2 && strcmp(argv[1], "--arena-activation-only") == 0) {
        return di_Di_ArenaSingletonActivationScenario_Run();
    }
    return di_Di_DiProgram_Run();
}

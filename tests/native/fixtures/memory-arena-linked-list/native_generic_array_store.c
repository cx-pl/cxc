#include "memoryArenaLinkedList.h"
#include <string.h>
#ifdef _WIN32
#include <windows.h>
#else
extern char *getenv(const char *name);
#endif

int memoryArenaLinkedList_NativeGenericArrayEscapeRequested(void)
{
#ifdef _WIN32
    char value[2];
    DWORD length = GetEnvironmentVariableA(
        "CX_M22_GENERIC_ARRAY_ESCAPE", value, (DWORD)sizeof(value));
    return length == 1u && (value[0] == '1' || value[0] == '2' || value[0] == '3')
        ? value[0] - '0' : 0;
#else
    const char *value = getenv("CX_M22_GENERIC_ARRAY_ESCAPE");
    return value != NULL && (strcmp(value, "1") == 0 || strcmp(value, "2") == 0 ||
        strcmp(value, "3") == 0)
        ? value[0] - '0' : 0;
#endif
}

void memoryArenaLinkedList_NativeStoreGenericArrayEscape(void)
{
    struct cxcore_System_Collections_Array *values =
        memoryArenaLinkedList_GenericArrayStoreCache_GetValues();
    struct memoryArenaLinkedList_GenericArrayStorePayload *value =
        memoryArenaLinkedList_CreateGenericArrayStorePayload();
    memoryArenaLinkedList_StoreConcreteGenericPayload(values, value);
}

void memoryArenaLinkedList_NativeStoreGenericFieldEscape(void)
{
    struct memoryArenaLinkedList_GenericFieldStoreBox *box =
        memoryArenaLinkedList_GenericFieldStoreCache_GetBox();
    struct memoryArenaLinkedList_GenericArrayStorePayload *value =
        memoryArenaLinkedList_CreateGenericArrayStorePayload();
    memoryArenaLinkedList_StoreConcreteGenericFieldPayload(box, value);
}

void memoryArenaLinkedList_NativeStoreGenericStructFieldEscape(void)
{
    cx_ptr box =
        memoryArenaLinkedList_GenericEnvelopeStoreCache_GetBox();
    memoryArenaLinkedList_CreateAndStoreGenericEnvelope(box);
}

// ProcBridge.Hook: injected into target processes. M0: only MinHook init/uninit, no hooks yet.
#include <windows.h>
#include <MinHook.h>

namespace
{
    bool g_minHookInitialized = false;
}

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID reserved)
{
    switch (reason)
    {
    case DLL_PROCESS_ATTACH:
        DisableThreadLibraryCalls(module);
        g_minHookInitialized = (MH_Initialize() == MH_OK);
        return g_minHookInitialized ? TRUE : FALSE;

    case DLL_PROCESS_DETACH:
        // reserved != nullptr: process is terminating, other threads are gone; skip cleanup.
        if (reserved == nullptr && g_minHookInitialized)
        {
            MH_DisableHook(MH_ALL_HOOKS);
            MH_Uninitialize();
            g_minHookInitialized = false;
        }
        break;
    }
    return TRUE;
}

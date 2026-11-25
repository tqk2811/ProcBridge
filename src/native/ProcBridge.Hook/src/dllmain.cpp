// ProcBridge.Hook: injected into target processes.
// DllMain does nothing heavy (plan 4.6). The injector calls the exported ProcBridgeHookInitialize on
// a remote thread after LoadLibraryW, before the target's main thread is resumed.
#include "NtApi.h"
#include "FileHooks.h"
#include "PathMapper.h"
#include <MinHook.h>

namespace
{
    bool g_minHookInitialized = false;
    SRWLOCK g_initLock = SRWLOCK_INIT;
    bool g_initDone = false;
    DWORD g_initResult = ERROR_SUCCESS;
    ProcBridge::Hook::PathMapper g_mapper;

    DWORD InitializeOnce() noexcept
    {
        const DWORD loaded = g_mapper.LoadFromEnvironment();
        if (loaded != ERROR_SUCCESS)
        {
            return loaded;
        }
        if (g_mapper.Count() == 0)
        {
            return ERROR_SUCCESS; // nothing to redirect, stay passive
        }
        if (MH_Initialize() != MH_OK)
        {
            return ERROR_GEN_FAILURE;
        }
        g_minHookInitialized = true;
        return ProcBridge::Hook::InstallFileHooks(g_mapper);
    }
}

/// Thread-start compatible initializer. Returns ERROR_SUCCESS or a Win32 error code.
extern "C" DWORD WINAPI ProcBridgeHookInitialize(LPVOID /*parameter*/)
{
    AcquireSRWLockExclusive(&g_initLock);
    if (!g_initDone)
    {
        g_initResult = InitializeOnce();
        g_initDone = true;
    }
    const DWORD result = g_initResult;
    ReleaseSRWLockExclusive(&g_initLock);
    if (result != ERROR_SUCCESS)
    {
        OutputDebugStringW(L"ProcBridge.Hook: initialization failed\n");
    }
    return result;
}

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID reserved)
{
    switch (reason)
    {
    case DLL_PROCESS_ATTACH:
        DisableThreadLibraryCalls(module);
        break;

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

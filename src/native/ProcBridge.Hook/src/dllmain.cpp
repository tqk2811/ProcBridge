// ProcBridge.Hook: injected into target processes.
// DllMain does nothing heavy (plan 4.6). The injector calls the exported ProcBridgeHookInitialize on
// a remote thread after LoadLibraryW, before the target's main thread is resumed.
#include "NtApi.h"
#include "BrokerResolver.h"
#include "FileHooks.h"
#include "PathMapper.h"
#include <MinHook.h>

namespace
{
    bool g_minHookInitialized = false;
    SRWLOCK g_initLock = SRWLOCK_INIT;
    bool g_initDone = false;
    DWORD g_initResult = ERROR_SUCCESS;
    // M1 local mapping (PROCBRIDGE_MAP) or M2 Broker session (PROCBRIDGE_SESSION); the session wins.
    ProcBridge::Hook::PathMapper g_mapper;
    ProcBridge::Hook::BrokerResolver g_broker;

    DWORD InitializeOnce() noexcept
    {
        const ProcBridge::Hook::IPathResolver* resolver = nullptr;
        if (ProcBridge::Hook::BrokerResolver::IsConfigured())
        {
            const DWORD connected = g_broker.Initialize();
            if (connected != ERROR_SUCCESS)
            {
                return connected;
            }
            resolver = g_broker.Count() > 0 ? &g_broker : nullptr;
        }
        else
        {
            const DWORD loaded = g_mapper.LoadFromEnvironment();
            if (loaded != ERROR_SUCCESS)
            {
                return loaded;
            }
            resolver = g_mapper.Count() > 0 ? &g_mapper : nullptr;
        }
        if (resolver == nullptr)
        {
            return ERROR_SUCCESS; // nothing to redirect, stay passive
        }
        if (MH_Initialize() != MH_OK)
        {
            return ERROR_GEN_FAILURE;
        }
        g_minHookInitialized = true;
        return ProcBridge::Hook::InstallFileHooks(*resolver);
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

BOOL APIENTRY DllMain(HMODULE /*module*/, DWORD reason, LPVOID reserved)
{
    switch (reason)
    {
    case DLL_PROCESS_ATTACH:
        // No DisableThreadLibraryCalls: the static CRT (/MT) needs thread notifications to free its
        // per-thread data.
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

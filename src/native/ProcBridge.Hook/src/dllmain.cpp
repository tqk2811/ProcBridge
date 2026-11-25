// ProcBridge.Hook: injected into target processes.
// DllMain does nothing heavy (plan 4.6). The injector calls the exported ProcBridgeHookInitialize on
// a remote thread after LoadLibraryW, before the target's main thread is resumed.
#include "NtApi.h"
#include "BrokerResolver.h"
#include "FileHooks.h"
#include "HandleInheritance.h"
#include "ImageRedirect.h"
#include "LoaderNames.h"
#include "PathMapper.h"
#include "ProcessHooks.h"
#include <MinHook.h>
#include <ProcBridge/InitBlock.h>
#include <string>

namespace
{
    // Configuration variables handed down to every child (see ProcBridge.Spawn).
    constexpr const wchar_t* HookX64Variable = L"PROCBRIDGE_HOOK_X64";
    constexpr const wchar_t* HookX86Variable = L"PROCBRIDGE_HOOK_X86";

    bool g_minHookInitialized = false;
    SRWLOCK g_initLock = SRWLOCK_INIT;
    bool g_initDone = false;
    DWORD g_initResult = ERROR_SUCCESS;
    // M1 local mapping (PROCBRIDGE_MAP) or M2 Broker session (PROCBRIDGE_SESSION); the session wins.
    ProcBridge::Hook::PathMapper g_mapper;
    ProcBridge::Hook::BrokerResolver g_broker;

    /// Parsed init block (plan 5): environment variables to set and inherited virtual handles.
    struct InitData
    {
        const wchar_t* Environment = nullptr;
        size_t EnvironmentChars = 0;
        const ProcBridge::Inject::InheritedHandle* Handles = nullptr;
        size_t HandleCount = 0;
        const BYTE* Extra = nullptr;
        size_t ExtraBytes = 0;
    };

    /// Sets each "NAME=VALUE" of the block in this process, so they are read below and inherited by
    /// children created with the default environment.
    DWORD ApplyEnvironment(const InitData& data)
    {
        const wchar_t* entry = data.Environment;
        const wchar_t* end = data.Environment + data.EnvironmentChars;
        while (entry < end)
        {
            const std::wstring text(entry, wcsnlen(entry, static_cast<size_t>(end - entry)));
            entry += text.size() + 1;
            const size_t equals = text.find(L'=');
            if (equals == std::wstring::npos || equals == 0)
            {
                continue;
            }
            const std::wstring name = text.substr(0, equals);
            // An empty value removes the variable: the configuration channel that is not used (PROCBRIDGE_MAP
            // or the Broker session) must not be taken over by a value inherited from elsewhere.
            const wchar_t* value = equals + 1 < text.size() ? text.c_str() + equals + 1 : nullptr;
            if (!SetEnvironmentVariableW(name.c_str(), value) && (value != nullptr || GetLastError() != ERROR_ENVVAR_NOT_FOUND))
            {
                return GetLastError();
            }
        }
        return ERROR_SUCCESS;
    }

    std::wstring GetVariable(const wchar_t* name)
    {
        const DWORD needed = GetEnvironmentVariableW(name, nullptr, 0);
        if (needed == 0)
        {
            return std::wstring();
        }
        std::wstring value(needed, L'\0');
        value.resize(GetEnvironmentVariableW(name, value.data(), needed));
        return value;
    }

    std::wstring GetSelfPath()
    {
        HMODULE self = nullptr;
        GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
            reinterpret_cast<LPCWSTR>(&GetSelfPath), &self);
        wchar_t path[MAX_PATH];
        const DWORD length = GetModuleFileNameW(self, path, MAX_PATH);
        return std::wstring(path, length < MAX_PATH ? length : 0);
    }

    /// Configuration handed to children: the variables of this process as initialized (a process
    /// that later clears them still passes them on).
    std::wstring CaptureChildEnvironment(ProcBridge::Inject::HookPaths& hooks)
    {
        hooks.X64 = GetVariable(HookX64Variable);
        hooks.X86 = GetVariable(HookX86Variable);
#ifdef _WIN64
        std::wstring& own = hooks.X64;
#else
        std::wstring& own = hooks.X86;
#endif
        if (own.empty())
        {
            own = GetSelfPath();
        }
        std::wstring environment;
        using ProcBridge::Hook::BrokerResolver;
        for (const wchar_t* name : { ProcBridge::Hook::PathMapper::EnvironmentVariable, BrokerResolver::SessionVariable,
                 BrokerResolver::BrokerProcessVariable, BrokerResolver::TimeoutVariable })
        {
            environment += std::wstring(name) + L'=' + GetVariable(name) + L'\0';
        }
        environment += std::wstring(HookX64Variable) + L'=' + hooks.X64 + L'\0';
        environment += std::wstring(HookX86Variable) + L'=' + hooks.X86 + L'\0';
        return environment;
    }

    DWORD InitializeOnce(const InitData& data)
    {
        DWORD result = ApplyEnvironment(data);
        if (result != ERROR_SUCCESS)
        {
            return result;
        }
        // Set by the parent's hook for this process only (a process started from the backing copy of a virtual image):
        // removed at once so the children of this process do not inherit it.
        const std::wstring image = GetVariable(ProcBridge::Hook::ImageRedirect::ImageVariable);
        SetEnvironmentVariableW(ProcBridge::Hook::ImageRedirect::ImageVariable, nullptr);
        const ProcBridge::Hook::IPathResolver* resolver = nullptr;
        if (ProcBridge::Hook::BrokerResolver::IsConfigured())
        {
            result = g_broker.Initialize();
            if (result != ERROR_SUCCESS)
            {
                return result;
            }
            resolver = g_broker.Count() > 0 ? &g_broker : nullptr;
        }
        else
        {
            result = g_mapper.LoadFromEnvironment();
            if (result != ERROR_SUCCESS)
            {
                return result;
            }
            resolver = g_mapper.Count() > 0 ? &g_mapper : nullptr;
        }
        if (resolver == nullptr)
        {
            return ERROR_SUCCESS; // nothing to redirect, stay passive
        }
        ProcBridge::Inject::HookPaths hooks;
        const std::wstring childEnvironment = CaptureChildEnvironment(hooks);
        if (MH_Initialize() != MH_OK)
        {
            return ERROR_GEN_FAILURE;
        }
        g_minHookInitialized = true;
        // Pinned: the hooks, the directory watch poller and thread pool callbacks run code of this module until the
        // process ends, so it must never be unloaded (a FreeLibrary that drops the count to 0 is ignored).
        HMODULE pinned = nullptr;
        GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_PIN,
            reinterpret_cast<LPCWSTR>(&GetSelfPath), &pinned);
        ProcBridge::Hook::IHandleInheritance& inheritance = ProcBridge::Hook::GetLocalHandleInheritance();
        result = ProcBridge::Hook::InstallFileHooks(*resolver);
        if (result == ERROR_SUCCESS)
        {
            result = ProcBridge::Hook::InstallProcessHooks(hooks, childEnvironment, inheritance, *resolver);
        }
        if (result != ERROR_SUCCESS)
        {
            return result;
        }
        // Before the hooks run: the inherited handles must already be known to the first name query.
        inheritance.ImportFromParent(data.Handles, data.HandleCount, data.Extra, data.ExtraBytes);
        if (MH_EnableHook(MH_ALL_HOOKS) != MH_OK)
        {
            return ERROR_GEN_FAILURE;
        }
        const size_t separator = image.find(L'*');
        if (separator != std::wstring::npos)
        {
            ProcBridge::Hook::RestoreVirtualImageNames(image.substr(0, separator), image.substr(separator + 1));
        }
        return ERROR_SUCCESS;
    }

    DWORD InitializeGuarded(const InitData& data) noexcept
    {
        try
        {
            return InitializeOnce(data);
        }
        catch (...)
        {
            return ERROR_NOT_ENOUGH_MEMORY;
        }
    }
}

/// Thread-start compatible initializer. parameter: an init block (ProcBridge/InitBlock.h) allocated in
/// this process by the injector, freed here; or nullptr (configuration from the environment only).
/// Returns ERROR_SUCCESS or a Win32 error code.
extern "C" DWORD WINAPI ProcBridgeHookInitialize(LPVOID parameter)
{
    InitData data;
    if (parameter != nullptr)
    {
        ProcBridge::Inject::InitBlockHeader header{};
        memcpy(&header, parameter, sizeof(header));
        if (!ProcBridge::Inject::ParseInitBlock(parameter, header.TotalBytes, data.Environment, data.EnvironmentChars, data.Handles,
            data.HandleCount, data.Extra, data.ExtraBytes))
        {
            VirtualFree(parameter, 0, MEM_RELEASE);
            return ERROR_INVALID_DATA;
        }
    }
    AcquireSRWLockExclusive(&g_initLock);
    if (!g_initDone)
    {
        g_initResult = InitializeGuarded(data);
        g_initDone = true;
    }
    const DWORD result = g_initResult;
    ReleaseSRWLockExclusive(&g_initLock);
    if (parameter != nullptr)
    {
        VirtualFree(parameter, 0, MEM_RELEASE);
    }
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

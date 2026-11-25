// ProcBridge.Hook: injected into target processes.
// DllMain does nothing heavy (plan 4.6). The injector calls the exported ProcBridgeHookInitialize on
// a remote thread after LoadLibraryW, before the target's main thread is resumed.
#include "NtApi.h"
#include "FileHooks.h"
#include "HandleInheritance.h"
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
    ProcBridge::Hook::PathMapper g_mapper;

    /// Parsed init block (plan 5): environment variables to set and inherited virtual handles.
    struct InitData
    {
        const wchar_t* Environment = nullptr;
        size_t EnvironmentChars = 0;
        const ProcBridge::Inject::InheritedHandle* Handles = nullptr;
        size_t HandleCount = 0;
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
            if (!SetEnvironmentVariableW(name.c_str(), text.c_str() + equals + 1))
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
        environment += std::wstring(ProcBridge::Hook::PathMapper::EnvironmentVariable) + L'=' + GetVariable(ProcBridge::Hook::PathMapper::EnvironmentVariable) + L'\0';
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
        result = g_mapper.LoadFromEnvironment();
        if (result != ERROR_SUCCESS)
        {
            return result;
        }
        if (g_mapper.Count() == 0)
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
        ProcBridge::Hook::IHandleInheritance& inheritance = ProcBridge::Hook::GetLocalHandleInheritance();
        result = ProcBridge::Hook::InstallFileHooks(g_mapper);
        if (result == ERROR_SUCCESS)
        {
            result = ProcBridge::Hook::InstallProcessHooks(hooks, childEnvironment, inheritance);
        }
        if (result != ERROR_SUCCESS)
        {
            return result;
        }
        // Before the hooks run: the inherited handles must already be known to the first name query.
        inheritance.ImportFromParent(data.Handles, data.HandleCount);
        return MH_EnableHook(MH_ALL_HOOKS) == MH_OK ? ERROR_SUCCESS : ERROR_GEN_FAILURE;
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
            data.HandleCount))
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

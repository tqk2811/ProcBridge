#pragma once
#include "HandleInheritance.h"
#include "PathMapper.h"
#include <ProcBridge/Inject.h>
#include <string>

namespace ProcBridge::Hook
{
    /// Creates the process creation hooks (plan 5.2): NtCreateUserProcess, NtResumeThread,
    /// NtAlertResumeThread, NtResumeProcess. MinHook must already be initialized; the caller enables the hooks.
    /// hooks: DLL of each architecture for the children. environment: the configuration variables
    /// ("NAME=VALUE", each NUL-terminated), captured at initialization so a process that clears them
    /// still passes them on. inheritance and resolver must outlive the hooks; resolver decides whether the image of a
    /// new process lives in a virtual folder (it is then started from its backing copy, see ImageRedirect).
    DWORD InstallProcessHooks(const Inject::HookPaths& hooks, const std::wstring& environment,
        const IHandleInheritance& inheritance, const IPathResolver& resolver) noexcept;
}

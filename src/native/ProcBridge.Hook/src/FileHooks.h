#pragma once
#include "PathMapper.h"

namespace ProcBridge::Hook
{
    /// Creates and enables the ntdll file hooks (MinHook must already be initialized).
    /// resolver must outlive the hooks. Returns ERROR_SUCCESS or a Win32 error.
    DWORD InstallFileHooks(const IPathResolver& resolver) noexcept;
}

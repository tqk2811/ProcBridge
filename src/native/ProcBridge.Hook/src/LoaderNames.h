#pragma once
#include <string>

namespace ProcBridge::Hook
{
    /// Gives a process started from the backing copy of a virtual image (ImageRedirect) its virtual names back: every
    /// loader entry (the image and the DLLs loaded next to it) whose full name lies in realDirectory, and the
    /// ImagePathName of the process parameters, are renamed into virtualDirectory. GetModuleFileName then reports the
    /// virtual path, and what the process looks up next to itself goes through the virtual folder. Both directories are
    /// DOS paths without a trailing '\'. Runs in the initializer, under the loader lock.
    void RestoreVirtualImageNames(const std::wstring& realDirectory, const std::wstring& virtualDirectory) noexcept;
}

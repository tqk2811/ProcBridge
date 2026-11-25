#pragma once
// Shared DLL injection helpers.
#include <windows.h>

namespace ProcBridge::Inject
{
    /// Loads dllPath into process with a remote LoadLibraryW thread, then runs the DLL's exported
    /// initializer (signature DWORD WINAPI(LPVOID)) on a second remote thread and waits for it.
    /// Works on a process created with CREATE_SUSPENDED (the first remote thread initializes the loader).
    /// M1: caller and target must have the same architecture (cross-architecture injection is M4).
    /// Returns ERROR_SUCCESS when both threads ran; initResult receives the initializer's return value.
    DWORD InjectDll(HANDLE process, const wchar_t* dllPath, const char* initExport, DWORD timeoutMs, DWORD& initResult) noexcept;
}

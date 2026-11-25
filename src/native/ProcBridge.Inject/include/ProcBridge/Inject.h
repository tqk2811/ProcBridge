#pragma once
// Shared DLL injection helpers (ProcBridge.Spawn and the process hooks of ProcBridge.Hook).
#include <windows.h>
#include <string>

namespace ProcBridge::Inject
{
    /// Name of the initializer exported by ProcBridge.Hook.
    constexpr const char* HookInitializeExport = "ProcBridgeHookInitialize";

    /// Hook DLL of each architecture. The cross-architecture helper is the ProcBridge.Spawn of the
    /// target's architecture, found next to that architecture's DLL.
    struct HookPaths
    {
        std::wstring X64;
        std::wstring X86;
    };

    /// Architecture the process runs as (IMAGE_FILE_MACHINE_AMD64, IMAGE_FILE_MACHINE_I386, ...).
    DWORD GetProcessMachine(HANDLE process, USHORT& machine) noexcept;

    /// Loads dllPath into process with a remote LoadLibraryW thread, then runs the DLL's exported
    /// initializer (signature DWORD WINAPI(LPVOID)) on a second remote thread and waits for it.
    /// initData (optional, initBytes bytes) is copied into the target and its address passed as the
    /// initializer's parameter; the initializer owns that memory (VirtualFree MEM_RELEASE).
    /// Works on a process created suspended (the first remote thread initializes the loader).
    /// Caller and target must have the same architecture, otherwise ERROR_EXE_MACHINE_TYPE_MISMATCH:
    /// use InjectHook, which goes through the helper of the target's architecture.
    /// Returns ERROR_SUCCESS when both threads ran; initResult receives the initializer's return value.
    DWORD InjectDll(HANDLE process, const wchar_t* dllPath, const char* initExport, const void* initData, size_t initBytes,
        DWORD timeoutMs, DWORD& initResult) noexcept;

    /// Injects the hook DLL of the target's architecture. Same architecture: InjectDll in this process.
    /// Other architecture: runs "ProcBridge.Spawn.<arch>.exe --inject" next to that DLL, which duplicates
    /// the target process handle and the init data (a section) from this process and calls InjectDll there.
    /// Returns ERROR_SUCCESS only when the hook was loaded and its initializer returned ERROR_SUCCESS;
    /// otherwise the Win32 error of the step that failed (initializer failure included).
    DWORD InjectHook(HANDLE process, const HookPaths& hooks, const void* initData, size_t initBytes, DWORD timeoutMs) noexcept;
}

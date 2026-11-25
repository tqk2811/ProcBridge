#include "ProcBridge/Inject.h"
#include <tlhelp32.h>
#include <string>

namespace ProcBridge::Inject
{
    namespace
    {
        /// Owns a Win32 handle.
        class ScopedHandle final
        {
        public:
            explicit ScopedHandle(HANDLE handle) noexcept : _handle(handle) {}
            ~ScopedHandle()
            {
                if (IsValid())
                {
                    CloseHandle(_handle);
                }
            }
            ScopedHandle(const ScopedHandle&) = delete;
            ScopedHandle& operator=(const ScopedHandle&) = delete;

            HANDLE Get() const noexcept { return _handle; }
            bool IsValid() const noexcept { return _handle != nullptr && _handle != INVALID_HANDLE_VALUE; }

        private:
            HANDLE _handle;
        };

        DWORD RunRemoteThread(HANDLE process, LPTHREAD_START_ROUTINE start, void* argument, DWORD timeoutMs, DWORD& exitCode) noexcept
        {
            ScopedHandle thread(CreateRemoteThread(process, nullptr, 0, start, argument, 0, nullptr));
            if (!thread.IsValid())
            {
                return GetLastError();
            }
            const DWORD wait = WaitForSingleObject(thread.Get(), timeoutMs);
            if (wait == WAIT_TIMEOUT)
            {
                return ERROR_TIMEOUT;
            }
            if (wait != WAIT_OBJECT_0)
            {
                return GetLastError();
            }
            if (!GetExitCodeThread(thread.Get(), &exitCode))
            {
                return GetLastError();
            }
            return ERROR_SUCCESS;
        }

        BYTE* FindRemoteModule(DWORD processId, const std::wstring& modulePath) noexcept
        {
            // ERROR_BAD_LENGTH: the module list changed while the snapshot was taken; retry.
            HANDLE handle = INVALID_HANDLE_VALUE;
            for (int attempt = 0; attempt < 10 && handle == INVALID_HANDLE_VALUE; ++attempt)
            {
                handle = CreateToolhelp32Snapshot(TH32CS_SNAPMODULE, processId);
                if (handle == INVALID_HANDLE_VALUE && GetLastError() != ERROR_BAD_LENGTH)
                {
                    return nullptr;
                }
            }
            ScopedHandle snapshot(handle);
            if (!snapshot.IsValid())
            {
                return nullptr;
            }
            MODULEENTRY32W entry{};
            entry.dwSize = sizeof(entry);
            for (BOOL more = Module32FirstW(snapshot.Get(), &entry); more; more = Module32NextW(snapshot.Get(), &entry))
            {
                if (CompareStringOrdinal(entry.szExePath, -1, modulePath.c_str(), -1, TRUE) == CSTR_EQUAL)
                {
                    return entry.modBaseAddr;
                }
            }
            return nullptr;
        }

        /// Offset of an export from the module base, read from a local, non-initialized mapping.
        DWORD GetExportOffset(const std::wstring& modulePath, const char* exportName, SIZE_T& offset) noexcept
        {
            const HMODULE local = LoadLibraryExW(modulePath.c_str(), nullptr, DONT_RESOLVE_DLL_REFERENCES);
            if (local == nullptr)
            {
                return GetLastError();
            }
            const FARPROC proc = GetProcAddress(local, exportName);
            const DWORD error = proc == nullptr ? GetLastError() : ERROR_SUCCESS;
            if (proc != nullptr)
            {
                offset = reinterpret_cast<const BYTE*>(proc) - reinterpret_cast<const BYTE*>(local);
            }
            FreeLibrary(local);
            return error;
        }
    }

    DWORD InjectDll(HANDLE process, const wchar_t* dllPath, const char* initExport, DWORD timeoutMs, DWORD& initResult) noexcept
    {
        initResult = ERROR_SUCCESS;
        wchar_t fullPath[MAX_PATH];
        const DWORD fullLength = GetFullPathNameW(dllPath, MAX_PATH, fullPath, nullptr);
        if (fullLength == 0 || fullLength >= MAX_PATH)
        {
            return fullLength == 0 ? GetLastError() : ERROR_FILENAME_EXCED_RANGE;
        }
        const std::wstring modulePath(fullPath, fullLength);

        SIZE_T exportOffset = 0;
        DWORD error = GetExportOffset(modulePath, initExport, exportOffset);
        if (error != ERROR_SUCCESS)
        {
            return error;
        }

        const SIZE_T pathBytes = (modulePath.size() + 1) * sizeof(wchar_t);
        void* remotePath = VirtualAllocEx(process, nullptr, pathBytes, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
        if (remotePath == nullptr)
        {
            return GetLastError();
        }
        DWORD loadResult = 0;
        if (!WriteProcessMemory(process, remotePath, modulePath.c_str(), pathBytes, nullptr))
        {
            error = GetLastError();
        }
        else
        {
            // kernel32 is mapped at the same address in every process of the same architecture.
            auto* loadLibrary = reinterpret_cast<LPTHREAD_START_ROUTINE>(
                GetProcAddress(GetModuleHandleW(L"kernel32.dll"), "LoadLibraryW"));
            error = RunRemoteThread(process, loadLibrary, remotePath, timeoutMs, loadResult);
        }
        if (error != ERROR_TIMEOUT)
        {
            VirtualFreeEx(process, remotePath, 0, MEM_RELEASE);
        }
        if (error != ERROR_SUCCESS)
        {
            return error;
        }
        if (loadResult == 0)
        {
            return ERROR_MOD_NOT_FOUND;
        }

        BYTE* remoteBase = FindRemoteModule(GetProcessId(process), modulePath);
        if (remoteBase == nullptr)
        {
            return ERROR_MOD_NOT_FOUND;
        }
        auto* initialize = reinterpret_cast<LPTHREAD_START_ROUTINE>(remoteBase + exportOffset);
        return RunRemoteThread(process, initialize, nullptr, timeoutMs, initResult);
    }
}

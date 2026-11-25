#include "ProcBridge/Inject.h"
#include "ProcBridge/InitBlock.h"
#include <tlhelp32.h>
#include <cstring>
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

        /// Copies bytes into a new allocation of the target. Returns nullptr on failure (error set).
        void* CopyToRemote(HANDLE process, const void* data, size_t bytes, DWORD& error) noexcept
        {
            void* remote = VirtualAllocEx(process, nullptr, bytes, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
            if (remote == nullptr)
            {
                error = GetLastError();
                return nullptr;
            }
            if (!WriteProcessMemory(process, remote, data, bytes, nullptr))
            {
                error = GetLastError();
                VirtualFreeEx(process, remote, 0, MEM_RELEASE);
                return nullptr;
            }
            error = ERROR_SUCCESS;
            return remote;
        }

        DWORD GetCurrentMachine(USHORT& machine) noexcept
        {
            return GetProcessMachine(GetCurrentProcess(), machine);
        }

        const wchar_t* ArchitectureName(USHORT machine) noexcept
        {
            switch (machine)
            {
            case IMAGE_FILE_MACHINE_AMD64:
                return L"x64";
            case IMAGE_FILE_MACHINE_I386:
                return L"x86";
            default:
                return nullptr;
            }
        }

        /// Runs the helper of the target's architecture: it duplicates the target process handle and a
        /// section holding the init data from this process, injects and exits with the result. Nothing
        /// is inheritable, so children created meanwhile by other threads receive neither handle.
        DWORD InjectWithHelper(HANDLE process, const std::wstring& dllPath, const wchar_t* architecture, const void* initData,
            size_t initBytes, DWORD timeoutMs) noexcept
        {
            const size_t slash = dllPath.find_last_of(L'\\');
            if (slash == std::wstring::npos)
            {
                return ERROR_BAD_PATHNAME;
            }
            const std::wstring helper = dllPath.substr(0, slash + 1) + L"ProcBridge.Spawn." + architecture + L".exe";

            const size_t sectionBytes = initBytes == 0 ? 1 : initBytes;
            ScopedHandle section(CreateFileMappingW(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE, 0,
                static_cast<DWORD>(sectionBytes), nullptr));
            if (!section.IsValid())
            {
                return GetLastError();
            }
            if (initBytes > 0)
            {
                void* view = MapViewOfFile(section.Get(), FILE_MAP_WRITE, 0, 0, initBytes);
                if (view == nullptr)
                {
                    return GetLastError();
                }
                memcpy(view, initData, initBytes);
                UnmapViewOfFile(view);
            }

            // Both handles stay open here until the helper exits (this function waits for it).
            std::wstring commandLine = L"\"" + helper + L"\" --inject " + std::to_wstring(GetCurrentProcessId()) + L" "
                + std::to_wstring(reinterpret_cast<ULONG_PTR>(process)) + L" --init-section "
                + std::to_wstring(reinterpret_cast<ULONG_PTR>(section.Get())) + L" " + std::to_wstring(initBytes) + L" --hook \""
                + dllPath + L"\"";
            STARTUPINFOW startup{};
            startup.cb = sizeof(startup);
            PROCESS_INFORMATION info{};
            if (!CreateProcessW(helper.c_str(), commandLine.data(), nullptr, nullptr, FALSE, DETACHED_PROCESS, nullptr, nullptr,
                &startup, &info))
            {
                return GetLastError();
            }
            ScopedHandle helperProcess(info.hProcess);
            ScopedHandle helperThread(info.hThread);
            // The helper runs two remote threads, each bounded by timeoutMs.
            const DWORD wait = WaitForSingleObject(helperProcess.Get(), timeoutMs == INFINITE ? INFINITE : timeoutMs * 2 + 5000);
            if (wait != WAIT_OBJECT_0)
            {
                TerminateProcess(helperProcess.Get(), ERROR_TIMEOUT);
                return wait == WAIT_TIMEOUT ? ERROR_TIMEOUT : GetLastError();
            }
            DWORD exitCode = 0;
            if (!GetExitCodeProcess(helperProcess.Get(), &exitCode))
            {
                return GetLastError();
            }
            return exitCode;
        }
    }

    DWORD GetProcessMachine(HANDLE process, USHORT& machine) noexcept
    {
        USHORT processMachine = 0;
        USHORT nativeMachine = 0;
        if (!IsWow64Process2(process, &processMachine, &nativeMachine))
        {
            return GetLastError();
        }
        machine = processMachine == IMAGE_FILE_MACHINE_UNKNOWN ? nativeMachine : processMachine;
        return ERROR_SUCCESS;
    }

    DWORD InjectDll(HANDLE process, const wchar_t* dllPath, const char* initExport, const void* initData, size_t initBytes,
        DWORD timeoutMs, DWORD& initResult) noexcept
    {
        initResult = ERROR_SUCCESS;
        // The remote LoadLibraryW address and the DLL come from this process: they are only valid in a
        // target of the same architecture.
        USHORT selfMachine = 0;
        USHORT targetMachine = 0;
        DWORD error = GetCurrentMachine(selfMachine);
        if (error == ERROR_SUCCESS)
        {
            error = GetProcessMachine(process, targetMachine);
        }
        if (error != ERROR_SUCCESS)
        {
            return error;
        }
        if (selfMachine != targetMachine)
        {
            return ERROR_EXE_MACHINE_TYPE_MISMATCH;
        }

        wchar_t fullPath[MAX_PATH];
        const DWORD fullLength = GetFullPathNameW(dllPath, MAX_PATH, fullPath, nullptr);
        if (fullLength == 0 || fullLength >= MAX_PATH)
        {
            return fullLength == 0 ? GetLastError() : ERROR_FILENAME_EXCED_RANGE;
        }
        const std::wstring modulePath(fullPath, fullLength);

        SIZE_T exportOffset = 0;
        error = GetExportOffset(modulePath, initExport, exportOffset);
        if (error != ERROR_SUCCESS)
        {
            return error;
        }

        void* remotePath = CopyToRemote(process, modulePath.c_str(), (modulePath.size() + 1) * sizeof(wchar_t), error);
        if (remotePath == nullptr)
        {
            return error;
        }
        // kernel32 is mapped at the same address in every process of the same architecture.
        auto* loadLibrary = reinterpret_cast<LPTHREAD_START_ROUTINE>(
            GetProcAddress(GetModuleHandleW(L"kernel32.dll"), "LoadLibraryW"));
        DWORD loadResult = 0;
        error = RunRemoteThread(process, loadLibrary, remotePath, timeoutMs, loadResult);
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
        void* remoteData = nullptr;
        if (initData != nullptr && initBytes > 0)
        {
            remoteData = CopyToRemote(process, initData, initBytes, error);
            if (remoteData == nullptr)
            {
                return error;
            }
        }
        auto* initialize = reinterpret_cast<LPTHREAD_START_ROUTINE>(remoteBase + exportOffset);
        // The initializer frees remoteData; on a failure to start it the block stays (a few bytes).
        return RunRemoteThread(process, initialize, remoteData, timeoutMs, initResult);
    }

    DWORD InjectHook(HANDLE process, const HookPaths& hooks, const void* initData, size_t initBytes, DWORD timeoutMs) noexcept
    {
        USHORT selfMachine = 0;
        USHORT targetMachine = 0;
        DWORD error = GetCurrentMachine(selfMachine);
        if (error == ERROR_SUCCESS)
        {
            error = GetProcessMachine(process, targetMachine);
        }
        if (error != ERROR_SUCCESS)
        {
            return error;
        }
        const wchar_t* architecture = ArchitectureName(targetMachine);
        if (architecture == nullptr)
        {
            return ERROR_EXE_MACHINE_TYPE_MISMATCH; // ARM64 and others: no hook build
        }
        const std::wstring& dllPath = targetMachine == IMAGE_FILE_MACHINE_AMD64 ? hooks.X64 : hooks.X86;
        if (dllPath.empty())
        {
            return ERROR_EXE_MACHINE_TYPE_MISMATCH;
        }
        if (targetMachine != selfMachine)
        {
            return InjectWithHelper(process, dllPath, architecture, initData, initBytes, timeoutMs);
        }
        DWORD initResult = ERROR_SUCCESS;
        error = InjectDll(process, dllPath.c_str(), HookInitializeExport, initData, initBytes, timeoutMs, initResult);
        return error != ERROR_SUCCESS ? error : initResult;
    }

    std::vector<BYTE> BuildInitBlock(const std::wstring& environment, const std::vector<InheritedHandle>& handles,
        const std::vector<BYTE>& extra)
    {
        const size_t environmentOffset = sizeof(InitBlockHeader);
        const size_t environmentBytes = environment.size() * sizeof(wchar_t);
        const size_t handleOffset = (environmentOffset + environmentBytes + 7) & ~static_cast<size_t>(7);
        const size_t extraOffset = (handleOffset + handles.size() * sizeof(InheritedHandle) + 7) & ~static_cast<size_t>(7);
        const size_t totalBytes = extraOffset + extra.size();
        std::vector<BYTE> block(totalBytes, 0);
        InitBlockHeader header{};
        header.Magic = InitBlockMagic;
        header.Version = InitBlockVersion;
        header.TotalBytes = static_cast<ULONG>(totalBytes);
        header.EnvironmentOffset = static_cast<ULONG>(environmentOffset);
        header.EnvironmentChars = static_cast<ULONG>(environment.size());
        header.HandleOffset = static_cast<ULONG>(handleOffset);
        header.HandleCount = static_cast<ULONG>(handles.size());
        header.ExtraOffset = static_cast<ULONG>(extraOffset);
        header.ExtraBytes = static_cast<ULONG>(extra.size());
        memcpy(block.data(), &header, sizeof(header));
        if (environmentBytes > 0)
        {
            memcpy(block.data() + environmentOffset, environment.data(), environmentBytes);
        }
        if (!handles.empty())
        {
            memcpy(block.data() + handleOffset, handles.data(), handles.size() * sizeof(InheritedHandle));
        }
        if (!extra.empty())
        {
            memcpy(block.data() + extraOffset, extra.data(), extra.size());
        }
        return block;
    }

    bool ParseInitBlock(const void* block, size_t blockBytes, const wchar_t*& environment, size_t& environmentChars,
        const InheritedHandle*& handles, size_t& handleCount, const BYTE*& extra, size_t& extraBytes) noexcept
    {
        if (block == nullptr || blockBytes < sizeof(InitBlockHeader))
        {
            return false;
        }
        InitBlockHeader header{};
        memcpy(&header, block, sizeof(header));
        const ULONGLONG environmentEnd = header.EnvironmentOffset + static_cast<ULONGLONG>(header.EnvironmentChars) * sizeof(wchar_t);
        const ULONGLONG handleEnd = header.HandleOffset + static_cast<ULONGLONG>(header.HandleCount) * sizeof(InheritedHandle);
        const ULONGLONG extraEnd = static_cast<ULONGLONG>(header.ExtraOffset) + header.ExtraBytes;
        if (header.Magic != InitBlockMagic || header.Version != InitBlockVersion || header.TotalBytes > blockBytes
            || environmentEnd > header.TotalBytes || handleEnd > header.TotalBytes || extraEnd > header.TotalBytes || header.HandleOffset % 8 != 0
            || header.EnvironmentOffset % sizeof(wchar_t) != 0)
        {
            return false;
        }
        const BYTE* base = static_cast<const BYTE*>(block);
        environment = reinterpret_cast<const wchar_t*>(base + header.EnvironmentOffset);
        environmentChars = header.EnvironmentChars;
        handles = reinterpret_cast<const InheritedHandle*>(base + header.HandleOffset);
        handleCount = header.HandleCount;
        extra = base + header.ExtraOffset;
        extraBytes = header.ExtraBytes;
        return true;
    }
}

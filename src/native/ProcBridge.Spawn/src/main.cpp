// ProcBridge.Spawn: minimal native launcher for the M1 spike.
// Creates the target suspended, injects ProcBridge.Hook, runs its initializer, resumes the target and
// returns the target's exit code. The C# Launcher (M2) starts the Broker and drives this helper; it
// also becomes the same-architecture inject helper of plan 5.4.
//
// Usage:
//   ProcBridge.Spawn.<arch>.exe [--hook <dll>] --map <virtual dir> <real dir> [--map ...] -- <command line>
// Everything after the first " -- " of the raw command line is passed to CreateProcessW verbatim.
#include <windows.h>
#include <ProcBridge/Inject.h>
#include <cstdio>
#include <cwchar>
#include <string>
#include <vector>

namespace
{
    // Exit code reserved for launcher failures (the target's own exit code is passed through otherwise).
    constexpr int SpawnFailure = 125;
    constexpr DWORD InjectTimeoutMs = 30000;

#ifdef _WIN64
    constexpr const wchar_t* DefaultHookName = L"ProcBridge.Hook.x64.dll";
#else
    constexpr const wchar_t* DefaultHookName = L"ProcBridge.Hook.x86.dll";
#endif

    struct Mapping
    {
        std::wstring VirtualDir;
        std::wstring RealDir;
    };

    std::wstring GetFullPath(const std::wstring& path)
    {
        const DWORD needed = GetFullPathNameW(path.c_str(), 0, nullptr, nullptr);
        if (needed == 0)
        {
            return path;
        }
        std::wstring full(needed, L'\0');
        const DWORD written = GetFullPathNameW(path.c_str(), needed, full.data(), nullptr);
        full.resize(written);
        return full;
    }

    std::wstring GetDefaultHookPath()
    {
        wchar_t self[MAX_PATH];
        const DWORD length = GetModuleFileNameW(nullptr, self, MAX_PATH);
        std::wstring path(self, length);
        const size_t slash = path.find_last_of(L'\\');
        path.resize(slash == std::wstring::npos ? 0 : slash + 1);
        return path + DefaultHookName;
    }

    int Fail(const wchar_t* message, DWORD error)
    {
        std::fwprintf(stderr, L"ProcBridge.Spawn: %ls (error %lu)\n", message, error);
        return SpawnFailure;
    }

    int Usage()
    {
        std::fwprintf(stderr,
            L"Usage: ProcBridge.Spawn [--hook <dll>] --map <virtual dir> <real dir> [--map ...] -- <command line>\n");
        return SpawnFailure;
    }

    /// Owns the PROCESS_INFORMATION handles.
    class ChildProcess final
    {
    public:
        ChildProcess() noexcept = default;
        ~ChildProcess()
        {
            if (_info.hThread != nullptr)
            {
                CloseHandle(_info.hThread);
            }
            if (_info.hProcess != nullptr)
            {
                CloseHandle(_info.hProcess);
            }
        }
        ChildProcess(const ChildProcess&) = delete;
        ChildProcess& operator=(const ChildProcess&) = delete;

        bool StartSuspended(std::wstring commandLine)
        {
            STARTUPINFOW startup{};
            startup.cb = sizeof(startup);
            return CreateProcessW(nullptr, commandLine.data(), nullptr, nullptr, TRUE, CREATE_SUSPENDED,
                nullptr, nullptr, &startup, &_info) != FALSE;
        }

        HANDLE Process() const noexcept { return _info.hProcess; }
        HANDLE Thread() const noexcept { return _info.hThread; }

    private:
        PROCESS_INFORMATION _info{};
    };
}

int wmain(int argc, wchar_t** argv)
{
    std::wstring hookPath;
    std::vector<Mapping> mappings;
    bool sawSeparator = false;
    for (int i = 1; i < argc; ++i)
    {
        const std::wstring arg = argv[i];
        if (arg == L"--")
        {
            sawSeparator = true;
            break;
        }
        if (arg == L"--hook" && i + 1 < argc)
        {
            hookPath = argv[++i];
        }
        else if (arg == L"--map" && i + 2 < argc)
        {
            mappings.push_back({ GetFullPath(argv[i + 1]), GetFullPath(argv[i + 2]) });
            i += 2;
        }
        else
        {
            return Usage();
        }
    }

    const std::wstring rawCommandLine = GetCommandLineW();
    const size_t separator = rawCommandLine.find(L" -- ");
    if (!sawSeparator || mappings.empty() || separator == std::wstring::npos)
    {
        return Usage();
    }
    const std::wstring commandLine = rawCommandLine.substr(separator + 4);
    if (commandLine.empty())
    {
        return Usage();
    }
    if (hookPath.empty())
    {
        hookPath = GetDefaultHookPath();
    }

    // M1 configuration channel: the hook reads PROCBRIDGE_MAP ("virtual*real|virtual*real").
    // M2 replaces it with the Broker pipe name.
    std::wstring mapValue;
    for (const Mapping& mapping : mappings)
    {
        if (!mapValue.empty())
        {
            mapValue += L'|';
        }
        mapValue += mapping.VirtualDir + L'*' + mapping.RealDir;
    }
    if (!SetEnvironmentVariableW(L"PROCBRIDGE_MAP", mapValue.c_str()))
    {
        return Fail(L"cannot set PROCBRIDGE_MAP", GetLastError());
    }

    ChildProcess child;
    if (!child.StartSuspended(commandLine))
    {
        return Fail(L"CreateProcessW failed", GetLastError());
    }

    DWORD initResult = ERROR_SUCCESS;
    const DWORD injectError = ProcBridge::Inject::InjectDll(child.Process(), hookPath.c_str(),
        "ProcBridgeHookInitialize", InjectTimeoutMs, initResult);
    if (injectError != ERROR_SUCCESS || initResult != ERROR_SUCCESS)
    {
        TerminateProcess(child.Process(), SpawnFailure);
        return Fail(injectError != ERROR_SUCCESS ? L"inject failed" : L"hook initialization failed",
            injectError != ERROR_SUCCESS ? injectError : initResult);
    }

    if (ResumeThread(child.Thread()) == static_cast<DWORD>(-1))
    {
        const DWORD error = GetLastError();
        TerminateProcess(child.Process(), SpawnFailure);
        return Fail(L"ResumeThread failed", error);
    }
    WaitForSingleObject(child.Process(), INFINITE);
    DWORD exitCode = 0;
    GetExitCodeProcess(child.Process(), &exitCode);
    return static_cast<int>(exitCode);
}

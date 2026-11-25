// ProcBridge.Spawn: minimal native launcher for the M1 spike.
// Creates the target suspended, injects ProcBridge.Hook, runs its initializer, resumes the target and
// returns the target's exit code. The C# Launcher (M2) starts the Broker and drives this helper; it
// also becomes the same-architecture inject helper of plan 5.4.
//
// Usage:
//   ProcBridge.Spawn.<arch>.exe [--hook <dll>] --map <virtual dir> <real dir> [--map ...] -- <command line>
//   ProcBridge.Spawn.<arch>.exe [--hook <dll>] --broker <session id> <broker pid> -- <command line>
// --map (M1) backs virtual folders by local folders; --broker (M2) hands the hook a Broker session, whose
// pipe must be owned by <broker pid>.
// Everything after the "--" argument of the raw command line is passed to CreateProcessW verbatim.
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

    /// "X:\folder...": the hook only maps folders on a drive letter, never a drive root or a UNC path.
    bool IsFolderOnDrive(const std::wstring& fullPath)
    {
        return fullPath.size() > 3 && iswalpha(fullPath[0]) && fullPath[1] == L':' && fullPath[2] == L'\\'
            && fullPath.find_first_not_of(L'\\', 3) != std::wstring::npos;
    }

    bool IsBlank(wchar_t c) noexcept
    {
        return c == L' ' || c == L'\t';
    }

    /// Position just after argument argIndex (0 = program name) of a raw command line, split with the
    /// UCRT rules that produced wmain's argv (quotes, backslashes before quotes, "" inside quotes).
    /// npos when the line has fewer arguments.
    size_t FindArgumentEnd(const std::wstring& line, int argIndex)
    {
        size_t p = 0;
        const size_t end = line.size();
        // Program name: quotes toggle, no escapes.
        bool inQuotes = false;
        while (p < end && (inQuotes || !IsBlank(line[p])))
        {
            if (line[p] == L'"')
            {
                inQuotes = !inQuotes;
            }
            ++p;
        }
        for (int index = 1; index <= argIndex; ++index)
        {
            while (p < end && IsBlank(line[p]))
            {
                ++p;
            }
            if (p == end)
            {
                return std::wstring::npos;
            }
            inQuotes = false;
            while (p < end)
            {
                size_t backslashes = 0;
                while (p < end && line[p] == L'\\')
                {
                    ++backslashes;
                    ++p;
                }
                if (p == end)
                {
                    break;
                }
                if (line[p] == L'"' && backslashes % 2 == 0)
                {
                    if (inQuotes && p + 1 < end && line[p + 1] == L'"')
                    {
                        ++p; // "" inside quotes: literal quote
                    }
                    else
                    {
                        inQuotes = !inQuotes;
                    }
                }
                else if (!inQuotes && IsBlank(line[p]))
                {
                    break;
                }
                ++p;
            }
        }
        return p;
    }

    int Usage()
    {
        std::fwprintf(stderr,
            L"Usage: ProcBridge.Spawn [--hook <dll>] --map <virtual dir> <real dir> [--map ...] -- <command line>\n"
            L"       ProcBridge.Spawn [--hook <dll>] --broker <session id> <broker pid> -- <command line>\n");
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
    std::wstring brokerSession;
    std::wstring brokerProcessId;
    int separatorIndex = 0;
    for (int i = 1; i < argc; ++i)
    {
        const std::wstring arg = argv[i];
        if (arg == L"--")
        {
            separatorIndex = i;
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
        else if (arg == L"--broker" && i + 2 < argc)
        {
            brokerSession = argv[i + 1];
            brokerProcessId = argv[i + 2];
            i += 2;
        }
        else
        {
            return Usage();
        }
    }

    // Cut the raw line right after the "--" token (not at the first " -- " substring, which may sit
    // inside a quoted --map path), so the target's command line keeps its own quoting.
    const std::wstring rawCommandLine = GetCommandLineW();
    size_t commandStart = separatorIndex == 0 ? std::wstring::npos : FindArgumentEnd(rawCommandLine, separatorIndex);
    // Exactly one configuration channel.
    if (mappings.empty() == brokerSession.empty() || commandStart == std::wstring::npos)
    {
        return Usage();
    }
    while (commandStart < rawCommandLine.size() && IsBlank(rawCommandLine[commandStart]))
    {
        ++commandStart;
    }
    const std::wstring commandLine = rawCommandLine.substr(commandStart);
    if (commandLine.empty())
    {
        return Usage();
    }
    for (const Mapping& mapping : mappings)
    {
        if (!IsFolderOnDrive(mapping.VirtualDir) || !IsFolderOnDrive(mapping.RealDir))
        {
            std::fwprintf(stderr, L"ProcBridge.Spawn: --map folders must be on a drive letter, not a drive root or UNC path: %ls -> %ls\n",
                mapping.VirtualDir.c_str(), mapping.RealDir.c_str());
            return SpawnFailure;
        }
    }
    if (hookPath.empty())
    {
        hookPath = GetDefaultHookPath();
    }

    // Configuration channels: PROCBRIDGE_MAP ("virtual*real|virtual*real", M1) or the Broker session
    // (PROCBRIDGE_SESSION + PROCBRIDGE_BROKER_PID, M2). The other one is cleared so an inherited value
    // cannot take over.
    if (!brokerSession.empty())
    {
        if (!SetEnvironmentVariableW(L"PROCBRIDGE_SESSION", brokerSession.c_str())
            || !SetEnvironmentVariableW(L"PROCBRIDGE_BROKER_PID", brokerProcessId.c_str())
            || (!SetEnvironmentVariableW(L"PROCBRIDGE_MAP", nullptr) && GetLastError() != ERROR_ENVVAR_NOT_FOUND))
        {
            return Fail(L"cannot set the Broker session variables", GetLastError());
        }
    }
    else if (!SetEnvironmentVariableW(L"PROCBRIDGE_SESSION", nullptr) && GetLastError() != ERROR_ENVVAR_NOT_FOUND)
    {
        return Fail(L"cannot clear PROCBRIDGE_SESSION", GetLastError());
    }
    std::wstring mapValue;
    for (const Mapping& mapping : mappings)
    {
        if (!mapValue.empty())
        {
            mapValue += L'|';
        }
        mapValue += mapping.VirtualDir + L'*' + mapping.RealDir;
    }
    if (!mappings.empty() && !SetEnvironmentVariableW(L"PROCBRIDGE_MAP", mapValue.c_str()))
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
        if (injectError == ERROR_EXE_MACHINE_TYPE_MISMATCH)
        {
            return Fail(L"target architecture differs from this launcher; use the ProcBridge.Spawn of the target's architecture",
                injectError);
        }
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

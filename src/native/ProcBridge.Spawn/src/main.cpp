// ProcBridge.Spawn: minimal native launcher (M1 spike, process tree of M4).
// Creates the target suspended inside a Job Object (KILL_ON_JOB_CLOSE), injects ProcBridge.Hook of the
// target's architecture, runs its initializer, resumes the target and waits until every process of
// the job has exited (the hook injects itself into children, which stay in the job). Returns the exit
// code of the first process. The C# Launcher (M2) starts the Broker and drives this helper.
//
// Usage:
//   ProcBridge.Spawn.<arch>.exe [--hook <dll>] [--hook-x64 <dll>] [--hook-x86 <dll>]
//                               --map <virtual dir> <real dir> [--map ...] -- <command line>
//   ProcBridge.Spawn.<arch>.exe [--hook <dll>] [--hook-x64 <dll>] [--hook-x86 <dll>]
//                               --broker <session id> <broker pid> -- <command line>
//   ProcBridge.Spawn.<arch>.exe --inject <injector pid> <process handle> --init-section <section handle> <bytes> --hook <dll>
// --map (M1) backs virtual folders by local folders; --broker (M2) hands the hook a Broker session, whose
// pipe must be owned by <broker pid> (PROCBRIDGE_IPC_TIMEOUT_MS of this process is passed on too).
// Everything after the "--" argument of the raw command line is passed to CreateProcessW verbatim.
// --hook overrides the DLL of this launcher's architecture. A DLL that is not given is looked up next
// to this executable, then in the other platform's build folder (out\<Platform>\<Configuration>).
// --inject is the cross-architecture helper of plan 5.4: it duplicates both handles from the injector
// (ProcBridge::Inject::InjectHook; nothing is inherited, so they cannot leak into the injector's other
// children), injects the DLL of its own architecture and exits with the result.
#include <windows.h>
#include <ProcBridge/InitBlock.h>
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

    constexpr const wchar_t* HookX64Name = L"ProcBridge.Hook.x64.dll";
    constexpr const wchar_t* HookX86Name = L"ProcBridge.Hook.x86.dll";

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

    bool FileExists(const std::wstring& path)
    {
        const DWORD attributes = GetFileAttributesW(path.c_str());
        return attributes != INVALID_FILE_ATTRIBUTES && (attributes & FILE_ATTRIBUTE_DIRECTORY) == 0;
    }

    std::wstring GetDirectory(const std::wstring& path)
    {
        const size_t slash = path.find_last_of(L'\\');
        return slash == std::wstring::npos ? std::wstring() : path.substr(0, slash);
    }

    /// Hook DLL next to this executable, else in the other platform's build folder
    /// (...\out\x64\Release <-> ...\out\Win32\Release). Empty when not found.
    std::wstring FindDefaultHook(const wchar_t* name, const wchar_t* platform)
    {
        wchar_t self[MAX_PATH];
        const DWORD length = GetModuleFileNameW(nullptr, self, MAX_PATH);
        const std::wstring directory = GetDirectory(std::wstring(self, length));
        std::wstring candidate = directory + L'\\' + name;
        if (FileExists(candidate))
        {
            return candidate;
        }
        const std::wstring platformDirectory = GetDirectory(directory);
        const std::wstring configuration = directory.substr(platformDirectory.size());
        candidate = GetDirectory(platformDirectory) + L'\\' + platform + configuration + L'\\' + name;
        return FileExists(candidate) ? GetFullPath(candidate) : std::wstring();
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
            L"Usage: ProcBridge.Spawn [--hook <dll>] [--hook-x64 <dll>] [--hook-x86 <dll>] --map <virtual dir> <real dir> [--map ...] -- <command line>\n"
            L"       ProcBridge.Spawn [--hook <dll>] [--hook-x64 <dll>] [--hook-x86 <dll>] --broker <session id> <broker pid> -- <command line>\n");
        return SpawnFailure;
    }

    HANDLE ParseHandle(const wchar_t* text)
    {
        return reinterpret_cast<HANDLE>(static_cast<ULONG_PTR>(std::wcstoull(text, nullptr, 10)));
    }

    /// Copies a handle of the injector into this process. Returns nullptr on failure (error set).
    HANDLE DuplicateFromInjector(HANDLE injector, const wchar_t* text)
    {
        HANDLE local = nullptr;
        return DuplicateHandle(injector, ParseHandle(text), GetCurrentProcess(), &local, 0, FALSE, DUPLICATE_SAME_ACCESS) ? local : nullptr;
    }

    /// --inject <injector pid> <process handle> --init-section <section handle> <bytes> --hook <dll>: the
    /// handle values belong to the injector, which waits for this helper. Exit code: ERROR_SUCCESS or
    /// the Win32 error (initializer included).
    int RunInjectHelper(int argc, wchar_t** argv)
    {
        if (argc != 9 || std::wcscmp(argv[4], L"--init-section") != 0 || std::wcscmp(argv[7], L"--hook") != 0)
        {
            return ERROR_INVALID_PARAMETER;
        }
        const HANDLE injector = OpenProcess(PROCESS_DUP_HANDLE, FALSE, static_cast<DWORD>(std::wcstoul(argv[2], nullptr, 10)));
        if (injector == nullptr)
        {
            return static_cast<int>(GetLastError());
        }
        const HANDLE process = DuplicateFromInjector(injector, argv[3]);
        const HANDLE section = process == nullptr ? nullptr : DuplicateFromInjector(injector, argv[5]);
        const DWORD duplicateError = section == nullptr ? GetLastError() : ERROR_SUCCESS;
        CloseHandle(injector);
        if (section == nullptr)
        {
            if (process != nullptr)
            {
                CloseHandle(process);
            }
            return static_cast<int>(duplicateError);
        }
        const size_t bytes = static_cast<size_t>(std::wcstoull(argv[6], nullptr, 10));
        const void* view = bytes == 0 ? nullptr : MapViewOfFile(section, FILE_MAP_READ, 0, 0, bytes);
        if (bytes != 0 && view == nullptr)
        {
            return static_cast<int>(GetLastError());
        }
        DWORD initResult = ERROR_SUCCESS;
        const DWORD error = ProcBridge::Inject::InjectDll(process, argv[8], ProcBridge::Inject::HookInitializeExport, view, bytes,
            InjectTimeoutMs, initResult);
        if (view != nullptr)
        {
            UnmapViewOfFile(view);
        }
        CloseHandle(section);
        CloseHandle(process);
        return static_cast<int>(error != ERROR_SUCCESS ? error : initResult);
    }

    /// Job Object holding the whole process tree: closing it (this launcher exits or dies) kills every
    /// process still in it.
    class TreeJob final
    {
    public:
        TreeJob() noexcept = default;
        ~TreeJob()
        {
            if (_job != nullptr)
            {
                CloseHandle(_job);
            }
            if (_port != nullptr)
            {
                CloseHandle(_port);
            }
        }
        TreeJob(const TreeJob&) = delete;
        TreeJob& operator=(const TreeJob&) = delete;

        DWORD Create() noexcept
        {
            _job = CreateJobObjectW(nullptr, nullptr);
            _port = CreateIoCompletionPort(INVALID_HANDLE_VALUE, nullptr, 0, 1);
            if (_job == nullptr || _port == nullptr)
            {
                return GetLastError();
            }
            JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits{};
            limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
            JOBOBJECT_ASSOCIATE_COMPLETION_PORT port{};
            port.CompletionKey = _job;
            port.CompletionPort = _port;
            if (!SetInformationJobObject(_job, JobObjectExtendedLimitInformation, &limits, sizeof(limits))
                || !SetInformationJobObject(_job, JobObjectAssociateCompletionPortInformation, &port, sizeof(port)))
            {
                return GetLastError();
            }
            return ERROR_SUCCESS;
        }

        bool Assign(HANDLE process) const noexcept { return AssignProcessToJobObject(_job, process) != FALSE; }

        /// Waits until no process is left in the job (children and grandchildren included).
        void WaitUntilEmpty() const noexcept
        {
            DWORD message = 0;
            ULONG_PTR key = 0;
            LPOVERLAPPED overlapped = nullptr;
            while (GetQueuedCompletionStatus(_port, &message, &key, &overlapped, INFINITE))
            {
                if (key == reinterpret_cast<ULONG_PTR>(_job) && message == JOB_OBJECT_MSG_ACTIVE_PROCESS_ZERO)
                {
                    return;
                }
            }
        }

    private:
        HANDLE _job = nullptr;
        HANDLE _port = nullptr;
    };

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
    if (argc >= 2 && std::wcscmp(argv[1], L"--inject") == 0)
    {
        return RunInjectHelper(argc, argv);
    }
    std::wstring hookPath;
    ProcBridge::Inject::HookPaths hooks;
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
            hookPath = GetFullPath(argv[++i]);
        }
        else if (arg == L"--hook-x64" && i + 1 < argc)
        {
            hooks.X64 = GetFullPath(argv[++i]);
        }
        else if (arg == L"--hook-x86" && i + 1 < argc)
        {
            hooks.X86 = GetFullPath(argv[++i]);
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
#ifdef _WIN64
    std::wstring& ownHook = hooks.X64;
#else
    std::wstring& ownHook = hooks.X86;
#endif
    if (!hookPath.empty())
    {
        ownHook = hookPath;
    }
    if (hooks.X64.empty())
    {
        hooks.X64 = FindDefaultHook(HookX64Name, L"x64");
    }
    if (hooks.X86.empty())
    {
        hooks.X86 = FindDefaultHook(HookX86Name, L"Win32");
    }

    // Configuration channels: PROCBRIDGE_MAP ("virtual*real|virtual*real", M1) or the Broker session
    // (PROCBRIDGE_SESSION + PROCBRIDGE_BROKER_PID, M2). The variables travel in the init block, so the hook
    // sets them in each process of the tree whatever environment its creator passed; the channel not used
    // goes as an empty value, which the hook removes, so an inherited value cannot take over.
    std::wstring mapValue;
    for (const Mapping& mapping : mappings)
    {
        if (!mapValue.empty())
        {
            mapValue += L'|';
        }
        mapValue += mapping.VirtualDir + L'*' + mapping.RealDir;
    }
    std::wstring timeout;
    if (!brokerSession.empty())
    {
        const DWORD needed = GetEnvironmentVariableW(L"PROCBRIDGE_IPC_TIMEOUT_MS", nullptr, 0);
        if (needed > 0)
        {
            timeout.resize(needed);
            timeout.resize(GetEnvironmentVariableW(L"PROCBRIDGE_IPC_TIMEOUT_MS", timeout.data(), needed));
        }
    }
    std::wstring environment = L"PROCBRIDGE_MAP=" + mapValue + L'\0';
    environment += L"PROCBRIDGE_SESSION=" + brokerSession + L'\0';
    environment += L"PROCBRIDGE_BROKER_PID=" + brokerProcessId + L'\0';
    environment += L"PROCBRIDGE_IPC_TIMEOUT_MS=" + timeout + L'\0';
    environment += L"PROCBRIDGE_HOOK_X64=" + hooks.X64 + L'\0';
    environment += L"PROCBRIDGE_HOOK_X86=" + hooks.X86 + L'\0';
    // Only the hook sets it, for a child started from the backing copy of a virtual image.
    environment += std::wstring(L"PROCBRIDGE_IMAGE=") + L'\0';
    const std::vector<BYTE> initBlock = ProcBridge::Inject::BuildInitBlock(environment, {});

    TreeJob job;
    const DWORD jobError = job.Create();
    if (jobError != ERROR_SUCCESS)
    {
        return Fail(L"cannot create the job object", jobError);
    }
    ChildProcess child;
    if (!child.StartSuspended(commandLine))
    {
        return Fail(L"CreateProcessW failed", GetLastError());
    }
    if (!job.Assign(child.Process()))
    {
        const DWORD error = GetLastError();
        TerminateProcess(child.Process(), SpawnFailure);
        return Fail(L"AssignProcessToJobObject failed", error);
    }

    const DWORD injectError = ProcBridge::Inject::InjectHook(child.Process(), hooks, initBlock.data(), initBlock.size(), InjectTimeoutMs);
    if (injectError != ERROR_SUCCESS)
    {
        TerminateProcess(child.Process(), SpawnFailure);
        if (injectError == ERROR_EXE_MACHINE_TYPE_MISMATCH)
        {
            return Fail(L"no hook DLL for the target architecture (use --hook-x64 / --hook-x86)", injectError);
        }
        return Fail(L"inject failed", injectError);
    }

    if (ResumeThread(child.Thread()) == static_cast<DWORD>(-1))
    {
        const DWORD error = GetLastError();
        TerminateProcess(child.Process(), SpawnFailure);
        return Fail(L"ResumeThread failed", error);
    }
    WaitForSingleObject(child.Process(), INFINITE);
    job.WaitUntilEmpty();
    DWORD exitCode = 0;
    GetExitCodeProcess(child.Process(), &exitCode);
    return static_cast<int>(exitCode);
}

// NativeProbe: runs a fixed read-only scenario of file APIs against a root folder and prints one line
// per observation (plan section 12). Every path is printed with the root replaced by <ROOT>, so a run
// on the real folder and a run through the virtual folder must print exactly the same text.
//
// Usage: ProcBridge.NativeProbe.<arch>.exe <root>
//        ProcBridge.NativeProbe.<arch>.exe --<command> <arguments>   (single checks, see RunCommand)
//        ProcBridge.NativeProbe.<arch>.exe --write <new folder>     (M3 write scenario, see WriteScenario)
//        ProcBridge.NativeProbe.<arch>.exe --notify <watched> <real> (M7 change notifications, see NotifyScenario)
// Expected tree under <root> (created by the test): a.txt, sub\b.txt, sub\deep\c.bin, empty\,
// ro.txt (read-only), unicode file name. Last access times are never printed (they drift), nor the times
// of ".." entries (the parent of a virtual root is not part of the virtual folder).
#define WIN32_NO_STATUS
#include <windows.h>
#undef WIN32_NO_STATUS
#include <ntstatus.h>
#include <winternl.h>
#include <algorithm>
#include <cstring>
#include <cstdio>
#include <string>
#include <vector>

namespace
{
    constexpr ULONG FileNameInformationClass = 9;
    constexpr ULONG FileAllInformationClass = 18;
    constexpr ULONG FileNamesInformationClass = 12;
    constexpr ULONG FileNormalizedNameInformationClass = 48;
    constexpr ULONG FileAllInfoNameOffset = 96;

    // Vietnamese + Latin-1 file name, built from code points so the source stays ASCII.
    constexpr wchar_t UnicodeName[] = { L't', 0x1EC7, L'p', L'-', 0x00FC, L'.', L't', L'x', L't', 0 };

    using NtQueryInformationFileFn = NTSTATUS(NTAPI*)(HANDLE, PIO_STATUS_BLOCK, PVOID, ULONG, ULONG);
    using NtOpenFileFn = NTSTATUS(NTAPI*)(PHANDLE, ACCESS_MASK, POBJECT_ATTRIBUTES, PIO_STATUS_BLOCK, ULONG, ULONG);
    using NtCreateFileFn = NTSTATUS(NTAPI*)(PHANDLE, ACCESS_MASK, POBJECT_ATTRIBUTES, PIO_STATUS_BLOCK,
        PLARGE_INTEGER, ULONG, ULONG, ULONG, ULONG, PVOID, ULONG);
    using NtQueryDirectoryFileFn = NTSTATUS(NTAPI*)(HANDLE, HANDLE, PIO_APC_ROUTINE, PVOID, PIO_STATUS_BLOCK,
        PVOID, ULONG, ULONG, BOOLEAN, PUNICODE_STRING, BOOLEAN);
    using NtQueryObjectFn = NTSTATUS(NTAPI*)(HANDLE, ULONG, PVOID, ULONG, PULONG);
    using NtSetInformationFileFn = NTSTATUS(NTAPI*)(HANDLE, PIO_STATUS_BLOCK, PVOID, ULONG, ULONG);
    using NtQueryAttributesFileFn = NTSTATUS(NTAPI*)(POBJECT_ATTRIBUTES, PVOID);
    using NtQueryInformationByNameFn = NTSTATUS(NTAPI*)(POBJECT_ATTRIBUTES, PIO_STATUS_BLOCK, PVOID, ULONG, ULONG);
    using NtDeleteFileFn = NTSTATUS(NTAPI*)(POBJECT_ATTRIBUTES);

    // FILE_STAT_INFORMATION (FileStatInformation = 68).
    constexpr ULONG FileStatInformationClass = 68;
    struct FileStatInfo
    {
        LARGE_INTEGER FileId;
        LARGE_INTEGER CreationTime;
        LARGE_INTEGER LastAccessTime;
        LARGE_INTEGER LastWriteTime;
        LARGE_INTEGER ChangeTime;
        LARGE_INTEGER AllocationSize;
        LARGE_INTEGER EndOfFile;
        ULONG FileAttributes;
        ULONG ReparseTag;
        ULONG NumberOfLinks;
        ACCESS_MASK EffectiveAccess;
    };

    std::wstring Hex(unsigned long long value)
    {
        wchar_t text[32];
        swprintf_s(text, L"0x%llX", value);
        return text;
    }

    std::wstring Num(long long value)
    {
        return std::to_wstring(value);
    }

    std::wstring Time(const FILETIME& time)
    {
        return Num((static_cast<long long>(time.dwHighDateTime) << 32) | time.dwLowDateTime);
    }

    std::wstring Time(const LARGE_INTEGER& time)
    {
        return Num(time.QuadPart);
    }

    std::wstring Error(DWORD error)
    {
        return L"err=" + Num(error);
    }

    std::wstring Status(NTSTATUS status)
    {
        return L"status=" + Hex(static_cast<unsigned long>(status));
    }

    unsigned long long Fnv1a(const BYTE* data, size_t size, unsigned long long hash = 14695981039346656037ull)
    {
        for (size_t i = 0; i < size; ++i)
        {
            hash ^= data[i];
            hash *= 1099511628211ull;
        }
        return hash;
    }

    /// Owns a Win32 handle.
    class Handle final
    {
    public:
        explicit Handle(HANDLE handle = INVALID_HANDLE_VALUE) noexcept : _handle(handle) {}
        ~Handle() { Reset(); }
        Handle(const Handle&) = delete;
        Handle& operator=(const Handle&) = delete;

        void Reset(HANDLE handle = INVALID_HANDLE_VALUE) noexcept
        {
            if (IsValid())
            {
                CloseHandle(_handle);
            }
            _handle = handle;
        }
        HANDLE Get() const noexcept { return _handle; }
        bool IsValid() const noexcept { return _handle != nullptr && _handle != INVALID_HANDLE_VALUE; }

    private:
        HANDLE _handle;
    };

    /// Writes UTF-8 lines to stdout, replacing the root by <ROOT>.
    class ProbeOutput final
    {
    public:
        explicit ProbeOutput(std::wstring root)
            : _root(std::move(root)), _stdout(GetStdHandle(STD_OUTPUT_HANDLE))
        {
            // Volume-relative form ("\dir\root") as returned by name queries without a drive.
            if (_root.size() > 2 && _root[1] == L':')
            {
                _rootNoDrive = _root.substr(2);
            }
        }

        void Line(const std::wstring& step, const std::wstring& text) const
        {
            std::wstring line = step + L": " + Normalize(text) + L"\n";
            const int bytes = WideCharToMultiByte(CP_UTF8, 0, line.c_str(), static_cast<int>(line.size()), nullptr, 0, nullptr, nullptr);
            std::string utf8(static_cast<size_t>(bytes), '\0');
            WideCharToMultiByte(CP_UTF8, 0, line.c_str(), static_cast<int>(line.size()), utf8.data(), bytes, nullptr, nullptr);
            DWORD written = 0;
            WriteFile(_stdout, utf8.data(), static_cast<DWORD>(utf8.size()), &written, nullptr);
        }

        size_t RootNoDriveLength() const noexcept { return _rootNoDrive.size(); }
        size_t RootLength() const noexcept { return _root.size(); }

    private:
        static void ReplaceAll(std::wstring& text, const std::wstring& what, const std::wstring& with)
        {
            if (what.empty())
            {
                return;
            }
            size_t position = 0;
            while (true)
            {
                const auto found = std::search(text.begin() + static_cast<std::ptrdiff_t>(position), text.end(), what.begin(), what.end(),
                    [](wchar_t a, wchar_t b) { return CharUpperW(reinterpret_cast<LPWSTR>(static_cast<ULONG_PTR>(a)))
                        == CharUpperW(reinterpret_cast<LPWSTR>(static_cast<ULONG_PTR>(b))); });
                if (found == text.end())
                {
                    return;
                }
                position = static_cast<size_t>(found - text.begin());
                text.replace(position, what.size(), with);
                position += with.size();
            }
        }

        std::wstring Normalize(std::wstring text) const
        {
            ReplaceAll(text, L"\\\\?\\", L"");
            ReplaceAll(text, _root, L"<ROOT>");
            ReplaceAll(text, _rootNoDrive, L"<ROOT>");
            return text;
        }

        std::wstring _root;
        std::wstring _rootNoDrive;
        HANDLE _stdout;
    };

    /// The scenario. Each method is one group of read APIs.
    class Probe final
    {
    public:
        Probe(std::wstring root, const ProbeOutput& output)
            : _root(std::move(root)), _out(output)
        {
            const HMODULE ntdll = GetModuleHandleW(L"ntdll.dll");
            _ntQueryInformationFile = reinterpret_cast<NtQueryInformationFileFn>(GetProcAddress(ntdll, "NtQueryInformationFile"));
            _ntOpenFile = reinterpret_cast<NtOpenFileFn>(GetProcAddress(ntdll, "NtOpenFile"));
            _ntCreateFile = reinterpret_cast<NtCreateFileFn>(GetProcAddress(ntdll, "NtCreateFile"));
            _ntQueryDirectoryFile = reinterpret_cast<NtQueryDirectoryFileFn>(GetProcAddress(ntdll, "NtQueryDirectoryFile"));
            _ntQueryObject = reinterpret_cast<NtQueryObjectFn>(GetProcAddress(ntdll, "NtQueryObject"));
            _ntSetInformationFile = reinterpret_cast<NtSetInformationFileFn>(GetProcAddress(ntdll, "NtSetInformationFile"));
            _ntQueryAttributesFile = reinterpret_cast<NtQueryAttributesFileFn>(GetProcAddress(ntdll, "NtQueryAttributesFile"));
            _ntQueryInformationByName = reinterpret_cast<NtQueryInformationByNameFn>(GetProcAddress(ntdll, "NtQueryInformationByName"));
        }

        void Run()
        {
            Attributes();
            ReadContents();
            HandleInformation(L"a.txt", false);
            HandleInformation(L"sub", true);
            HandleInformation(UnicodeName, false);
            NameQueries(L"sub\\b.txt");
            FinalPaths(L"sub\\deep\\c.bin", false);
            FinalPaths(L"sub", true);
            FindFiles();
            DirectoryClasses();
            NtDirectoryNames();
            CurrentDirectory();
            MemoryMap();
            RelativeNtOpen();
            OverlappedRead();
            Errors();
            BadPointers();
            QueryByName();
        }

    private:
        std::wstring Path(const std::wstring& relative) const
        {
            return relative.empty() ? _root : _root + L"\\" + relative;
        }

        void Line(const std::wstring& step, const std::wstring& text) const { _out.Line(step, text); }

        HANDLE Open(const std::wstring& path, bool directory, DWORD flags = 0) const
        {
            return CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr,
                OPEN_EXISTING, (directory ? FILE_FLAG_BACKUP_SEMANTICS : 0) | flags, nullptr);
        }

        /// ".." describes the parent folder, which for the root lies outside the folder under test.
        static bool IsParentEntry(const wchar_t* name, size_t chars) noexcept
        {
            return chars == 2 && name[0] == L'.' && name[1] == L'.';
        }

        std::wstring HashHandle(HANDLE handle) const
        {
            BYTE buffer[16384];
            unsigned long long hash = 14695981039346656037ull;
            unsigned long long total = 0;
            DWORD read = 0;
            while (ReadFile(handle, buffer, sizeof(buffer), &read, nullptr) && read > 0)
            {
                hash = Fnv1a(buffer, read, hash);
                total += read;
            }
            return L"size=" + Num(static_cast<long long>(total)) + L" fnv=" + Hex(hash);
        }

        void Attributes() const
        {
            for (const wchar_t* relative : { L"", L"a.txt", L"sub", L"empty", L"ro.txt", L"missing.txt", L"missing\\x.txt",
                     L"SUB\\..\\A.TXT", L"sub\\deep\\c.bin" })
            {
                const DWORD attributes = GetFileAttributesW(Path(relative).c_str());
                Line(L"GetFileAttributes", Path(relative) + L" -> "
                    + (attributes == INVALID_FILE_ATTRIBUTES ? Error(GetLastError()) : Hex(attributes)));

                WIN32_FILE_ATTRIBUTE_DATA data{};
                if (GetFileAttributesExW(Path(relative).c_str(), GetFileExInfoStandard, &data))
                {
                    Line(L"GetFileAttributesEx", Path(relative) + L" -> attr=" + Hex(data.dwFileAttributes)
                        + L" size=" + Num((static_cast<long long>(data.nFileSizeHigh) << 32) | data.nFileSizeLow)
                        + L" created=" + Time(data.ftCreationTime) + L" written=" + Time(data.ftLastWriteTime));
                }
                else
                {
                    Line(L"GetFileAttributesEx", Path(relative) + L" -> " + Error(GetLastError()));
                }
            }
        }

        void ReadContents() const
        {
            for (const wchar_t* relative : { L"a.txt", L"sub\\b.txt", L"sub\\deep\\c.bin", L"ro.txt", UnicodeName, L"SUB\\..\\A.TXT" })
            {
                Handle file(Open(Path(relative), false));
                Line(L"ReadFile", Path(relative) + L" -> " + (file.IsValid() ? HashHandle(file.Get()) : Error(GetLastError())));
            }
            // Win32 long-path prefix goes through the same NT path.
            Handle prefixed(Open(L"\\\\?\\" + Path(L"a.txt"), false));
            Line(L"ReadFile.prefixed", prefixed.IsValid() ? HashHandle(prefixed.Get()) : Error(GetLastError()));
        }

        void HandleInformation(const std::wstring& relative, bool directory) const
        {
            Handle file(Open(Path(relative), directory));
            const std::wstring step = L"Handle[" + relative + L"]";
            if (!file.IsValid())
            {
                Line(step, Error(GetLastError()));
                return;
            }
            BY_HANDLE_FILE_INFORMATION info{};
            if (GetFileInformationByHandle(file.Get(), &info))
            {
                Line(step + L".ByHandle", L"attr=" + Hex(info.dwFileAttributes) + L" size="
                    + Num((static_cast<long long>(info.nFileSizeHigh) << 32) | info.nFileSizeLow)
                    + L" links=" + Num(info.nNumberOfLinks) + L" index=" + Hex((static_cast<unsigned long long>(info.nFileIndexHigh) << 32) | info.nFileIndexLow)
                    + L" serial=" + Hex(info.dwVolumeSerialNumber) + L" created=" + Time(info.ftCreationTime)
                    + L" written=" + Time(info.ftLastWriteTime));
            }
            else
            {
                Line(step + L".ByHandle", Error(GetLastError()));
            }

            FILE_BASIC_INFO basic{};
            Line(step + L".Basic", GetFileInformationByHandleEx(file.Get(), FileBasicInfo, &basic, sizeof(basic))
                ? L"attr=" + Hex(basic.FileAttributes) + L" created=" + Time(basic.CreationTime) + L" written="
                    + Time(basic.LastWriteTime) + L" changed=" + Time(basic.ChangeTime)
                : Error(GetLastError()));

            FILE_STANDARD_INFO standard{};
            Line(step + L".Standard", GetFileInformationByHandleEx(file.Get(), FileStandardInfo, &standard, sizeof(standard))
                ? L"eof=" + Num(standard.EndOfFile.QuadPart) + L" alloc=" + Num(standard.AllocationSize.QuadPart) + L" links="
                    + Num(standard.NumberOfLinks) + L" dir=" + Num(standard.Directory) + L" delete=" + Num(standard.DeletePending)
                : Error(GetLastError()));

            FILE_ATTRIBUTE_TAG_INFO tag{};
            Line(step + L".AttributeTag", GetFileInformationByHandleEx(file.Get(), FileAttributeTagInfo, &tag, sizeof(tag))
                ? L"attr=" + Hex(tag.FileAttributes) + L" tag=" + Hex(tag.ReparseTag)
                : Error(GetLastError()));

            FILE_ID_INFO id{};
            if (GetFileInformationByHandleEx(file.Get(), FileIdInfo, &id, sizeof(id)))
            {
                unsigned long long low = 0;
                memcpy(&low, id.FileId.Identifier, sizeof(low));
                Line(step + L".Id", L"serial=" + Hex(id.VolumeSerialNumber) + L" id=" + Hex(low));
            }
            else
            {
                Line(step + L".Id", Error(GetLastError()));
            }

            alignas(8) BYTE nameBuffer[4096];
            auto* name = reinterpret_cast<FILE_NAME_INFO*>(nameBuffer);
            Line(step + L".Name", GetFileInformationByHandleEx(file.Get(), FileNameInfo, name, sizeof(nameBuffer))
                ? std::wstring(name->FileName, name->FileNameLength / sizeof(wchar_t))
                : Error(GetLastError()));

            wchar_t fsName[MAX_PATH] = {};
            DWORD serial = 0;
            DWORD maxComponent = 0;
            DWORD fsFlags = 0;
            Line(step + L".Volume", GetVolumeInformationByHandleW(file.Get(), nullptr, 0, &serial, &maxComponent, &fsFlags, fsName, MAX_PATH)
                ? L"serial=" + Hex(serial) + L" max=" + Num(maxComponent) + L" flags=" + Hex(fsFlags) + L" fs=" + fsName
                : Error(GetLastError()));
        }

        void NameQueries(const std::wstring& relative) const
        {
            Handle file(Open(Path(relative), false));
            const std::wstring step = L"NtName[" + relative + L"]";
            if (!file.IsValid())
            {
                Line(step, Error(GetLastError()));
                return;
            }
            alignas(8) BYTE buffer[8192];
            for (const ULONG cls : { FileNameInformationClass, FileNormalizedNameInformationClass, FileAllInformationClass })
            {
                IO_STATUS_BLOCK io{};
                const NTSTATUS status = _ntQueryInformationFile(file.Get(), &io, buffer, sizeof(buffer), cls);
                const ULONG offset = cls == FileAllInformationClass ? FileAllInfoNameOffset : 0;
                const ULONG nameLength = *reinterpret_cast<const ULONG*>(buffer + offset);
                const auto* nameText = reinterpret_cast<const wchar_t*>(buffer + offset + sizeof(ULONG));
                Line(step + L".class" + Num(cls), Status(status) + (NT_SUCCESS(status)
                    ? L" info=" + Num(static_cast<long long>(io.Information) - nameLength) + L" name=" + std::wstring(nameText, nameLength / sizeof(wchar_t))
                    : L""));
            }
            // Small buffer: STATUS_BUFFER_OVERFLOW with the full length (reported relative to the root length).
            alignas(8) BYTE small[12] = {};
            IO_STATUS_BLOCK io{};
            const NTSTATUS status = _ntQueryInformationFile(file.Get(), &io, small, sizeof(small), FileNameInformationClass);
            const ULONG fullLength = *reinterpret_cast<const ULONG*>(small);
            Line(step + L".small", Status(status) + L" info=" + Num(static_cast<long long>(io.Information)) + L" lengthBeyondRoot="
                + Num(static_cast<long long>(fullLength / sizeof(wchar_t)) - static_cast<long long>(_out.RootNoDriveLength())));
        }

        void FinalPaths(const std::wstring& relative, bool directory) const
        {
            Handle file(Open(Path(relative), directory));
            const std::wstring step = L"FinalPath[" + relative + L"]";
            if (!file.IsValid())
            {
                Line(step, Error(GetLastError()));
                return;
            }
            const struct { const wchar_t* Name; DWORD Flags; } kinds[] = {
                { L"dos", FILE_NAME_NORMALIZED | VOLUME_NAME_DOS },
                { L"opened", FILE_NAME_OPENED | VOLUME_NAME_DOS },
                { L"none", FILE_NAME_NORMALIZED | VOLUME_NAME_NONE },
                { L"nt", FILE_NAME_NORMALIZED | VOLUME_NAME_NT },
                { L"guid", FILE_NAME_NORMALIZED | VOLUME_NAME_GUID },
            };
            for (const auto& kind : kinds)
            {
                wchar_t path[1024];
                const DWORD length = GetFinalPathNameByHandleW(file.Get(), path, 1024, kind.Flags);
                Line(step + L"." + kind.Name, length == 0 || length >= 1024 ? Error(GetLastError()) : std::wstring(path, length));
            }
            wchar_t tiny[4];
            const DWORD required = GetFinalPathNameByHandleW(file.Get(), tiny, 4, FILE_NAME_NORMALIZED | VOLUME_NAME_DOS);
            Line(step + L".required", L"beyondRoot=" + Num(static_cast<long long>(required) - static_cast<long long>(_out.RootLength())));
        }

        void FindFiles() const
        {
            for (const wchar_t* pattern : { L"*", L"sub\\*.txt", L"sub\\*", L"sub", L"a.txt", L"empty\\*", L"*.none", L"missing\\*" })
            {
                WIN32_FIND_DATAW data{};
                const HANDLE find = FindFirstFileExW(Path(pattern).c_str(), FindExInfoBasic, &data, FindExSearchNameMatch, nullptr,
                    FIND_FIRST_EX_LARGE_FETCH);
                const std::wstring step = L"FindFile[" + std::wstring(pattern) + L"]";
                if (find == INVALID_HANDLE_VALUE)
                {
                    Line(step, Error(GetLastError()));
                    continue;
                }
                std::vector<std::wstring> entries;
                do
                {
                    entries.push_back(std::wstring(data.cFileName) + L" attr=" + Hex(data.dwFileAttributes) + L" size="
                        + Num((static_cast<long long>(data.nFileSizeHigh) << 32) | data.nFileSizeLow)
                        + (IsParentEntry(data.cFileName, wcslen(data.cFileName)) ? L"" : L" written=" + Time(data.ftLastWriteTime)));
                } while (FindNextFileW(find, &data));
                const DWORD endError = GetLastError();
                FindClose(find);
                std::sort(entries.begin(), entries.end());
                for (const std::wstring& entry : entries)
                {
                    Line(step, entry);
                }
                Line(step + L".end", Error(endError));
            }
        }

        template <typename TEntry>
        void EnumerateClass(const wchar_t* name, FILE_INFO_BY_HANDLE_CLASS restartClass, FILE_INFO_BY_HANDLE_CLASS nextClass,
            const std::wstring& relative) const
        {
            Handle directory(Open(Path(relative), true));
            const std::wstring step = std::wstring(L"DirClass.") + name + L"[" + relative + L"]";
            if (!directory.IsValid())
            {
                Line(step, Error(GetLastError()));
                return;
            }
            alignas(8) BYTE buffer[65536];
            std::vector<std::wstring> entries;
            FILE_INFO_BY_HANDLE_CLASS cls = restartClass;
            while (GetFileInformationByHandleEx(directory.Get(), cls, buffer, sizeof(buffer)))
            {
                cls = nextClass;
                auto* entry = reinterpret_cast<const TEntry*>(buffer);
                while (true)
                {
                    const bool parent = IsParentEntry(entry->FileName, entry->FileNameLength / sizeof(wchar_t));
                    entries.push_back(std::wstring(entry->FileName, entry->FileNameLength / sizeof(wchar_t)) + L" attr="
                        + Hex(entry->FileAttributes) + L" eof=" + Num(entry->EndOfFile.QuadPart)
                        + (parent ? L"" : L" created=" + Time(entry->CreationTime) + L" written=" + Time(entry->LastWriteTime)));
                    if (entry->NextEntryOffset == 0)
                    {
                        break;
                    }
                    entry = reinterpret_cast<const TEntry*>(reinterpret_cast<const BYTE*>(entry) + entry->NextEntryOffset);
                }
            }
            const DWORD endError = GetLastError();
            std::sort(entries.begin(), entries.end());
            for (const std::wstring& entry : entries)
            {
                Line(step, entry);
            }
            Line(step + L".end", Error(endError));
        }

        void DirectoryClasses() const
        {
            for (const wchar_t* relative : { L"", L"sub" })
            {
                EnumerateClass<FILE_ID_BOTH_DIR_INFO>(L"IdBoth", FileIdBothDirectoryRestartInfo, FileIdBothDirectoryInfo, relative);
                EnumerateClass<FILE_FULL_DIR_INFO>(L"Full", FileFullDirectoryRestartInfo, FileFullDirectoryInfo, relative);
                EnumerateClass<FILE_ID_EXTD_DIR_INFO>(L"IdExtd", FileIdExtdDirectoryRestartInfo, FileIdExtdDirectoryInfo, relative);
            }
        }

        void NtDirectoryNames() const
        {
            Handle directory(Open(Path(L""), true));
            if (!directory.IsValid())
            {
                Line(L"NtQueryDirectoryFile", Error(GetLastError()));
                return;
            }
            UNICODE_STRING mask{};
            RtlInitUnicodeString(&mask, L"*.txt");
            alignas(8) BYTE buffer[8192];
            std::vector<std::wstring> names;
            BOOLEAN restart = TRUE;
            NTSTATUS status;
            while (true)
            {
                IO_STATUS_BLOCK io{};
                status = _ntQueryDirectoryFile(directory.Get(), nullptr, nullptr, nullptr, &io, buffer, sizeof(buffer),
                    FileNamesInformationClass, FALSE, restart ? &mask : nullptr, restart);
                restart = FALSE;
                if (!NT_SUCCESS(status))
                {
                    break;
                }
                const BYTE* entry = buffer;
                while (true)
                {
                    const ULONG next = *reinterpret_cast<const ULONG*>(entry);
                    const ULONG length = *reinterpret_cast<const ULONG*>(entry + 8);
                    names.emplace_back(reinterpret_cast<const wchar_t*>(entry + 12), length / sizeof(wchar_t));
                    if (next == 0)
                    {
                        break;
                    }
                    entry += next;
                }
            }
            std::sort(names.begin(), names.end());
            for (const std::wstring& name : names)
            {
                Line(L"NtQueryDirectoryFile.Names", name);
            }
            Line(L"NtQueryDirectoryFile.end", Status(status));
        }

        void CurrentDirectory() const
        {
            wchar_t saved[MAX_PATH];
            GetCurrentDirectoryW(MAX_PATH, saved);
            if (!SetCurrentDirectoryW(_root.c_str()))
            {
                Line(L"SetCurrentDirectory", Error(GetLastError()));
                return;
            }
            wchar_t current[MAX_PATH];
            GetCurrentDirectoryW(MAX_PATH, current);
            Line(L"GetCurrentDirectory", current);
            Handle file(Open(L"sub\\b.txt", false));
            Line(L"Cwd.ReadFile", file.IsValid() ? HashHandle(file.Get()) : Error(GetLastError()));
            const DWORD attributes = GetFileAttributesW(L"a.txt");
            Line(L"Cwd.GetFileAttributes", attributes == INVALID_FILE_ATTRIBUTES ? Error(GetLastError()) : Hex(attributes));
            wchar_t full[MAX_PATH];
            GetFullPathNameW(L"sub\\..\\a.txt", MAX_PATH, full, nullptr);
            Line(L"Cwd.GetFullPathName", full);
            SetCurrentDirectoryW(L"sub");
            Handle nested(Open(L"deep\\c.bin", false));
            Line(L"Cwd.Nested.ReadFile", nested.IsValid() ? HashHandle(nested.Get()) : Error(GetLastError()));
            SetCurrentDirectoryW(saved);
        }

        void MemoryMap() const
        {
            Handle file(Open(Path(L"sub\\deep\\c.bin"), false));
            if (!file.IsValid())
            {
                Line(L"MapViewOfFile", Error(GetLastError()));
                return;
            }
            Handle mapping(CreateFileMappingW(file.Get(), nullptr, PAGE_READONLY, 0, 0, nullptr));
            if (!mapping.IsValid())
            {
                Line(L"MapViewOfFile", Error(GetLastError()));
                return;
            }
            LARGE_INTEGER size{};
            GetFileSizeEx(file.Get(), &size);
            const void* view = MapViewOfFile(mapping.Get(), FILE_MAP_READ, 0, 0, 0);
            if (view == nullptr)
            {
                Line(L"MapViewOfFile", Error(GetLastError()));
                return;
            }
            Line(L"MapViewOfFile", L"size=" + Num(size.QuadPart) + L" fnv="
                + Hex(Fnv1a(static_cast<const BYTE*>(view), static_cast<size_t>(size.QuadPart))));
            UnmapViewOfFile(view);
        }

        void RelativeNtOpen() const
        {
            Handle directory(Open(Path(L"sub"), true));
            if (!directory.IsValid())
            {
                Line(L"NtOpenFile.relative", Error(GetLastError()));
                return;
            }
            UNICODE_STRING name{};
            RtlInitUnicodeString(&name, L"b.txt");
            OBJECT_ATTRIBUTES attributes{};
            InitializeObjectAttributes(&attributes, &name, OBJ_CASE_INSENSITIVE, directory.Get(), nullptr);
            IO_STATUS_BLOCK io{};
            HANDLE raw = nullptr;
            NTSTATUS status = _ntOpenFile(&raw, FILE_GENERIC_READ, &attributes, &io, FILE_SHARE_READ,
                FILE_SYNCHRONOUS_IO_NONALERT | FILE_NON_DIRECTORY_FILE);
            Handle file(NT_SUCCESS(status) ? raw : INVALID_HANDLE_VALUE);
            Line(L"NtOpenFile.relative", Status(status) + (file.IsValid() ? L" " + HashHandle(file.Get()) : L""));
            if (file.IsValid())
            {
                alignas(8) BYTE buffer[4096];
                IO_STATUS_BLOCK nameIo{};
                status = _ntQueryInformationFile(file.Get(), &nameIo, buffer, sizeof(buffer), FileNameInformationClass);
                Line(L"NtOpenFile.relative.name", Status(status) + (NT_SUCCESS(status)
                    ? L" " + std::wstring(reinterpret_cast<const wchar_t*>(buffer + 4), *reinterpret_cast<const ULONG*>(buffer) / sizeof(wchar_t))
                    : L""));
            }

            RtlInitUnicodeString(&name, L"deep\\c.bin");
            InitializeObjectAttributes(&attributes, &name, OBJ_CASE_INSENSITIVE, directory.Get(), nullptr);
            raw = nullptr;
            status = _ntCreateFile(&raw, FILE_GENERIC_READ, &attributes, &io, nullptr, 0, FILE_SHARE_READ, FILE_OPEN,
                FILE_SYNCHRONOUS_IO_NONALERT | FILE_NON_DIRECTORY_FILE, nullptr, 0);
            Handle deep(NT_SUCCESS(status) ? raw : INVALID_HANDLE_VALUE);
            Line(L"NtCreateFile.relative", Status(status) + L" info=" + Num(static_cast<long long>(io.Information))
                + (deep.IsValid() ? L" " + HashHandle(deep.Get()) : L""));

            RtlInitUnicodeString(&name, L"missing.txt");
            InitializeObjectAttributes(&attributes, &name, OBJ_CASE_INSENSITIVE, directory.Get(), nullptr);
            raw = nullptr;
            status = _ntOpenFile(&raw, FILE_GENERIC_READ, &attributes, &io, FILE_SHARE_READ, FILE_SYNCHRONOUS_IO_NONALERT);
            Handle missing(NT_SUCCESS(status) ? raw : INVALID_HANDLE_VALUE);
            Line(L"NtOpenFile.relative.missing", Status(status));

            // Absolute NT path.
            const std::wstring ntPath = L"\\??\\" + Path(L"a.txt");
            RtlInitUnicodeString(&name, ntPath.c_str());
            InitializeObjectAttributes(&attributes, &name, OBJ_CASE_INSENSITIVE, nullptr, nullptr);
            raw = nullptr;
            status = _ntOpenFile(&raw, FILE_GENERIC_READ, &attributes, &io, FILE_SHARE_READ,
                FILE_SYNCHRONOUS_IO_NONALERT | FILE_NON_DIRECTORY_FILE);
            Handle absolute(NT_SUCCESS(status) ? raw : INVALID_HANDLE_VALUE);
            Line(L"NtOpenFile.absolute", Status(status) + (absolute.IsValid() ? L" " + HashHandle(absolute.Get()) : L""));
        }

        void OverlappedRead() const
        {
            Handle file(Open(Path(L"sub\\deep\\c.bin"), false, FILE_FLAG_OVERLAPPED));
            if (!file.IsValid())
            {
                Line(L"OverlappedRead", Error(GetLastError()));
                return;
            }
            Handle event(CreateEventW(nullptr, TRUE, FALSE, nullptr));
            std::vector<BYTE> buffer(4096);
            OVERLAPPED overlapped{};
            overlapped.Offset = 1000;
            overlapped.hEvent = event.Get();
            DWORD read = 0;
            if (!ReadFile(file.Get(), buffer.data(), static_cast<DWORD>(buffer.size()), nullptr, &overlapped)
                && GetLastError() != ERROR_IO_PENDING)
            {
                Line(L"OverlappedRead", Error(GetLastError()));
                return;
            }
            Line(L"OverlappedRead", GetOverlappedResult(file.Get(), &overlapped, &read, TRUE)
                ? L"read=" + Num(read) + L" fnv=" + Hex(Fnv1a(buffer.data(), read))
                : Error(GetLastError()));
        }

        void Errors() const
        {
            for (const wchar_t* relative : { L"missing.txt", L"missing\\x.txt", L"a.txt\\x" })
            {
                Handle file(Open(Path(relative), false));
                Line(L"OpenError[" + std::wstring(relative) + L"]", file.IsValid() ? L"opened" : Error(GetLastError()));
            }
            // Directory opened without FILE_FLAG_BACKUP_SEMANTICS.
            Handle directory(Open(Path(L"sub"), false));
            Line(L"OpenError[sub-as-file]", directory.IsValid() ? L"opened" : Error(GetLastError()));
            // Create must not succeed on an existing file.
            Handle created(CreateFileW(Path(L"a.txt").c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, CREATE_NEW, 0, nullptr));
            Line(L"OpenError[create-new-existing]", created.IsValid() ? L"opened" : Error(GetLastError()));
        }

        /// Unreadable caller pointers: the system answers STATUS_ACCESS_VIOLATION and so must the hooks
        /// (instead of crashing the process).
        void BadPointers() const
        {
            // Reserved, never committed: any access faults.
            void* bad = VirtualAlloc(nullptr, 0x10000, MEM_RESERVE, PAGE_NOACCESS);
            // Read access only: a read-only share (M2) refuses DELETE. The rename check needs DELETE (the access
            // check comes before the buffer) and the hook reads the buffer whatever the handle, so it uses a
            // temporary file outside the root.
            Handle file(CreateFileW(Path(L"a.txt").c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                nullptr, OPEN_EXISTING, 0, nullptr));
            wchar_t tempFolder[MAX_PATH];
            GetTempPathW(MAX_PATH, tempFolder);
            const std::wstring tempPath = std::wstring(tempFolder) + L"ProcBridge.NativeProbe-" + Num(GetCurrentProcessId()) + L".tmp";
            Handle renamable(CreateFileW(tempPath.c_str(), GENERIC_READ | DELETE, 0, nullptr, CREATE_ALWAYS, FILE_FLAG_DELETE_ON_CLOSE, nullptr));
            if (bad == nullptr || !file.IsValid() || !renamable.IsValid())
            {
                Line(L"BadPointers", Error(GetLastError()));
                return;
            }
            alignas(8) BYTE buffer[4096];
            IO_STATUS_BLOCK io{};
            Line(L"BadPointers.QueryName.buffer", Status(_ntQueryInformationFile(file.Get(), &io, bad, 4096, FileNameInformationClass)));
            Line(L"BadPointers.QueryName.io", Status(_ntQueryInformationFile(file.Get(), static_cast<PIO_STATUS_BLOCK>(bad), buffer,
                sizeof(buffer), FileNameInformationClass)));
            Line(L"BadPointers.QueryObject.buffer", Status(_ntQueryObject(file.Get(), 1, bad, 4096, nullptr)));
            Line(L"BadPointers.QueryObject.length", Status(_ntQueryObject(file.Get(), 1, buffer, sizeof(buffer), static_cast<PULONG>(bad))));
            Line(L"BadPointers.Rename.buffer", Status(_ntSetInformationFile(renamable.Get(), &io, bad, 64, 10)));

            HANDLE raw = nullptr;
            Line(L"BadPointers.Open.attributes", Status(_ntOpenFile(&raw, FILE_GENERIC_READ, static_cast<POBJECT_ATTRIBUTES>(bad), &io,
                FILE_SHARE_READ, FILE_SYNCHRONOUS_IO_NONALERT)));
            OBJECT_ATTRIBUTES attributes{};
            InitializeObjectAttributes(&attributes, static_cast<PUNICODE_STRING>(bad), OBJ_CASE_INSENSITIVE, nullptr, nullptr);
            Line(L"BadPointers.Open.name", Status(_ntOpenFile(&raw, FILE_GENERIC_READ, &attributes, &io, FILE_SHARE_READ,
                FILE_SYNCHRONOUS_IO_NONALERT)));
            UNICODE_STRING name{};
            name.Buffer = static_cast<PWSTR>(bad);
            name.Length = 40;
            name.MaximumLength = 40;
            InitializeObjectAttributes(&attributes, &name, OBJ_CASE_INSENSITIVE, nullptr, nullptr);
            Line(L"BadPointers.Open.text", Status(_ntOpenFile(&raw, FILE_GENERIC_READ, &attributes, &io, FILE_SHARE_READ,
                FILE_SYNCHRONOUS_IO_NONALERT)));
            alignas(8) BYTE basic[40]{};
            Line(L"BadPointers.QueryAttributes.name", Status(_ntQueryAttributesFile(&attributes, basic)));
            // Valid virtual name, unreadable handle pointer for the result.
            const std::wstring ntPath = L"\\??\\" + Path(L"a.txt");
            RtlInitUnicodeString(&name, ntPath.c_str());
            InitializeObjectAttributes(&attributes, &name, OBJ_CASE_INSENSITIVE, nullptr, nullptr);
            Line(L"BadPointers.Open.handle", Status(_ntOpenFile(static_cast<PHANDLE>(bad), FILE_GENERIC_READ, &attributes, &io,
                FILE_SHARE_READ, FILE_SYNCHRONOUS_IO_NONALERT)));
            VirtualFree(bad, 0, MEM_RELEASE);
        }

        /// NtQueryInformationByName (no handle). Older systems without the export print "unsupported".
        void QueryByName() const
        {
            for (const wchar_t* relative : { L"a.txt", L"sub", L"sub\\deep\\c.bin", L"missing.txt" })
            {
                if (_ntQueryInformationByName == nullptr)
                {
                    Line(L"NtQueryInformationByName", L"unsupported");
                    return;
                }
                const std::wstring ntPath = L"\\??\\" + Path(relative);
                UNICODE_STRING name{};
                RtlInitUnicodeString(&name, ntPath.c_str());
                OBJECT_ATTRIBUTES attributes{};
                InitializeObjectAttributes(&attributes, &name, OBJ_CASE_INSENSITIVE, nullptr, nullptr);
                IO_STATUS_BLOCK io{};
                FileStatInfo stat{};
                const NTSTATUS status = _ntQueryInformationByName(&attributes, &io, &stat, sizeof(stat), FileStatInformationClass);
                Line(L"NtQueryInformationByName", Path(relative) + L" -> " + Status(status) + (NT_SUCCESS(status)
                    ? L" attr=" + Hex(stat.FileAttributes) + L" size=" + Num(stat.EndOfFile.QuadPart) + L" created="
                        + Time(stat.CreationTime) + L" written=" + Time(stat.LastWriteTime)
                    : L""));
            }
        }

        std::wstring _root;
        const ProbeOutput& _out;
        NtQueryInformationFileFn _ntQueryInformationFile;
        NtOpenFileFn _ntOpenFile;
        NtCreateFileFn _ntCreateFile;
        NtQueryDirectoryFileFn _ntQueryDirectoryFile;
        NtQueryObjectFn _ntQueryObject;
        NtSetInformationFileFn _ntSetInformationFile;
        NtQueryAttributesFileFn _ntQueryAttributesFile;
        NtQueryInformationByNameFn _ntQueryInformationByName;
    };
}

namespace
{
    void WriteLine(const std::wstring& text)
    {
        const std::wstring line = text + L"\n";
        const int bytes = WideCharToMultiByte(CP_UTF8, 0, line.c_str(), static_cast<int>(line.size()), nullptr, 0, nullptr, nullptr);
        std::string utf8(static_cast<size_t>(bytes), '\0');
        WideCharToMultiByte(CP_UTF8, 0, line.c_str(), static_cast<int>(line.size()), utf8.data(), bytes, nullptr, nullptr);
        DWORD written = 0;
        WriteFile(GetStdHandle(STD_OUTPUT_HANDLE), utf8.data(), static_cast<DWORD>(utf8.size()), &written, nullptr);
    }

    std::wstring FinalPath(HANDLE handle)
    {
        wchar_t path[MAX_PATH * 2];
        const DWORD length = GetFinalPathNameByHandleW(handle, path, static_cast<DWORD>(std::size(path)), FILE_NAME_NORMALIZED);
        return length == 0 || length >= std::size(path) ? Error(GetLastError()) : std::wstring(path, length);
    }

    HANDLE OpenRead(const wchar_t* path)
    {
        return CreateFileW(path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr, OPEN_EXISTING, 0, nullptr);
    }

    /// --stale-handle <virtual file> <real file>: closes a virtual handle without NtClose
    /// (DuplicateHandle with DUPLICATE_CLOSE_SOURCE and no target process), reopens the real file until
    /// the old handle value comes back and prints the final path of that new handle.
    int StaleHandle(const wchar_t* virtualFile, const wchar_t* realFile)
    {
        const HANDLE stale = OpenRead(virtualFile);
        if (stale == INVALID_HANDLE_VALUE)
        {
            WriteLine(L"open virtual: " + Error(GetLastError()));
            return 1;
        }
        if (!DuplicateHandle(GetCurrentProcess(), stale, nullptr, nullptr, 0, FALSE, DUPLICATE_CLOSE_SOURCE))
        {
            WriteLine(L"close source: " + Error(GetLastError()));
            return 1;
        }
        std::vector<HANDLE> others;
        HANDLE reused = INVALID_HANDLE_VALUE;
        for (int attempt = 0; attempt < 1024 && reused == INVALID_HANDLE_VALUE; ++attempt)
        {
            const HANDLE handle = OpenRead(realFile);
            if (handle == INVALID_HANDLE_VALUE)
            {
                WriteLine(L"open real: " + Error(GetLastError()));
                return 1;
            }
            (handle == stale ? reused : others.emplace_back()) = handle;
        }
        WriteLine(reused == INVALID_HANDLE_VALUE ? L"reused: none" : L"reused: " + FinalPath(reused));
        for (HANDLE handle : others)
        {
            CloseHandle(handle);
        }
        if (reused != INVALID_HANDLE_VALUE)
        {
            CloseHandle(reused);
        }
        return 0;
    }

    /// --failed-close <file>: a close that fails (handle protected from close) keeps the handle open, so its
    /// final path must not change.
    int FailedClose(const wchar_t* file)
    {
        const HANDLE handle = OpenRead(file);
        if (handle == INVALID_HANDLE_VALUE)
        {
            WriteLine(L"open: " + Error(GetLastError()));
            return 1;
        }
        SetHandleInformation(handle, HANDLE_FLAG_PROTECT_FROM_CLOSE, HANDLE_FLAG_PROTECT_FROM_CLOSE);
        const BOOL closed = CloseHandle(handle);
        WriteLine(L"close: " + (closed ? std::wstring(L"closed") : Error(GetLastError())));
        WriteLine(L"after: " + FinalPath(handle));
        SetHandleInformation(handle, HANDLE_FLAG_PROTECT_FROM_CLOSE, 0);
        CloseHandle(handle);
        return 0;
    }

    struct ThreadCheck
    {
        const wchar_t* VirtualFile;
        const wchar_t* RealFile;
        std::wstring ExpectedVirtual;
        std::wstring ExpectedReal;
        volatile LONG Mismatches;
    };

    DWORD WINAPI ThreadCheckWorker(LPVOID parameter)
    {
        auto* check = static_cast<ThreadCheck*>(parameter);
        for (int i = 0; i < 300; ++i)
        {
            const bool useVirtual = (i + GetCurrentThreadId()) % 2 == 0;
            const HANDLE handle = OpenRead(useVirtual ? check->VirtualFile : check->RealFile);
            if (handle == INVALID_HANDLE_VALUE || FinalPath(handle) != (useVirtual ? check->ExpectedVirtual : check->ExpectedReal))
            {
                InterlockedIncrement(&check->Mismatches);
            }
            if (handle != INVALID_HANDLE_VALUE)
            {
                CloseHandle(handle);
            }
        }
        return 0;
    }

    /// --threads <virtual file> <real file>: 8 threads open and close both files concurrently (handle
    /// values are reused all the time) and check every final path; prints the number of mismatches.
    int ThreadedHandles(const wchar_t* virtualFile, const wchar_t* realFile)
    {
        ThreadCheck check{ virtualFile, realFile, std::wstring(L"\\\\?\\") + virtualFile, std::wstring(L"\\\\?\\") + realFile, 0 };
        HANDLE threads[8];
        for (HANDLE& thread : threads)
        {
            thread = CreateThread(nullptr, 0, ThreadCheckWorker, &check, 0, nullptr);
        }
        WaitForMultipleObjects(static_cast<DWORD>(std::size(threads)), threads, TRUE, INFINITE);
        for (HANDLE thread : threads)
        {
            CloseHandle(thread);
        }
        WriteLine(L"mismatches: " + Num(check.Mismatches));
        return 0;
    }

    /// --stdin-name: final path of the standard input handle (inherited from the parent, plan 5.5).
    int StdinName()
    {
        WriteLine(L"stdin: " + FinalPath(GetStdHandle(STD_INPUT_HANDLE)));
        return 0;
    }

    /// File id and last write time of handle, as GetFileInformationByHandle reports them (with the Broker:
    /// machine B's, not the cache copy's).
    std::wstring Identity(HANDLE handle)
    {
        BY_HANDLE_FILE_INFORMATION info{};
        if (!GetFileInformationByHandle(handle, &info))
        {
            return L"error " + std::to_wstring(GetLastError());
        }
        const ULONGLONG id = (static_cast<ULONGLONG>(info.nFileIndexHigh) << 32) | info.nFileIndexLow;
        const ULONGLONG written = (static_cast<ULONGLONG>(info.ftLastWriteTime.dwHighDateTime) << 32) | info.ftLastWriteTime.dwLowDateTime;
        return L"id " + std::to_wstring(id) + L" written " + std::to_wstring(written);
    }

    /// --dir-classes <dir>: lists dir with every directory information class (ntifs.h numbers) and prints,
    /// per class, the status and each entry's name, attributes and size (sorted, "." and ".." skipped).
    int DirectoryClasses(const wchar_t* directoryPath)
    {
        struct ClassInfo
        {
            ULONG Class;
            const wchar_t* Name;
            ULONG NameOffset;
            bool NamesOnly;
        };
        constexpr ClassInfo classes[] = {
            { 1, L"Directory", 64, false }, { 2, L"FullDirectory", 68, false }, { 3, L"BothDirectory", 94, false },
            { 12, L"Names", 12, true }, { 37, L"IdBothDirectory", 104, false }, { 38, L"IdFullDirectory", 80, false },
            { 60, L"IdExtdDirectory", 88, false }, { 63, L"IdExtdBothDirectory", 114, false },
            { 78, L"Id64ExtdDirectory", 80, false }, { 79, L"Id64ExtdBothDirectory", 106, false },
            { 80, L"IdAllExtdDirectory", 96, false }, { 81, L"IdAllExtdBothDirectory", 122, false },
        };
        const auto query = reinterpret_cast<NtQueryDirectoryFileFn>(GetProcAddress(GetModuleHandleW(L"ntdll.dll"), "NtQueryDirectoryFile"));
        const HANDLE directory = CreateFileW(directoryPath, FILE_LIST_DIRECTORY | SYNCHRONIZE, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
            nullptr, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, nullptr);
        if (directory == INVALID_HANDLE_VALUE)
        {
            WriteLine(L"open: error " + std::to_wstring(GetLastError()));
            return 1;
        }
        std::vector<BYTE> buffer(64 * 1024);
        for (const ClassInfo& info : classes)
        {
            std::vector<std::wstring> lines;
            NTSTATUS status = STATUS_SUCCESS;
            BOOLEAN restart = TRUE;
            while (true)
            {
                IO_STATUS_BLOCK io{};
                status = query(directory, nullptr, nullptr, nullptr, &io, buffer.data(), static_cast<ULONG>(buffer.size()), info.Class,
                    FALSE, nullptr, restart);
                restart = FALSE;
                if (!NT_SUCCESS(status))
                {
                    break;
                }
                const BYTE* entry = buffer.data();
                while (true)
                {
                    ULONG next = 0;
                    ULONG nameBytes = 0;
                    memcpy(&next, entry, sizeof(next));
                    memcpy(&nameBytes, entry + (info.NamesOnly ? 8 : 60), sizeof(nameBytes));
                    const std::wstring name(reinterpret_cast<const wchar_t*>(entry + info.NameOffset), nameBytes / sizeof(wchar_t));
                    if (name != L"." && name != L"..")
                    {
                        std::wstring line = name;
                        if (!info.NamesOnly)
                        {
                            LONGLONG size = 0;
                            ULONG attributes = 0;
                            memcpy(&size, entry + 40, sizeof(size));
                            memcpy(&attributes, entry + 56, sizeof(attributes));
                            line += L" size=" + std::to_wstring(size) + L" attributes=" + Hex(attributes & ~FILE_ATTRIBUTE_ARCHIVE);
                        }
                        lines.push_back(line);
                    }
                    if (next == 0)
                    {
                        break;
                    }
                    entry += next;
                }
            }
            std::sort(lines.begin(), lines.end());
            WriteLine(std::wstring(info.Name) + L": " + Status(status == STATUS_NO_MORE_FILES ? STATUS_SUCCESS : status));
            for (const std::wstring& line : lines)
            {
                WriteLine(std::wstring(L"  ") + line);
            }
        }
        CloseHandle(directory);
        return 0;
    }

    /// --stdin-id: identity of the standard input handle (inherited, plan 5.5).
    int StdinIdentity()
    {
        WriteLine(L"identity: " + Identity(GetStdHandle(STD_INPUT_HANDLE)));
        return 0;
    }

    /// --file-id <file>: identity of a file opened by path.
    int FileIdentity(const wchar_t* file)
    {
        const HANDLE handle = CreateFileW(file, FILE_READ_ATTRIBUTES, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr,
            OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, nullptr);
        if (handle == INVALID_HANDLE_VALUE)
        {
            WriteLine(L"identity: error " + std::to_wstring(GetLastError()));
            return 1;
        }
        WriteLine(L"identity: " + Identity(handle));
        CloseHandle(handle);
        return 0;
    }

    /// --delete <file>: NtDeleteFile on the DOS path, prints the status.
    int DeleteByName(const wchar_t* file)
    {
        const auto deleteFile = reinterpret_cast<NtDeleteFileFn>(GetProcAddress(GetModuleHandleW(L"ntdll.dll"), "NtDeleteFile"));
        const std::wstring ntPath = std::wstring(L"\\??\\") + file;
        UNICODE_STRING name{};
        RtlInitUnicodeString(&name, ntPath.c_str());
        OBJECT_ATTRIBUTES attributes{};
        InitializeObjectAttributes(&attributes, &name, OBJ_CASE_INSENSITIVE, nullptr, nullptr);
        WriteLine(L"delete: " + Status(deleteFile(&attributes)));
        return 0;
    }

    // ---- Child process checks (plan 5): the probe runs hooked and creates children of its own. ----

    constexpr int ChildNameMatches = 0;
    constexpr int ChildNameDiffers = 1;

    HANDLE ParseHandleValue(const wchar_t* text)
    {
        return reinterpret_cast<HANDLE>(static_cast<ULONG_PTR>(std::wcstoull(text, nullptr, 10)));
    }

    std::wstring HandleText(HANDLE handle)
    {
        return std::to_wstring(reinterpret_cast<ULONG_PTR>(handle));
    }

    std::wstring SelfPath()
    {
        wchar_t path[MAX_PATH];
        const DWORD length = GetModuleFileNameW(nullptr, path, MAX_PATH);
        return std::wstring(path, length);
    }

    /// --handle-name <value>: final path of a handle value received from the parent.
    int HandleName(const wchar_t* value)
    {
        WriteLine(L"handle: " + FinalPath(ParseHandleValue(value)));
        return 0;
    }

    /// --final-name <file>: opens the file and prints its final path.
    int FinalName(const wchar_t* file)
    {
        const HANDLE handle = OpenRead(file);
        if (handle == INVALID_HANDLE_VALUE)
        {
            WriteLine(L"name: " + Error(GetLastError()));
            return 1;
        }
        WriteLine(L"name: " + FinalPath(handle));
        CloseHandle(handle);
        return 0;
    }

    /// --handle-is <value> <file>: exit code ChildNameMatches when the handle value shows the file's
    /// name, ChildNameDiffers otherwise (a stale virtual entry renames it).
    int HandleIs(const wchar_t* value, const wchar_t* file)
    {
        const std::wstring name = FinalPath(ParseHandleValue(value));
        WriteLine(L"handle: " + name);
        return name == std::wstring(L"\\\\?\\") + file ? ChildNameMatches : ChildNameDiffers;
    }

    struct ChildProcess
    {
        HANDLE Process = nullptr;
        HANDLE Thread = nullptr;
        DWORD ProcessId = 0;

        ~ChildProcess()
        {
            if (Thread != nullptr)
            {
                CloseHandle(Thread);
            }
            if (Process != nullptr)
            {
                CloseHandle(Process);
            }
        }
    };

    /// Starts a probe (this one when exe is empty) with the given arguments. attributes may be null.
    bool StartProbe(const std::wstring& exe, const std::wstring& arguments, BOOL inheritHandles, DWORD flags,
        LPPROC_THREAD_ATTRIBUTE_LIST attributes, ChildProcess& child)
    {
        const std::wstring path = exe.empty() ? SelfPath() : exe;
        std::wstring commandLine = L"\"" + path + L"\" " + arguments;
        STARTUPINFOEXW startup{};
        startup.StartupInfo.cb = attributes == nullptr ? sizeof(STARTUPINFOW) : sizeof(startup);
        startup.lpAttributeList = attributes;
        PROCESS_INFORMATION info{};
        if (!CreateProcessW(path.c_str(), commandLine.data(), nullptr, nullptr, inheritHandles,
            flags | (attributes == nullptr ? 0 : EXTENDED_STARTUPINFO_PRESENT), nullptr, nullptr, &startup.StartupInfo, &info))
        {
            WriteLine(L"create: " + Error(GetLastError()));
            return false;
        }
        child.Process = info.hProcess;
        child.Thread = info.hThread;
        child.ProcessId = info.dwProcessId;
        return true;
    }

    DWORD WaitExitCode(HANDLE process)
    {
        WaitForSingleObject(process, 60000);
        DWORD code = 0;
        GetExitCodeProcess(process, &code);
        return code;
    }

    HANDLE OpenInheritable(const wchar_t* file)
    {
        SECURITY_ATTRIBUTES inherit{ sizeof(inherit), nullptr, TRUE };
        return CreateFileW(file, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, &inherit, OPEN_EXISTING, 0, nullptr);
    }

    /// Single-attribute PROC_THREAD_ATTRIBUTE_LIST.
    class AttributeList final
    {
    public:
        AttributeList()
        {
            SIZE_T bytes = 0;
            InitializeProcThreadAttributeList(nullptr, 1, 0, &bytes);
            _storage.resize(bytes);
            _ready = InitializeProcThreadAttributeList(Get(), 1, 0, &bytes) != FALSE;
        }
        ~AttributeList()
        {
            if (_ready)
            {
                DeleteProcThreadAttributeList(Get());
            }
        }

        LPPROC_THREAD_ATTRIBUTE_LIST Get() { return reinterpret_cast<LPPROC_THREAD_ATTRIBUTE_LIST>(_storage.data()); }

        bool Set(DWORD_PTR attribute, void* value, SIZE_T bytes)
        {
            return _ready && UpdateProcThreadAttribute(Get(), 0, attribute, value, bytes, nullptr, nullptr) != FALSE;
        }

    private:
        std::vector<BYTE> _storage;
        bool _ready = false;
    };

    /// --child suspended-close <virtual file>: creates a child suspended that inherits a virtual handle,
    /// closes the handle before resuming it; the child must still see the virtual name.
    int ChildSuspendedClose(const wchar_t* virtualFile)
    {
        const HANDLE handle = OpenInheritable(virtualFile);
        if (handle == INVALID_HANDLE_VALUE)
        {
            WriteLine(L"open: " + Error(GetLastError()));
            return 1;
        }
        ChildProcess child;
        if (!StartProbe(L"", L"--handle-name " + HandleText(handle), TRUE, CREATE_SUSPENDED, nullptr, child))
        {
            return 1;
        }
        CloseHandle(handle);
        ResumeThread(child.Thread);
        return static_cast<int>(WaitExitCode(child.Process));
    }

    /// --write-handle <handle> <text>: writes text at the current position of an inherited handle, then closes it.
    int WriteHandle(const wchar_t* value, const wchar_t* text)
    {
        const HANDLE handle = ParseHandleValue(value);
        std::string bytes;
        for (const wchar_t* c = text; *c != 0; ++c)
        {
            bytes.push_back(static_cast<char>(*c));
        }
        DWORD written = 0;
        if (!WriteFile(handle, bytes.data(), static_cast<DWORD>(bytes.size()), &written, nullptr))
        {
            WriteLine(L"child write: " + Error(GetLastError()));
            return 1;
        }
        return CloseHandle(handle) ? 0 : 1;
    }

    /// --child inherit-write <virtual file>: creates the file for writing on an inheritable handle, writes "parent",
    /// starts a child suspended that inherits it, closes its own handle, then lets the child append "+child" and
    /// close. Both writes must reach machine B (the child holds the write token after the parent closed).
    int ChildInheritWrite(const wchar_t* virtualFile)
    {
        SECURITY_ATTRIBUTES inherit{ sizeof(inherit), nullptr, TRUE };
        const HANDLE handle = CreateFileW(virtualFile, GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, &inherit, CREATE_ALWAYS, 0, nullptr);
        if (handle == INVALID_HANDLE_VALUE)
        {
            WriteLine(L"open: " + Error(GetLastError()));
            return 1;
        }
        DWORD written = 0;
        if (!WriteFile(handle, "parent", 6, &written, nullptr))
        {
            WriteLine(L"write: " + Error(GetLastError()));
            return 1;
        }
        ChildProcess child;
        if (!StartProbe(L"", L"--write-handle " + HandleText(handle) + L" +child", TRUE, CREATE_SUSPENDED, nullptr, child))
        {
            return 1;
        }
        CloseHandle(handle);
        ResumeThread(child.Thread);
        const DWORD code = WaitExitCode(child.Process);
        WriteLine(L"child: " + std::to_wstring(code));
        return static_cast<int>(code);
    }

    /// --child handle-list|parent-process <virtual file> <real file>: the child does not inherit the
    /// virtual handle (a handle list without it, or another parent process), so it must not receive
    /// its entry: the same value in the child, holding the real file, must show the real name. Prints
    /// "child: <exit code of --handle-is>".
    int ChildWithoutVirtualHandle(bool useHandleList, const wchar_t* virtualFile, const wchar_t* realFile)
    {
        // Push the virtual handle above the values the child uses at startup.
        std::vector<HANDLE> padding;
        for (int i = 0; i < 256; ++i)
        {
            padding.push_back(CreateEventW(nullptr, TRUE, FALSE, nullptr));
        }
        const HANDLE handle = OpenInheritable(virtualFile);
        if (handle == INVALID_HANDLE_VALUE)
        {
            WriteLine(L"open: " + Error(GetLastError()));
            return 1;
        }
        AttributeList attributes;
        SECURITY_ATTRIBUTES inherit{ sizeof(inherit), nullptr, TRUE };
        HANDLE listed = CreateEventW(&inherit, TRUE, FALSE, nullptr);
        HANDLE parent = nullptr;
        bool ready = false;
        if (useHandleList)
        {
            ready = attributes.Set(PROC_THREAD_ATTRIBUTE_HANDLE_LIST, &listed, sizeof(listed));
        }
        else
        {
            PROCESS_BASIC_INFORMATION basic{};
            if (NT_SUCCESS(NtQueryInformationProcess(GetCurrentProcess(), ProcessBasicInformation, &basic, sizeof(basic), nullptr)))
            {
                parent = OpenProcess(PROCESS_CREATE_PROCESS | PROCESS_DUP_HANDLE, FALSE,
                    static_cast<DWORD>(reinterpret_cast<ULONG_PTR>(basic.Reserved3)));
            }
            ready = parent != nullptr && attributes.Set(PROC_THREAD_ATTRIBUTE_PARENT_PROCESS, &parent, sizeof(parent));
        }
        if (!ready)
        {
            WriteLine(L"attributes: " + Error(GetLastError()));
            return 1;
        }
        ChildProcess child;
        if (!StartProbe(L"", L"--handle-is " + HandleText(handle) + L" \"" + realFile + L"\"", TRUE,
            CREATE_SUSPENDED | CREATE_NO_WINDOW, attributes.Get(), child))
        {
            return 1;
        }
        // Give the child a handle to the real file at the value of the virtual handle, without a hooked
        // open in the child (an open there would replace any stale entry).
        const HANDLE real = OpenRead(realFile);
        std::vector<HANDLE> extra;
        bool placed = false;
        for (int attempt = 0; attempt < 4096 && !placed; ++attempt)
        {
            HANDLE value = nullptr;
            if (!DuplicateHandle(GetCurrentProcess(), real, child.Process, &value, 0, FALSE, DUPLICATE_SAME_ACCESS))
            {
                break;
            }
            placed = value == handle;
            if (!placed)
            {
                extra.push_back(value);
            }
        }
        for (HANDLE value : extra)
        {
            DuplicateHandle(child.Process, value, nullptr, nullptr, 0, FALSE, DUPLICATE_CLOSE_SOURCE);
        }
        CloseHandle(real);
        if (!placed)
        {
            TerminateProcess(child.Process, 1);
            WriteLine(L"value not placed");
            return 1;
        }
        ResumeThread(child.Thread);
        WriteLine(L"child: " + Num(WaitExitCode(child.Process)));
        CloseHandle(handle);
        CloseHandle(listed);
        if (parent != nullptr)
        {
            CloseHandle(parent);
        }
        for (HANDLE event : padding)
        {
            CloseHandle(event);
        }
        return 0;
    }

    /// --child alert-resume <virtual file>: creates a child suspended and resumes it with
    /// NtAlertResumeThread; the child must be hooked.
    int ChildAlertResume(const wchar_t* virtualFile)
    {
        using NtAlertResumeThreadFn = NTSTATUS(NTAPI*)(HANDLE, PULONG);
        const auto alertResume = reinterpret_cast<NtAlertResumeThreadFn>(
            GetProcAddress(GetModuleHandleW(L"ntdll.dll"), "NtAlertResumeThread"));
        ChildProcess child;
        if (!StartProbe(L"", std::wstring(L"--final-name \"") + virtualFile + L"\"", FALSE, CREATE_SUSPENDED, nullptr, child))
        {
            return 1;
        }
        ULONG previous = 0;
        alertResume(child.Thread, &previous);
        return static_cast<int>(WaitExitCode(child.Process));
    }

    struct ConcurrentResume
    {
        HANDLE Start;
        HANDLE Thread;
    };

    DWORD WINAPI ConcurrentResumeWorker(LPVOID parameter)
    {
        auto* resume = static_cast<ConcurrentResume*>(parameter);
        WaitForSingleObject(resume->Start, INFINITE);
        ResumeThread(resume->Thread);
        return 0;
    }

    /// --child double-resume <virtual file>: two threads resume a child created suspended at the same
    /// time; neither may let it run before it is hooked. Repeated a few times.
    int ChildDoubleResume(const wchar_t* virtualFile)
    {
        for (int round = 0; round < 5; ++round)
        {
            ChildProcess child;
            if (!StartProbe(L"", std::wstring(L"--final-name \"") + virtualFile + L"\"", FALSE, CREATE_SUSPENDED, nullptr, child))
            {
                return 1;
            }
            ConcurrentResume resume{ CreateEventW(nullptr, TRUE, FALSE, nullptr), child.Thread };
            HANDLE threads[2];
            for (HANDLE& thread : threads)
            {
                thread = CreateThread(nullptr, 0, ConcurrentResumeWorker, &resume, 0, nullptr);
            }
            Sleep(50);
            SetEvent(resume.Start);
            WaitForMultipleObjects(2, threads, TRUE, INFINITE);
            for (HANDLE thread : threads)
            {
                CloseHandle(thread);
            }
            CloseHandle(resume.Start);
            WaitExitCode(child.Process);
        }
        return 0;
    }

    /// --child pending-exit: children created suspended and terminated before any resume must not keep
    /// a handle in the hook's pending list. Prints the growth of this process's handle count.
    int ChildPendingExit()
    {
        auto round = [](int count)
        {
            std::vector<ChildProcess> children(count);
            for (ChildProcess& child : children)
            {
                StartProbe(L"", L"--final-name nul", FALSE, CREATE_SUSPENDED, nullptr, child);
            }
            for (ChildProcess& child : children)
            {
                if (child.Process != nullptr)
                {
                    TerminateProcess(child.Process, 1);
                    WaitForSingleObject(child.Process, 10000);
                }
            }
        };
        round(4);
        Sleep(1000);
        DWORD before = 0;
        GetProcessHandleCount(GetCurrentProcess(), &before);
        round(20);
        Sleep(2000);
        DWORD after = 0;
        GetProcessHandleCount(GetCurrentProcess(), &after);
        WriteLine(L"growth: " + Num(static_cast<long long>(after) - static_cast<long long>(before)));
        return 0;
    }

    struct SystemHandleEntryEx
    {
        PVOID Object;
        ULONG_PTR UniqueProcessId;
        ULONG_PTR HandleValue;
        ULONG GrantedAccess;
        USHORT CreatorBackTraceIndex;
        USHORT ObjectTypeIndex;
        ULONG HandleAttributes;
        ULONG Reserved;
    };

    struct SystemHandleInfoEx
    {
        ULONG_PTR NumberOfHandles;
        ULONG_PTR Reserved;
        SystemHandleEntryEx Handles[1];
    };

    /// Number of process and section handles held by processId (types found from this process's own
    /// handles in the same snapshot). -1 when the snapshot fails.
    int CountProcessAndSectionHandles(DWORD processId, HANDLE ownProcess, HANDLE ownSection, std::vector<BYTE>& buffer)
    {
        constexpr SYSTEM_INFORMATION_CLASS SystemExtendedHandleInformation = static_cast<SYSTEM_INFORMATION_CLASS>(64);
        NTSTATUS status = STATUS_INFO_LENGTH_MISMATCH;
        for (int attempt = 0; attempt < 8 && status == STATUS_INFO_LENGTH_MISMATCH; ++attempt)
        {
            ULONG needed = 0;
            status = NtQuerySystemInformation(SystemExtendedHandleInformation, buffer.data(), static_cast<ULONG>(buffer.size()), &needed);
            if (status == STATUS_INFO_LENGTH_MISMATCH)
            {
                buffer.resize((needed > buffer.size() ? needed : buffer.size()) * 2);
            }
        }
        if (!NT_SUCCESS(status))
        {
            return -1;
        }
        const auto* info = reinterpret_cast<const SystemHandleInfoEx*>(buffer.data());
        const ULONG_PTR self = GetCurrentProcessId();
        USHORT processType = 0xFFFF;
        USHORT sectionType = 0xFFFF;
        for (ULONG_PTR i = 0; i < info->NumberOfHandles; ++i)
        {
            const SystemHandleEntryEx& entry = info->Handles[i];
            if (entry.UniqueProcessId == self && entry.HandleValue == reinterpret_cast<ULONG_PTR>(ownProcess))
            {
                processType = entry.ObjectTypeIndex;
            }
            if (entry.UniqueProcessId == self && entry.HandleValue == reinterpret_cast<ULONG_PTR>(ownSection))
            {
                sectionType = entry.ObjectTypeIndex;
            }
        }
        int count = 0;
        for (ULONG_PTR i = 0; i < info->NumberOfHandles; ++i)
        {
            const SystemHandleEntryEx& entry = info->Handles[i];
            if (entry.UniqueProcessId == processId && (entry.ObjectTypeIndex == processType || entry.ObjectTypeIndex == sectionType))
            {
                ++count;
            }
        }
        return count;
    }

    struct OtherArchitectureRun
    {
        const wchar_t* Probe;
        const wchar_t* VirtualFile;
        volatile LONG Done;
    };

    DWORD WINAPI OtherArchitectureWorker(LPVOID parameter)
    {
        auto* run = static_cast<OtherArchitectureRun*>(parameter);
        for (int i = 0; i < 4; ++i)
        {
            ChildProcess child;
            if (StartProbe(run->Probe, std::wstring(L"--final-name \"") + run->VirtualFile + L"\"", FALSE, CREATE_NO_WINDOW,
                nullptr, child))
            {
                WaitExitCode(child.Process);
            }
        }
        InterlockedExchange(&run->Done, 1);
        return 0;
    }

    /// --child sibling-leak <other architecture probe> <virtual file>: while one thread starts children
    /// of the other architecture (injected through the helper), another creates children that inherit
    /// every inheritable handle; those must never receive the helper's process or section handle.
    /// Prints "leaked: <count>".
    int ChildSiblingLeak(const wchar_t* otherProbe, const wchar_t* virtualFile)
    {
        const HANDLE ownProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, GetCurrentProcessId());
        const HANDLE ownSection = CreateFileMappingW(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE, 0, 1, nullptr);
        std::vector<BYTE> buffer(1 << 20);
        OtherArchitectureRun run{ otherProbe, virtualFile, 0 };
        const HANDLE worker = CreateThread(nullptr, 0, OtherArchitectureWorker, &run, 0, nullptr);
        int leaked = 0;
        int rounds = 0;
        while (InterlockedCompareExchange(&run.Done, 0, 0) == 0)
        {
            ChildProcess child;
            if (!StartProbe(L"", L"--final-name nul", TRUE, CREATE_SUSPENDED | CREATE_NO_WINDOW, nullptr, child))
            {
                break;
            }
            const int count = CountProcessAndSectionHandles(child.ProcessId, ownProcess, ownSection, buffer);
            if (count < 0)
            {
                WriteLine(L"snapshot failed");
                TerminateProcess(child.Process, 1);
                break;
            }
            leaked += count;
            ++rounds;
            TerminateProcess(child.Process, 1);
            WaitForSingleObject(child.Process, 10000);
        }
        WaitForSingleObject(worker, INFINITE);
        CloseHandle(worker);
        CloseHandle(ownSection);
        CloseHandle(ownProcess);
        std::fwprintf(stderr, L"rounds: %d\n", rounds);
        WriteLine(L"leaked: " + Num(leaked));
        return 0;
    }

    /// M3 write scenario (plan 12): creates <root> and changes files in it with the Win32 and NT write APIs. Every
    /// step prints its error code and what it observed, so a run on a real folder and one through a writable virtual
    /// folder print the same text. Times are only printed where the scenario set them.
    class WriteScenario final
    {
    public:
        explicit WriteScenario(const std::wstring& root) : _root(root), _output(root) {}

        int Run()
        {
            Step(L"mkdir", Result(CreateDirectoryW(_root.c_str(), nullptr)));
            CreateWriteAppendTruncate();
            RenameAndReplace();
            OverwriteAndSupersede();
            Directories();
            AttributesAndTimes();
            DeleteOnCloseAndDisposition();
            ShareModes();
            ReplaceFileLikeEditors();
            Listing();
            return 0;
        }

    private:
        std::wstring Path(const wchar_t* name) const { return _root + L"\\" + name; }

        void Step(const std::wstring& step, const std::wstring& text) const { _output.Line(L"Write." + step, text); }

        static std::wstring Result(BOOL ok) { return ok ? L"ok" : Error(GetLastError()); }

        HANDLE Open(const wchar_t* name, DWORD access, DWORD share, DWORD disposition, DWORD flags = FILE_ATTRIBUTE_NORMAL) const
        {
            return CreateFileW(Path(name).c_str(), access, share, nullptr, disposition, flags, nullptr);
        }

        static std::wstring WriteText(HANDLE handle, const char* text)
        {
            DWORD written = 0;
            const BOOL ok = WriteFile(handle, text, static_cast<DWORD>(strlen(text)), &written, nullptr);
            return ok ? L"wrote=" + Num(written) : Error(GetLastError());
        }

        /// Content and size of a file read through a new handle.
        std::wstring ReadAll(const wchar_t* name) const
        {
            Handle file(Open(name, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, OPEN_EXISTING));
            if (!file.IsValid())
            {
                return Error(GetLastError());
            }
            char buffer[256] = {};
            DWORD read = 0;
            if (!ReadFile(file.Get(), buffer, sizeof(buffer) - 1, &read, nullptr))
            {
                return Error(GetLastError());
            }
            LARGE_INTEGER size{};
            GetFileSizeEx(file.Get(), &size);
            std::wstring text;
            for (DWORD i = 0; i < read; ++i)
            {
                text += static_cast<wchar_t>(buffer[i]);
            }
            return L"size=" + Num(size.QuadPart) + L" text=[" + text + L"]";
        }

        std::wstring Attributes(const wchar_t* name) const
        {
            const DWORD attributes = GetFileAttributesW(Path(name).c_str());
            return attributes == INVALID_FILE_ATTRIBUTES ? Error(GetLastError()) : L"attr=" + Hex(attributes & ~FILE_ATTRIBUTE_ARCHIVE);
        }

        void CreateFileWithText(const wchar_t* name, const char* text) const
        {
            Handle file(Open(name, GENERIC_WRITE, 0, CREATE_NEW));
            Step(std::wstring(L"create ") + name, file.IsValid() ? WriteText(file.Get(), text) : Error(GetLastError()));
        }

        void CreateWriteAppendTruncate()
        {
            CreateFileWithText(L"a.txt", "hello");
            Handle again(Open(L"a.txt", GENERIC_WRITE, 0, CREATE_NEW));
            Step(L"create-new existing", again.IsValid() ? L"opened" : Error(GetLastError()));
            {
                Handle append(Open(L"a.txt", FILE_APPEND_DATA | SYNCHRONIZE, FILE_SHARE_READ, OPEN_EXISTING));
                Step(L"append", append.IsValid() ? WriteText(append.Get(), " world") : Error(GetLastError()));
            }
            Step(L"read appended", ReadAll(L"a.txt"));
            {
                Handle truncate(Open(L"a.txt", GENERIC_WRITE, 0, OPEN_EXISTING));
                LARGE_INTEGER position{};
                position.QuadPart = 5;
                const BOOL ok = truncate.IsValid() && SetFilePointerEx(truncate.Get(), position, nullptr, FILE_BEGIN) && SetEndOfFile(truncate.Get());
                Step(L"truncate", Result(ok));
            }
            Step(L"read truncated", ReadAll(L"a.txt"));
            {
                // Grow through FileEndOfFileInformation, then write past the old end.
                Handle grow(Open(L"a.txt", GENERIC_WRITE, 0, OPEN_EXISTING));
                FILE_END_OF_FILE_INFO end{};
                end.EndOfFile.QuadPart = 8;
                Step(L"set end of file", Result(grow.IsValid() && SetFileInformationByHandle(grow.Get(), FileEndOfFileInfo, &end, sizeof(end))));
            }
            Step(L"read grown", ReadAll(L"a.txt").substr(0, 6));
        }

        void RenameAndReplace()
        {
            Step(L"rename a->b", Result(MoveFileExW(Path(L"a.txt").c_str(), Path(L"b.txt").c_str(), 0)));
            Step(L"old name", Attributes(L"a.txt"));
            Step(L"read b", ReadAll(L"b.txt").substr(0, 6));
            CreateFileWithText(L"c.txt", "ccc");
            Step(L"rename onto existing", Result(MoveFileExW(Path(L"b.txt").c_str(), Path(L"c.txt").c_str(), 0)));
            Step(L"rename replace", Result(MoveFileExW(Path(L"b.txt").c_str(), Path(L"c.txt").c_str(), MOVEFILE_REPLACE_EXISTING)));
            Step(L"read c", ReadAll(L"c.txt").substr(0, 6));
        }

        void OverwriteAndSupersede()
        {
            {
                Handle file(Open(L"c.txt", GENERIC_WRITE, 0, CREATE_ALWAYS));
                const DWORD error = GetLastError();
                Step(L"create always", file.IsValid() ? L"last=" + Num(error) + L" " + WriteText(file.Get(), "new") : Error(error));
            }
            Step(L"read overwritten", ReadAll(L"c.txt"));
            {
                Handle file(Open(L"c.txt", GENERIC_WRITE, 0, TRUNCATE_EXISTING));
                Step(L"truncate existing", file.IsValid() ? WriteText(file.Get(), "tr") : Error(GetLastError()));
            }
            Step(L"read truncated existing", ReadAll(L"c.txt"));
            Handle missing(Open(L"none.txt", GENERIC_WRITE, 0, TRUNCATE_EXISTING));
            Step(L"truncate missing", missing.IsValid() ? L"opened" : Error(GetLastError()));

            const auto ntCreateFile = reinterpret_cast<NtCreateFileFn>(GetProcAddress(GetModuleHandleW(L"ntdll.dll"), "NtCreateFile"));
            UNICODE_STRING name{};
            const std::wstring ntPath = L"\\??\\" + Path(L"c.txt");
            name.Buffer = const_cast<PWSTR>(ntPath.c_str());
            name.Length = static_cast<USHORT>(ntPath.size() * sizeof(wchar_t));
            name.MaximumLength = name.Length;
            OBJECT_ATTRIBUTES attributes{};
            InitializeObjectAttributes(&attributes, &name, OBJ_CASE_INSENSITIVE, nullptr, nullptr);
            IO_STATUS_BLOCK io{};
            HANDLE raw = nullptr;
            constexpr ULONG FileSupersede = 0;
            constexpr ULONG SynchronousNonAlert = 0x20;
            const NTSTATUS status = ntCreateFile(&raw, GENERIC_WRITE | SYNCHRONIZE, &attributes, &io, nullptr, FILE_ATTRIBUTE_NORMAL, 0,
                FileSupersede, SynchronousNonAlert | 0x40 /* FILE_NON_DIRECTORY_FILE */, nullptr, 0);
            Handle superseded(NT_SUCCESS(status) ? raw : INVALID_HANDLE_VALUE);
            Step(L"supersede", Status(status) + L" info=" + Num(static_cast<long long>(io.Information))
                + (superseded.IsValid() ? L" " + WriteText(superseded.Get(), "sup") : L""));
            superseded.Reset();
            Step(L"read superseded", ReadAll(L"c.txt"));
        }

        void Directories()
        {
            Step(L"mkdir dir", Result(CreateDirectoryW(Path(L"dir").c_str(), nullptr)));
            Step(L"mkdir existing", Result(CreateDirectoryW(Path(L"dir").c_str(), nullptr)));
            Step(L"mkdir missing parent", Result(CreateDirectoryW(Path(L"nope\\dir").c_str(), nullptr)));
            CreateFileWithText(L"dir\\f.txt", "f");
            Step(L"rmdir not empty", Result(RemoveDirectoryW(Path(L"dir").c_str())));
            Step(L"rename dir", Result(MoveFileExW(Path(L"dir").c_str(), Path(L"dir2").c_str(), 0)));
            Step(L"read moved child", ReadAll(L"dir2\\f.txt"));
            Step(L"delete child", Result(DeleteFileW(Path(L"dir2\\f.txt").c_str())));
            Step(L"delete missing", Result(DeleteFileW(Path(L"dir2\\f.txt").c_str())));
            Step(L"rmdir", Result(RemoveDirectoryW(Path(L"dir2").c_str())));
            Step(L"rmdir gone", Attributes(L"dir2"));
            Step(L"mkdir kept", Result(CreateDirectoryW(Path(L"kept").c_str(), nullptr)));
        }

        void AttributesAndTimes()
        {
            Step(L"set hidden+readonly", Result(SetFileAttributesW(Path(L"c.txt").c_str(), FILE_ATTRIBUTE_HIDDEN | FILE_ATTRIBUTE_READONLY)));
            Step(L"attributes", Attributes(L"c.txt"));
            Step(L"delete readonly", Result(DeleteFileW(Path(L"c.txt").c_str())));
            Handle write(Open(L"c.txt", GENERIC_WRITE, 0, OPEN_EXISTING));
            Step(L"open readonly for write", write.IsValid() ? L"opened" : Error(GetLastError()));
            Step(L"set normal", Result(SetFileAttributesW(Path(L"c.txt").c_str(), FILE_ATTRIBUTE_NORMAL)));
            Step(L"attributes normal", Attributes(L"c.txt"));

            FILETIME created{};
            FILETIME written{};
            ULARGE_INTEGER value{};
            value.QuadPart = 130000000000000000ull; // 2012
            created.dwLowDateTime = value.LowPart;
            created.dwHighDateTime = value.HighPart;
            value.QuadPart = 131000000000000000ull; // 2016
            written.dwLowDateTime = value.LowPart;
            written.dwHighDateTime = value.HighPart;
            {
                Handle file(Open(L"c.txt", FILE_WRITE_ATTRIBUTES, FILE_SHARE_READ, OPEN_EXISTING));
                Step(L"set times", Result(file.IsValid() && SetFileTime(file.Get(), &created, nullptr, &written)));
            }
            WIN32_FILE_ATTRIBUTE_DATA data{};
            Step(L"times by name", GetFileAttributesExW(Path(L"c.txt").c_str(), GetFileExInfoStandard, &data)
                ? L"created=" + Time(data.ftCreationTime) + L" written=" + Time(data.ftLastWriteTime) : Error(GetLastError()));
            Handle file(Open(L"c.txt", FILE_READ_ATTRIBUTES, FILE_SHARE_READ, OPEN_EXISTING));
            FILETIME c{};
            FILETIME a{};
            FILETIME w{};
            Step(L"times by handle", file.IsValid() && GetFileTime(file.Get(), &c, &a, &w) ? L"created=" + Time(c) + L" written=" + Time(w)
                                                                                            : Error(GetLastError()));
        }

        void DeleteOnCloseAndDisposition()
        {
            {
                Handle temp(Open(L"t.tmp", GENERIC_WRITE | DELETE, FILE_SHARE_READ | FILE_SHARE_DELETE, CREATE_NEW, FILE_FLAG_DELETE_ON_CLOSE));
                Step(L"delete-on-close create", temp.IsValid() ? WriteText(temp.Get(), "tmp") : Error(GetLastError()));
                Step(L"delete-on-close while open", Attributes(L"t.tmp"));
            }
            Step(L"delete-on-close after close", Attributes(L"t.tmp"));

            CreateFileWithText(L"e.txt", "e");
            {
                Handle file(Open(L"e.txt", DELETE, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, OPEN_EXISTING));
                FILE_DISPOSITION_INFO disposition{};
                disposition.DeleteFile = TRUE;
                Step(L"disposition", Result(file.IsValid() && SetFileInformationByHandle(file.Get(), FileDispositionInfo, &disposition, sizeof(disposition))));
            }
            Step(L"disposition after close", Attributes(L"e.txt"));
            CreateFileWithText(L"kept\\k.txt", "k");
            {
                Handle dir(CreateFileW(Path(L"kept").c_str(), DELETE, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr, OPEN_EXISTING,
                    FILE_FLAG_BACKUP_SEMANTICS, nullptr));
                FILE_DISPOSITION_INFO disposition{};
                disposition.DeleteFile = TRUE;
                Step(L"disposition non-empty dir", Result(dir.IsValid() && SetFileInformationByHandle(dir.Get(), FileDispositionInfo, &disposition, sizeof(disposition))));
            }
        }

        void ShareModes()
        {
            Handle reader(Open(L"c.txt", GENERIC_READ, FILE_SHARE_READ, OPEN_EXISTING));
            Step(L"share open reader", reader.IsValid() ? L"opened" : Error(GetLastError()));
            Handle writer(Open(L"c.txt", GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, OPEN_EXISTING));
            Step(L"share writer denied", writer.IsValid() ? L"opened" : Error(GetLastError()));
            Handle second(Open(L"c.txt", GENERIC_READ, FILE_SHARE_READ, OPEN_EXISTING));
            Step(L"share second reader", second.IsValid() ? L"opened" : Error(GetLastError()));
            Handle both(Open(L"s.txt", GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, CREATE_NEW));
            Handle other(Open(L"s.txt", GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, OPEN_EXISTING));
            Step(L"share two writers", both.IsValid() && other.IsValid() ? WriteText(both.Get(), "one") + L" " + WriteText(other.Get(), "TWO!")
                                                                       : Error(GetLastError()));
            both.Reset();
            other.Reset();
            Step(L"read two writers", ReadAll(L"s.txt"));
        }

        void ReplaceFileLikeEditors()
        {
            CreateFileWithText(L"d.txt", "version 1");
            CreateFileWithText(L"d.txt.tmp", "version 2");
            Step(L"replace file", Result(ReplaceFileW(Path(L"d.txt").c_str(), Path(L"d.txt.tmp").c_str(), nullptr, 0, nullptr, nullptr)));
            Step(L"read replaced", ReadAll(L"d.txt"));
            Step(L"replacement gone", Attributes(L"d.txt.tmp"));
            // Save through a temp file and a rename over the original (MOVEFILE_REPLACE_EXISTING).
            CreateFileWithText(L"d.txt.new", "version 3");
            Step(L"save by rename", Result(MoveFileExW(Path(L"d.txt.new").c_str(), Path(L"d.txt").c_str(), MOVEFILE_REPLACE_EXISTING)));
            Step(L"read saved", ReadAll(L"d.txt"));
        }

        void Listing()
        {
            WIN32_FIND_DATAW data{};
            HANDLE find = FindFirstFileW(Path(L"*").c_str(), &data);
            if (find == INVALID_HANDLE_VALUE)
            {
                Step(L"list", Error(GetLastError()));
                return;
            }
            std::vector<std::wstring> lines;
            do
            {
                if (wcscmp(data.cFileName, L".") == 0 || wcscmp(data.cFileName, L"..") == 0)
                {
                    continue;
                }
                const long long size = (static_cast<long long>(data.nFileSizeHigh) << 32) | data.nFileSizeLow;
                lines.push_back(std::wstring(data.cFileName) + L" size=" + Num(size) + L" attr=" + Hex(data.dwFileAttributes & ~FILE_ATTRIBUTE_ARCHIVE));
            } while (FindNextFileW(find, &data));
            FindClose(find);
            std::sort(lines.begin(), lines.end());
            for (const std::wstring& line : lines)
            {
                Step(L"list", line);
            }
        }

        std::wstring _root;
        ProbeOutput _output;
    };

    // ---- Change notifications (M7): ReadDirectoryChangesW and NtNotifyChangeDirectoryFile on <watched>, while the
    // files are made in <real> (machine B's folder behind the virtual one, or the same folder for the reference run).
    // Only the first entry of a notification is printed: the file system may add more (a modification) later. ----

    using NtNotifyChangeDirectoryFileFn = NTSTATUS(NTAPI*)(HANDLE, HANDLE, PIO_APC_ROUTINE, PVOID, PIO_STATUS_BLOCK, PVOID, ULONG,
        ULONG, BOOLEAN);

    constexpr DWORD NotifyTimeoutMs = 20000;
    constexpr DWORD NotifyChangeDelayMs = 700;

    struct DelayedCreate
    {
        std::wstring Path;
        DWORD DelayMs;
    };

    DWORD WINAPI DelayedCreateThread(PVOID context)
    {
        const auto* job = static_cast<const DelayedCreate*>(context);
        Sleep(job->DelayMs);
        const HANDLE file = CreateFileW(job->Path.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_NEW, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (file != INVALID_HANDLE_VALUE)
        {
            CloseHandle(file);
        }
        return 0;
    }

    /// Creates path (an empty file) after delayMs on a thread of its own; Join waits for it.
    class LaterCreate final
    {
    public:
        LaterCreate(std::wstring path, DWORD delayMs) : _job{ std::move(path), delayMs }
        {
            _thread = CreateThread(nullptr, 0, &DelayedCreateThread, &_job, 0, nullptr);
        }
        ~LaterCreate() { Join(); }
        LaterCreate(const LaterCreate&) = delete;
        LaterCreate& operator=(const LaterCreate&) = delete;

        void Join()
        {
            if (_thread != nullptr)
            {
                WaitForSingleObject(_thread, INFINITE);
                CloseHandle(_thread);
                _thread = nullptr;
            }
        }

    private:
        DelayedCreate _job;
        HANDLE _thread = nullptr;
    };

    std::wstring FirstNotifyEntry(const BYTE* buffer, DWORD bytes, bool extended)
    {
        if (bytes == 0)
        {
            return L"entries=0";
        }
        DWORD action = 0;
        DWORD nameBytes = 0;
        const wchar_t* name = nullptr;
        if (extended)
        {
            const auto* entry = reinterpret_cast<const FILE_NOTIFY_EXTENDED_INFORMATION*>(buffer);
            action = entry->Action;
            nameBytes = entry->FileNameLength;
            name = entry->FileName;
        }
        else
        {
            const auto* entry = reinterpret_cast<const FILE_NOTIFY_INFORMATION*>(buffer);
            action = entry->Action;
            nameBytes = entry->FileNameLength;
            name = entry->FileName;
        }
        return L"action=" + Num(action) + L" name=" + std::wstring(name, nameBytes / sizeof(wchar_t));
    }

    HANDLE OpenWatched(const std::wstring& directory, bool overlapped)
    {
        return CreateFileW(directory.c_str(), FILE_LIST_DIRECTORY, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr,
            OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS | (overlapped ? FILE_FLAG_OVERLAPPED : 0), nullptr);
    }

    constexpr DWORD NameFilter = FILE_NOTIFY_CHANGE_FILE_NAME | FILE_NOTIFY_CHANGE_DIR_NAME;

    void NotifySync(const std::wstring& watched, const std::wstring& real)
    {
        Handle directory(OpenWatched(watched, false));
        LaterCreate create(real + L"\\n-sync.txt", NotifyChangeDelayMs);
        alignas(8) BYTE buffer[4096];
        DWORD bytes = 0;
        const BOOL ok = ReadDirectoryChangesW(directory.Get(), buffer, sizeof(buffer), FALSE, NameFilter, &bytes, nullptr, nullptr);
        WriteLine(L"notify.sync: ok=" + Num(ok) + L" " + (ok ? FirstNotifyEntry(buffer, bytes, false) : Error(GetLastError())));
    }

    void NotifyEvent(const std::wstring& watched, const std::wstring& real, bool extended)
    {
        const std::wstring step = extended ? L"notify.ex" : L"notify.event";
        Handle directory(OpenWatched(watched, true));
        Handle event(CreateEventW(nullptr, TRUE, FALSE, nullptr));
        OVERLAPPED overlapped{};
        overlapped.hEvent = event.Get();
        alignas(8) BYTE buffer[4096];
        const BOOL issued = extended
            ? ReadDirectoryChangesExW(directory.Get(), buffer, sizeof(buffer), FALSE, NameFilter, nullptr, &overlapped, nullptr,
                ReadDirectoryNotifyExtendedInformation)
            : ReadDirectoryChangesW(directory.Get(), buffer, sizeof(buffer), FALSE, NameFilter, nullptr, &overlapped, nullptr);
        if (!issued)
        {
            WriteLine(step + L": issue " + Error(GetLastError()));
            return;
        }
        LaterCreate create(real + (extended ? L"\\n-ex.txt" : L"\\n-event.txt"), 0);
        if (WaitForSingleObject(event.Get(), NotifyTimeoutMs) != WAIT_OBJECT_0)
        {
            WriteLine(step + L": timeout");
            CancelIoEx(directory.Get(), &overlapped);
            DWORD ignored = 0;
            GetOverlappedResult(directory.Get(), &overlapped, &ignored, TRUE);
            return;
        }
        DWORD bytes = 0;
        const BOOL ok = GetOverlappedResult(directory.Get(), &overlapped, &bytes, FALSE);
        WriteLine(step + L": ok=" + Num(ok) + L" " + (ok ? FirstNotifyEntry(buffer, bytes, extended) : Error(GetLastError())));
    }

    /// The caller closes its event handle right after issuing the request: the system keeps the event object, so the
    /// request still signals it (the wait uses a second handle to the same event).
    void NotifyEventClosed(const std::wstring& watched, const std::wstring& real)
    {
        Handle directory(OpenWatched(watched, true));
        Handle event(CreateEventW(nullptr, TRUE, FALSE, nullptr));
        HANDLE waitCopy = nullptr;
        if (!DuplicateHandle(GetCurrentProcess(), event.Get(), GetCurrentProcess(), &waitCopy, 0, FALSE, DUPLICATE_SAME_ACCESS))
        {
            WriteLine(L"notify.eventClosed: duplicate " + Error(GetLastError()));
            return;
        }
        Handle waiter(waitCopy);
        IO_STATUS_BLOCK io{};
        alignas(8) BYTE buffer[4096];
        const auto notify = reinterpret_cast<NtNotifyChangeDirectoryFileFn>(
            GetProcAddress(GetModuleHandleW(L"ntdll.dll"), "NtNotifyChangeDirectoryFile"));
        const NTSTATUS issued = notify(directory.Get(), event.Get(), nullptr, nullptr, &io, buffer, sizeof(buffer), NameFilter, FALSE);
        event.Reset();
        if (issued != STATUS_PENDING)
        {
            WriteLine(L"notify.eventClosed: issued=" + Status(issued));
            return;
        }
        LaterCreate create(real + L"\\n-eclosed.txt", 0);
        const DWORD waited = WaitForSingleObject(waiter.Get(), NotifyTimeoutMs);
        if (waited != WAIT_OBJECT_0)
        {
            WriteLine(L"notify.eventClosed: timeout");
            CancelIoEx(directory.Get(), nullptr);
            WaitForSingleObject(directory.Get(), 0);
            Sleep(200);
            return;
        }
        WriteLine(L"notify.eventClosed: signalled=1 " + Status(io.Status));
    }

    struct ApcResult
    {
        bool Done;
        DWORD Error;
        DWORD Bytes;
    };
    ApcResult g_apcResult{};

    VOID CALLBACK NotifyCompletionRoutine(DWORD error, DWORD bytes, LPOVERLAPPED)
    {
        g_apcResult = { true, error, bytes };
    }

    void NotifyApc(const std::wstring& watched, const std::wstring& real)
    {
        Handle directory(OpenWatched(watched, true));
        OVERLAPPED overlapped{};
        alignas(8) BYTE buffer[4096];
        g_apcResult = {};
        if (!ReadDirectoryChangesW(directory.Get(), buffer, sizeof(buffer), FALSE, NameFilter, nullptr, &overlapped,
                &NotifyCompletionRoutine))
        {
            WriteLine(L"notify.apc: issue " + Error(GetLastError()));
            return;
        }
        LaterCreate create(real + L"\\n-apc.txt", 0);
        const ULONGLONG deadline = GetTickCount64() + NotifyTimeoutMs;
        while (!g_apcResult.Done && GetTickCount64() < deadline)
        {
            SleepEx(100, TRUE);
        }
        if (!g_apcResult.Done)
        {
            WriteLine(L"notify.apc: timeout");
            CancelIoEx(directory.Get(), &overlapped);
            SleepEx(1000, TRUE);
            return;
        }
        WriteLine(L"notify.apc: error=" + Num(g_apcResult.Error) + L" " + FirstNotifyEntry(buffer, g_apcResult.Bytes, false));
    }

    void NotifyCompletionPort(const std::wstring& watched, const std::wstring& real)
    {
        constexpr ULONG_PTR Key = 0x1234;
        Handle directory(OpenWatched(watched, true));
        const HANDLE port = CreateIoCompletionPort(directory.Get(), nullptr, Key, 0);
        if (port == nullptr)
        {
            WriteLine(L"notify.iocp: port " + Error(GetLastError()));
            return;
        }
        OVERLAPPED overlapped{};
        alignas(8) BYTE buffer[4096];
        if (!ReadDirectoryChangesW(directory.Get(), buffer, sizeof(buffer), FALSE, NameFilter, nullptr, &overlapped, nullptr))
        {
            WriteLine(L"notify.iocp: issue " + Error(GetLastError()));
            CloseHandle(port);
            return;
        }
        LaterCreate create(real + L"\\n-iocp.txt", 0);
        DWORD bytes = 0;
        ULONG_PTR key = 0;
        LPOVERLAPPED completed = nullptr;
        const BOOL ok = GetQueuedCompletionStatus(port, &bytes, &key, &completed, NotifyTimeoutMs);
        if (completed == nullptr)
        {
            WriteLine(L"notify.iocp: timeout");
            CancelIoEx(directory.Get(), &overlapped);
            GetQueuedCompletionStatus(port, &bytes, &key, &completed, NotifyTimeoutMs);
        }
        else
        {
            WriteLine(L"notify.iocp: ok=" + Num(ok) + L" key=" + Hex(key) + L" overlapped=" + Num(completed == &overlapped) + L" "
                + FirstNotifyEntry(buffer, bytes, false));
        }
        create.Join();
        directory.Reset();
        CloseHandle(port);
    }

    /// NtNotifyChangeDirectoryFile with a 4 byte buffer: no entry fits.
    void NotifySmallBuffer(const std::wstring& watched, const std::wstring& real)
    {
        const auto notify = reinterpret_cast<NtNotifyChangeDirectoryFileFn>(
            GetProcAddress(GetModuleHandleW(L"ntdll.dll"), "NtNotifyChangeDirectoryFile"));
        Handle directory(OpenWatched(watched, true));
        Handle event(CreateEventW(nullptr, TRUE, FALSE, nullptr));
        IO_STATUS_BLOCK io{};
        DWORD buffer = 0;
        const NTSTATUS issued = notify(directory.Get(), event.Get(), nullptr, nullptr, &io, &buffer, sizeof(buffer), NameFilter, FALSE);
        LaterCreate create(real + L"\\n-small.txt", 0);
        if (issued == STATUS_PENDING && WaitForSingleObject(event.Get(), NotifyTimeoutMs) != WAIT_OBJECT_0)
        {
            WriteLine(L"notify.small: timeout");
            CancelIoEx(directory.Get(), nullptr);
            return;
        }
        WriteLine(L"notify.small: issued=" + Status(issued) + L" " + Status(io.Status) + L" information=" + Num(static_cast<long long>(io.Information)));
    }

    /// cancelEx: CancelIoEx on the request; cancel: CancelIo; close: CloseHandle while the request waits.
    void NotifyEnd(const std::wstring& watched, const std::wstring& how)
    {
        Handle directory(OpenWatched(watched, true));
        Handle event(CreateEventW(nullptr, TRUE, FALSE, nullptr));
        OVERLAPPED overlapped{};
        overlapped.hEvent = event.Get();
        alignas(8) BYTE buffer[4096];
        if (!ReadDirectoryChangesW(directory.Get(), buffer, sizeof(buffer), FALSE, NameFilter, nullptr, &overlapped, nullptr))
        {
            WriteLine(L"notify." + how + L": issue " + Error(GetLastError()));
            return;
        }
        BOOL ended = TRUE;
        if (how == L"cancelEx")
        {
            ended = CancelIoEx(directory.Get(), &overlapped);
        }
        else if (how == L"cancel")
        {
            ended = CancelIo(directory.Get());
        }
        else
        {
            directory.Reset();
        }
        const DWORD waited = WaitForSingleObject(event.Get(), NotifyTimeoutMs);
        WriteLine(L"notify." + how + L": ended=" + Num(ended) + L" signalled=" + Num(waited == WAIT_OBJECT_0) + L" "
            + Status(static_cast<NTSTATUS>(overlapped.Internal)));
    }

    struct SyncWait
    {
        HANDLE Directory;
        BOOL Ok;
        DWORD Error;
    };

    DWORD WINAPI SyncWaitThread(PVOID context)
    {
        auto* wait = static_cast<SyncWait*>(context);
        alignas(8) BYTE buffer[4096];
        DWORD bytes = 0;
        wait->Ok = ReadDirectoryChangesW(wait->Directory, buffer, sizeof(buffer), FALSE, NameFilter, &bytes, nullptr, nullptr);
        wait->Error = wait->Ok ? 0 : GetLastError();
        return 0;
    }

    /// CancelSynchronousIo on a thread blocked in a synchronous ReadDirectoryChangesW.
    void NotifySyncCancel(const std::wstring& watched)
    {
        Handle directory(OpenWatched(watched, false));
        SyncWait wait{ directory.Get(), FALSE, 0 };
        const HANDLE thread = CreateThread(nullptr, 0, &SyncWaitThread, &wait, 0, nullptr);
        BOOL cancelled = FALSE;
        // The thread may not have started its wait yet: retry until something was cancelled.
        for (int i = 0; i < 100 && !cancelled; ++i)
        {
            Sleep(100);
            cancelled = CancelSynchronousIo(thread);
        }
        const DWORD waited = WaitForSingleObject(thread, NotifyTimeoutMs);
        WriteLine(L"notify.syncCancel: cancelled=" + Num(cancelled) + L" finished=" + Num(waited == WAIT_OBJECT_0) + L" ok="
            + Num(wait.Ok) + L" " + Error(wait.Error));
        if (waited != WAIT_OBJECT_0)
        {
            TerminateThread(thread, 1);
        }
        CloseHandle(thread);
    }

    void NotifyTree(const std::wstring& watched, const std::wstring& real)
    {
        Handle directory(OpenWatched(watched, true));
        Handle event(CreateEventW(nullptr, TRUE, FALSE, nullptr));
        OVERLAPPED overlapped{};
        overlapped.hEvent = event.Get();
        alignas(8) BYTE buffer[4096];
        if (!ReadDirectoryChangesW(directory.Get(), buffer, sizeof(buffer), TRUE, NameFilter, nullptr, &overlapped, nullptr))
        {
            WriteLine(L"notify.tree: issue " + Error(GetLastError()));
            return;
        }
        LaterCreate create(real + L"\\sub\\n-tree.txt", 0);
        if (WaitForSingleObject(event.Get(), NotifyTimeoutMs) != WAIT_OBJECT_0)
        {
            WriteLine(L"notify.tree: timeout");
            CancelIoEx(directory.Get(), &overlapped);
            return;
        }
        DWORD bytes = 0;
        const BOOL ok = GetOverlappedResult(directory.Get(), &overlapped, &bytes, FALSE);
        WriteLine(L"notify.tree: ok=" + Num(ok) + L" " + (ok ? FirstNotifyEntry(buffer, bytes, false) : Error(GetLastError())));
    }

    /// --notify <watched> <real>: every notify case in turn, one line each.
    int NotifyScenario(const wchar_t* watchedArgument, const wchar_t* realArgument)
    {
        const std::wstring watched = watchedArgument;
        const std::wstring real = realArgument;
        NotifySync(watched, real);
        NotifyEvent(watched, real, false);
        NotifyEvent(watched, real, true);
        NotifyEventClosed(watched, real);
        NotifyApc(watched, real);
        NotifyCompletionPort(watched, real);
        NotifyTree(watched, real);
        NotifySmallBuffer(watched, real);
        NotifyEnd(watched, L"cancelEx");
        NotifyEnd(watched, L"cancel");
        NotifyEnd(watched, L"close");
        NotifySyncCancel(watched);
        return 0;
    }

    int WriteScenarioCommand(const wchar_t* rootArgument)
    {
        wchar_t full[MAX_PATH];
        const DWORD length = GetFullPathNameW(rootArgument, MAX_PATH, full, nullptr);
        if (length == 0 || length >= MAX_PATH)
        {
            return 2;
        }
        return WriteScenario(std::wstring(full, length)).Run();
    }

    int RunCommand(int argc, wchar_t** argv)
    {
        const std::wstring command = argv[1];
        if (command == L"--handle-name" && argc == 3)
        {
            return HandleName(argv[2]);
        }
        if (command == L"--final-name" && argc == 3)
        {
            return FinalName(argv[2]);
        }
        if (command == L"--handle-is" && argc == 4)
        {
            return HandleIs(argv[2], argv[3]);
        }
        if (command == L"--child" && argc >= 3)
        {
            const std::wstring mode = argv[2];
            if (mode == L"suspended-close" && argc == 4)
            {
                return ChildSuspendedClose(argv[3]);
            }
            if (mode == L"inherit-write" && argc == 4)
            {
                return ChildInheritWrite(argv[3]);
            }
            if ((mode == L"handle-list" || mode == L"parent-process") && argc == 5)
            {
                return ChildWithoutVirtualHandle(mode == L"handle-list", argv[3], argv[4]);
            }
            if (mode == L"alert-resume" && argc == 4)
            {
                return ChildAlertResume(argv[3]);
            }
            if (mode == L"double-resume" && argc == 4)
            {
                return ChildDoubleResume(argv[3]);
            }
            if (mode == L"pending-exit" && argc == 3)
            {
                return ChildPendingExit();
            }
            if (mode == L"sibling-leak" && argc == 5)
            {
                return ChildSiblingLeak(argv[3], argv[4]);
            }
        }
        if (command == L"--write-handle" && argc == 4)
        {
            return WriteHandle(argv[2], argv[3]);
        }
        if (command == L"--stale-handle" && argc == 4)
        {
            return StaleHandle(argv[2], argv[3]);
        }
        if (command == L"--failed-close" && argc == 3)
        {
            return FailedClose(argv[2]);
        }
        if (command == L"--delete" && argc == 3)
        {
            return DeleteByName(argv[2]);
        }
        if (command == L"--stdin-name" && argc == 2)
        {
            return StdinName();
        }
        if (command == L"--dir-classes" && argc == 3)
        {
            return DirectoryClasses(argv[2]);
        }
        if (command == L"--stdin-id" && argc == 2)
        {
            return StdinIdentity();
        }
        if (command == L"--file-id" && argc == 3)
        {
            return FileIdentity(argv[2]);
        }
        if (command == L"--write" && argc == 3)
        {
            return WriteScenarioCommand(argv[2]);
        }
        if (command == L"--notify" && argc == 4)
        {
            return NotifyScenario(argv[2], argv[3]);
        }
        if (command == L"--threads" && argc == 4)
        {
            return ThreadedHandles(argv[2], argv[3]);
        }
        std::fputs("ProcBridge.NativeProbe: unknown command\n", stderr);
        return 2;
    }
}

int wmain(int argc, wchar_t** argv)
{
    if (argc >= 2 && argv[1][0] == L'-' && argv[1][1] == L'-')
    {
        return RunCommand(argc, argv);
    }
    if (argc != 2)
    {
        std::fputs("Usage: ProcBridge.NativeProbe <root>\n", stderr);
        return 2;
    }
    wchar_t full[MAX_PATH];
    const DWORD length = GetFullPathNameW(argv[1], MAX_PATH, full, nullptr);
    if (length == 0 || length >= MAX_PATH)
    {
        std::fputs("ProcBridge.NativeProbe: invalid root\n", stderr);
        return 2;
    }
    std::wstring root(full, length);
    while (root.size() > 3 && root.back() == L'\\')
    {
        root.pop_back();
    }
    const ProbeOutput output(root);
    Probe probe(root, output);
    probe.Run();
    return 0;
}

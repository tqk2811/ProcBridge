// NativeProbe: runs a fixed read-only scenario of file APIs against a root folder and prints one line
// per observation (plan section 12). Every path is printed with the root replaced by <ROOT>, so a run
// on the real folder and a run through the virtual folder must print exactly the same text.
//
// Usage: ProcBridge.NativeProbe.<arch>.exe <root>
//        ProcBridge.NativeProbe.<arch>.exe --<command> <arguments>   (single checks, see RunCommand)
// Expected tree under <root> (created by the test): a.txt, sub\b.txt, sub\deep\c.bin, empty\,
// ro.txt (read-only), unicode file name. Last access times are never printed (they drift), nor the times
// of ".." entries (the parent of a virtual root is not part of the virtual folder).
#define WIN32_NO_STATUS
#include <windows.h>
#undef WIN32_NO_STATUS
#include <ntstatus.h>
#include <winternl.h>
#include <algorithm>
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

    int RunCommand(int argc, wchar_t** argv)
    {
        const std::wstring command = argv[1];
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

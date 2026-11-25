#include "RemoteHandles.h"
#include "BrokerClient.h"
#include "PathString.h"
#include <cstring>
#include <new>

extern "C"
{
    NTSYSAPI BOOLEAN NTAPI RtlIsNameInExpression(PUNICODE_STRING expression, PUNICODE_STRING name, BOOLEAN ignoreCase, PWCH upcaseTable);
    NTSYSAPI NTSTATUS NTAPI RtlUpcaseUnicodeString(PUNICODE_STRING destination, PCUNICODE_STRING source, BOOLEAN allocate);
}

namespace ProcBridge::Hook
{
    namespace
    {
        /// Where the fields of one FILE_*_INFORMATION directory class live. All classes but FileNamesInformation
        /// share the FILE_DIRECTORY_INFORMATION head (times at 8..39, sizes at 40/48, attributes at 56, name length
        /// at 60). Offsets follow ntifs.h; -1 = field absent.
        struct ClassLayout
        {
            ULONG Class;
            ULONG NameOffset;   // FileName, i.e. size of the fixed part
            int EaSize;
            int ReparseTag;
            int FileId64;
            int FileId128;
            int ShortNameLength; // CCHAR, ShortName[12] follows at +2
        };

        constexpr ClassLayout Layouts[] = {
            { 1, 64, -1, -1, -1, -1, -1 },   // FileDirectoryInformation
            { 2, 68, 64, -1, -1, -1, -1 },   // FileFullDirectoryInformation
            { 3, 94, 64, -1, -1, -1, 68 },   // FileBothDirectoryInformation
            { 12, 12, -1, -1, -1, -1, -1 },  // FileNamesInformation (name length at 8)
            { 37, 104, 64, -1, 96, -1, 68 }, // FileIdBothDirectoryInformation
            { 38, 80, 64, -1, 72, -1, -1 },  // FileIdFullDirectoryInformation
            { 60, 88, 64, 68, -1, 72, -1 },  // FileIdExtdDirectoryInformation
            { 63, 114, 64, 68, -1, 72, 88 }, // FileIdExtdBothDirectoryInformation
            { 81, 80, 64, 68, 72, -1, -1 },  // FileId64ExtdDirectoryInformation
            { 82, 106, 64, 68, 72, -1, 80 }, // FileId64ExtdBothDirectoryInformation
            { 83, 96, 64, 68, 72, 80, -1 },  // FileIdAllExtdDirectoryInformation
            { 84, 122, 64, 68, 72, 80, 96 }, // FileIdAllExtdBothDirectoryInformation
        };

        const ClassLayout* FindLayout(ULONG informationClass) noexcept
        {
            for (const ClassLayout& layout : Layouts)
            {
                if (layout.Class == informationClass)
                {
                    return &layout;
                }
            }
            return nullptr;
        }

        /// One entry kept by a scan: FileInfo, name length, name text, padded to 8 bytes.
        struct Record
        {
            HookIpc::FileInfo Info;
            ULONG NameBytes;
            ULONG Size; // whole record, multiple of 8
            // WCHAR Name[] follows
            const wchar_t* Name() const noexcept { return reinterpret_cast<const wchar_t*>(this + 1); }
        };
        static_assert(sizeof(Record) % 8 == 0);

        template <typename T>
        void Store(BYTE* entry, int offset, const T& value) noexcept
        {
            if (offset >= 0)
            {
                memcpy(entry + offset, &value, sizeof(value));
            }
        }

        ULONG AlignUp8(ULONG value) noexcept
        {
            return (value + 7) & ~7UL;
        }

        /// Process-heap byte buffer that only grows.
        class Blob final
        {
        public:
            ~Blob() { Clear(); }

            bool Append(const void* data, ULONG bytes) noexcept
            {
                if (_size + bytes > _capacity)
                {
                    ULONG capacity = _capacity == 0 ? 4096 : _capacity * 2;
                    while (capacity < _size + bytes)
                    {
                        capacity *= 2;
                    }
                    void* grown = _data == nullptr ? HeapAlloc(GetProcessHeap(), 0, capacity)
                                                   : HeapReAlloc(GetProcessHeap(), 0, _data, capacity);
                    if (grown == nullptr)
                    {
                        return false;
                    }
                    _data = static_cast<BYTE*>(grown);
                    _capacity = capacity;
                }
                if (bytes > 0)
                {
                    memcpy(_data + _size, data, bytes);
                }
                _size += bytes;
                return true;
            }

            void Clear() noexcept
            {
                if (_data != nullptr)
                {
                    HeapFree(GetProcessHeap(), 0, _data);
                }
                _data = nullptr;
                _size = 0;
                _capacity = 0;
            }

            const BYTE* Data() const noexcept { return _data; }
            ULONG Size() const noexcept { return _size; }

        private:
            BYTE* _data = nullptr;
            ULONG _size = 0;
            ULONG _capacity = 0;
        };

        bool AppendRecord(Blob& records, const HookIpc::FileInfo& info, const void* name, ULONG nameBytes) noexcept
        {
            Record record{};
            record.Info = info;
            record.NameBytes = nameBytes;
            record.Size = AlignUp8(static_cast<ULONG>(sizeof(Record)) + nameBytes);
            const BYTE padding[8] = {};
            return records.Append(&record, sizeof(record)) && records.Append(name, nameBytes)
                && records.Append(padding, record.Size - static_cast<ULONG>(sizeof(Record)) - nameBytes);
        }
    }

    struct RemoteHandleTable::State
    {
        HANDLE Handle;
        int Mapping;
        UNICODE_STRING Path;
        HookIpc::FileInfo Self; // machine B's metadata; for a directory also the "." entry
        bool Directory;
        SRWLOCK Lock;
        bool Loaded;
        bool ReturnedAny;   // a query of this scan already completed (since the last restart)
        ULONG Cursor;       // byte offset of the next record
        Blob Records;
        UNICODE_STRING Pattern; // upcased; empty = match all

        void Destroy() noexcept
        {
            FreeString(Path);
            FreeString(Pattern);
            Records.Clear();
            this->~State();
            HeapFree(GetProcessHeap(), 0, this);
        }

        /// Takes a new pattern (copied, upcased). "*" and an empty pattern match everything.
        NTSTATUS SetPattern(const UNICODE_STRING* fileName) noexcept
        {
            FreeString(Pattern);
            if (fileName == nullptr || fileName->Length == 0
                || (fileName->Length == sizeof(wchar_t) && fileName->Buffer[0] == L'*'))
            {
                return STATUS_SUCCESS;
            }
            if (!AllocateString(Pattern, fileName->Length))
            {
                return STATUS_NO_MEMORY;
            }
            UNICODE_STRING upcased = Pattern;
            upcased.MaximumLength = fileName->Length;
            if (!NT_SUCCESS(RtlUpcaseUnicodeString(&upcased, fileName, FALSE)))
            {
                FreeString(Pattern);
                return STATUS_OBJECT_NAME_INVALID;
            }
            Pattern.Length = upcased.Length;
            return STATUS_SUCCESS;
        }

        bool Matches(const Record& record) const noexcept
        {
            if (Pattern.Length == 0)
            {
                return true;
            }
            UNICODE_STRING name{};
            name.Buffer = const_cast<wchar_t*>(record.Name());
            name.Length = static_cast<USHORT>(record.NameBytes);
            name.MaximumLength = name.Length;
            UNICODE_STRING pattern = Pattern;
            return RtlIsNameInExpression(&pattern, &name, TRUE, nullptr) != FALSE;
        }

        /// Fetches the whole listing from the resolver: ".", "..", then machine B's entries.
        NTSTATUS Load(const IPathResolver& resolver) noexcept
        {
            Records.Clear();
            Cursor = 0;
            Loaded = false;
            HookIpc::FileInfo parent = Self;
            const PathMapping& root = resolver.GetMapping(Mapping);
            if (Path.Length > root.VirtualNt.Length)
            {
                UNICODE_STRING parentPath = Path;
                while (parentPath.Length > 0 && parentPath.Buffer[parentPath.Length / sizeof(wchar_t) - 1] != L'\\')
                {
                    parentPath.Length -= sizeof(wchar_t);
                }
                parentPath.Length -= parentPath.Length > 0 ? sizeof(wchar_t) : 0;
                if (parentPath.Length >= root.VirtualNt.Length)
                {
                    HookIpc::FileInfo info{};
                    if (NT_SUCCESS(resolver.Stat(Mapping, parentPath, PathKind::Nt, info)))
                    {
                        parent = info;
                    }
                }
            }
            // The virtual root's parent is not on machine B: ".." of the root repeats the root's metadata.
            if (!AppendRecord(Records, Self, L".", sizeof(wchar_t)) || !AppendRecord(Records, parent, L"..", 2 * sizeof(wchar_t)))
            {
                return STATUS_NO_MEMORY;
            }
            HeapBuffer page(HookIpc::MaxMessageSize);
            if (!page)
            {
                return STATUS_NO_MEMORY;
            }
            uint32_t start = 0;
            while (true)
            {
                DirectoryPage result{};
                const NTSTATUS status = resolver.List(Mapping, Path, start, page.Get(), result);
                if (!NT_SUCCESS(status))
                {
                    return status;
                }
                MessageReader reader(result.Entries, result.Bytes);
                for (USHORT i = 0; i < result.Count; ++i)
                {
                    HookIpc::FileInfo info{};
                    const BYTE* name = nullptr;
                    USHORT chars = 0;
                    reader.Read(&info, sizeof(info)); // validated by List
                    reader.ReadString(name, chars);
                    if (!AppendRecord(Records, info, name, chars * static_cast<ULONG>(sizeof(wchar_t))))
                    {
                        return STATUS_NO_MEMORY;
                    }
                }
                if (!result.HasMore || result.Count == 0)
                {
                    break;
                }
                start += result.Count;
            }
            Loaded = true;
            return STATUS_SUCCESS;
        }
    };

    RemoteHandleTable::~RemoteHandleTable()
    {
        // Runs at DLL detach; hooks may still be called afterwards (NtClose from other destructors), so leave
        // the table empty rather than dangling.
        const size_t count = _count.exchange(0);
        for (size_t i = 0; i < count; ++i)
        {
            _entries[i]->Destroy();
        }
        if (_entries != nullptr)
        {
            HeapFree(GetProcessHeap(), 0, _entries);
        }
        _entries = nullptr;
        _capacity = 0;
    }

    void RemoteHandleTable::Add(HANDLE handle, int mapping, const UNICODE_STRING& virtualNtPath, const HookIpc::FileInfo& info) noexcept
    {
        void* memory = HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, sizeof(State));
        if (memory == nullptr)
        {
            return;
        }
        State* state = new (memory) State{};
        state->Handle = handle;
        state->Mapping = mapping;
        state->Self = info;
        state->Directory = (info.Attributes & FILE_ATTRIBUTE_DIRECTORY) != 0;
        InitializeSRWLock(&state->Lock);
        // Kernelbase opens directories with a trailing '\' ("dir\" for FindFirstFile("dir\*")).
        UNICODE_STRING path = virtualNtPath;
        while (path.Length > 0 && path.Buffer[path.Length / sizeof(wchar_t) - 1] == L'\\')
        {
            path.Length -= sizeof(wchar_t);
        }
        if (!ConcatString(state->Path, path, nullptr, 0))
        {
            state->Destroy();
            return;
        }
        Remove(handle);
        AcquireSRWLockExclusive(&_lock);
        size_t count = _count.load(std::memory_order_relaxed);
        if (count == _capacity)
        {
            const size_t capacity = _capacity == 0 ? 16 : _capacity * 2;
            void* grown = _entries == nullptr ? HeapAlloc(GetProcessHeap(), 0, capacity * sizeof(State*))
                                              : HeapReAlloc(GetProcessHeap(), 0, _entries, capacity * sizeof(State*));
            if (grown == nullptr)
            {
                ReleaseSRWLockExclusive(&_lock);
                state->Destroy();
                return;
            }
            _entries = static_cast<State**>(grown);
            _capacity = capacity;
        }
        _entries[count] = state;
        _count.store(count + 1, std::memory_order_release);
        ReleaseSRWLockExclusive(&_lock);
    }

    void RemoteHandleTable::Remove(HANDLE handle) noexcept
    {
        if (_count.load(std::memory_order_acquire) == 0)
        {
            return;
        }
        State* removed = nullptr;
        AcquireSRWLockExclusive(&_lock);
        const size_t count = _count.load(std::memory_order_relaxed);
        for (size_t i = 0; i < count; ++i)
        {
            if (_entries[i]->Handle == handle)
            {
                removed = _entries[i];
                _entries[i] = _entries[count - 1];
                _count.store(count - 1, std::memory_order_release);
                break;
            }
        }
        ReleaseSRWLockExclusive(&_lock);
        if (removed != nullptr)
        {
            // Wait for a query still running on it (it holds the state lock).
            AcquireSRWLockExclusive(&removed->Lock);
            ReleaseSRWLockExclusive(&removed->Lock);
            removed->Destroy();
        }
    }

    RemoteHandleTable::State* RemoteHandleTable::Lock(HANDLE handle) const noexcept
    {
        if (_count.load(std::memory_order_acquire) == 0)
        {
            return nullptr;
        }
        State* found = nullptr;
        AcquireSRWLockShared(&_lock);
        const size_t count = _count.load(std::memory_order_relaxed);
        for (size_t i = 0; i < count; ++i)
        {
            if (_entries[i]->Handle == handle)
            {
                found = _entries[i];
                // Taken before the table lock is released, so Remove cannot free it in between.
                AcquireSRWLockExclusive(&found->Lock);
                break;
            }
        }
        ReleaseSRWLockShared(&_lock);
        return found;
    }

    bool RemoteHandleTable::GetInfo(HANDLE handle, HookIpc::FileInfo& info) const noexcept
    {
        State* state = Lock(handle);
        if (state == nullptr)
        {
            return false;
        }
        info = state->Self;
        ReleaseSRWLockExclusive(&state->Lock);
        return true;
    }

    bool RemoteHandleTable::GetDirectoryPath(HANDLE handle, int& mapping, UNICODE_STRING& path) const noexcept
    {
        State* state = Lock(handle);
        if (state == nullptr)
        {
            return false;
        }
        mapping = state->Mapping;
        const bool copied = state->Directory && ConcatString(path, state->Path, nullptr, 0);
        ReleaseSRWLockExclusive(&state->Lock);
        return copied;
    }

    bool RemoteHandleTable::Query(HANDLE handle, const IPathResolver& resolver, const DirectoryQuery& query, NTSTATUS& status,
        ULONG& written) noexcept
    {
        State* state = Lock(handle);
        if (state == nullptr)
        {
            return false;
        }
        if (!state->Directory)
        {
            // A file: the system answers (STATUS_INVALID_PARAMETER) on the cache copy.
            ReleaseSRWLockExclusive(&state->Lock);
            return false;
        }
        written = 0;
        status = STATUS_SUCCESS;
        const ClassLayout* layout = FindLayout(query.InformationClass);
        if (layout == nullptr)
        {
            status = STATUS_INVALID_INFO_CLASS;
        }
        else if (query.Length < layout->NameOffset)
        {
            status = STATUS_INFO_LENGTH_MISMATCH;
        }
        else
        {
            // The pattern is fixed by the first query of a scan, a restart may give a new one.
            if (!state->Loaded || query.RestartScan)
            {
                if (!state->Loaded || query.FileName != nullptr)
                {
                    status = state->SetPattern(query.FileName);
                }
                state->ReturnedAny = false;
                if (NT_SUCCESS(status))
                {
                    status = state->Load(resolver);
                }
            }
        }
        if (NT_SUCCESS(status))
        {
            constexpr ULONG NoEntry = 0xFFFFFFFF;
            const bool namesOnly = layout->Class == 12;
            const int nameLengthOffset = namesOnly ? 8 : 60;
            ULONG previous = NoEntry;
            const BYTE* records = state->Records.Data();
            while (state->Cursor < state->Records.Size())
            {
                const auto* record = reinterpret_cast<const Record*>(records + state->Cursor);
                if (!state->Matches(*record))
                {
                    state->Cursor += record->Size;
                    continue;
                }
                const ULONG start = AlignUp8(written);
                const ULONG fixed = layout->NameOffset;
                if (start + fixed + record->NameBytes > query.Length || start < written)
                {
                    if (previous == NoEntry)
                    {
                        // Not even the first entry fits: partial name, entry stays for the next call.
                        if (start + fixed <= query.Length)
                        {
                            BYTE* entry = query.Output + start;
                            memset(entry, 0, fixed);
                            Store(entry, nameLengthOffset, record->NameBytes);
                            const ULONG partial = query.Length - start - fixed;
                            memcpy(entry + fixed, record->Name(), partial & ~1UL);
                            written = start + fixed + (partial & ~1UL);
                        }
                        status = STATUS_BUFFER_OVERFLOW;
                    }
                    break;
                }
                BYTE* entry = query.Output + start;
                memset(entry, 0, fixed);
                if (!namesOnly)
                {
                    const HookIpc::FileInfo& info = record->Info;
                    Store(entry, 8, info.CreationTime);
                    Store(entry, 16, info.LastAccessTime);
                    Store(entry, 24, info.LastWriteTime);
                    Store(entry, 32, info.ChangeTime);
                    Store(entry, 40, info.Size);
                    Store(entry, 48, info.AllocationSize);
                    Store(entry, 56, info.Attributes);
                    Store(entry, layout->FileId64, info.FileId);
                    Store(entry, layout->FileId128, info.FileId); // low 64 bits, high bits stay 0
                }
                Store(entry, nameLengthOffset, record->NameBytes);
                memcpy(entry + fixed, record->Name(), record->NameBytes);
                if (previous != NoEntry)
                {
                    const ULONG next = start - previous;
                    memcpy(query.Output + previous, &next, sizeof(next));
                }
                previous = start;
                written = start + fixed + record->NameBytes;
                state->Cursor += record->Size;
                if (query.ReturnSingleEntry)
                {
                    break;
                }
            }
            if (status == STATUS_SUCCESS && previous == NoEntry)
            {
                status = state->ReturnedAny ? STATUS_NO_MORE_FILES : STATUS_NO_SUCH_FILE;
                written = 0;
            }
            // Only the first query of a scan reports "no such file"; later ones report the end.
            state->ReturnedAny = true;
        }
        ReleaseSRWLockExclusive(&state->Lock);
        return true;
    }
}

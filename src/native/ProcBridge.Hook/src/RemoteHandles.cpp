#include "RemoteHandles.h"
#include "BrokerClient.h"
#include "DirectoryEntries.h"
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

        ULONG AlignUp8(ULONG value) noexcept
        {
            return AlignDirectoryEntry(value);
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
        ULONG64 Generation;
        // One reference held by the table, one by each Lock in progress; the last one destroys the state.
        LONG References;
        bool Removed;       // set under the state lock once the table dropped it
        int Mapping;
        UNICODE_STRING Path;
        HookIpc::FileInfo Self; // machine B's metadata; for a directory also the "." entry
        bool Directory;
        uint64_t WriteToken;    // Broker write token (M3), 0 = none
        bool DeleteOnClose;
        bool Orphaned;          // replaced by a rename: no name on machine B any more
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

    void RemoteHandleTable::Release(State* state) noexcept
    {
        if (InterlockedDecrement(&state->References) == 0)
        {
            state->Destroy();
        }
    }

    void RemoteHandleTable::Unlock(State* state) noexcept
    {
        ReleaseSRWLockExclusive(&state->Lock);
        Release(state);
    }

    RemoteHandleTable::~RemoteHandleTable()
    {
        // Runs at DLL detach; hooks may still be called afterwards (NtClose from other destructors), so leave
        // the table empty rather than dangling.
        const size_t count = _count.exchange(0);
        for (size_t i = 0; i < count; ++i)
        {
            Release(_entries[i]);
        }
        if (_entries != nullptr)
        {
            HeapFree(GetProcessHeap(), 0, _entries);
        }
        _entries = nullptr;
        _capacity = 0;
    }

    void RemoteHandleTable::RetainToken(uint64_t token) noexcept
    {
        for (size_t i = 0; i < _tokenCount; ++i)
        {
            if (_tokens[i].Token == token)
            {
                ++_tokens[i].Count;
                return;
            }
        }
        if (_tokenCount == _tokenCapacity)
        {
            const size_t capacity = _tokenCapacity == 0 ? 8 : _tokenCapacity * 2;
            void* grown = _tokens == nullptr ? HeapAlloc(GetProcessHeap(), 0, capacity * sizeof(TokenCount))
                                             : HeapReAlloc(GetProcessHeap(), 0, _tokens, capacity * sizeof(TokenCount));
            if (grown == nullptr)
            {
                return; // uncounted: ReleaseToken then reports the first close as the last one
            }
            _tokens = static_cast<TokenCount*>(grown);
            _tokenCapacity = capacity;
        }
        _tokens[_tokenCount++] = { token, 1 };
    }

    bool RemoteHandleTable::HoldsToken(uint64_t token) const noexcept
    {
        bool held = false;
        AcquireSRWLockShared(&_lock);
        for (size_t i = 0; i < _tokenCount && !held; ++i)
        {
            held = _tokens[i].Token == token;
        }
        ReleaseSRWLockShared(&_lock);
        return held;
    }

    bool RemoteHandleTable::ReleaseToken(uint64_t token) noexcept
    {
        for (size_t i = 0; i < _tokenCount; ++i)
        {
            if (_tokens[i].Token == token)
            {
                if (--_tokens[i].Count > 0)
                {
                    return false;
                }
                _tokens[i] = _tokens[--_tokenCount];
                return true;
            }
        }
        return true;
    }

    void RemoteHandleTable::Add(HANDLE handle, int mapping, const UNICODE_STRING& virtualNtPath, const HookIpc::FileInfo& info,
        uint64_t writeToken, bool deleteOnClose) noexcept
    {
        void* memory = HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, sizeof(State));
        if (memory == nullptr)
        {
            return;
        }
        State* state = new (memory) State{};
        state->Handle = handle;
        state->References = 1;
        state->Mapping = mapping;
        state->Self = info;
        state->Directory = (info.Attributes & FILE_ATTRIBUTE_DIRECTORY) != 0;
        state->WriteToken = writeToken;
        state->DeleteOnClose = deleteOnClose;
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
        state->Generation = ++_nextGeneration;
        if (writeToken != 0)
        {
            RetainToken(writeToken);
        }
        _entries[count] = state;
        _count.store(count + 1, std::memory_order_release);
        ReleaseSRWLockExclusive(&_lock);
    }

    bool RemoteHandleTable::Find(HANDLE handle, ULONG64& generation) const noexcept
    {
        generation = 0;
        if (_count.load(std::memory_order_acquire) == 0)
        {
            return false;
        }
        AcquireSRWLockShared(&_lock);
        const size_t count = _count.load(std::memory_order_relaxed);
        for (size_t i = 0; i < count; ++i)
        {
            if (_entries[i]->Handle == handle)
            {
                generation = _entries[i]->Generation;
                break;
            }
        }
        ReleaseSRWLockShared(&_lock);
        return generation != 0;
    }

    void RemoteHandleTable::Remove(HANDLE handle, ULONG64 generation, RemovedRemoteHandle* result) noexcept
    {
        // Every NtClose comes here: look under the shared lock first, most handles are not tracked.
        ULONG64 found = 0;
        if (!Find(handle, found) || (generation != 0 && found != generation))
        {
            return;
        }
        State* removed = nullptr;
        AcquireSRWLockExclusive(&_lock);
        const size_t count = _count.load(std::memory_order_relaxed);
        for (size_t i = 0; i < count; ++i)
        {
            if (_entries[i]->Handle == handle && (generation == 0 || _entries[i]->Generation == generation))
            {
                removed = _entries[i];
                _entries[i] = _entries[count - 1];
                _count.store(count - 1, std::memory_order_release);
                const bool last = removed->WriteToken != 0 && ReleaseToken(removed->WriteToken);
                if (result != nullptr)
                {
                    result->WriteToken = last ? removed->WriteToken : 0;
                    result->DeleteOnClose = removed->DeleteOnClose;
                    result->Mapping = removed->Mapping;
                    result->FileId = removed->Self.FileId;
                    if (removed->DeleteOnClose && !ConcatString(result->Path, removed->Path, nullptr, 0))
                    {
                        result->DeleteOnClose = false;
                    }
                }
                break;
            }
        }
        ReleaseSRWLockExclusive(&_lock);
        if (removed != nullptr)
        {
            // A query still running on it keeps a reference; it sees Removed once it gets the lock. Nothing waits
            // here for the state lock (a query may be waiting on the network while holding it).
            InterlockedExchange8(reinterpret_cast<volatile char*>(&removed->Removed), 1);
            Release(removed);
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
                InterlockedIncrement(&found->References);
                break;
            }
        }
        ReleaseSRWLockShared(&_lock);
        if (found == nullptr)
        {
            return nullptr;
        }
        AcquireSRWLockExclusive(&found->Lock);
        if (found->Removed)
        {
            Unlock(found); // closed while this thread waited
            return nullptr;
        }
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
        Unlock(state);
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
        Unlock(state);
        return copied;
    }

    uint64_t RemoteHandleTable::GetWriteToken(HANDLE handle) const noexcept
    {
        if (IsEmpty())
        {
            return 0;
        }
        State* state = Lock(handle);
        if (state == nullptr)
        {
            return 0;
        }
        const uint64_t token = state->WriteToken;
        Unlock(state);
        return token;
    }

    bool RemoteHandleTable::SetDeleteOnClose(HANDLE handle, bool deleteOnClose) noexcept
    {
        State* state = Lock(handle);
        if (state == nullptr)
        {
            return false;
        }
        state->DeleteOnClose = deleteOnClose;
        Unlock(state);
        return true;
    }

    void RemoteHandleTable::Update(HANDLE handle, const UNICODE_STRING* newPath, const HookIpc::FileInfo* info) noexcept
    {
        State* state = Lock(handle);
        if (state == nullptr)
        {
            return;
        }
        if (newPath != nullptr)
        {
            UNICODE_STRING copy{};
            if (ConcatString(copy, *newPath, nullptr, 0))
            {
                FreeString(state->Path);
                state->Path = copy;
            }
        }
        if (info != nullptr)
        {
            const uint32_t rangeId = state->Self.RangeId;
            state->Self = *info;
            state->Self.RangeId = rangeId;
        }
        Unlock(state);
    }

    void RemoteHandleTable::ClearRange(HANDLE handle) noexcept
    {
        State* state = Lock(handle);
        if (state == nullptr)
        {
            return;
        }
        state->Self.RangeId = 0;
        Unlock(state);
    }

    void RemoteHandleTable::OrphanPath(const UNICODE_STRING& path, HANDLE except) noexcept
    {
        AcquireSRWLockShared(&_lock);
        const size_t count = _count.load(std::memory_order_relaxed);
        for (size_t i = 0; i < count; ++i)
        {
            State* state = _entries[i];
            // Path and the flags are only changed under the state lock, which a directory query may hold while it
            // waits on the network; directories are never replaced by a rename, so skip them without waiting.
            if (state->Handle == except || state->Directory || state->Path.Length != path.Length)
            {
                continue;
            }
            if (TryAcquireSRWLockExclusive(&state->Lock))
            {
                if (CompareStringOrdinal(state->Path.Buffer, state->Path.Length / sizeof(wchar_t), path.Buffer,
                        path.Length / sizeof(wchar_t), TRUE) == CSTR_EQUAL)
                {
                    state->Orphaned = true;
                    state->DeleteOnClose = false;
                }
                ReleaseSRWLockExclusive(&state->Lock);
            }
        }
        ReleaseSRWLockShared(&_lock);
    }

    bool RemoteHandleTable::IsOrphaned(HANDLE handle) const noexcept
    {
        State* state = Lock(handle);
        if (state == nullptr)
        {
            return false;
        }
        const bool orphaned = state->Orphaned;
        Unlock(state);
        return orphaned;
    }

    bool RemoteHandleTable::GetEntry(HANDLE handle, int& mapping, UNICODE_STRING& path, HookIpc::FileInfo& info,
        uint64_t* writeToken, bool* deleteOnClose) const noexcept
    {
        State* state = Lock(handle);
        if (state == nullptr)
        {
            return false;
        }
        mapping = state->Mapping;
        info = state->Self;
        if (writeToken != nullptr)
        {
            *writeToken = state->WriteToken;
        }
        if (deleteOnClose != nullptr)
        {
            *deleteOnClose = state->DeleteOnClose;
        }
        const bool copied = ConcatString(path, state->Path, nullptr, 0);
        Unlock(state);
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
            Unlock(state);
            return false;
        }
        written = 0;
        status = STATUS_SUCCESS;
        const DirectoryEntryFormat format(query.InformationClass);
        if (!format.IsSupported())
        {
            status = STATUS_INVALID_INFO_CLASS;
        }
        else if (query.Length < format.NameOffset())
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
            const ULONG nameLengthOffset = format.NameLengthOffset();
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
                const ULONG fixed = format.NameOffset();
                if (start + fixed + record->NameBytes > query.Length || start < written)
                {
                    if (previous == NoEntry)
                    {
                        // Not even the first entry fits: partial name, entry stays for the next call.
                        if (start + fixed <= query.Length)
                        {
                            BYTE* entry = query.Output + start;
                            memset(entry, 0, fixed);
                            memcpy(entry + nameLengthOffset, &record->NameBytes, sizeof(record->NameBytes));
                            const ULONG partial = query.Length - start - fixed;
                            memcpy(entry + fixed, record->Name(), partial & ~1UL);
                            written = start + fixed + (partial & ~1UL);
                        }
                        status = STATUS_BUFFER_OVERFLOW;
                    }
                    break;
                }
                format.Write(query.Output + start, record->Info, record->Name(), record->NameBytes);
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
        Unlock(state);
        return true;
    }
}

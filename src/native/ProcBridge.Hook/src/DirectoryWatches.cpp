#include "DirectoryWatches.h"
#include "BrokerClient.h"
#include "CallerMemory.h"
#include "HookGuard.h"
#include <cstddef>

namespace ProcBridge::Hook
{
    namespace
    {
        constexpr ULONG DirectoryNotifyInformation = 1;
        constexpr ULONG DirectoryNotifyExtendedInformation = 2;
        constexpr ULONG FileNotifyValidMask = 0x00000FFF; // FILE_NOTIFY_VALID_MASK

        // The Broker reports every change of the directory (it does not know which attribute changed); the filter is
        // applied here per action: names need FILE_NOTIFY_CHANGE_FILE_NAME/DIR_NAME, a modification any other bit.
        constexpr ULONG NameFilterBits = FILE_NOTIFY_CHANGE_FILE_NAME | FILE_NOTIFY_CHANGE_DIR_NAME;
        constexpr ULONG StreamFilterBits = 0x00000200 | 0x00000400 | 0x00000800; // FILE_NOTIFY_CHANGE_STREAM_*

        bool PassesFilter(ULONG action, ULONG completionFilter) noexcept
        {
            switch (action)
            {
            case FILE_ACTION_ADDED:
            case FILE_ACTION_REMOVED:
            case FILE_ACTION_RENAMED_OLD_NAME:
            case FILE_ACTION_RENAMED_NEW_NAME:
                return (completionFilter & NameFilterBits) != 0;
            case FILE_ACTION_MODIFIED:
                return (completionFilter & ~NameFilterBits & ~StreamFilterBits) != 0;
            default:
                return (completionFilter & StreamFilterBits) != 0;
            }
        }

        constexpr ULONG AlignUp(ULONG value, ULONG alignment) noexcept
        {
            return (value + alignment - 1) & ~(alignment - 1);
        }

        HANDLE DuplicateLocal(HANDLE source) noexcept
        {
            HANDLE copy = nullptr;
            return DuplicateHandle(GetCurrentProcess(), source, GetCurrentProcess(), &copy, 0, FALSE, DUPLICATE_SAME_ACCESS)
                ? copy
                : nullptr;
        }

        void CloseLocal(HANDLE handle) noexcept
        {
            if (handle != nullptr)
            {
                CloseHandle(handle);
            }
        }
    }

    struct DirectoryWatches::Request
    {
        Request* Next;
        bool Queued;               // linked in its entry's queue (under _lock)
        HANDLE Event;
        PIO_APC_ROUTINE ApcRoutine;
        PVOID ApcContext;
        PIO_STATUS_BLOCK IoStatusBlock;
        PVOID Buffer;
        ULONG Length;
        ULONG InformationClass;
        DWORD ThreadId;
        HANDLE Thread;             // own copy, for the APC (nullptr without one)
        HANDLE Port;               // own copy of the completion port of the handle (nullptr when none)
        PVOID Key;
        bool Synchronous;
        HANDLE Done;               // synchronous only: signalled once Status is set
        NTSTATUS CancelStatus;     // non-zero: complete with it instead of waiting (under _lock)
        NTSTATUS Status;
    };

    struct DirectoryWatches::Entry
    {
        DirectoryWatches* Owner;
        volatile LONG References;
        HANDLE Handle;
        ULONG64 Generation;
        SRWLOCK RegisterLock;      // guards WatchId/Channel while the watch is registered
        uint32_t WatchId;
        ULONG CompletionFilter;    // of the call that registered the watch
        WatchChannel* Channel;     // used by the worker only once registered
        HANDLE Port;               // own copy (under _lock)
        PVOID Key;
        bool Closed;               // under _lock
        bool Running;              // a worker serves the queue (under _lock)
        HANDLE CancelEvent;        // abandons the worker's long poll
        Request* Head;             // under _lock
        BYTE* Reply;               // the worker's reply buffer
    };

    DWORD DirectoryWatches::Initialize(const IPathResolver& resolver) noexcept
    {
        const HMODULE ntdll = GetModuleHandleW(L"ntdll.dll");
        if (ntdll == nullptr)
        {
            return GetLastError();
        }
        _setIoCompletion = reinterpret_cast<NtSetIoCompletionFn>(GetProcAddress(ntdll, "NtSetIoCompletion"));
        _queueApcThread = reinterpret_cast<NtQueueApcThreadFn>(GetProcAddress(ntdll, "NtQueueApcThread"));
        if (_setIoCompletion == nullptr || _queueApcThread == nullptr)
        {
            return ERROR_PROC_NOT_FOUND;
        }
        _resolver = &resolver;
        return ERROR_SUCCESS;
    }

    DirectoryWatches::Entry* DirectoryWatches::Acquire(HANDLE handle, ULONG64 generation) noexcept
    {
        // Created before the lock: nothing that may call hooked functions runs under it.
        auto* created = static_cast<Entry*>(HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, sizeof(Entry)));
        if (created == nullptr)
        {
            return nullptr;
        }
        created->Owner = this;
        created->References = 2; // the table and the caller
        created->Handle = handle;
        created->Generation = generation;
        InitializeSRWLock(&created->RegisterLock);
        created->CancelEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        created->Reply = static_cast<BYTE*>(HeapAlloc(GetProcessHeap(), 0, HookIpc::MaxMessageSize));
        if (created->CancelEvent == nullptr || created->Reply == nullptr)
        {
            created->References = 1;
            Release(created);
            return nullptr;
        }

        Entry* found = nullptr;
        Entry* stale = nullptr;
        bool added = false;
        AcquireSRWLockExclusive(&_lock);
        for (size_t i = 0; i < _count.load(std::memory_order_relaxed); ++i)
        {
            if (_entries[i]->Handle == handle)
            {
                found = _entries[i];
                break;
            }
        }
        if (found != nullptr && found->Generation == generation)
        {
            InterlockedIncrement(&found->References);
        }
        else
        {
            if (found != nullptr)
            {
                stale = DetachLocked(handle);
            }
            found = nullptr;
            const size_t count = _count.load(std::memory_order_relaxed);
            if (count == _capacity)
            {
                const size_t capacity = _capacity == 0 ? 16 : _capacity * 2;
                void* grown = _entries == nullptr
                    ? HeapAlloc(GetProcessHeap(), 0, capacity * sizeof(Entry*))
                    : HeapReAlloc(GetProcessHeap(), 0, _entries, capacity * sizeof(Entry*));
                if (grown != nullptr)
                {
                    _entries = static_cast<Entry**>(grown);
                    _capacity = capacity;
                }
            }
            if (count < _capacity)
            {
                _entries[count] = created;
                _count.store(count + 1, std::memory_order_release);
                found = created;
                added = true;
            }
        }
        ReleaseSRWLockExclusive(&_lock);
        if (stale != nullptr)
        {
            Retire(stale);
        }
        if (!added)
        {
            created->References = 1;
            Release(created);
        }
        return found;
    }

    DirectoryWatches::Entry* DirectoryWatches::DetachLocked(HANDLE handle) noexcept
    {
        const size_t count = _count.load(std::memory_order_relaxed);
        for (size_t i = 0; i < count; ++i)
        {
            Entry* entry = _entries[i];
            if (entry->Handle == handle)
            {
                _entries[i] = _entries[count - 1];
                _count.store(count - 1, std::memory_order_release);
                entry->Closed = true;
                return entry;
            }
        }
        return nullptr;
    }

    void DirectoryWatches::Retire(Entry* entry) noexcept
    {
        // Closed is already set: the worker completes what waits with STATUS_NOTIFY_CLEANUP once woken.
        SetEvent(entry->CancelEvent);
        AcquireSRWLockShared(&entry->RegisterLock);
        const uint32_t watchId = entry->WatchId;
        ReleaseSRWLockShared(&entry->RegisterLock);
        if (watchId != 0)
        {
            // On a pooled instance: the channel may be busy with the long poll being abandoned.
            _resolver->Unwatch(nullptr, watchId);
        }
        Release(entry);
    }

    void DirectoryWatches::Release(Entry* entry) noexcept
    {
        if (InterlockedDecrement(&entry->References) != 0)
        {
            return;
        }
        if (entry->Channel != nullptr)
        {
            entry->Owner->_resolver->Unwatch(entry->Channel, 0); // closes the channel only
        }
        CloseLocal(entry->Port);
        CloseLocal(entry->CancelEvent);
        if (entry->Reply != nullptr)
        {
            HeapFree(GetProcessHeap(), 0, entry->Reply);
        }
        HeapFree(GetProcessHeap(), 0, entry);
    }

    void DirectoryWatches::FreeRequest(Request* request) noexcept
    {
        CloseLocal(request->Thread);
        CloseLocal(request->Port);
        CloseLocal(request->Done);
        HeapFree(GetProcessHeap(), 0, request);
    }

    NTSTATUS DirectoryWatches::Notify(const NotifyCall& call) noexcept
    {
        if (_resolver == nullptr)
        {
            return STATUS_NOT_SUPPORTED;
        }
        if (call.InformationClass != DirectoryNotifyInformation && call.InformationClass != DirectoryNotifyExtendedInformation)
        {
            return STATUS_INVALID_INFO_CLASS;
        }
        if (call.CompletionFilter == 0 || (call.CompletionFilter & ~FileNotifyValidMask) != 0)
        {
            return STATUS_INVALID_PARAMETER;
        }
        Entry* entry = Acquire(call.Handle, call.Generation);
        if (entry == nullptr)
        {
            return STATUS_INSUFFICIENT_RESOURCES;
        }

        // The first call registers the watch; later calls keep its filter, like the file system.
        NTSTATUS status = STATUS_SUCCESS;
        AcquireSRWLockExclusive(&entry->RegisterLock);
        if (entry->WatchId == 0)
        {
            WatchChannel* channel = nullptr;
            uint32_t watchId = 0;
            status = _resolver->Watch(call.Mapping, *call.Path, call.WatchTree, call.CompletionFilter, channel, watchId);
            if (NT_SUCCESS(status))
            {
                entry->WatchId = watchId;
                entry->CompletionFilter = call.CompletionFilter;
                entry->Channel = channel;
            }
        }
        ReleaseSRWLockExclusive(&entry->RegisterLock);
        if (!NT_SUCCESS(status))
        {
            Release(entry);
            return status;
        }

        auto* request = static_cast<Request*>(HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, sizeof(Request)));
        if (request == nullptr)
        {
            Release(entry);
            return STATUS_INSUFFICIENT_RESOURCES;
        }
        request->Event = call.Event;
        request->ApcRoutine = call.ApcRoutine;
        request->ApcContext = call.ApcContext;
        request->IoStatusBlock = call.IoStatusBlock;
        request->Buffer = call.Buffer;
        request->Length = call.Length;
        request->InformationClass = call.InformationClass;
        request->ThreadId = GetCurrentThreadId();
        request->Synchronous = call.Synchronous;

        AcquireSRWLockShared(&_lock);
        const HANDLE port = entry->Port;
        const PVOID key = entry->Key;
        // Our own copy of the port: valid until this request completes even if the binding changes.
        request->Port = port != nullptr ? DuplicateLocal(port) : nullptr;
        ReleaseSRWLockShared(&_lock);
        request->Key = key;
        if (port != nullptr && call.ApcRoutine != nullptr)
        {
            // The kernel refuses an APC on a handle bound to a completion port.
            FreeRequest(request);
            Release(entry);
            return STATUS_INVALID_PARAMETER;
        }
        if (call.ApcRoutine != nullptr)
        {
            request->Thread = DuplicateLocal(GetCurrentThread());
        }
        if (call.Synchronous)
        {
            request->Done = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        }
        if ((port != nullptr && request->Port == nullptr) || (call.ApcRoutine != nullptr && request->Thread == nullptr)
            || (call.Synchronous && request->Done == nullptr))
        {
            FreeRequest(request);
            Release(entry);
            return STATUS_INSUFFICIENT_RESOURCES;
        }
        // The I/O manager clears the event when the request starts.
        if (call.Event != nullptr && !ResetEvent(call.Event))
        {
            FreeRequest(request);
            Release(entry);
            return STATUS_INVALID_HANDLE;
        }

        bool start = false;
        AcquireSRWLockExclusive(&_lock);
        if (entry->Closed)
        {
            ReleaseSRWLockExclusive(&_lock);
            FreeRequest(request);
            Release(entry);
            return STATUS_INVALID_HANDLE;
        }
        Request** tail = &entry->Head;
        while (*tail != nullptr)
        {
            tail = &(*tail)->Next;
        }
        *tail = request;
        request->Queued = true;
        start = !entry->Running;
        entry->Running = true;
        ReleaseSRWLockExclusive(&_lock);

        if (start)
        {
            InterlockedIncrement(&entry->References); // the worker's
            if (!QueueUserWorkItem(&WorkerThunk, entry, WT_EXECUTELONGFUNCTION))
            {
                AcquireSRWLockExclusive(&_lock);
                entry->Head = nullptr; // only this request: nothing ran the queue yet
                request->Queued = false;
                entry->Running = false;
                ReleaseSRWLockExclusive(&_lock);
                InterlockedDecrement(&entry->References);
                FreeRequest(request);
                Release(entry);
                return STATUS_INSUFFICIENT_RESOURCES;
            }
        }
        if (!call.Synchronous)
        {
            Release(entry);
            return STATUS_PENDING;
        }

        // Synchronous handle: the caller waits on the file object in the kernel; here on the request.
        const DWORD waited = WaitForSingleObjectEx(request->Done, INFINITE, call.Alertable ? TRUE : FALSE);
        if (waited == WAIT_IO_COMPLETION)
        {
            // An APC ran: the wait ends and the request is cancelled.
            bool unlinked = false;
            AcquireSRWLockExclusive(&_lock);
            if (request->Queued && request->CancelStatus == 0)
            {
                request->CancelStatus = STATUS_CANCELLED;
                if (entry->Head == request)
                {
                    SetEvent(entry->CancelEvent); // the worker completes it
                }
                else
                {
                    Request** link = &entry->Head;
                    while (*link != request)
                    {
                        link = &(*link)->Next;
                    }
                    *link = request->Next;
                    request->Queued = false;
                    unlinked = true;
                }
            }
            ReleaseSRWLockExclusive(&_lock);
            if (unlinked)
            {
                Finish(entry, request, STATUS_CANCELLED, 0);
            }
            WaitForSingleObject(request->Done, INFINITE);
            status = STATUS_USER_APC;
        }
        else
        {
            status = request->Status;
        }
        FreeRequest(request);
        Release(entry);
        return status;
    }

    void DirectoryWatches::SetCompletionPort(HANDLE handle, ULONG64 generation, HANDLE port, PVOID key) noexcept
    {
        Entry* entry = Acquire(handle, generation);
        if (entry == nullptr)
        {
            return;
        }
        const HANDLE copy = port != nullptr ? DuplicateLocal(port) : nullptr;
        AcquireSRWLockExclusive(&_lock);
        const HANDLE old = entry->Port;
        entry->Port = copy;
        entry->Key = key;
        ReleaseSRWLockExclusive(&_lock);
        CloseLocal(old);
        Release(entry);
    }

    int DirectoryWatches::Cancel(HANDLE handle, DWORD threadId, PIO_STATUS_BLOCK ioStatusBlock, bool synchronousOnly) noexcept
    {
        if (IsEmpty())
        {
            return 0;
        }
        struct Unlinked
        {
            Entry* Owner;
            Request* Item;
        };
        constexpr int MaxUnlinked = 64;
        Unlinked unlinked[MaxUnlinked];
        int unlinkedCount = 0;
        int cancelled = 0;
        AcquireSRWLockExclusive(&_lock);
        const size_t count = _count.load(std::memory_order_relaxed);
        for (size_t i = 0; i < count; ++i)
        {
            Entry* entry = _entries[i];
            if (handle != nullptr && entry->Handle != handle)
            {
                continue;
            }
            Request** link = &entry->Head;
            while (*link != nullptr)
            {
                Request* request = *link;
                const bool matches = request->CancelStatus == 0 && (threadId == 0 || request->ThreadId == threadId)
                    && (ioStatusBlock == nullptr || request->IoStatusBlock == ioStatusBlock)
                    && (!synchronousOnly || request->Synchronous);
                if (!matches)
                {
                    link = &request->Next;
                    continue;
                }
                ++cancelled;
                if (request == entry->Head || unlinkedCount == MaxUnlinked)
                {
                    // The worker completes it (the head is in its long poll).
                    request->CancelStatus = STATUS_CANCELLED;
                    SetEvent(entry->CancelEvent);
                    link = &request->Next;
                    continue;
                }
                *link = request->Next;
                request->Queued = false;
                InterlockedIncrement(&entry->References);
                unlinked[unlinkedCount++] = { entry, request };
            }
        }
        ReleaseSRWLockExclusive(&_lock);
        for (int i = 0; i < unlinkedCount; ++i)
        {
            Finish(unlinked[i].Owner, unlinked[i].Item, STATUS_CANCELLED, 0);
            Release(unlinked[i].Owner);
        }
        return cancelled;
    }

    void DirectoryWatches::Closed(HANDLE handle) noexcept
    {
        if (IsEmpty())
        {
            return;
        }
        AcquireSRWLockExclusive(&_lock);
        Entry* entry = DetachLocked(handle);
        ReleaseSRWLockExclusive(&_lock);
        if (entry != nullptr)
        {
            Retire(entry);
        }
    }

    DWORD WINAPI DirectoryWatches::WorkerThunk(PVOID context) noexcept
    {
        auto* entry = static_cast<Entry*>(context);
        entry->Owner->Work(entry);
        return 0;
    }

    void DirectoryWatches::Work(Entry* entry) noexcept
    {
        // Everything this thread calls (pipe opens and closes) goes straight to the system.
        ReentryGuard guard;
        while (true)
        {
            AcquireSRWLockExclusive(&_lock);
            Request* request = entry->Head;
            if (request == nullptr)
            {
                entry->Running = false;
                ReleaseSRWLockExclusive(&_lock);
                break;
            }
            const NTSTATUS stop = entry->Closed ? STATUS_NOTIFY_CLEANUP : request->CancelStatus;
            ResetEvent(entry->CancelEvent);
            ReleaseSRWLockExclusive(&_lock);
            if (stop != 0)
            {
                Finish(entry, request, stop, 0);
                continue;
            }
            ChangeBatch batch{};
            NTSTATUS status = _resolver->WaitChanges(entry->Channel, entry->WatchId, entry->CancelEvent, entry->Reply, batch);
            if (status == STATUS_CANCELLED)
            {
                continue; // closed or cancelled: the next round completes the request
            }
            if (!NT_SUCCESS(status))
            {
                Finish(entry, request, status, 0);
                continue;
            }
            if (batch.Count == 0 && !batch.Overflow)
            {
                continue; // timeout, or the watch is being removed
            }
            ULONG_PTR information = 0;
            if (Answer(request, entry->CompletionFilter, batch, status, information))
            {
                Finish(entry, request, status, information);
            }
        }
        Release(entry);
    }

    bool DirectoryWatches::Answer(Request* request, ULONG completionFilter, const ChangeBatch& batch, NTSTATUS& status,
        ULONG_PTR& information) noexcept
    {
        status = STATUS_NOTIFY_ENUM_DIR;
        information = 0;
        if (batch.Overflow || batch.Count == 0)
        {
            return true;
        }
        const bool extended = request->InformationClass == DirectoryNotifyExtendedInformation;
        const ULONG header = extended ? static_cast<ULONG>(offsetof(FILE_NOTIFY_EXTENDED_INFORMATION, FileName))
                                      : static_cast<ULONG>(offsetof(FILE_NOTIFY_INFORMATION, FileName));
        // Entries start at the alignment of the structure (LARGE_INTEGER fields in the extended one).
        const ULONG alignment = extended ? 8 : 4;

        // First pass: the size the answer needs.
        ULONG total = 0;
        ULONG passing = 0;
        bool tooLarge = false;
        MessageReader sizing(batch.Entries, batch.Bytes);
        for (USHORT i = 0; i < batch.Count; ++i)
        {
            HookIpc::ChangeEntry change{};
            const BYTE* text = nullptr;
            USHORT chars = 0;
            if (!sizing.Read(&change, sizeof(change)) || !sizing.ReadString(text, chars))
            {
                status = STATUS_INVALID_NETWORK_RESPONSE;
                return true;
            }
            if (!PassesFilter(change.Action, completionFilter))
            {
                continue;
            }
            total = (passing == 0 ? 0 : AlignUp(total, alignment)) + header + chars * static_cast<ULONG>(sizeof(wchar_t));
            ++passing;
            tooLarge = tooLarge || total > request->Length;
        }
        if (passing == 0)
        {
            return false;
        }
        if (tooLarge)
        {
            return true; // the caller's buffer cannot hold the changes: STATUS_NOTIFY_ENUM_DIR, like the file system
        }

        BYTE* answer = static_cast<BYTE*>(HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, total));
        if (answer == nullptr)
        {
            return true;
        }
        MessageReader reader(batch.Entries, batch.Bytes);
        ULONG offset = 0;
        ULONG previous = 0;
        bool first = true;
        for (USHORT i = 0; i < batch.Count; ++i)
        {
            HookIpc::ChangeEntry change{};
            const BYTE* text = nullptr;
            USHORT chars = 0;
            reader.Read(&change, sizeof(change));
            reader.ReadString(text, chars);
            if (!PassesFilter(change.Action, completionFilter))
            {
                continue;
            }
            if (!first)
            {
                offset = AlignUp(offset, alignment);
                const DWORD next = offset - previous;
                memcpy(answer + previous, &next, sizeof(next)); // NextEntryOffset is the first field of both structures
            }
            const DWORD nameBytes = chars * static_cast<DWORD>(sizeof(wchar_t));
            if (extended)
            {
                auto* item = reinterpret_cast<FILE_NOTIFY_EXTENDED_INFORMATION*>(answer + offset);
                item->Action = change.Action;
                item->FileNameLength = nameBytes;
            }
            else
            {
                auto* item = reinterpret_cast<FILE_NOTIFY_INFORMATION*>(answer + offset);
                item->Action = change.Action;
                item->FileNameLength = nameBytes;
            }
            memcpy(answer + offset + header, text, nameBytes);
            previous = offset;
            offset += header + nameBytes;
            first = false;
        }
        if (SafeCopy(request->Buffer, answer, total))
        {
            status = STATUS_SUCCESS;
            information = total;
        }
        else
        {
            status = STATUS_INVALID_USER_BUFFER;
        }
        HeapFree(GetProcessHeap(), 0, answer);
        return true;
    }

    void DirectoryWatches::Finish(Entry* entry, Request* request, NTSTATUS status, ULONG_PTR information) noexcept
    {
        AcquireSRWLockExclusive(&_lock);
        if (request->Queued)
        {
            Request** link = &entry->Head;
            while (*link != nullptr && *link != request)
            {
                link = &(*link)->Next;
            }
            if (*link == request)
            {
                *link = request->Next;
            }
            request->Queued = false;
        }
        ReleaseSRWLockExclusive(&_lock);

        // Same order as the I/O manager: status block, event, then the completion port or the APC.
        SafeSetIoStatus(request->IoStatusBlock, status, information);
        if (request->Event != nullptr)
        {
            SetEvent(request->Event);
        }
        if (request->Port != nullptr && request->ApcContext != nullptr)
        {
            _setIoCompletion(request->Port, request->Key, request->ApcContext, status, information);
        }
        else if (request->ApcRoutine != nullptr && request->Thread != nullptr)
        {
            _queueApcThread(request->Thread, reinterpret_cast<PVOID>(request->ApcRoutine), request->ApcContext,
                request->IoStatusBlock, nullptr);
        }
        if (request->Synchronous)
        {
            request->Status = status;
            SetEvent(request->Done); // the waiting caller frees the request
        }
        else
        {
            FreeRequest(request);
        }
    }
}

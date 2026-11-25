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
        Entry* Owner;              // holds a reference while the request lives
        bool Queued;               // linked in its entry's queue (under _lock)
        HANDLE Event;              // own copy (nullptr without one): the caller may close its handle while the request waits
        PIO_APC_ROUTINE ApcRoutine;
        PVOID ApcContext;
        PIO_STATUS_BLOCK IoStatusBlock;
        PVOID Buffer;
        ULONG Length;
        ULONG InformationClass;
        DWORD ThreadId;
        HANDLE Thread;             // own copy, for the APC (nullptr without one)
        bool Synchronous;
        HANDLE Done;               // synchronous only: signalled once Status is set
        NTSTATUS CancelStatus;     // set while the request is claimed by Deliver: complete with it (under _lock)
        NTSTATUS Status;
    };

    struct DirectoryWatches::Entry
    {
        DirectoryWatches* Owner;
        volatile LONG References;
        HANDLE Handle;
        ULONG64 Generation;
        SRWLOCK RegisterLock;      // serializes the registration with Unwatch
        uint32_t WatchId;          // written under RegisterLock and _lock
        ULONG CompletionFilter;    // of the call that registered the watch
        bool Unwatched;            // retired: Unwatch was sent or is due (under RegisterLock)
        HANDLE Port;               // own copy (under _lock)
        PVOID Key;
        bool Closed;               // under _lock
        Request* Head;             // under _lock
        Request* Claimed;          // request Deliver is answering, out of the queue (under _lock)
        SRWLOCK DeliverLock;       // guards the buffer below and serializes Deliver
        BYTE* Pending;             // buffered changes: PendingCount x (ChangeEntry, String Name)
        ULONG PendingBytes;
        ULONG PendingCapacity;
        USHORT PendingCount;
        bool PendingOverflow;      // changes were lost: the next request gets STATUS_NOTIFY_ENUM_DIR
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

    NTSTATUS DirectoryWatches::EnsurePoller() noexcept
    {
        NTSTATUS status = STATUS_SUCCESS;
        AcquireSRWLockExclusive(&_pollerLock);
        if (!_pollerStarted)
        {
            if (_pollBuffer == nullptr)
            {
                _pollBuffer = static_cast<BYTE*>(HeapAlloc(GetProcessHeap(), 0, HookIpc::MaxMessageSize));
            }
            if (_pollBuffer == nullptr)
            {
                status = STATUS_INSUFFICIENT_RESOURCES;
            }
            else if (_channel == nullptr)
            {
                status = _resolver->OpenWatchChannel(_channel);
            }
            if (NT_SUCCESS(status))
            {
                // The poller lives as long as the process; with no watch it waits in the Broker for the next one.
                _pollerStarted = QueueUserWorkItem(&PollerThunk, this, WT_EXECUTELONGFUNCTION) != FALSE;
                status = _pollerStarted ? STATUS_SUCCESS : STATUS_INSUFFICIENT_RESOURCES;
            }
        }
        ReleaseSRWLockExclusive(&_pollerLock);
        return status;
    }

    DWORD WINAPI DirectoryWatches::PollerThunk(PVOID context) noexcept
    {
        static_cast<DirectoryWatches*>(context)->Poll();
    }

    void DirectoryWatches::Poll() noexcept
    {
        // Everything this thread calls (pipe opens and closes) goes straight to the system.
        ReentryGuard guard;
        while (true)
        {
            ULONG bytes = 0;
            const NTSTATUS status = _resolver->WaitAnyChanges(_channel, _pollBuffer, bytes);
            if (NT_SUCCESS(status))
            {
                Dispatch(_pollBuffer, bytes);
                continue;
            }
            // The Broker is gone or refuses the poll: what waits fails like a broken network redirector would.
            FailAll(status);
            Sleep(1000);
        }
    }

    void DirectoryWatches::Dispatch(const BYTE* body, ULONG bytes) noexcept
    {
        MessageReader reader(body, bytes);
        HookIpc::WaitAnyChangesReply reply{};
        if (!reader.Read(&reply, sizeof(reply)))
        {
            FailAll(STATUS_INVALID_NETWORK_RESPONSE);
            return;
        }
        for (USHORT group = 0; group < reply.GroupCount; ++group)
        {
            HookIpc::WatchChangesHeader header{};
            if (!reader.Read(&header, sizeof(header)))
            {
                FailAll(STATUS_INVALID_NETWORK_RESPONSE);
                return;
            }
            const BYTE* start = reader.Position();
            for (USHORT i = 0; i < header.Count; ++i)
            {
                HookIpc::ChangeEntry change{};
                const BYTE* text = nullptr;
                USHORT chars = 0;
                if (!reader.Read(&change, sizeof(change)) || !reader.ReadString(text, chars))
                {
                    FailAll(STATUS_INVALID_NETWORK_RESPONSE);
                    return;
                }
            }
            Entry* entry = FindWatch(header.WatchId);
            if (entry == nullptr)
            {
                continue; // closed meanwhile
            }
            Append(entry, start, static_cast<ULONG>(reader.Position() - start), header.Count,
                (header.Flags & HookIpc::ChangesFlagOverflow) != 0);
            Deliver(entry);
            Release(entry);
        }
    }

    void DirectoryWatches::FailAll(NTSTATUS status) noexcept
    {
        Request* failed = nullptr;
        Request** tail = &failed;
        AcquireSRWLockExclusive(&_lock);
        const size_t count = _count.load(std::memory_order_relaxed);
        for (size_t i = 0; i < count; ++i)
        {
            Entry* entry = _entries[i];
            for (Request* request = entry->Head; request != nullptr; request = request->Next)
            {
                request->Queued = false;
            }
            *tail = entry->Head;
            while (*tail != nullptr)
            {
                tail = &(*tail)->Next;
            }
            entry->Head = nullptr;
        }
        ReleaseSRWLockExclusive(&_lock);
        FinishAll(failed, status);
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
        InitializeSRWLock(&created->DeliverLock);

        Entry* found = nullptr;
        Entry* stale = nullptr;
        Request* staleRequests = nullptr;
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
                stale = DetachLocked(handle, &staleRequests);
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
            Retire(stale, staleRequests);
        }
        if (!added)
        {
            created->References = 1;
            Release(created);
        }
        return found;
    }

    DirectoryWatches::Entry* DirectoryWatches::FindWatch(uint32_t watchId) noexcept
    {
        Entry* found = nullptr;
        AcquireSRWLockShared(&_lock);
        const size_t count = _count.load(std::memory_order_relaxed);
        for (size_t i = 0; i < count; ++i)
        {
            if (_entries[i]->WatchId == watchId)
            {
                found = _entries[i];
                InterlockedIncrement(&found->References);
                break;
            }
        }
        ReleaseSRWLockShared(&_lock);
        return found;
    }

    DirectoryWatches::Entry* DirectoryWatches::DetachLocked(HANDLE handle, Request** requests) noexcept
    {
        *requests = nullptr;
        const size_t count = _count.load(std::memory_order_relaxed);
        for (size_t i = 0; i < count; ++i)
        {
            Entry* entry = _entries[i];
            if (entry->Handle == handle)
            {
                _entries[i] = _entries[count - 1];
                _count.store(count - 1, std::memory_order_release);
                entry->Closed = true;
                for (Request* request = entry->Head; request != nullptr; request = request->Next)
                {
                    request->Queued = false;
                }
                *requests = entry->Head;
                entry->Head = nullptr;
                if (entry->Claimed != nullptr)
                {
                    entry->Claimed->CancelStatus = STATUS_NOTIFY_CLEANUP;
                }
                return entry;
            }
        }
        return nullptr;
    }

    void DirectoryWatches::Retire(Entry* entry, Request* requests) noexcept
    {
        FinishAll(requests, STATUS_NOTIFY_CLEANUP);
        // The Unwatch exchange runs on the thread pool: closing the handle must not wait for the Broker.
        if (!QueueUserWorkItem(&RetireThunk, entry, WT_EXECUTEDEFAULT))
        {
            RetireNow(entry);
        }
    }

    DWORD WINAPI DirectoryWatches::RetireThunk(PVOID context) noexcept
    {
        auto* entry = static_cast<Entry*>(context);
        ReentryGuard guard;
        entry->Owner->RetireNow(entry);
        return 0;
    }

    void DirectoryWatches::RetireNow(Entry* entry) noexcept
    {
        AcquireSRWLockExclusive(&entry->RegisterLock);
        const uint32_t watchId = entry->WatchId;
        entry->Unwatched = true;
        ReleaseSRWLockExclusive(&entry->RegisterLock);
        _resolver->Unwatch(watchId);
        Release(entry);
    }

    void DirectoryWatches::Release(Entry* entry) noexcept
    {
        if (InterlockedDecrement(&entry->References) != 0)
        {
            return;
        }
        CloseLocal(entry->Port);
        if (entry->Pending != nullptr)
        {
            HeapFree(GetProcessHeap(), 0, entry->Pending);
        }
        HeapFree(GetProcessHeap(), 0, entry);
    }

    void DirectoryWatches::FreeRequest(Request* request) noexcept
    {
        CloseLocal(request->Event);
        CloseLocal(request->Thread);
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
        // The poll channel is open before any watch exists, so the Broker never drops a watch for lack of a pipe.
        NTSTATUS status = EnsurePoller();
        if (!NT_SUCCESS(status))
        {
            return status;
        }
        Entry* entry = Acquire(call.Handle, call.Generation);
        if (entry == nullptr)
        {
            return STATUS_INSUFFICIENT_RESOURCES;
        }

        // The first call registers the watch; later calls keep its filter, like the file system.
        AcquireSRWLockExclusive(&entry->RegisterLock);
        if (entry->Unwatched)
        {
            status = STATUS_INVALID_HANDLE; // the handle was closed before the watch started: nothing to register
        }
        else if (entry->WatchId == 0)
        {
            uint32_t watchId = 0;
            status = _resolver->Watch(call.Mapping, *call.Path, call.WatchTree, call.CompletionFilter, watchId);
            if (NT_SUCCESS(status))
            {
                AcquireSRWLockExclusive(&_lock);
                entry->WatchId = watchId;
                entry->CompletionFilter = call.CompletionFilter;
                ReleaseSRWLockExclusive(&_lock);
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
        request->Owner = entry;
        request->ApcRoutine = call.ApcRoutine;
        request->ApcContext = call.ApcContext;
        request->IoStatusBlock = call.IoStatusBlock;
        request->Buffer = call.Buffer;
        request->Length = call.Length;
        request->InformationClass = call.InformationClass;
        request->ThreadId = GetCurrentThreadId();
        request->Synchronous = call.Synchronous;

        AcquireSRWLockShared(&_lock);
        const bool bound = entry->Port != nullptr;
        ReleaseSRWLockShared(&_lock);
        // The port itself is read when the request completes: the binding may change while it waits.
        if (bound && call.ApcRoutine != nullptr)
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
        if ((call.ApcRoutine != nullptr && request->Thread == nullptr) || (call.Synchronous && request->Done == nullptr))
        {
            FreeRequest(request);
            Release(entry);
            return STATUS_INSUFFICIENT_RESOURCES;
        }
        // Own copy of the event: the caller may close its handle while the request waits, the kernel keeps the object.
        if (call.Event != nullptr)
        {
            request->Event = DuplicateLocal(call.Event);
        }
        // The I/O manager clears the event when the request starts.
        if (call.Event != nullptr && (request->Event == nullptr || !ResetEvent(request->Event)))
        {
            FreeRequest(request);
            Release(entry);
            return STATUS_INVALID_HANDLE;
        }

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
        InterlockedIncrement(&entry->References); // the request's
        ReleaseSRWLockExclusive(&_lock);

        // Changes buffered since the last request answer this one at once.
        Deliver(entry);
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
            if (request->Queued)
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
            else if (entry->Claimed == request && request->CancelStatus == 0)
            {
                request->CancelStatus = STATUS_CANCELLED;
            }
            ReleaseSRWLockExclusive(&_lock);
            if (unlinked)
            {
                Finish(request, STATUS_CANCELLED, 0);
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
        const auto matches = [&](const Request* request) noexcept
        {
            return request->CancelStatus == 0 && (threadId == 0 || request->ThreadId == threadId)
                && (ioStatusBlock == nullptr || request->IoStatusBlock == ioStatusBlock)
                && (!synchronousOnly || request->Synchronous);
        };
        Request* cancelled = nullptr;
        int count = 0;
        AcquireSRWLockExclusive(&_lock);
        const size_t entries = _count.load(std::memory_order_relaxed);
        for (size_t i = 0; i < entries; ++i)
        {
            Entry* entry = _entries[i];
            if (handle != nullptr && entry->Handle != handle)
            {
                continue;
            }
            if (entry->Claimed != nullptr && matches(entry->Claimed))
            {
                // Being answered right now: Deliver completes it with the cancel status unless it has an answer.
                entry->Claimed->CancelStatus = STATUS_CANCELLED;
                ++count;
            }
            Request** link = &entry->Head;
            while (*link != nullptr)
            {
                Request* request = *link;
                if (!matches(request))
                {
                    link = &request->Next;
                    continue;
                }
                *link = request->Next;
                request->Queued = false;
                request->Next = cancelled;
                cancelled = request;
                ++count;
            }
        }
        ReleaseSRWLockExclusive(&_lock);
        FinishAll(cancelled, STATUS_CANCELLED);
        return count;
    }

    void DirectoryWatches::Closed(HANDLE handle) noexcept
    {
        if (IsEmpty())
        {
            return;
        }
        Request* requests = nullptr;
        AcquireSRWLockExclusive(&_lock);
        Entry* entry = DetachLocked(handle, &requests);
        ReleaseSRWLockExclusive(&_lock);
        if (entry != nullptr)
        {
            Retire(entry, requests);
        }
    }

    void DirectoryWatches::Append(Entry* entry, const BYTE* changes, ULONG bytes, USHORT count, bool overflow) noexcept
    {
        AcquireSRWLockExclusive(&entry->DeliverLock);
        if (!entry->PendingOverflow)
        {
            const ULONG needed = entry->PendingBytes + bytes;
            const bool fits = !overflow && needed <= MaxPendingBytes && entry->PendingCount + static_cast<ULONG>(count) <= 0xFFFF;
            if (fits && needed > entry->PendingCapacity)
            {
                void* grown = entry->Pending == nullptr
                    ? HeapAlloc(GetProcessHeap(), 0, MaxPendingBytes)
                    : nullptr;
                if (grown != nullptr)
                {
                    entry->Pending = static_cast<BYTE*>(grown);
                    entry->PendingCapacity = MaxPendingBytes;
                }
            }
            if (fits && needed <= entry->PendingCapacity)
            {
                memcpy(entry->Pending + entry->PendingBytes, changes, bytes);
                entry->PendingBytes = needed;
                entry->PendingCount = static_cast<USHORT>(entry->PendingCount + count);
            }
            else
            {
                // Lost (by the Broker or here): the details are worthless, the process lists the directory again.
                entry->PendingOverflow = true;
                entry->PendingBytes = 0;
                entry->PendingCount = 0;
            }
        }
        ReleaseSRWLockExclusive(&entry->DeliverLock);
    }

    void DirectoryWatches::Deliver(Entry* entry) noexcept
    {
        AcquireSRWLockExclusive(&entry->DeliverLock);
        while (entry->PendingOverflow || entry->PendingCount > 0)
        {
            AcquireSRWLockExclusive(&_lock);
            Request* request = entry->Closed ? nullptr : entry->Head;
            if (request != nullptr)
            {
                entry->Head = request->Next;
                request->Queued = false;
                request->Next = nullptr;
                entry->Claimed = request;
            }
            ReleaseSRWLockExclusive(&_lock);
            if (request == nullptr)
            {
                break;
            }

            const ChangeBatch batch{ entry->Pending, entry->PendingBytes, entry->PendingCount, entry->PendingOverflow };
            NTSTATUS status = STATUS_SUCCESS;
            ULONG_PTR information = 0;
            const bool answered = Answer(request, entry->CompletionFilter, batch, status, information);
            // Like the kernel buffer: the changes go with the request they answered, or nowhere when none passed the filter.
            entry->PendingBytes = 0;
            entry->PendingCount = 0;
            entry->PendingOverflow = false;

            AcquireSRWLockExclusive(&_lock);
            entry->Claimed = nullptr;
            const NTSTATUS cancel = request->CancelStatus;
            if (!answered && cancel == 0)
            {
                request->Next = entry->Head; // still waiting, first in line
                entry->Head = request;
                request->Queued = true;
            }
            ReleaseSRWLockExclusive(&_lock);
            if (answered)
            {
                Finish(request, status, information);
            }
            else if (cancel != 0)
            {
                Finish(request, cancel, 0);
            }
        }
        ReleaseSRWLockExclusive(&entry->DeliverLock);
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

    void DirectoryWatches::FinishAll(Request* requests, NTSTATUS status) noexcept
    {
        while (requests != nullptr)
        {
            Request* next = requests->Next;
            Finish(requests, status, 0);
            requests = next;
        }
    }

    void DirectoryWatches::Finish(Request* request, NTSTATUS status, ULONG_PTR information) noexcept
    {
        Entry* entry = request->Owner;
        AcquireSRWLockShared(&_lock);
        // The binding as of now, like the I/O manager: our own copy of the port stays valid if it changes meanwhile.
        const HANDLE port = entry->Port != nullptr ? DuplicateLocal(entry->Port) : nullptr;
        const PVOID key = entry->Key;
        ReleaseSRWLockShared(&_lock);

        // Same order as the I/O manager: status block, event, then the completion port or the APC.
        SafeSetIoStatus(request->IoStatusBlock, status, information);
        if (request->Event != nullptr)
        {
            SetEvent(request->Event);
        }
        if (port != nullptr && request->ApcContext != nullptr)
        {
            _setIoCompletion(port, key, request->ApcContext, status, information);
        }
        else if (request->ApcRoutine != nullptr && request->Thread != nullptr)
        {
            _queueApcThread(request->Thread, reinterpret_cast<PVOID>(request->ApcRoutine), request->ApcContext,
                request->IoStatusBlock, nullptr);
        }
        CloseLocal(port);
        if (request->Synchronous)
        {
            request->Status = status;
            SetEvent(request->Done); // the waiting caller frees the request
        }
        else
        {
            FreeRequest(request);
        }
        Release(entry); // the request's reference
    }
}

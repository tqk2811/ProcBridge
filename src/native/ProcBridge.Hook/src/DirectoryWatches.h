#pragma once
// NtNotifyChangeDirectoryFile[Ex] on virtual directories (plan section 10, M7).
#include "PathMapper.h"
#include <atomic>

namespace ProcBridge::Hook
{
    /// Arguments of one NtNotifyChangeDirectoryFile[Ex] call on a tracked virtual directory handle.
    struct NotifyCall
    {
        HANDLE Handle;
        ULONG64 Generation;           // RemoteHandleTable generation of the handle
        int Mapping;
        const UNICODE_STRING* Path;   // virtual NT path of the directory
        HANDLE Event;
        PIO_APC_ROUTINE ApcRoutine;
        PVOID ApcContext;
        PIO_STATUS_BLOCK IoStatusBlock;
        PVOID Buffer;
        ULONG Length;
        ULONG CompletionFilter;
        bool WatchTree;
        ULONG InformationClass;       // DirectoryNotifyInformation (1) or DirectoryNotifyExtendedInformation (2)
        bool Synchronous;             // handle opened with FILE_SYNCHRONOUS_IO_*: the call waits for the result
        bool Alertable;               // FILE_SYNCHRONOUS_IO_ALERT: the wait ends with STATUS_USER_APC on an APC
    };

    /// Directory change notifications of virtual directory handles, served by the Broker.
    /// The first notify call on a handle registers a watch (WatchDirectory). One poller thread per process collects
    /// the changes of every watch with a single WaitAnyChanges long poll on a pipe instance of its own (opened before
    /// the first watch, it keeps this process known to the Broker), whatever the number of watched handles. Changes
    /// are buffered per handle like the kernel's notify buffer and complete the handle's waiting requests in order:
    /// IO_STATUS_BLOCK, event, then completion port (FileCompletionInformation recorded by SetCompletionPort) or APC
    /// to the issuing thread. A synchronous handle makes the caller wait.
    /// Closing the handle removes the watch (Unwatch) and completes what waits with STATUS_NOTIFY_CLEANUP;
    /// NtCancelIoFile[Ex] completes the matching requests with STATUS_CANCELLED.
    class DirectoryWatches final
    {
    public:
        DirectoryWatches() noexcept = default;
        /// Lives as long as the process (like BrokerClient): workers may still run at exit.
        ~DirectoryWatches() = default;
        DirectoryWatches(const DirectoryWatches&) = delete;
        DirectoryWatches& operator=(const DirectoryWatches&) = delete;

        DWORD Initialize(const IPathResolver& resolver) noexcept;

        /// Fast check before any lock: false when no handle has a watch or a completion port recorded.
        bool IsEmpty() const noexcept { return _count.load(std::memory_order_acquire) == 0; }

        /// Answers the call: STATUS_PENDING for an asynchronous handle (completed later), the result for a
        /// synchronous one, or an error when the request could not start (nothing is signalled then).
        NTSTATUS Notify(const NotifyCall& call) noexcept;

        /// The handle was bound to an I/O completion port (FileCompletionInformation; port null = removed).
        void SetCompletionPort(HANDLE handle, ULONG64 generation, HANDLE port, PVOID key) noexcept;

        /// Cancels waiting requests with STATUS_CANCELLED: handle (nullptr = any), issuing thread (0 = any),
        /// IO_STATUS_BLOCK (nullptr = any), synchronous requests only. Returns the number of requests cancelled.
        int Cancel(HANDLE handle, DWORD threadId, PIO_STATUS_BLOCK ioStatusBlock, bool synchronousOnly) noexcept;

        /// The handle was closed: Unwatch, and its requests complete with STATUS_NOTIFY_CLEANUP.
        void Closed(HANDLE handle) noexcept;

    private:
        struct Request;
        struct Entry;

        using NtSetIoCompletionFn = NTSTATUS(NTAPI*)(HANDLE, PVOID, PVOID, NTSTATUS, ULONG_PTR);
        using NtQueueApcThreadFn = NTSTATUS(NTAPI*)(HANDLE, PVOID, PVOID, PVOID, PVOID);

        /// Changes kept per handle between two requests; beyond that the next request gets STATUS_NOTIFY_ENUM_DIR.
        /// The buffer grows from a small size and is freed once its changes went to a request.
        static constexpr ULONG MaxPendingBytes = HookIpc::MaxMessageSize;
        static constexpr ULONG InitialPendingBytes = 1024;
        /// Groups of a watch id not known yet (the reply raced the registration), kept until Notify records the id.
        static constexpr int MaxOrphans = 8;
        struct Orphan
        {
            uint32_t WatchId;
            BYTE* Changes;
            ULONG Bytes;
            USHORT Count;
            bool Overflow;
        };

        /// Opens the poll channel and starts the poller thread once per process.
        NTSTATUS EnsurePoller() noexcept;
        static DWORD WINAPI PollerThunk(PVOID context) noexcept;
        /// Runs as long as the process, unless the Broker refuses the poll for good (Fatal).
        void Poll() noexcept;
        /// The poll channel broke (Broker restarted or gone): its watches are lost. Forgets every watch id (the next
        /// notify call registers again) and completes waiting requests with STATUS_NOTIFY_ENUM_DIR.
        void ResetWatches() noexcept;
        /// Keeps a group whose watch id no entry has yet, when a registration is in progress (under _lock).
        void KeepOrphanLocked(uint32_t watchId, const BYTE* changes, ULONG bytes, USHORT count, bool overflow) noexcept;
        /// Hands the groups of one WaitAnyChanges reply to their handles.
        void Dispatch(const BYTE* body, ULONG bytes) noexcept;

        /// Entry of handle with a reference, created when missing (nullptr when out of memory). An entry of an older
        /// generation (the handle value was closed and reused) is detached first.
        Entry* Acquire(HANDLE handle, ULONG64 generation) noexcept;
        /// Removes the entry of handle from the table and marks it closed; its requests move to *requests (under _lock).
        Entry* DetachLocked(HANDLE handle, Request** requests) noexcept;
        /// Ends a detached entry: completes the detached requests, then sends Unwatch and drops the table's
        /// reference on the thread pool.
        void Retire(Entry* entry, Request* requests) noexcept;
        static DWORD WINAPI RetireThunk(PVOID context) noexcept;
        void RetireNow(Entry* entry) noexcept;
        void Release(Entry* entry) noexcept;

        /// Adds the changes of one group to the handle's buffer.
        void Append(Entry* entry, const BYTE* changes, ULONG bytes, USHORT count, bool overflow) noexcept;
        /// Answers waiting requests of entry from its buffer, in order, while both are there.
        void Deliver(Entry* entry) noexcept;
        /// Builds the notify answer of a batch into the request's buffer. False when no change of the batch passes
        /// completionFilter (the request keeps waiting).
        bool Answer(Request* request, ULONG completionFilter, const ChangeBatch& batch, NTSTATUS& status, ULONG_PTR& information) noexcept;
        /// Completes an unlinked request like the kernel and drops its reference on the entry.
        void Finish(Request* request, NTSTATUS status, ULONG_PTR information) noexcept;
        /// Completes a list of unlinked requests (linked by Next) with status.
        void FinishAll(Request* requests, NTSTATUS status) noexcept;
        static void FreeRequest(Request* request) noexcept;

        const IPathResolver* _resolver = nullptr;
        NtSetIoCompletionFn _setIoCompletion = nullptr;
        NtQueueApcThreadFn _queueApcThread = nullptr;
        mutable SRWLOCK _lock = SRWLOCK_INIT;
        Entry** _entries = nullptr;     // under _lock
        size_t _capacity = 0;
        std::atomic<size_t> _count{ 0 }; // written under _lock
        SRWLOCK _pollerLock = SRWLOCK_INIT;
        bool _pollerStarted = false;    // under _pollerLock
        WatchChannel* _channel = nullptr; // the poller's once started
        BYTE* _pollBuffer = nullptr;
        std::atomic<NTSTATUS> _fatal{ 0 };  // the Broker refuses the poll for good: notify calls fail with it
        ULONG64 _channelEpoch = 0;          // bumped by ResetWatches (under _lock)
        int _registering = 0;               // registrations in progress (under _lock)
        Orphan _orphans[MaxOrphans]{};      // under _lock
        int _orphanCount = 0;
        DWORD _registerDelayMs = 0;         // test only (PROCBRIDGE_TEST_WATCH_REGISTER_DELAY_MS): widens the registration race
    };
}

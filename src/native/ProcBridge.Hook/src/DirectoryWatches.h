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
    /// The first notify call on a handle registers a watch (WatchDirectory) on a pipe instance of its own (a channel),
    /// which then carries the WaitChanges long polls; while the watch lives the channel keeps this process known to the
    /// Broker. Each request waits in the handle's queue and a worker (thread pool, one per handle at a time) completes
    /// the requests in order, like the kernel: IO_STATUS_BLOCK, event, then completion port (FileCompletionInformation
    /// recorded by SetCompletionPort) or APC to the issuing thread. A synchronous handle makes the caller wait.
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

        /// Entry of handle with a reference, created when missing (nullptr when out of memory). An entry of an older
        /// generation (the handle value was closed and reused) is detached first.
        Entry* Acquire(HANDLE handle, ULONG64 generation) noexcept;
        /// Removes the entry of handle from the table (under the lock) and marks it closed; nullptr when none.
        Entry* DetachLocked(HANDLE handle) noexcept;
        /// Ends a detached entry: wakes its worker, then sends Unwatch and drops the table's reference on the thread pool.
        void Retire(Entry* entry) noexcept;
        static DWORD WINAPI RetireThunk(PVOID context) noexcept;
        void RetireNow(Entry* entry) noexcept;
        void Release(Entry* entry) noexcept;

        static DWORD WINAPI WorkerThunk(PVOID context) noexcept;
        void Work(Entry* entry) noexcept;
        /// Builds the notify answer of a batch into the request's buffer. False when no change of the batch passes
        /// completionFilter (the request keeps waiting).
        bool Answer(Request* request, ULONG completionFilter, const ChangeBatch& batch, NTSTATUS& status, ULONG_PTR& information) noexcept;
        /// Unlinks request (when still queued) and completes it like the kernel.
        void Finish(Entry* entry, Request* request, NTSTATUS status, ULONG_PTR information) noexcept;
        static void FreeRequest(Request* request) noexcept;

        const IPathResolver* _resolver = nullptr;
        NtSetIoCompletionFn _setIoCompletion = nullptr;
        NtQueueApcThreadFn _queueApcThread = nullptr;
        mutable SRWLOCK _lock = SRWLOCK_INIT;
        Entry** _entries = nullptr;     // under _lock
        size_t _capacity = 0;
        std::atomic<size_t> _count{ 0 }; // written under _lock
    };
}

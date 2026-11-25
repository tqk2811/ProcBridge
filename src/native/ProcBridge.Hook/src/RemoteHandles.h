#pragma once
#include "PathMapper.h"
#include <atomic>

namespace ProcBridge::Hook
{
    /// Arguments of one NtQueryDirectoryFile[Ex] call, already copied out of caller memory.
    struct DirectoryQuery
    {
        BYTE* Output;               // hook-owned buffer, copied to the caller afterwards
        ULONG Length;
        ULONG InformationClass;
        bool ReturnSingleEntry;
        bool RestartScan;
        const UNICODE_STRING* FileName; // pattern, nullptr when not given
    };

    /// Handles opened through a virtual path when the resolver is remote (plan 4.4): each is a real handle to
    /// the cache copy, so the table keeps machine B's view of it. Metadata queries report machine B's times and
    /// attributes (the cache copy has its own), and a directory handle (an empty placeholder folder) gets its
    /// listings built from the Broker's List pages, with its own scan state (pattern, cursor) like a file
    /// system keeps per file object.
    class RemoteHandleTable final
    {
    public:
        RemoteHandleTable() noexcept = default;
        ~RemoteHandleTable();
        RemoteHandleTable(const RemoteHandleTable&) = delete;
        RemoteHandleTable& operator=(const RemoteHandleTable&) = delete;

        /// Records handle (replacing a stale entry with the same value). virtualNtPath is copied (trailing '\'
        /// dropped); info is machine B's metadata of the file or directory. Each entry gets a new generation.
        void Add(HANDLE handle, int mapping, const UNICODE_STRING& virtualNtPath, const HookIpc::FileInfo& info) noexcept;

        /// Fast check used before any lock: false when no handle is tracked at all.
        bool IsEmpty() const noexcept { return _count.load(std::memory_order_acquire) == 0; }

        /// Machine B's metadata of handle. False when not tracked.
        bool GetInfo(HANDLE handle, HookIpc::FileInfo& info) const noexcept;

        /// Removes the entry of handle; with a generation (from Find), only that entry (a close that raced with
        /// a new open of the same value leaves the new entry alone).
        void Remove(HANDLE handle, ULONG64 generation = 0) noexcept;

        /// True when handle is tracked; generation receives its entry's generation.
        bool Find(HANDLE handle, ULONG64& generation) const noexcept;

        /// Copies the virtual NT path of the directory handle into path (freed with FreeString). False when the
        /// handle is not a tracked directory.
        bool GetDirectoryPath(HANDLE handle, int& mapping, UNICODE_STRING& path) const noexcept;

        /// Copies what is needed to track the same file under another handle value or in a child process
        /// (mapping, virtual NT path freed with FreeString, machine B's metadata). The scan state of a directory
        /// is not part of it: a copy starts a new listing. False when the handle is not tracked.
        bool GetEntry(HANDLE handle, int& mapping, UNICODE_STRING& path, HookIpc::FileInfo& info) const noexcept;

        /// Answers a directory query for handle. False when handle is not a tracked virtual directory (the call
        /// then goes to the system); otherwise status and written (bytes of query.Output) are the answer.
        bool Query(HANDLE handle, const IPathResolver& resolver, const DirectoryQuery& query, NTSTATUS& status, ULONG& written) noexcept;

    private:
        struct State;

        /// The state of handle with its own lock held exclusively (and a reference), or nullptr. The table lock
        /// is not held while waiting for the state lock, nor afterwards: a query may wait on the network.
        State* Lock(HANDLE handle) const noexcept;

        /// Releases what Lock took.
        static void Unlock(State* state) noexcept;

        static void Release(State* state) noexcept;

        mutable SRWLOCK _lock = SRWLOCK_INIT;
        State** _entries = nullptr;
        size_t _capacity = 0;
        // Written under the exclusive lock; the unlocked read is only a fast path for untracked handles.
        std::atomic<size_t> _count{ 0 };
        ULONG64 _nextGeneration = 0; // under the exclusive lock
    };
}

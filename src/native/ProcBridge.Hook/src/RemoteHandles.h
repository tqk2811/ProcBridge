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

    /// What a removed entry leaves for the caller to finish (NtClose): the Broker's write token and delete-on-close.
    struct RemovedRemoteHandle
    {
        uint64_t WriteToken = 0;     // non-zero: the last handle of this process holding the token was removed
        bool DeleteOnClose = false;
        int Mapping = -1;
        UNICODE_STRING Path{};       // virtual NT path, freed by the caller with FreeString (only with DeleteOnClose)
        uint64_t FileId = 0;         // machine B's file id of the handle (0 when unknown)
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
        /// writeToken (M3): the Broker's token of an open that may change content; copies of the handle in this
        /// process share it (counted), so the Broker hears Close once, when the last copy is closed.
        void Add(HANDLE handle, int mapping, const UNICODE_STRING& virtualNtPath, const HookIpc::FileInfo& info,
            uint64_t writeToken = 0, bool deleteOnClose = false) noexcept;

        /// Fast check used before any lock: false when no handle is tracked at all.
        bool IsEmpty() const noexcept { return _count.load(std::memory_order_acquire) == 0; }

        /// Machine B's metadata of handle. False when not tracked.
        bool GetInfo(HANDLE handle, HookIpc::FileInfo& info) const noexcept;

        /// Removes the entry of handle; with a generation (from Find), only that entry (a close that raced with
        /// a new open of the same value leaves the new entry alone).
        void Remove(HANDLE handle, ULONG64 generation = 0, RemovedRemoteHandle* removed = nullptr) noexcept;

        /// True when some handle of this process still holds token (counted by Add, released by Remove).
        bool HoldsToken(uint64_t token) const noexcept;

        /// Write token of handle (0 when none or not tracked).
        uint64_t GetWriteToken(HANDLE handle) const noexcept;

        /// Sets or clears delete-on-close of handle (FILE_DISPOSITION_ON_CLOSE). False when not tracked.
        bool SetDeleteOnClose(HANDLE handle, bool deleteOnClose) noexcept;

        /// The ranged cache copy of handle is complete (M8): reads stop asking the Broker.
        void ClearRange(HANDLE handle) noexcept;

        /// After a rename (newPath, copied) or an attribute change (info): the entry follows. Either may be null.
        /// The range id of the entry is kept (metadata replies carry none).
        void Update(HANDLE handle, const UNICODE_STRING* newPath, const HookIpc::FileInfo* info) noexcept;

        /// A rename replaced the file at path: other handles still open on the old file no longer have a name on
        /// machine B. They keep reading their cache copy, but deleting or writing them back must not touch the new
        /// file (ReplaceFile deletes the replaced file through its handle).
        void OrphanPath(const UNICODE_STRING& path, HANDLE except) noexcept;

        /// True when handle is tracked but was orphaned by OrphanPath.
        bool IsOrphaned(HANDLE handle) const noexcept;

        /// True when handle is tracked; generation receives its entry's generation.
        bool Find(HANDLE handle, ULONG64& generation) const noexcept;

        /// Copies the virtual NT path of the directory handle into path (freed with FreeString). False when the
        /// handle is not a tracked directory.
        bool GetDirectoryPath(HANDLE handle, int& mapping, UNICODE_STRING& path) const noexcept;

        /// Copies what is needed to track the same file under another handle value or in a child process
        /// (mapping, virtual NT path freed with FreeString, machine B's metadata). The scan state of a directory
        /// is not part of it: a copy starts a new listing. False when the handle is not tracked.
        /// writeToken/deleteOnClose (optional) receive the write state, for a copy in this process only.
        bool GetEntry(HANDLE handle, int& mapping, UNICODE_STRING& path, HookIpc::FileInfo& info,
            uint64_t* writeToken = nullptr, bool* deleteOnClose = nullptr) const noexcept;

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

        /// Token reference counts, under the exclusive table lock.
        void RetainToken(uint64_t token) noexcept;
        /// True when this was the last reference.
        bool ReleaseToken(uint64_t token) noexcept;

        struct TokenCount
        {
            uint64_t Token;
            LONG Count;
        };

        mutable SRWLOCK _lock = SRWLOCK_INIT;
        State** _entries = nullptr;
        size_t _capacity = 0;
        // Written under the exclusive lock; the unlocked read is only a fast path for untracked handles.
        std::atomic<size_t> _count{ 0 };
        ULONG64 _nextGeneration = 0; // under the exclusive lock
        TokenCount* _tokens = nullptr; // under the exclusive lock
        size_t _tokenCount = 0;
        size_t _tokenCapacity = 0;
    };
}

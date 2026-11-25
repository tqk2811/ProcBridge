#pragma once
#include "NtApi.h"
#include <atomic>

namespace ProcBridge::Hook
{
    /// Real (non-virtual) directory handles used as OBJECT_ATTRIBUTES.RootDirectory -> NT device name of
    /// the directory (\Device\HarddiskVolume3\dir, no trailing '\'). Kernelbase passes the current
    /// directory handle as RootDirectory for every path below the current directory, so a virtual root
    /// that sits below the current directory is only visible after joining the two names.
    /// An empty name records a handle whose name could not be read. Entries are dropped by NtClose and
    /// whenever a hooked open returns the same handle value again (stale entry).
    class DirectoryNameCache final
    {
    public:
        static constexpr size_t MaxEntries = 64;

        DirectoryNameCache() noexcept = default;
        ~DirectoryNameCache();
        DirectoryNameCache(const DirectoryNameCache&) = delete;
        DirectoryNameCache& operator=(const DirectoryNameCache&) = delete;

        /// Calls visitor(const UNICODE_STRING& name) under the shared lock when handle is cached.
        /// The name is only valid inside the visitor. Returns false when handle is not cached.
        template <typename Visitor>
        bool Visit(HANDLE handle, Visitor&& visitor) const noexcept
        {
            if (_count.load(std::memory_order_acquire) == 0)
            {
                return false;
            }
            bool found = false;
            AcquireSRWLockShared(&_lock);
            const size_t count = _count.load(std::memory_order_relaxed);
            for (size_t i = 0; i < count; ++i)
            {
                if (_entries[i].Handle == handle)
                {
                    visitor(static_cast<const UNICODE_STRING&>(_entries[i].Name));
                    found = true;
                    break;
                }
            }
            ReleaseSRWLockShared(&_lock);
            return found;
        }

        /// Stores a copy of name (bytes may be 0). Ignored when the cache is full or out of memory.
        void Add(HANDLE handle, const wchar_t* name, size_t bytes) noexcept;

        void Remove(HANDLE handle) noexcept;

    private:
        struct Entry
        {
            HANDLE Handle;
            UNICODE_STRING Name;
        };

        mutable SRWLOCK _lock = SRWLOCK_INIT;
        Entry _entries[MaxEntries] = {};
        // Written under the exclusive lock; the unlocked read in Visit/Remove is only a fast path.
        std::atomic<size_t> _count{ 0 };
    };
}

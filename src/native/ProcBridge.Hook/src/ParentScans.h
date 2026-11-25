#pragma once
#include "NtApi.h"
#include <atomic>
#include <cstdint>

namespace ProcBridge::Hook
{
    /// Scan state of real directory handles whose folder holds virtual roots (the parent of D:\Work\virtual is
    /// D:\Work). A virtual root does not exist in that folder, so when the system's scan of the folder ends the hook
    /// lists the roots itself; this table remembers per handle which roots the folder holds, the pattern of the scan
    /// (the system only receives it with the first query) and which roots were already listed.
    /// Entries are dropped by NtClose, by NtDuplicateObject closing the source, and by a hooked open returning the same
    /// handle value. Holds at most MaxEntries handles; more parent scans at once go unlisted.
    class ParentScans final
    {
    public:
        static constexpr size_t MaxEntries = 64;

        ParentScans() noexcept = default;
        ~ParentScans();
        ParentScans(const ParentScans&) = delete;
        ParentScans& operator=(const ParentScans&) = delete;

        /// Starts a scan of handle: roots is the mask of virtual roots (mapping indexes) in its folder. pattern (copied
        /// and upper-cased) replaces the scan's pattern; nullptr keeps the previous one (a restart without a new
        /// pattern), or lists every root for a new entry. Nothing is listed yet afterwards.
        void Start(HANDLE handle, uint32_t roots, const UNICODE_STRING* pattern) noexcept;

        /// The roots of handle not listed yet, and a copy of its pattern (empty: any name; freed with FreeString).
        /// False when handle has no scan.
        bool Get(HANDLE handle, uint32_t& pending, UNICODE_STRING& pattern) const noexcept;

        /// Marks roots of handle as listed.
        void MarkListed(HANDLE handle, uint32_t roots) noexcept;

        void Remove(HANDLE handle) noexcept;

        /// Fast check before any lock.
        bool IsEmpty() const noexcept { return _count.load(std::memory_order_acquire) == 0; }

    private:
        struct Entry
        {
            HANDLE Handle;
            uint32_t Roots;
            uint32_t Listed;
            UNICODE_STRING Pattern; // upper case, empty = any name
        };

        mutable SRWLOCK _lock = SRWLOCK_INIT;
        Entry _entries[MaxEntries] = {};
        // Written under the exclusive lock; the unlocked read is only a fast path.
        std::atomic<size_t> _count{ 0 };
    };
}

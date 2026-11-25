#pragma once
#include "NtApi.h"
#include <atomic>

namespace ProcBridge::Hook
{
    /// Handles opened through a virtual path -> mapping index. The handle itself is a real handle to
    /// the backing file (plan 4.4); the table only remembers that names must be rewritten.
    /// M1 keeps a small array (few virtual handles per process); a hash table is in the backlog.
    class HandleTable final
    {
    public:
        HandleTable() noexcept = default;
        ~HandleTable();
        HandleTable(const HandleTable&) = delete;
        HandleTable& operator=(const HandleTable&) = delete;

        void Add(HANDLE handle, int mapping) noexcept;

        /// Mapping index or -1. Lock-free when the table is empty (the common case for NtClose).
        int Find(HANDLE handle) const noexcept;

        void Remove(HANDLE handle) noexcept;

    private:
        struct Entry
        {
            HANDLE Handle;
            int Mapping;
        };

        mutable SRWLOCK _lock = SRWLOCK_INIT;
        Entry* _entries = nullptr;
        size_t _capacity = 0;
        std::atomic<size_t> _count{ 0 };
    };
}

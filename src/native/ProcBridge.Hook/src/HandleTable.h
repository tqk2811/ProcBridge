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
        /// generation (optional) receives the entry's generation, for Remove(handle, generation).
        int Find(HANDLE handle, ULONG64* generation = nullptr) const noexcept;

        void Remove(HANDLE handle) noexcept;

        /// Removes the entry only if it still has this generation, i.e. it was not replaced by a new
        /// handle with the same value in the meantime (NtClose removes after the close succeeded).
        void Remove(HANDLE handle, ULONG64 generation) noexcept;

        /// Copies up to capacity entries; returns how many were copied.
        size_t CopyEntries(HANDLE* handles, int* mappings, size_t capacity) const noexcept;

    private:
        struct Entry
        {
            HANDLE Handle;
            int Mapping;
            ULONG64 Generation;
        };

        void RemoveIf(HANDLE handle, bool anyGeneration, ULONG64 generation) noexcept;

        mutable SRWLOCK _lock = SRWLOCK_INIT;
        Entry* _entries = nullptr;
        size_t _capacity = 0;
        std::atomic<size_t> _count{ 0 };
        ULONG64 _nextGeneration = 1;
    };
}

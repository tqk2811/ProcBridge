#include "HandleTable.h"
#include <cstring>

namespace ProcBridge::Hook
{
    HandleTable::~HandleTable()
    {
        // Runs at DLL detach, also when the process exits: the hooks stay installed then and NtClose from later
        // detach code still calls Find. Leave the table empty, not dangling (an access violation at exit otherwise).
        AcquireSRWLockExclusive(&_lock);
        _count.store(0, std::memory_order_release);
        Entry* entries = _entries;
        _entries = nullptr;
        _capacity = 0;
        ReleaseSRWLockExclusive(&_lock);
        if (entries != nullptr)
        {
            HeapFree(GetProcessHeap(), 0, entries);
        }
    }

    void HandleTable::Add(HANDLE handle, int mapping) noexcept
    {
        if (handle == nullptr || handle == INVALID_HANDLE_VALUE)
        {
            return;
        }
        AcquireSRWLockExclusive(&_lock);
        const size_t count = _count.load(std::memory_order_relaxed);
        for (size_t i = 0; i < count; ++i)
        {
            if (_entries[i].Handle == handle)
            {
                // Stale entry (handle closed without passing through NtClose, e.g. by the kernel).
                _entries[i].Mapping = mapping;
                _entries[i].Generation = _nextGeneration++;
                ReleaseSRWLockExclusive(&_lock);
                return;
            }
        }
        if (count == _capacity)
        {
            const size_t capacity = _capacity == 0 ? 64 : _capacity * 2;
            auto* entries = static_cast<Entry*>(HeapAlloc(GetProcessHeap(), 0, capacity * sizeof(Entry)));
            if (entries == nullptr)
            {
                // Out of memory: the handle keeps working, only name rewriting is lost.
                ReleaseSRWLockExclusive(&_lock);
                return;
            }
            if (_entries != nullptr)
            {
                memcpy(entries, _entries, count * sizeof(Entry));
                HeapFree(GetProcessHeap(), 0, _entries);
            }
            _entries = entries;
            _capacity = capacity;
        }
        _entries[count] = { handle, mapping, _nextGeneration++ };
        _count.store(count + 1, std::memory_order_release);
        ReleaseSRWLockExclusive(&_lock);
    }

    int HandleTable::Find(HANDLE handle, ULONG64* generation) const noexcept
    {
        if (_count.load(std::memory_order_acquire) == 0)
        {
            return -1;
        }
        int mapping = -1;
        AcquireSRWLockShared(&_lock);
        const size_t count = _count.load(std::memory_order_relaxed);
        for (size_t i = 0; i < count; ++i)
        {
            if (_entries[i].Handle == handle)
            {
                mapping = _entries[i].Mapping;
                if (generation != nullptr)
                {
                    *generation = _entries[i].Generation;
                }
                break;
            }
        }
        ReleaseSRWLockShared(&_lock);
        return mapping;
    }

    void HandleTable::Remove(HANDLE handle) noexcept
    {
        RemoveIf(handle, true, 0);
    }

    void HandleTable::Remove(HANDLE handle, ULONG64 generation) noexcept
    {
        RemoveIf(handle, false, generation);
    }

    size_t HandleTable::CopyEntries(HANDLE* handles, int* mappings, size_t capacity) const noexcept
    {
        if (_count.load(std::memory_order_acquire) == 0)
        {
            return 0;
        }
        AcquireSRWLockShared(&_lock);
        const size_t count = _count.load(std::memory_order_relaxed);
        const size_t copied = count < capacity ? count : capacity;
        for (size_t i = 0; i < copied; ++i)
        {
            handles[i] = _entries[i].Handle;
            mappings[i] = _entries[i].Mapping;
        }
        ReleaseSRWLockShared(&_lock);
        return copied;
    }

    void HandleTable::RemoveIf(HANDLE handle, bool anyGeneration, ULONG64 generation) noexcept
    {
        // Shared probe first: Remove runs for every successful open that is not virtual.
        if (Find(handle) < 0)
        {
            return;
        }
        AcquireSRWLockExclusive(&_lock);
        const size_t count = _count.load(std::memory_order_relaxed);
        for (size_t i = 0; i < count; ++i)
        {
            if (_entries[i].Handle == handle && (anyGeneration || _entries[i].Generation == generation))
            {
                _entries[i] = _entries[count - 1];
                _count.store(count - 1, std::memory_order_release);
                break;
            }
        }
        ReleaseSRWLockExclusive(&_lock);
    }
}

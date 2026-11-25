#include "DirectoryNameCache.h"
#include "PathString.h"

namespace ProcBridge::Hook
{
    DirectoryNameCache::~DirectoryNameCache()
    {
        const size_t count = _count.load(std::memory_order_relaxed);
        for (size_t i = 0; i < count; ++i)
        {
            FreeString(_entries[i].Name);
        }
    }

    void DirectoryNameCache::Add(HANDLE handle, const wchar_t* name, size_t bytes) noexcept
    {
        if (handle == nullptr || handle == INVALID_HANDLE_VALUE)
        {
            return;
        }
        UNICODE_STRING copy{};
        if (bytes > 0)
        {
            UNICODE_STRING source{};
            source.Buffer = const_cast<PWSTR>(name);
            if (bytes > 0xFFFC || !ConcatString(copy, source, name, bytes))
            {
                return;
            }
        }
        AcquireSRWLockExclusive(&_lock);
        const size_t count = _count.load(std::memory_order_relaxed);
        size_t slot = count;
        for (size_t i = 0; i < count; ++i)
        {
            if (_entries[i].Handle == handle)
            {
                slot = i;
                break;
            }
        }
        if (slot == MaxEntries)
        {
            // Full: the handle is simply queried again next time.
            ReleaseSRWLockExclusive(&_lock);
            FreeString(copy);
            return;
        }
        if (slot < count)
        {
            FreeString(_entries[slot].Name);
        }
        _entries[slot].Handle = handle;
        _entries[slot].Name = copy;
        if (slot == count)
        {
            _count.store(count + 1, std::memory_order_release);
        }
        ReleaseSRWLockExclusive(&_lock);
    }

    void DirectoryNameCache::Remove(HANDLE handle) noexcept
    {
        if (_count.load(std::memory_order_acquire) == 0)
        {
            return;
        }
        // Shared probe first: most handles passed here (every hooked open) are not cached.
        if (!Visit(handle, [](const UNICODE_STRING&) {}))
        {
            return;
        }
        UNICODE_STRING removed{};
        AcquireSRWLockExclusive(&_lock);
        const size_t count = _count.load(std::memory_order_relaxed);
        for (size_t i = 0; i < count; ++i)
        {
            if (_entries[i].Handle == handle)
            {
                removed = _entries[i].Name;
                _entries[i] = _entries[count - 1];
                _entries[count - 1] = {};
                _count.store(count - 1, std::memory_order_release);
                break;
            }
        }
        ReleaseSRWLockExclusive(&_lock);
        FreeString(removed);
    }
}

#include "ParentScans.h"
#include "PathString.h"

extern "C"
{
    NTSYSAPI NTSTATUS NTAPI RtlUpcaseUnicodeString(PUNICODE_STRING destination, PCUNICODE_STRING source, BOOLEAN allocate);
}

namespace ProcBridge::Hook
{
    ParentScans::~ParentScans()
    {
        // Runs at DLL detach while the hooks may still run: empty the table before freeing.
        AcquireSRWLockExclusive(&_lock);
        const size_t count = _count.exchange(0, std::memory_order_acq_rel);
        for (size_t i = 0; i < count; ++i)
        {
            FreeString(_entries[i].Pattern);
            _entries[i] = {};
        }
        ReleaseSRWLockExclusive(&_lock);
    }

    void ParentScans::Start(HANDLE handle, uint32_t roots, const UNICODE_STRING* pattern) noexcept
    {
        UNICODE_STRING copy{};
        if (pattern != nullptr && pattern->Length > 0)
        {
            if (!ConcatString(copy, *pattern, nullptr, 0))
            {
                return;
            }
            RtlUpcaseUnicodeString(&copy, &copy, FALSE);
        }
        UNICODE_STRING replaced{};
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
            ReleaseSRWLockExclusive(&_lock);
            FreeString(copy);
            return;
        }
        Entry& entry = _entries[slot];
        if (slot == count)
        {
            entry = {};
            entry.Handle = handle;
            entry.Pattern = copy;
            _count.store(count + 1, std::memory_order_release);
        }
        else if (pattern != nullptr)
        {
            replaced = entry.Pattern;
            entry.Pattern = copy;
        }
        entry.Roots = roots;
        entry.Listed = 0;
        ReleaseSRWLockExclusive(&_lock);
        FreeString(replaced);
    }

    bool ParentScans::Get(HANDLE handle, uint32_t& pending, UNICODE_STRING& pattern) const noexcept
    {
        pending = 0;
        pattern = {};
        if (IsEmpty())
        {
            return false;
        }
        bool found = false;
        bool copied = true;
        AcquireSRWLockShared(&_lock);
        const size_t count = _count.load(std::memory_order_relaxed);
        for (size_t i = 0; i < count; ++i)
        {
            if (_entries[i].Handle == handle)
            {
                found = true;
                pending = _entries[i].Roots & ~_entries[i].Listed;
                if (_entries[i].Pattern.Length > 0)
                {
                    copied = ConcatString(pattern, _entries[i].Pattern, nullptr, 0);
                }
                break;
            }
        }
        ReleaseSRWLockShared(&_lock);
        return found && copied;
    }

    void ParentScans::MarkListed(HANDLE handle, uint32_t roots) noexcept
    {
        AcquireSRWLockExclusive(&_lock);
        const size_t count = _count.load(std::memory_order_relaxed);
        for (size_t i = 0; i < count; ++i)
        {
            if (_entries[i].Handle == handle)
            {
                _entries[i].Listed |= roots;
                break;
            }
        }
        ReleaseSRWLockExclusive(&_lock);
    }

    void ParentScans::Remove(HANDLE handle) noexcept
    {
        if (IsEmpty())
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
                removed = _entries[i].Pattern;
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

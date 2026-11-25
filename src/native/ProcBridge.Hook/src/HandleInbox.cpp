#include "HandleInbox.h"
#include <cstdio>
#include <cstring>

namespace ProcBridge::Hook
{
    namespace
    {
        constexpr LONG SlotFree = 0;
        constexpr LONG SlotWriting = 1;
        constexpr LONG SlotReady = 2;

        constexpr uint32_t FlagRemote = 0x1;
        constexpr uint32_t FlagDeleteOnClose = 0x2;

        void InboxName(DWORD processId, wchar_t (&name)[64]) noexcept
        {
            swprintf_s(name, L"Local\\ProcBridge-HandleInbox-%lu", processId);
        }
    }

    struct HandleInbox::Slot
    {
        volatile LONG State;
        uint32_t Handle;
        int32_t Mapping;
        uint32_t Flags;
        uint64_t WriteToken;
        HookIpc::FileInfo Info;
        uint32_t PathBytes;
        wchar_t Path[MaxPathChars];
    };

    struct HandleInbox::Header
    {
        volatile LONG Sequence; // bumped after each slot becomes ready
        ULONG SlotTotal;
        Slot Slots[HandleInbox::SlotCount];
    };

    HandleInbox::~HandleInbox()
    {
        // Runs at DLL detach while hooks may still run: Drain checks _view first.
        AcquireSRWLockExclusive(&_lock);
        Header* view = _view;
        _view = nullptr;
        ReleaseSRWLockExclusive(&_lock);
        if (view != nullptr)
        {
            UnmapViewOfFile(view);
        }
        if (_section != nullptr)
        {
            CloseHandle(_section);
        }
    }

    DWORD HandleInbox::Open() noexcept
    {
        wchar_t name[64];
        InboxName(GetCurrentProcessId(), name);
        // A section left by a process with the same id (still held by a sender) is reused: its slots name handles of
        // that process, so it is cleared.
        _section = CreateFileMappingW(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE, 0, sizeof(Header), name);
        if (_section == nullptr)
        {
            return GetLastError();
        }
        _view = static_cast<Header*>(MapViewOfFile(_section, FILE_MAP_WRITE, 0, 0, sizeof(Header)));
        if (_view == nullptr)
        {
            const DWORD error = GetLastError();
            CloseHandle(_section);
            _section = nullptr;
            return error;
        }
        memset(_view, 0, sizeof(Header));
        _view->SlotTotal = SlotCount;
        return ERROR_SUCCESS;
    }

    LONG HandleInbox::ReadSequence() const noexcept
    {
        return InterlockedCompareExchange(&_view->Sequence, 0, 0);
    }

    bool HandleInbox::Take(ULONG index, DuplicatedHandle& entry) noexcept
    {
        Slot& slot = _view->Slots[index];
        if (InterlockedCompareExchange(&slot.State, SlotWriting, SlotReady) != SlotReady)
        {
            return false;
        }
        const uint32_t pathBytes = slot.PathBytes <= sizeof(_path) ? slot.PathBytes : 0;
        entry.Handle = slot.Handle;
        entry.Mapping = slot.Mapping;
        entry.Remote = (slot.Flags & FlagRemote) != 0;
        entry.DeleteOnClose = (slot.Flags & FlagDeleteOnClose) != 0;
        entry.WriteToken = slot.WriteToken;
        entry.Info = slot.Info;
        memcpy(_path, slot.Path, pathBytes);
        entry.Path.Buffer = _path;
        entry.Path.Length = static_cast<USHORT>(pathBytes);
        entry.Path.MaximumLength = static_cast<USHORT>(pathBytes);
        InterlockedExchange(&slot.State, SlotFree);
        return true;
    }

    bool HandleInbox::Send(DWORD processId, const DuplicatedHandle& entry) noexcept
    {
        if (entry.Path.Length > MaxPathChars * sizeof(wchar_t))
        {
            return false;
        }
        wchar_t name[64];
        InboxName(processId, name);
        const HANDLE section = OpenFileMappingW(FILE_MAP_WRITE, FALSE, name);
        if (section == nullptr)
        {
            return false;
        }
        auto* view = static_cast<Header*>(MapViewOfFile(section, FILE_MAP_WRITE, 0, 0, sizeof(Header)));
        bool sent = false;
        if (view != nullptr)
        {
            for (ULONG i = 0; i < SlotCount && !sent; ++i)
            {
                Slot& slot = view->Slots[i];
                if (InterlockedCompareExchange(&slot.State, SlotWriting, SlotFree) != SlotFree)
                {
                    continue;
                }
                slot.Handle = entry.Handle;
                slot.Mapping = entry.Mapping;
                slot.Flags = (entry.Remote ? FlagRemote : 0) | (entry.DeleteOnClose ? FlagDeleteOnClose : 0);
                slot.WriteToken = entry.WriteToken;
                slot.Info = entry.Info;
                slot.PathBytes = entry.Path.Length;
                memcpy(slot.Path, entry.Path.Buffer, entry.Path.Length);
                InterlockedExchange(&slot.State, SlotReady);
                InterlockedIncrement(&view->Sequence);
                sent = true;
            }
            UnmapViewOfFile(view);
        }
        CloseHandle(section);
        return sent;
    }
}

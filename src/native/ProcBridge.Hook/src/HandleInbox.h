#pragma once
#include "NtApi.h"
#include <cstdint>

namespace ProcBridge::Hook
{
    /// A virtual handle duplicated into another hooked process (DuplicateHandle with another process as target). The entry
    /// is only a hint: the receiver trusts nothing in it but the handle value, and rebuilds what it tracks from the handle
    /// itself (its real name must lie under the mapping's backing folder) and, with the Broker, from Stat.
    struct DuplicatedHandle
    {
        uint32_t Handle = 0;     // value in the receiving process
        uint32_t SenderPid = 0;
        int32_t Mapping = -1;
        uint32_t RootHash = 0;   // RootHash(VirtualNt) of the mapping on the sender's side
        uint32_t RangeId = 0;    // ranged cache copy (M8) of the sender's entry, 0 = complete
    };

    /// Shared memory through which hooked processes hand each other virtual handles they duplicate across processes
    /// (plan 13, "Đường dẫn và handle ngoài thư mục ảo"). Each hooked process creates its inbox at start: a section
    /// Local\ProcBridge-HandleInbox-<pid> whose DACL only lets the user and SYSTEM in and whose mandatory label is the
    /// process's own integrity level (no write up). A name that already exists (squatted, or left by a dead process with
    /// the same id) turns the feature off for the process. The header carries the owner's creation time, so a sender never
    /// writes into the section of an earlier process with the same id. Slots stuck in writing (sender died) are reclaimed
    /// after a timeout. The section is never unmapped: hooks may drain it until the process ends.
    class HandleInbox final
    {
    public:
        static constexpr ULONG SlotCount = 16;

        HandleInbox() noexcept = default;
        HandleInbox(const HandleInbox&) = delete;
        HandleInbox& operator=(const HandleInbox&) = delete;

        /// Creates the inbox of this process. Returns ERROR_SUCCESS or a Win32 error (the feature then stays off).
        DWORD Open() noexcept;

        /// Calls adopt(const DuplicatedHandle&) for each entry received since the last call. One shared memory read when
        /// nothing arrived.
        template <typename Adopt>
        void Drain(Adopt&& adopt) noexcept
        {
            Header* view = _view;
            if (view == nullptr || ReadSequence(view) == _seen)
            {
                return;
            }
            // Only taking the slots runs under the lock; adopt may call the Broker (Stat), so it runs after the lock is
            // released. Each entry is taken by exactly one caller, so a concurrent Drain cannot adopt it twice.
            DuplicatedHandle taken[SlotCount];
            ULONG count = 0;
            AcquireSRWLockExclusive(&_lock);
            const LONG sequence = ReadSequence(view);
            if (sequence != _seen)
            {
                _seen = sequence;
                for (ULONG i = 0; i < SlotCount; ++i)
                {
                    if (Take(view, i, taken[count]))
                    {
                        ++count;
                    }
                }
            }
            ReleaseSRWLockExclusive(&_lock);
            for (ULONG i = 0; i < count; ++i)
            {
                adopt(static_cast<const DuplicatedHandle&>(taken[i]));
            }
        }

        /// Writes entry into the inbox of targetProcess. False when that process has no inbox, its inbox belongs to an
        /// earlier process with the same id, or it is full.
        static bool Send(HANDLE targetProcess, const DuplicatedHandle& entry) noexcept;

        /// Case-insensitive hash of a virtual root, so the receiver can tell its mapping is the one the sender meant.
        static uint32_t RootHash(const UNICODE_STRING& root) noexcept;

    private:
        struct Slot;
        struct Header;

        static LONG ReadSequence(const Header* view) noexcept;

        /// Copies a ready slot into entry and frees it; frees a slot stuck in writing for too long.
        static bool Take(Header* view, ULONG index, DuplicatedHandle& entry) noexcept;

        Header* volatile _view = nullptr;
        SRWLOCK _lock = SRWLOCK_INIT;
        LONG _seen = 0;
    };
}

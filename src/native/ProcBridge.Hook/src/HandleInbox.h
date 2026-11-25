#pragma once
#include "NtApi.h"
#include <ProcBridge/HookIpc.h>
#include <cstdint>

namespace ProcBridge::Hook
{
    /// A virtual handle duplicated into another hooked process (DuplicateHandle with another process as target): what
    /// the receiver needs to track its copy like the sender tracks the original.
    struct DuplicatedHandle
    {
        uint32_t Handle = 0;       // value in the receiving process
        int32_t Mapping = -1;
        bool Remote = false;       // the sender had a RemoteHandleTable entry (Broker)
        bool DeleteOnClose = false;
        uint64_t WriteToken = 0;   // retained for the receiver (RetainWrite) before sending
        HookIpc::FileInfo Info{};
        UNICODE_STRING Path{};     // virtual NT path (view, valid during the call)
    };

    /// Shared memory through which hooked processes hand each other the entries of virtual handles they duplicate across
    /// processes (plan 13.8). Each hooked process creates its own inbox (a named section Local\ProcBridge-HandleInbox-<pid>)
    /// at start; a sender writes a slot into the receiver's inbox right after the duplicate succeeded, before the caller
    /// can pass the value on, and the receiver drains the inbox at the start of the hooks that look a handle up. The inbox
    /// is a fixed set of slots: a full inbox, a path longer than MaxPathChars or a receiver without hooks (no inbox) leaves
    /// the copy untracked, as before.
    class HandleInbox final
    {
    public:
        static constexpr ULONG SlotCount = 16;
        static constexpr ULONG MaxPathChars = 1024;

        HandleInbox() noexcept = default;
        ~HandleInbox();
        HandleInbox(const HandleInbox&) = delete;
        HandleInbox& operator=(const HandleInbox&) = delete;

        /// Creates the inbox of this process. Returns ERROR_SUCCESS or a Win32 error.
        DWORD Open() noexcept;

        /// Calls adopt(const DuplicatedHandle&) for each entry received since the last call. One shared memory read when
        /// nothing arrived.
        template <typename Adopt>
        void Drain(Adopt&& adopt) noexcept
        {
            if (_view == nullptr || ReadSequence() == _seen)
            {
                return;
            }
            AcquireSRWLockExclusive(&_lock);
            const LONG sequence = ReadSequence();
            if (sequence != _seen)
            {
                for (ULONG i = 0; i < SlotCount; ++i)
                {
                    DuplicatedHandle entry;
                    if (Take(i, entry))
                    {
                        adopt(static_cast<const DuplicatedHandle&>(entry));
                    }
                }
                _seen = sequence;
            }
            ReleaseSRWLockExclusive(&_lock);
        }

        /// Writes entry into the inbox of process processId. False when that process has no inbox or it is full.
        static bool Send(DWORD processId, const DuplicatedHandle& entry) noexcept;

    private:
        struct Slot;
        struct Header;

        LONG ReadSequence() const noexcept;

        /// Copies a ready slot into entry (its path into _path) and frees the slot.
        bool Take(ULONG index, DuplicatedHandle& entry) noexcept;

        HANDLE _section = nullptr;
        Header* _view = nullptr;
        SRWLOCK _lock = SRWLOCK_INIT;
        LONG _seen = 0;
        wchar_t _path[MaxPathChars] = {};
    };
}
